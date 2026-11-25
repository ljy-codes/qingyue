using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Qingyue.Core;

public sealed class AnnotationStore
{
    private const int MaximumItems = 5000;
    private const int MaximumQuoteLength = 4000;
    private const int MaximumNoteLength = 4000;
    private const int MaximumContextLength = 160;
    private const int MaximumTags = 10;
    private const int MaximumTagLength = 32;
    private const long MaximumBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false
    };
    private readonly string root;
    private readonly string path;
    private readonly object gate = new();
    private List<DocumentAnnotation> items = [];

    public AnnotationStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("批注目录不能为空。", nameof(root));
        this.root = Path.GetFullPath(root);
        path = Path.Combine(this.root, "Annotations.json");
    }

    public IReadOnlyList<DocumentAnnotation> Items
    {
        get
        {
            lock (gate) return items.ToArray();
        }
    }

    public string? LastWarning { get; private set; }

    public async Task LoadAsync()
    {
        var loaded = await Task.Run(Read).ConfigureAwait(false);
        lock (gate)
        {
            items = loaded.Items;
            LastWarning = loaded.Warning;
        }
    }

    public IReadOnlyList<DocumentAnnotation> For(DocumentFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string? currentPath = NormalizePath(file.Path);
        lock (gate)
        {
            return items.Where(item => currentPath is not null
                    ? string.Equals(NormalizePath(item.Path), currentPath, StringComparison.OrdinalIgnoreCase)
                    : item.DocumentId == file.Id)
                .OrderBy(item => item.SourceStart)
                .ThenBy(item => item.CreatedAt)
                .ToArray();
        }
    }

    public DocumentAnnotation Add(DocumentFile file, AnnotationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(draft);
        lock (gate)
        {
            if (items.Count >= MaximumItems)
                throw new InvalidOperationException("批注数量已达到 5000 条上限。");
        }
        if (draft.SourceStart < 0 || draft.SourceEnd < draft.SourceStart)
            throw new ArgumentOutOfRangeException(nameof(draft), "批注源位置无效。");
        ValidateText(draft.Quote, MaximumQuoteLength, "引文", allowEmpty: true);
        ValidateText(draft.ContextBefore, MaximumContextLength, "前置上下文", allowEmpty: true);
        ValidateText(draft.ContextAfter, MaximumContextLength, "后置上下文", allowEmpty: true);
        if (!Enum.IsDefined(draft.Color))
            throw new ArgumentOutOfRangeException(nameof(draft), "批注颜色无效。");
        string? path = NormalizePath(draft.Path);
        string? note = NormalizeNote(draft.Note);
        IReadOnlyList<string> tags = NormalizeTags(draft.Tags);

        var now = DateTimeOffset.UtcNow;
        var item = new DocumentAnnotation(
            Guid.NewGuid(), draft.DocumentId, path,
            draft.SourceStart, draft.SourceEnd, draft.Quote,
            draft.ContextBefore, draft.ContextAfter, note, tags, draft.Color,
            now, now, AnnotationAnchorState.Exact);
        lock (gate)
        {
            if (items.Count >= MaximumItems)
                throw new InvalidOperationException("批注数量已达到 5000 条上限。");
            items.Add(item);
        }
        return item;
    }

    public bool Update(Guid id, string? note, IReadOnlyList<string> tags, AnnotationColor color)
    {
        if (note is { Length: > MaximumNoteLength })
            throw new ArgumentException("备注超过 4000 字符。", nameof(note));
        string? normalizedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        var suppliedTags = tags ?? [];
        if (suppliedTags.Count > MaximumTags)
            throw new ArgumentException("标签最多 10 个。", nameof(tags));
        var normalizedTags = new List<string>();
        foreach (string tag in suppliedTags)
        {
            string value = tag.Trim();
            if (value.Length == 0 || value.Length > MaximumTagLength)
                throw new ArgumentException("标签不能为空，每个最多 32 字符。", nameof(tags));
            if (!normalizedTags.Contains(value))
                normalizedTags.Add(value);
        }
        if (!Enum.IsDefined(color)) throw new ArgumentOutOfRangeException(nameof(color));

        lock (gate)
        {
            int index = items.FindIndex(item => item.Id == id);
            if (index < 0) return false;
            items[index] = items[index] with
            {
                Note = normalizedNote,
                Tags = normalizedTags.ToArray(),
                Color = color,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            return true;
        }
    }

    public bool Remove(Guid id)
    {
        lock (gate) return items.RemoveAll(item => item.Id == id) > 0;
    }

    public int MigratePath(Guid documentId, string? oldPath, string newPath)
    {
        string target = NormalizePath(newPath)
            ?? throw new ArgumentException("批注新路径不能为空。", nameof(newPath));
        string? source = NormalizePath(oldPath);
        int changed = 0;
        lock (gate)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                // A saved document gets a new ID when reopened. Its path is the
                // durable identity, so an old-path match must migrate even when
                // the tab's in-memory document ID changed.
                bool matches = source is null
                    ? item.DocumentId == documentId && item.Path is null
                    : string.Equals(NormalizePath(item.Path), source, StringComparison.OrdinalIgnoreCase);
                if (!matches) continue;
                items[i] = item with
                {
                    DocumentId = documentId,
                    Path = target,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                changed++;
            }
        }
        return changed;
    }

    public int ApplyAnchorRepairs(IReadOnlyList<AnnotationAnchorRepair> repairs, DateTimeOffset repairedAt)
    {
        ArgumentNullException.ThrowIfNull(repairs);
        int changed = 0;
        lock (gate)
        {
            foreach (var repair in repairs)
            {
                if (!Enum.IsDefined(repair.AnchorState))
                    throw new InvalidDataException("锚点状态无效。");
                int index = items.FindIndex(item => item.Id == repair.Id);
                if (index < 0) continue;
                int start = Math.Max(0, repair.SourceStart);
                int end = Math.Max(start, repair.SourceEnd);
                var item = items[index];
                if (item.SourceStart == start && item.SourceEnd == end
                    && item.AnchorState == repair.AnchorState)
                    continue;
                items[index] = item with
                {
                    SourceStart = start,
                    SourceEnd = end,
                    AnchorState = repair.AnchorState,
                    UpdatedAt = repairedAt
                };
                changed++;
            }
        }
        return changed;
    }

    public Task<AnnotationSaveResult> SaveAsync()
    {
        DocumentAnnotation[] snapshot;
        lock (gate) snapshot = items.ToArray();
        return Task.Run(() => Save(snapshot));
    }

    private AnnotationSaveResult Save(DocumentAnnotation[] snapshot)
    {
        var errors = new List<string>();
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(root);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            if (bytes.LongLength > MaximumBytes)
                throw new InvalidDataException("批注文件超过 64 MiB 上限。");
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            temporary = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            errors.Add("批注保存失败：" + ex.Message);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
        return new AnnotationSaveResult(DateTimeOffset.UtcNow, errors);
    }

    private (List<DocumentAnnotation> Items, string? Warning) Read()
    {
        if (!File.Exists(path)) return ([], null);
        try
        {
            if (new FileInfo(path).Length > MaximumBytes)
                throw new InvalidDataException("批注文件超过 64 MiB 上限。");
            var loaded = JsonSerializer.Deserialize<List<DocumentAnnotation>>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new InvalidDataException("批注文件为空。");
            if (loaded.Count > MaximumItems)
                throw new InvalidDataException("批注数量超过 5,000 条上限。");
            if (loaded.Select(item => item.Id).Distinct().Count() != loaded.Count)
                throw new InvalidDataException("批注包含重复标识。");
            foreach (var item in loaded) Validate(item);
            return (loaded, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            string? quarantine = null;
            try
            {
                quarantine = path + ".corrupt-" + Guid.NewGuid().ToString("N");
                File.Move(path, quarantine, overwrite: false);
            }
            catch { }
            return ([], "批注文件损坏，已忽略" + (quarantine is null ? "" : "并隔离") + "：" + ex.Message);
        }
    }

    private static void Validate(DocumentAnnotation item)
    {
        if (item.Id == Guid.Empty || item.DocumentId == Guid.Empty
            || item.SourceStart < 0 || item.SourceEnd < item.SourceStart
            || item.CreatedAt == default || item.UpdatedAt == default
            || !Enum.IsDefined(item.AnchorState) || !Enum.IsDefined(item.Color)
            || item.Path is not null && !IsValidPath(item.Path))
            throw new InvalidDataException("批注字段无效。");
        ValidateTextForStorage(item.Quote, MaximumQuoteLength, "引文", allowEmpty: true);
        ValidateTextForStorage(item.ContextBefore, MaximumContextLength, "前置上下文", allowEmpty: true);
        ValidateTextForStorage(item.ContextAfter, MaximumContextLength, "后置上下文", allowEmpty: true);
        try
        {
            _ = NormalizeNote(item.Note);
            _ = NormalizeTags(item.Tags);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("批注备注或标签无效。", ex);
        }
    }

    private static void ValidateText(string? value, int maximum, string name, bool allowEmpty)
    {
        if (value is null || value.Length > maximum || !allowEmpty && value.Length == 0)
            throw new ArgumentException($"{name}长度无效。", name);
    }

    private static void ValidateTextForStorage(string? value, int maximum, string name, bool allowEmpty)
    {
        if (value is null || value.Length > maximum || !allowEmpty && value.Length == 0)
            throw new InvalidDataException($"{name}长度无效。");
    }

    private static string? NormalizeNote(string? note)
    {
        if (note is null) return null;
        if (note.Length > MaximumNoteLength)
            throw new ArgumentException("备注超过 4000 字符。", nameof(note));
        string value = note.Trim();
        return value.Length == 0 ? null : value;
    }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        var supplied = tags ?? [];
        if (supplied.Count > MaximumTags)
            throw new ArgumentException("标签最多 10 个。", nameof(tags));
        var normalized = new List<string>();
        foreach (string tag in supplied)
        {
            if (tag is null)
                throw new ArgumentException("标签不能为空。", nameof(tags));
            string value = tag.Trim();
            if (value.Length == 0 || value.Length > MaximumTagLength)
                throw new ArgumentException("标签不能为空，每个最多 32 字符。", nameof(tags));
            if (!normalized.Contains(value))
                normalized.Add(value);
        }
        return normalized.ToArray();
    }

    private static string? NormalizePath(string? value)
    {
        if (value is null) return null;
        if (!IsValidPath(value))
            throw new ArgumentException("批注需要有效的本机绝对路径。", nameof(value));
        return Path.GetFullPath(value);
    }

    private static bool IsValidPath(string value) =>
        value.Length > 0
        && !value.Contains('\0')
        && Encoding.UTF8.GetByteCount(value) <= 4096
        && Path.IsPathFullyQualified(value);
}
