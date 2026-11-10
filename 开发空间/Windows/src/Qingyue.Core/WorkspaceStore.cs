using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Qingyue.Core;

public sealed class WorkspaceStore
{
    private const int MaximumDocuments = 200;
    private const long MaximumDraftBytes = 150L * 1024 * 1024;
    private const int MaximumSessionBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false
    };
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _clearGeneration;
    private bool _recoveryPrepared;
    private readonly HashSet<string> _unopenedDrafts = new(StringComparer.OrdinalIgnoreCase);
    // Encoding hints are metadata only; clean file contents always come from disk.
    private sealed record StoredSession(int Version, List<WorkspaceEntry> Entries, Guid? ActiveId, Dictionary<Guid, TextCodec>? Codecs);
    private string SessionPath => System.IO.Path.Combine(_root, "Session.json");
    private string DraftsPath => System.IO.Path.Combine(_root, "Drafts");

    public WorkspaceStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("工作区目录不能为空。", nameof(root));
        _root = System.IO.Path.GetFullPath(root);
    }

    // Copy before the first await, while the caller still owns the supplied lists.
    // Snapshot records and their strings are immutable.
    public Task<WorkspaceSaveResult> SaveAsync(WorkspaceSession session, IReadOnlyList<DocumentSnapshot> drafts) =>
        SaveAsync(session, drafts, Array.Empty<Guid>());

    public Task<WorkspaceSaveResult> SaveAsync(WorkspaceSession session, IReadOnlyList<DocumentSnapshot> drafts,
        IReadOnlyCollection<Guid> openedDocumentIds)
    {
        long generation = Interlocked.Read(ref _clearGeneration);
        try
        {
            var copy = session with { Entries = session.Entries.ToList() };
            var snapshots = drafts.ToArray();
            var opened = openedDocumentIds.Concat(copy.Entries.Select(e => e.Id)).ToHashSet();
            return SaveCoreAsync(copy, snapshots, opened, generation);
        }
        catch (Exception ex) when (ex is ArgumentException or NullReferenceException or InvalidOperationException)
        {
            return Task.FromResult(new WorkspaceSaveResult(DateTimeOffset.UtcNow, ["会话快照无效：" + ex.Message]));
        }
    }

    private async Task<WorkspaceSaveResult> SaveCoreAsync(WorkspaceSession session, DocumentSnapshot[] snapshots,
        HashSet<Guid> openedDocumentIds, long generation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                if (generation != Interlocked.Read(ref _clearGeneration))
                    return new WorkspaceSaveResult(DateTimeOffset.UtcNow, []);
                return SaveLocked(session, snapshots, openedDocumentIds);
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkspaceLoadResult> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(LoadLocked).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Protect drafts that this window has not opened, independently of session
    /// restoration and recovery preferences. Enumerate names only, including drafts
    /// above the load budget; inability to prepare must prevent subsequent autosaves.
    /// </summary>
    public async Task PrepareRecoveryAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_recoveryPrepared) return;
            var paths = await Task.Run(() => Directory.Exists(DraftsPath)
                ? Directory.GetFiles(DraftsPath, "*.json") : Array.Empty<string>()).ConfigureAwait(false);
            _unopenedDrafts.UnionWith(paths);
            _recoveryPrepared = true;
        }
        finally { _gate.Release(); }
    }

    public Task ClearDraftsAsync()
    {
        // Invalidate even saves already waiting for the semaphore.
        Interlocked.Increment(ref _clearGeneration);
        return ClearCoreAsync();
    }
    private async Task ClearCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                // Mark metadata first. If deletion fails, stale draft files are
                // explicitly suppressed when this session is read again.
                WorkspaceSession session;
                Dictionary<Guid, TextCodec> codecs;
                try { session = ReadSession(out codecs) ?? new WorkspaceSession(1, [], null); }
                catch (Exception ex) when (IsDataError(ex)) { session = new WorkspaceSession(1, [], null); codecs = []; }
                session = session with
                {
                    Entries = session.Entries.Select(e => e with { RecoverDraft = false }).ToList()
                };
                if (session.ActiveId is not null && session.Entries.All(e => e.Id != session.ActiveId)) session = session with { ActiveId = null };
                Directory.CreateDirectory(_root);
                AtomicWrite(SessionPath, SessionBytes(session, codecs), null);
                _unopenedDrafts.Clear();
                if (Directory.Exists(DraftsPath))
                    foreach (var file in Directory.EnumerateFiles(DraftsPath, "*.json")) File.Delete(file);
            }).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("清除草稿失败：访问被拒绝。", ex); }
        catch (IOException ex) { throw new IOException("清除草稿失败：" + ex.Message, ex); }
        finally { _gate.Release(); }
    }

    private WorkspaceSaveResult SaveLocked(WorkspaceSession session, DocumentSnapshot[] snapshots, HashSet<Guid> openedDocumentIds)
    {
        var errors = new List<string>();
        var committed = new List<(string Target, string? Backup)>();
        var backups = new List<string>();
        bool sessionCommitted = false;
        try
        {
            ValidateSession(session);
            if (snapshots.Length > MaximumDocuments || snapshots.Any(s => s is null)
                || snapshots.Select(s => s.Id).Distinct().Count() != snapshots.Length)
                throw new InvalidDataException("草稿数量超限或包含无效、重复 ID。");
            var entries = session.Entries.ToDictionary(e => e.Id);
            var wanted = new List<DocumentSnapshot>();
            long minimumBytes = 0;
            foreach (var snapshot in snapshots)
            {
                var file = DocumentFile.Restore(snapshot);
                if (!entries.TryGetValue(snapshot.Id, out var entry))
                    throw new InvalidDataException("草稿 ID 不在本次会话中。");
                if (!PathsEqual(entry.Path, snapshot.Path))
                    throw new InvalidDataException("草稿路径与会话元数据不一致。");
                if (!entry.RecoverDraft || (!file.IsDirty && snapshot.WasSaved)) continue;
                minimumBytes += (long)Encoding.UTF8.GetByteCount(snapshot.Text) + Encoding.UTF8.GetByteCount(snapshot.BaselineText);
                if (minimumBytes > MaximumDraftBytes) throw new InvalidDataException("草稿总量超过 150 MiB 上限。");
                wanted.Add(snapshot);
            }
            // Validate and serialize everything before touching existing valid files.
            var payloads = new List<(Guid Id, byte[] Bytes)>();
            long actualBytes = 0;
            foreach (var snapshot in wanted)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
                actualBytes += bytes.LongLength;
                if (actualBytes > MaximumDraftBytes) throw new InvalidDataException("序列化草稿总量超过 150 MiB 上限。");
                payloads.Add((snapshot.Id, bytes));
            }
            var sessionBytes = SessionBytes(session, snapshots.ToDictionary(s => s.Id, s => s.Codec));
            if (sessionBytes.Length > MaximumSessionBytes) throw new InvalidDataException("会话元数据超过大小上限。");
            Directory.CreateDirectory(DraftsPath);
            foreach (var (id, bytes) in payloads)
            {
                var target = DraftPath(id);
                var backup = File.Exists(target) ? target + "." + Guid.NewGuid().ToString("N") + ".bak" : null;
                if (backup is not null) backups.Add(backup);
                AtomicWrite(target, bytes, backup);
                committed.Add((target, backup));
            }
            AtomicWrite(SessionPath, sessionBytes, null);
            sessionCommitted = true;
            foreach (var id in openedDocumentIds) _unopenedDrafts.Remove(DraftPath(id));
            // Old drafts are removed only after the new session is durable.
            var retained = payloads.Select(p => p.Id).ToHashSet();
            foreach (var path in Directory.EnumerateFiles(DraftsPath, "*.json"))
                if (!_unopenedDrafts.Contains(path)
                    && (!Guid.TryParse(System.IO.Path.GetFileNameWithoutExtension(path), out var id) || !retained.Contains(id)))
                    TryCleanup(path, errors);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            errors.Add("会话保存失败：" + ex.Message);
            if (!sessionCommitted)
            {
                for (int i = committed.Count - 1; i >= 0; i--)
                {
                    var (target, backup) = committed[i];
                    try
                    {
                        if (backup is null) File.Delete(target);
                        else File.Replace(backup, target, null, ignoreMetadataErrors: false);
                    }
                    catch (Exception rollback) when (IsDataError(rollback))
                    {
                        if (backup is not null) backups.Remove(backup); // Preserve the last good copy for recovery.
                        errors.Add("草稿回滚失败，旧副本保留在 " + backup + "：" + rollback.Message);
                    }
                }
            }
        }
        finally
        {
            foreach (var backup in backups) TryCleanup(backup, errors);
        }
        return new WorkspaceSaveResult(DateTimeOffset.UtcNow, errors);
    }

    private WorkspaceLoadResult LoadLocked()
    {
        var warnings = new List<string>();
        WorkspaceSession? session = null;
        Dictionary<Guid, TextCodec> codecs = [];
        try { session = ReadSession(out codecs); }
        catch (Exception ex) when (IsDataError(ex))
        {
            warnings.Add("会话元数据损坏，将继续找回独立草稿：" + ex.Message);
            Quarantine(SessionPath, warnings);
        }
        var entries = session?.Entries ?? [];
        var entryById = entries.ToDictionary(e => e.Id);
        var drafts = new Dictionary<Guid, DocumentFile>();
        long totalBytes = 0;
        if (Directory.Exists(DraftsPath))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(DraftsPath, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (!Guid.TryParse(System.IO.Path.GetFileNameWithoutExtension(path), out var id) || id == Guid.Empty)
                            throw new InvalidDataException("草稿文件名不是有效的文档 ID。");
                        var length = new FileInfo(path).Length;
                        if (length > MaximumDraftBytes || totalBytes + length > MaximumDraftBytes)
                        {
                            warnings.Add("草稿超过 150 MiB 恢复上限，文件保留：" + path);
                            continue;
                        }
                        if (drafts.Count >= MaximumDocuments)
                        {
                            warnings.Add("草稿超过 200 文档恢复上限，其他文件保留。");
                            break;
                        }
                        var snapshot = ReadJson<DocumentSnapshot>(path, MaximumDraftBytes)
                            ?? throw new InvalidDataException("草稿内容为空。");
                        if (snapshot.Id != id || drafts.ContainsKey(id)) throw new InvalidDataException("草稿 ID 不匹配或重复。");
                        var file = DocumentFile.Restore(snapshot);
                        if (entryById.TryGetValue(id, out var metadata) && !PathsEqual(metadata.Path, snapshot.Path))
                        {
                            // A process can stop after committing a renamed draft but
                            // before committing Session.json. Recover the valid body,
                            // without trusting either path or acquiring write authority.
                            file = RestorePathless(file);
                            if (metadata.RecoverDraft)
                                warnings.Add("草稿路径与会话元数据不一致，已恢复为无路径副本：" + file.Title);
                        }
                        drafts.Add(id, file);
                        totalBytes += length;
                    }
                    catch (Exception ex) when (IsDataError(ex))
                    {
                        warnings.Add("草稿损坏，已跳过：" + System.IO.Path.GetFileName(path) + "：" + ex.Message);
                        Quarantine(path, warnings);
                    }
                }
            }
            catch (Exception ex) when (IsDataError(ex)) { warnings.Add("草稿目录读取失败：" + ex.Message); }
        }
        var result = new List<RestoredDocument>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            seenIds.Add(entry.Id);
            try
            {
                if (entry.RecoverDraft && drafts.TryGetValue(entry.Id, out var draft))
                {
                    Add(entry, draft);
                }
                else if (entry.Path is not null)
                {
                    var file = DocumentFile.OpenForSession(entry.Path, entry.Id,
                        codecs.TryGetValue(entry.Id, out var codec) ? codec : null);
                    Add(entry, file);
                }
                else if (entry.RecoverDraft) warnings.Add("未命名文档没有可恢复草稿：" + entry.Id);
            }
            catch (Exception ex) when (IsDataError(ex)) { warnings.Add("文档恢复失败：" + (entry.Path ?? entry.Id.ToString()) + "：" + ex.Message); }
        }
        foreach (var (id, file) in drafts)
        {
            if (seenIds.Contains(id)) continue;
            if (result.Count >= MaximumDocuments) { warnings.Add("恢复文档超过 200 个，孤立草稿仍保留。"); break; }
            Add(new WorkspaceEntry(id, file.Path, true, 0, 0, 0), file);
            warnings.Add("已找回孤立草稿：" + file.Title);
        }
        Guid? active = session?.ActiveId;
        if (active is null || result.All(d => d.Entry.Id != active)) active = result.FirstOrDefault()?.Entry.Id;
        return new WorkspaceLoadResult(result, active, warnings);

        void Add(WorkspaceEntry entry, DocumentFile file)
        {
            if (file.Path is not null && !seenPaths.Add(file.Path))
            {
                // No second dirty document receives authority over the same disk path.
                var originalName = file.Title;
                file = RestorePathless(file);
                entry = entry with { Path = null };
                warnings.Add("重复路径已恢复为独立未命名副本：" + originalName);
            }
            result.Add(new RestoredDocument(entry with { Path = file.Path }, file));
        }
    }

    private static DocumentFile RestorePathless(DocumentFile file) =>
        DocumentFile.Restore(file.CaptureSnapshot() with
        {
            Path = null, SuggestedName = file.Title, WasSaved = false,
            BaselineDigest = null, BaselineText = "", BaselineCodec = TextCodec.Utf8,
            BaselineLineEnding = LineEnding.Lf, HadBom = false
        });

    private WorkspaceSession? ReadSession(out Dictionary<Guid, TextCodec> codecs)
    {
        codecs = [];
        if (!File.Exists(SessionPath)) return null;
        var stored = ReadJson<StoredSession>(SessionPath, MaximumSessionBytes)
            ?? throw new InvalidDataException("会话元数据为空。");
        var session = new WorkspaceSession(stored.Version, stored.Entries, stored.ActiveId);
        ValidateSession(session);
        codecs = stored.Codecs ?? [];
        if (codecs.Count > MaximumDocuments || codecs.Any(p => !Enum.IsDefined(p.Value) || session.Entries.All(e => e.Id != p.Key)))
            throw new InvalidDataException("会话编码提示无效。");
        return session;
    }
    private static byte[] SessionBytes(WorkspaceSession session, Dictionary<Guid, TextCodec> codecs) =>
        JsonSerializer.SerializeToUtf8Bytes(new StoredSession(session.Version, session.Entries, session.ActiveId, codecs), JsonOptions);
    private static void ValidateSession(WorkspaceSession session)
    {
        if (session.Version != 1 || session.Entries is null || session.Entries.Count > MaximumDocuments
            || session.Entries.Any(e => e is null || e.Id == Guid.Empty || !Enum.IsDefined(e.Kind)
                || e.SourceAnchor < 0 || e.SelectionStart < 0 || e.SelectionLength < 0)
            || session.Entries.Select(e => e.Id).Distinct().Count() != session.Entries.Count
            || (session.ActiveId is not null && session.Entries.All(e => e.Id != session.ActiveId)))
            throw new InvalidDataException("会话版本、文档数量、ID 或位置元数据无效。");
        foreach (var entry in session.Entries)
            if (entry.Path is not null && (!System.IO.Path.IsPathFullyQualified(entry.Path)
                || System.IO.Path.GetFullPath(entry.Path) != entry.Path))
                throw new InvalidDataException("会话文件路径必须是规范的绝对路径。");
    }
    private static T? ReadJson<T>(string path, long maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException("文件超过恢复大小上限。");
        return JsonSerializer.Deserialize<T>(stream, JsonOptions);
    }
    private void Quarantine(string path, List<string> warnings)
    {
        if (!File.Exists(path)) return;
        try
        {
            var directory = System.IO.Path.Combine(_root, "Quarantine");
            Directory.CreateDirectory(directory);
            File.Move(path, System.IO.Path.Combine(directory, System.IO.Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".bad"), false);
        }
        catch (Exception ex) when (IsDataError(ex)) { warnings.Add("无法隔离损坏文件，原件仍保留：" + ex.Message); }
    }
    private string DraftPath(Guid id) => System.IO.Path.Combine(DraftsPath, id.ToString("D") + ".json");
    private static bool PathsEqual(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool IsDataError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or InvalidDataException or JsonException or ArgumentException or NotSupportedException;
    private static void TryCleanup(string path, List<string> errors)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (IsDataError(ex)) { errors.Add("清理旧草稿失败：" + path + "：" + ex.Message); }
    }
    private static void AtomicWrite(string target, byte[] bytes, string? backup)
    {
        var directory = System.IO.Path.GetDirectoryName(target)!;
        var temporary = System.IO.Path.Combine(directory, ".qingyue-session-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(target)) File.Replace(temporary, target, backup, ignoreMetadataErrors: false);
            else File.Move(temporary, target, false);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
