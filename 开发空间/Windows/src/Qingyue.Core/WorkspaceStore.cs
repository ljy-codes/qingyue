using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Qingyue.Core;

public sealed class WorkspaceStore
{
    private const int MaximumDocuments = 200;
    private const long MaximumDraftBytes = 150L * 1024 * 1024;
    private const int MaximumSessionBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false
    };
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _recoveryPrepared;
    private bool _legacyWasLoaded;
    private readonly HashSet<string> _unopenedDrafts = new(StringComparer.OrdinalIgnoreCase);
    // Encoding hints are metadata only; clean file contents always come from disk.
    private sealed record StoredSession(int Version, List<WorkspaceEntry> Entries, Guid? ActiveId, Dictionary<Guid, TextCodec>? Codecs);
    private sealed record LegacyRecovery(int Version, List<DocumentSnapshot> Documents);
    private string SessionPath => System.IO.Path.Combine(_root, "Session.json");
    private string DraftsPath => System.IO.Path.Combine(_root, "Drafts");
    private string QuarantinePath => System.IO.Path.Combine(_root, "Quarantine");
    private string LegacyRecoveryPath => System.IO.Path.Combine(_root, "Recovery.json");

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
        try
        {
            var copy = session with { Entries = session.Entries.ToList() };
            var snapshots = drafts.ToArray();
            var opened = openedDocumentIds.Concat(copy.Entries.Select(e => e.Id)).ToHashSet();
            return SaveCoreAsync(copy, snapshots, opened);
        }
        catch (Exception ex) when (ex is ArgumentException or NullReferenceException or InvalidOperationException)
        {
            return Task.FromResult(new WorkspaceSaveResult(DateTimeOffset.UtcNow, ["会话快照无效：" + ex.Message]));
        }
    }

    private async Task<WorkspaceSaveResult> SaveCoreAsync(WorkspaceSession session, DocumentSnapshot[] snapshots,
        HashSet<Guid> openedDocumentIds)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => SaveLocked(session, snapshots, openedDocumentIds)).ConfigureAwait(false);
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
        return ClearCoreAsync();
    }

    /// <summary>
    /// Forget one document's recovery draft without touching any other draft.
    /// Session metadata is committed first so a failed file deletion cannot make
    /// the draft reappear as recoverable on the next start.
    /// </summary>
    public Task ClearDraftAsync(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("文档 ID 不能为空。", nameof(id));
        return ClearDraftCoreAsync(id);
    }

    private async Task ClearDraftCoreAsync(Guid id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                WorkspaceSession session;
                Dictionary<Guid, TextCodec> codecs;
                try { session = ReadSession(out codecs) ?? new WorkspaceSession(1, [], null); }
                catch (Exception ex) when (IsDataError(ex))
                {
                    // Never rewrite a corrupt session while only one draft was
                    // requested for deletion; that could erase unrelated recovery data.
                    throw new IOException("会话元数据损坏，不能安全清除单个草稿：" + ex.Message, ex);
                }
                session = session with
                {
                    Entries = session.Entries.Select(entry => entry.Id == id
                        ? entry with { RecoverDraft = false }
                        : entry).ToList()
                };
                Directory.CreateDirectory(_root);
                AtomicWrite(SessionPath, SessionBytes(session, codecs), null);
                string draft = DraftPath(id);
                _unopenedDrafts.Remove(draft);
                if (File.Exists(draft)) File.Delete(draft);
                if (Directory.Exists(QuarantinePath))
                    foreach (var file in Directory.EnumerateFiles(QuarantinePath, "*" + id.ToString("D") + "*"))
                        File.Delete(file);
            }).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("清除当前文档草稿失败：访问被拒绝。", ex); }
        catch (IOException) { throw; }
        catch (Exception ex) when (IsDataError(ex))
        {
            throw new IOException("清除当前文档草稿失败：" + ex.Message, ex);
        }
        finally { _gate.Release(); }
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
                if (File.Exists(LegacyRecoveryPath)) File.Delete(LegacyRecoveryPath);
                if (Directory.Exists(QuarantinePath))
                    foreach (var file in Directory.EnumerateFiles(QuarantinePath))
                        if (System.IO.Path.GetFileName(file).StartsWith("draft-", StringComparison.OrdinalIgnoreCase)
                            || System.IO.Path.GetFileName(file).StartsWith("legacy-", StringComparison.OrdinalIgnoreCase)
                            || System.IO.Path.GetFileName(file).StartsWith("duplicate-", StringComparison.OrdinalIgnoreCase)
                            || System.IO.Path.GetFileName(file).StartsWith("capacity-", StringComparison.OrdinalIgnoreCase))
                            File.Delete(file);
                _legacyWasLoaded = false;
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
            var allowed = session.Entries.Where(e => e.RecoverDraft).Select(e => e.Id).ToHashSet();
            if (snapshots.Any(s => !allowed.Contains(s.Id)))
                throw new InvalidDataException("草稿标识与可恢复标签不一致，原有会话和草稿已保留。");

            // A brand-new workspace must not publish metadata when the supplied set
            // itself exceeds the aggregate budget. Existing workspaces keep their
            // last good state and isolate only the document that no longer fits.
            if (!File.Exists(SessionPath))
            {
                long aggregate = 0;
                foreach (var snapshot in snapshots.Where(snapshot => ShouldRetainSnapshot(snapshot, entries[snapshot.Id])))
                {
                    try
                    {
                        _ = DocumentFile.Restore(snapshot);
                        aggregate += JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions).LongLength;
                    }
                    catch (Exception ex) when (IsDataError(ex))
                    {
                        continue;
                    }
                    if (aggregate > MaximumDraftBytes)
                        throw new InvalidDataException("草稿总量超过 150 MiB 上限。");
                }
            }

            Directory.CreateDirectory(DraftsPath);
            var existing = Directory.Exists(DraftsPath)
                ? Directory.EnumerateFiles(DraftsPath, "*.json")
                    .Select(path => (Path: path, Id: TryDraftId(path)))
                    .Where(item => item.Id is not null)
                    .ToDictionary(item => item.Id!.Value, item => item.Path)
                : [];
            var retained = snapshots
                .Where(snapshot => ShouldRetainSnapshot(snapshot, entries[snapshot.Id]))
                .Select(snapshot => snapshot.Id)
                .ToHashSet();
            long totalBytes = 0;
            foreach (var id in retained)
                if (existing.TryGetValue(id, out var path))
                    try { totalBytes += FileSize(path); }
                    catch (Exception ex) when (IsDataError(ex))
                    {
                        errors.Add("草稿容量无法读取：" + path + "：" + ex.Message);
                    }

            // A single invalid or oversized document must not discard every other
            // valid update. Its previous draft stays in place because it remains live.
            foreach (var snapshot in snapshots)
            {
                if (!retained.Contains(snapshot.Id)) continue;
                var target = DraftPath(snapshot.Id);
                try
                {
                    _ = DocumentFile.Restore(snapshot);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
                    long oldSize = File.Exists(target) ? FileSize(target) : 0;
                    if (bytes.LongLength > MaximumDraftBytes
                        || totalBytes - oldSize > MaximumDraftBytes - bytes.LongLength)
                        throw new InvalidDataException("草稿总量超过 150 MiB，已保留这份文档上一次成功的草稿。");
                    var backup = File.Exists(target) ? target + "." + Guid.NewGuid().ToString("N") + ".bak" : null;
                    if (backup is not null) backups.Add(backup);
                    AtomicWrite(target, bytes, backup);
                    committed.Add((target, backup));
                    totalBytes = totalBytes - oldSize + bytes.LongLength;
                }
                catch (Exception ex) when (IsDataError(ex))
                {
                    errors.Add((snapshot.Path is null ? snapshot.SuggestedName ?? "未命名" : System.IO.Path.GetFileName(snapshot.Path))
                        + "：" + ex.Message);
                }
            }

            var codecs = SnapshotCodecs(snapshots);
            Dictionary<Guid, TextCodec> previousCodecs = [];
            try { _ = ReadSession(out previousCodecs); }
            catch (Exception ex) when (IsDataError(ex)) { previousCodecs = []; }
            foreach (var entry in session.Entries)
                if (!codecs.ContainsKey(entry.Id) && previousCodecs.TryGetValue(entry.Id, out var codec))
                    codecs[entry.Id] = codec;
            var sessionBytes = SessionBytes(session, codecs);
            if (sessionBytes.Length > MaximumSessionBytes) throw new InvalidDataException("会话元数据超过大小上限。");
            AtomicWrite(SessionPath, sessionBytes, null);
            sessionCommitted = true;
            foreach (var id in openedDocumentIds) _unopenedDrafts.Remove(DraftPath(id));

            // Old drafts are removed only after the new session is durable.
            foreach (var path in Directory.EnumerateFiles(DraftsPath, "*.json"))
            {
                var id = TryDraftId(path);
                if (id is not null && !_unopenedDrafts.Contains(path) && !retained.Contains(id.Value))
                    TryCleanup(path, errors);
            }
            if (_legacyWasLoaded && errors.Count == 0 && File.Exists(LegacyRecoveryPath))
            {
                if (Quarantine(LegacyRecoveryPath, "legacy", errors)) _legacyWasLoaded = false;
            }
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
                        else File.Move(backup, target, overwrite: true);
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
        bool hadSessionFile = File.Exists(SessionPath);
        WorkspaceSession? session = null;
        Dictionary<Guid, TextCodec> codecs = [];
        try { session = ReadSession(out codecs); }
        catch (Exception ex) when (IsDataError(ex))
        {
            warnings.Add("会话元数据损坏，将继续找回独立草稿：" + ex.Message);
            Quarantine(SessionPath, "session", warnings);
        }
        var entries = session?.Entries ?? [];
        var entryById = entries.ToDictionary(e => e.Id);
        var drafts = new Dictionary<Guid, (DocumentFile File, string Path)>();
        var draftPaths = new List<string>();
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
                            throw new InvalidDataException("恢复草稿超过本次加载容量。");
                        if (drafts.Count >= MaximumDocuments)
                            throw new InvalidDataException("恢复文档超过 200 个。");
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
                        drafts.Add(id, (file, path));
                        draftPaths.Add(path);
                        totalBytes += length;
                    }
                    catch (Exception ex) when (IsDataError(ex))
                    {
                        warnings.Add("草稿损坏，已跳过：" + System.IO.Path.GetFileName(path) + "：" + ex.Message);
                        Quarantine(path, "draft", warnings);
                    }
                }
            }
            catch (Exception ex) when (IsDataError(ex)) { warnings.Add("草稿目录读取失败：" + ex.Message); }
        }
        var result = new List<RestoredDocument>();
        var pathIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var remappedIds = new Dictionary<Guid, Guid>();
        var handledIds = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            handledIds.Add(entry.Id);
            try
            {
                if (entry.RecoverDraft && drafts.TryGetValue(entry.Id, out var draft)
                    && (draft.File.IsDirty || draft.File.Path is null))
                {
                    Add(entry, draft.File, draft.Path);
                }
                else if (entry.Path is not null)
                {
                    var file = DocumentFile.OpenForSession(entry.Path, entry.Id,
                        codecs.TryGetValue(entry.Id, out var codec) ? codec : null);
                    Add(entry, file, null);
                }
                else if (entry.RecoverDraft) warnings.Add("未命名文档没有可恢复草稿：" + entry.Id);
            }
            catch (Exception ex) when (IsDataError(ex)) { warnings.Add("文档恢复失败：" + (entry.Path ?? entry.Id.ToString()) + "：" + ex.Message); }
        }
        foreach (var (id, draft) in drafts)
        {
            if (handledIds.Contains(id)) continue;
            handledIds.Add(id);
            var entry = new WorkspaceEntry(id, draft.File.Path, false, 0, 0, 0);
            try
            {
                if (!draft.File.IsDirty && draft.File.Path is not null)
                {
                    Add(entry, DocumentFile.OpenForSession(draft.File.Path, id,
                        codecs.TryGetValue(id, out var codec) ? codec : null), null);
                }
                else
                {
                    Add(entry, draft.File, draft.Path);
                    warnings.Add("已找回孤立草稿：" + draft.File.Title);
                }
            }
            catch (Exception ex) when (IsDataError(ex))
            {
                warnings.Add("未能打开草稿对应文件：" + ex.Message);
            }
        }
        if (!hadSessionFile && draftPaths.Count == 0 && File.Exists(LegacyRecoveryPath))
        {
            try
            {
                var legacy = ReadJson<LegacyRecovery>(LegacyRecoveryPath, MaximumDraftBytes)
                    ?? throw new InvalidDataException("旧版恢复文件为空。");
                if (legacy.Version != 1 || legacy.Documents is null || legacy.Documents.Count > MaximumDocuments)
                    throw new InvalidDataException("旧版恢复文件版本或文档数量无效。");
                foreach (var snapshot in legacy.Documents.Take(MaximumDocuments))
                {
                    var file = DocumentFile.Restore(snapshot);
                    Add(new WorkspaceEntry(file.Id, file.Path, false, 0, 0, 0), file, null);
                }
                _legacyWasLoaded = legacy.Documents.Count > 0;
                if (_legacyWasLoaded)
                    warnings.Add("已读取旧版本未保存草稿，将在新记录成功写入后归档旧文件。");
            }
            catch (Exception ex) when (IsDataError(ex))
            {
                warnings.Add("旧版本恢复草稿无法读取，已尝试隔离：" + ex.Message);
                Quarantine(LegacyRecoveryPath, "legacy-invalid", warnings);
            }
        }
        Guid? active = session?.ActiveId;
        var visitedActive = new HashSet<Guid>();
        while (active is not null && visitedActive.Add(active.Value)
            && remappedIds.TryGetValue(active.Value, out var remapped) && remapped != active)
            active = remapped;
        if (active is null || result.All(d => d.Entry.Id != active)) active = result.FirstOrDefault()?.Entry.Id;
        return new WorkspaceLoadResult(result, active, warnings);

        void Add(WorkspaceEntry entry, DocumentFile file, string? draftPath)
        {
            string? pathKey = file.Path is null ? null : CanonicalPath(file.Path);
            if (pathKey is not null && pathIndexes.TryGetValue(pathKey, out int index))
            {
                var existing = result[index];
                if (file.IsDirty && !existing.File.IsDirty)
                {
                    var replacement = existing.Entry with
                    {
                        Id = entry.Id, Path = file.Path, RecoverDraft = true
                    };
                    result[index] = new RestoredDocument(replacement, file);
                    remappedIds[existing.Entry.Id] = entry.Id;
                    warnings.Add("已找回更新的未保存草稿：" + file.Title);
                    return;
                }
                if (file.IsDirty && existing.File.IsDirty)
                {
                    if (result.Count >= MaximumDocuments)
                    {
                        warnings.Add("同文件的额外草稿超过恢复上限，已尝试隔离保留。");
                        if (draftPath is not null) Quarantine(draftPath, "capacity", warnings);
                        return;
                    }
                    var detached = RestorePathless(file, Guid.NewGuid());
                    var detachedEntry = entry with
                    {
                        Id = detached.Id, Path = null, RecoverDraft = true
                    };
                    result.Add(new RestoredDocument(detachedEntry, detached));
                    remappedIds[entry.Id] = detached.Id;
                    warnings.Add(file.Title + " 存在另一份未保存内容，已作为独立恢复副本打开；请另存为。");
                    return;
                }
                remappedIds[entry.Id] = existing.Entry.Id;
                warnings.Add("重复文件已跳过：" + file.Title);
                return;
            }
            if (result.Count >= MaximumDocuments)
            {
                warnings.Add("超过恢复文档上限的草稿已尝试隔离。");
                if (draftPath is not null) Quarantine(draftPath, "capacity", warnings);
                return;
            }
            if (pathKey is not null) pathIndexes[pathKey] = result.Count;
            result.Add(new RestoredDocument(entry with { Path = file.Path }, file));
        }
    }

    private static DocumentFile RestorePathless(DocumentFile file, Guid? id = null) =>
        DocumentFile.Restore(file.CaptureSnapshot() with
        {
            Id = id ?? file.Id, Path = null, SuggestedName = file.Title, WasSaved = false,
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
                || e.SourceAnchor is < 0 or > 100_000_000
                || e.SelectionStart is < 0 or > 100_000_000
                || e.SelectionLength is < 0 or > 100_000_000)
            || session.Entries.Select(e => e.Id).Distinct().Count() != session.Entries.Count
            || (session.ActiveId is not null && session.Entries.All(e => e.Id != session.ActiveId)))
            throw new InvalidDataException("会话版本、文档数量、ID 或位置元数据无效。");
        foreach (var entry in session.Entries)
            if (entry.Path is not null && (entry.Path.Length == 0
                || entry.Path.Contains('\0') || Encoding.UTF8.GetByteCount(entry.Path) > 32768
                || !System.IO.Path.IsPathFullyQualified(entry.Path)
                || System.IO.Path.GetFullPath(entry.Path) != entry.Path))
                throw new InvalidDataException("会话文件路径必须是规范的绝对路径。");
    }
    private static T? ReadJson<T>(string path, long maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException("文件超过恢复大小上限。");
        return JsonSerializer.Deserialize<T>(stream, JsonOptions);
    }
    private static bool ShouldRetainSnapshot(DocumentSnapshot snapshot, WorkspaceEntry entry)
    {
        if (!entry.RecoverDraft) return false;
        try
        {
            var file = DocumentFile.Restore(snapshot);
            return file.IsDirty || !snapshot.WasSaved;
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            // Invalid snapshots remain live so the previous usable draft is not
            // removed. The per-document write below reports the isolated failure.
            return true;
        }
    }
    private static Dictionary<Guid, TextCodec> SnapshotCodecs(IEnumerable<DocumentSnapshot> snapshots) =>
        snapshots.ToDictionary(snapshot => snapshot.Id, snapshot => snapshot.Codec);
    private static Guid? TryDraftId(string path) =>
        Guid.TryParse(System.IO.Path.GetFileNameWithoutExtension(path), out var id) && id != Guid.Empty ? id : null;
    private bool Quarantine(string path, string prefix, List<string> warnings)
    {
        if (!File.Exists(path)) return false;
        try
        {
            Directory.CreateDirectory(QuarantinePath);
            var destination = System.IO.Path.Combine(QuarantinePath,
                prefix + "-" + Guid.NewGuid().ToString("N") + "-" + System.IO.Path.GetFileName(path));
            File.Move(path, destination, false);
            return true;
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            warnings.Add("无法隔离损坏文件，原件仍保留：" + ex.Message);
            return false;
        }
    }
    private string DraftPath(Guid id) => System.IO.Path.Combine(DraftsPath, id.ToString("D") + ".json");
    private static bool PathsEqual(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string CanonicalPath(string path)
    {
        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return System.IO.Path.GetFullPath(target?.FullName ?? path);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            return System.IO.Path.GetFullPath(path);
        }
    }
    private static long FileSize(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("恢复记录不是普通文件。");
        return new FileInfo(path).Length;
    }
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
            if (File.Exists(target))
            {
                if (backup is not null) File.Copy(target, backup, overwrite: false);
                File.Move(temporary, target, overwrite: true);
            }
            else File.Move(temporary, target, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
