namespace Qingyue.Core;

public enum ExternalRefreshStatus { Skipped, Unchanged, Changed, Conflict, Unavailable }

/// <summary>
/// Capture on the document's owning thread, read on a worker, then TryApply on the
/// owning thread. Never reads mutable document state from the worker.
/// </summary>
public sealed class ExternalFileRefresh
{
    private readonly DocumentFile source;
    private readonly long version;
    public DocumentSnapshot Snapshot { get; }
    public bool IsCurrent => source.ChangeVersion == version && source.CaptureSnapshot() == Snapshot;

    public ExternalFileRefresh(DocumentFile source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        Snapshot = source.CaptureSnapshot();
        version = source.ChangeVersion;
    }

    public Task<ExternalRefreshResult> ReadAsync(CancellationToken cancellationToken = default,
        bool includeLocalChanges = false) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Read(includeLocalChanges);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);

    private ExternalRefreshResult Read(bool includeLocalChanges)
    {
        if (!Snapshot.WasSaved || Snapshot.Path is null)
            return Result(ExternalRefreshStatus.Skipped);
        try
        {
            bool dirty = Snapshot.Text != Snapshot.BaselineText || Snapshot.Codec != Snapshot.BaselineCodec
                || Snapshot.LineEnding != Snapshot.BaselineLineEnding;
            if (dirty && !includeLocalChanges)
                return Result(DocumentFile.Restore(Snapshot).DiskChanged()
                    ? ExternalRefreshStatus.Conflict : ExternalRefreshStatus.Unchanged);
            // Preserve explicitly chosen BOM-less UTF-16/GB18030. UTF-8 BOM is
            // detected from the new bytes so removing it does not add it back on Save.
            var codec = Snapshot.Codec == TextCodec.Utf8Bom ? TextCodec.Utf8 : Snapshot.Codec;
            var disk = DocumentFile.Open(Snapshot.Path, codec);
            return disk.CaptureSnapshot().BaselineDigest == Snapshot.BaselineDigest
                ? Result(ExternalRefreshStatus.Unchanged)
                : new ExternalRefreshResult(this, ExternalRefreshStatus.Changed, disk, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new ExternalRefreshResult(this, ExternalRefreshStatus.Unavailable, null, ex.Message);
        }
    }

    private ExternalRefreshResult Result(ExternalRefreshStatus status) => new(this, status, null, null);

    internal bool TryApply(DocumentFile replacement, bool discardLocalChanges) =>
        source.TryAcceptExternalVersion(Snapshot, version, replacement, discardLocalChanges);
}

public sealed class ExternalRefreshResult
{
    private readonly ExternalFileRefresh request;
    private readonly DocumentFile? replacement;
    public ExternalRefreshStatus Status { get; }
    public string? Error { get; }
    public string? Text => replacement?.Text;
    public bool IsCurrent => request.IsCurrent;

    internal ExternalRefreshResult(ExternalFileRefresh request, ExternalRefreshStatus status,
        DocumentFile? replacement, string? error)
    {
        this.request = request;
        this.replacement = replacement;
        Status = status;
        Error = error;
    }

    /// <summary>False for dirty, changed, renamed, saved, or already-applied documents.</summary>
    public bool TryApply(bool discardLocalChanges = false) => Status == ExternalRefreshStatus.Changed && replacement is not null
        && request.TryApply(replacement, discardLocalChanges);
}
