using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Qingyue.Core;

public sealed class BookmarkStore
{
    private const int MaximumItems = 500;
    private const int MaximumLabelLength = 160;
    private const long MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false
    };
    private readonly string root;
    private readonly string path;
    private readonly object gate = new();
    private List<DocumentBookmark> items = [];

    public BookmarkStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("书签目录不能为空。", nameof(root));
        this.root = System.IO.Path.GetFullPath(root);
        path = System.IO.Path.Combine(this.root, "Bookmarks.json");
    }

    public IReadOnlyList<DocumentBookmark> Items
    {
        get { lock (gate) return items.ToArray(); }
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

    public IReadOnlyList<DocumentBookmark> For(DocumentFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string? currentPath = NormalizePath(file.Path);
        lock (gate)
        {
            return items.Where(item => currentPath is not null
                    ? string.Equals(NormalizePath(item.Path), currentPath, StringComparison.OrdinalIgnoreCase)
                    : item.DocumentId == file.Id && item.Path is null)
                .OrderByDescending(item => item.UpdatedAt)
                .ToArray();
        }
    }

    public DocumentBookmark Add(DocumentFile file, int sourceAnchor, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        var item = new DocumentBookmark(
            Guid.NewGuid(),
            file.Id,
            NormalizePath(file.Path),
            Math.Clamp(sourceAnchor, 0, file.Text.Length),
            NormalizeLabel(label, file.Text, sourceAnchor),
            DateTimeOffset.UtcNow);
        lock (gate)
        {
            items.RemoveAll(existing => SameDocument(existing, file) && existing.SourceAnchor == item.SourceAnchor);
            items.Insert(0, item);
            if (items.Count > MaximumItems) items.RemoveRange(MaximumItems, items.Count - MaximumItems);
        }
        return item;
    }

    public bool Remove(Guid bookmarkId)
    {
        lock (gate) return items.RemoveAll(item => item.Id == bookmarkId) > 0;
    }

    public int MigratePath(Guid documentId, string? oldPath, string newPath)
    {
        string target = NormalizePath(newPath)
            ?? throw new ArgumentException("书签新路径不能为空。", nameof(newPath));
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

    public Task<BookmarkSaveResult> SaveAsync()
    {
        DocumentBookmark[] snapshot;
        lock (gate) snapshot = items.ToArray();
        return Task.Run(() => Save(snapshot));
    }

    private BookmarkSaveResult Save(DocumentBookmark[] snapshot)
    {
        var errors = new List<string>();
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(root);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            if (bytes.LongLength > MaximumBytes) throw new InvalidDataException("书签文件超过 1 MiB 上限。");
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            temporary = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            errors.Add("书签保存失败：" + ex.Message);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
        return new BookmarkSaveResult(DateTimeOffset.UtcNow, errors);
    }

    private (List<DocumentBookmark> Items, string? Warning) Read()
    {
        if (!File.Exists(path)) return ([], null);
        try
        {
            if (new FileInfo(path).Length > MaximumBytes)
                throw new InvalidDataException("书签文件超过 1 MiB 上限。");
            var loaded = JsonSerializer.Deserialize<List<DocumentBookmark>>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new InvalidDataException("书签文件为空。");
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
            return ([], "书签文件损坏，已忽略" + (quarantine is null ? "" : "并隔离") + "：" + ex.Message);
        }
    }

    private static void Validate(DocumentBookmark item)
    {
        if (item.Id == Guid.Empty || item.DocumentId == Guid.Empty
            || item.SourceAnchor < 0 || item.Label is null || item.Label.Length > MaximumLabelLength
            || item.Path is not null && !System.IO.Path.IsPathFullyQualified(item.Path))
            throw new InvalidDataException("书签字段无效。");
    }

    private static bool SameDocument(DocumentBookmark item, DocumentFile file)
    {
        string? current = NormalizePath(file.Path);
        return current is not null
            ? string.Equals(NormalizePath(item.Path), current, StringComparison.OrdinalIgnoreCase)
            : item.DocumentId == file.Id && item.Path is null;
    }

    private static string? NormalizePath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : System.IO.Path.GetFullPath(value);

    private static string NormalizeLabel(string? label, string text, int sourceAnchor)
    {
        if (!string.IsNullOrWhiteSpace(label)) return label.Trim()[..Math.Min(label.Trim().Length, MaximumLabelLength)];
        int anchor = Math.Clamp(sourceAnchor, 0, text.Length);
        int start = text.LastIndexOf('\n', Math.Max(0, anchor - 1));
        int end = text.IndexOf('\n', anchor);
        string line = text[(start + 1)..(end < 0 ? text.Length : end)].Trim();
        return string.IsNullOrWhiteSpace(line) ? "阅读位置" : line[..Math.Min(line.Length, MaximumLabelLength)];
    }
}
