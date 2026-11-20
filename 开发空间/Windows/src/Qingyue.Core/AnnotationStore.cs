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
                    : item.DocumentId == file.Id && item.Path is null)
                .OrderBy(item => item.SourceStart)
                .ThenBy(item => item.CreatedAt)
                .ToArray();
        }
    }

    public DocumentAnnotation Add(DocumentFile file, AnnotationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.DocumentId != file.Id)
            throw new ArgumentException("批注草稿不属于当前文档。", nameof(draft));
        if (draft.SourceStart < 0 || draft.SourceEnd < draft.SourceStart
            || draft.SourceEnd > file.Text.Length)
            throw new ArgumentOutOfRangeException(nameof(draft), "批注源位置超出当前文档。");
        ValidateText(draft.Quote, MaximumQuoteLength, "引文", allowEmpty: false);
        ValidateText(draft.ContextBefore, MaximumContextLength, "前置上下文", allowEmpty: true);
        ValidateText(draft.ContextAfter, MaximumContextLength, "后置上下文", allowEmpty: true);

        var now = DateTimeOffset.UtcNow;
        var item = new DocumentAnnotation(
            Guid.NewGuid(), file.Id, NormalizePath(file.Path),
            draft.SourceStart, draft.SourceEnd, draft.Quote,
            draft.ContextBefore, draft.ContextAfter, null, [], AnnotationColor.Amber,
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
        string? normalizedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (normalizedNote is { Length: > MaximumNoteLength })
            throw new ArgumentException("备注超过 4000 字符。", nameof(note));

        string[] normalizedTags = (tags ?? [])
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedTags.Length > MaximumTags || normalizedTags.Any(tag => tag.Length > MaximumTagLength))
            throw new ArgumentException("标签最多 10 个，每个最多 32 字符。", nameof(tags));
        if (!Enum.IsDefined(color)) throw new ArgumentOutOfRangeException(nameof(color));

        lock (gate)
        {
            int index = items.FindIndex(item => item.Id == id);
            if (index < 0) return false;
            items[index] = items[index] with
            {
                Note = normalizedNote,
                Tags = normalizedTags,
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
                bool matches = item.DocumentId == documentId
                    && (source is null
                        ? item.Path is null
                        : string.Equals(NormalizePath(item.Path), source, StringComparison.OrdinalIgnoreCase));
                if (!matches) continue;
                items[i] = item with { Path = target, UpdatedAt = DateTimeOffset.UtcNow };
                changed++;
            }
        }
        return changed;
    }

    public int ApplyAnchorRepairs(IReadOnlyList<AnnotationAnchorRepair> repairs, DateTimeOffset repairedAt)
    {
        ArgumentNullException.ThrowIfNull(repairs);
        var byId = repairs
            .GroupBy(repair => repair.Id)
            .ToDictionary(group => group.Key, group => group.Last());
        int changed = 0;
        lock (gate)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (!byId.TryGetValue(items[i].Id, out var repair)) continue;
                if (repair.SourceStart < 0 || repair.SourceEnd < repair.SourceStart)
                    throw new InvalidDataException("锚点修复范围无效。");
                if (!Enum.IsDefined(repair.AnchorState))
                    throw new InvalidDataException("锚点状态无效。");
                items[i] = items[i] with
                {
                    SourceStart = repair.SourceStart,
                    SourceEnd = repair.SourceEnd,
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
            if (loaded.Count > MaximumItems) loaded = loaded.Take(MaximumItems).ToList();
            foreach (var item in loaded) Validate(item);
            return (loaded.DistinctBy(item => item.Id).ToList(), null);
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
            || item.Path is not null && !Path.IsPathFullyQualified(item.Path))
            throw new InvalidDataException("批注字段无效。");
        ValidateTextForStorage(item.Quote, MaximumQuoteLength, "引文", allowEmpty: false);
        ValidateTextForStorage(item.ContextBefore, MaximumContextLength, "前置上下文", allowEmpty: true);
        ValidateTextForStorage(item.ContextAfter, MaximumContextLength, "后置上下文", allowEmpty: true);
        if (item.Note is { Length: > MaximumNoteLength })
            throw new InvalidDataException("批注备注过长。");
        if (item.Tags is null || item.Tags.Count > MaximumTags
            || item.Tags.Any(tag => tag is null || tag.Length is 0 or > MaximumTagLength))
            throw new InvalidDataException("批注标签无效。");
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

    private static string? NormalizePath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
}
