using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Qingyue.Core;

public sealed class ReadingListStore
{
    private const int MaximumItems = 500;
    private const long MaximumBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false
    };
    private readonly string root;
    private readonly string path;
    private readonly object gate = new();
    private List<ReadingListEntry> items = [];

    public ReadingListStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("阅读清单目录不能为空。", nameof(root));
        this.root = Path.GetFullPath(root);
        path = Path.Combine(this.root, "ReadingList.json");
    }

    public IReadOnlyList<ReadingListEntry> Items
    {
        get
        {
            lock (gate)
            {
                return items.OrderByDescending(item => item.IsPinned)
                    .ThenByDescending(item => item.UpdatedAt)
                    .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
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

    public ReadingListEntry? Find(string path)
    {
        string normalized;
        try { normalized = NormalizePath(path); }
        catch (ArgumentException) { return null; }
        lock (gate) return items.FirstOrDefault(item =>
            string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public ReadingListEntry Upsert(
        string path,
        int progressPercent,
        int sourceAnchor,
        bool? isRead = null,
        bool? isPinned = null)
    {
        string normalized = NormalizePath(path);
        if (sourceAnchor < 0)
            throw new ArgumentException("阅读位置不能小于 0。", nameof(sourceAnchor));
        lock (gate)
        {
            int index = items.FindIndex(item =>
                string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (index < 0)
            {
                if (items.Count >= MaximumItems)
                    throw new InvalidOperationException("阅读清单已达到 500 条上限。");
                var created = new ReadingListEntry(Guid.NewGuid(), normalized, isPinned ?? false,
                    isRead ?? false, Math.Clamp(progressPercent, 0, 100), sourceAnchor, now, now);
                items.Insert(0, created);
                return created;
            }
            var current = items[index];
            var updated = current with
            {
                IsRead = isRead ?? current.IsRead,
                IsPinned = isPinned ?? current.IsPinned,
                ProgressPercent = Math.Clamp(progressPercent, 0, 100),
                SourceAnchor = sourceAnchor,
                LastOpenedAt = now,
                UpdatedAt = now
            };
            items[index] = updated;
            return updated;
        }
    }

    public bool SetPinned(string path, bool pinned)
    {
        string normalized = NormalizePath(path);
        lock (gate) return Update(normalized, item => item with
        {
            IsPinned = pinned,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    public bool UpdateProgress(string path, int progressPercent, int sourceAnchor, bool? isRead = null)
    {
        string normalized = NormalizePath(path);
        if (sourceAnchor < 0)
            throw new ArgumentException("阅读位置不能小于 0。", nameof(sourceAnchor));
        lock (gate) return Update(normalized, item => item with
        {
            IsRead = isRead ?? item.IsRead,
            ProgressPercent = Math.Clamp(progressPercent, 0, 100),
            SourceAnchor = sourceAnchor,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    public bool MarkRead(string path, bool isRead)
    {
        string normalized = NormalizePath(path);
        lock (gate) return Update(normalized, item => item with
        {
            IsRead = isRead,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    public bool Remove(string path)
    {
        string normalized;
        try { normalized = NormalizePath(path); }
        catch (ArgumentException) { return false; }
        lock (gate) return items.RemoveAll(item =>
            string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>
    /// Moves one reading-list identity after a save, save-as, or rename. When the
    /// destination already has a record, the source record wins because it belongs
    /// to the document that was just migrated. The old record is always removed.
    /// </summary>
    public int MigratePath(string oldPath, string newPath)
    {
        string sourcePath = NormalizePath(oldPath);
        string targetPath = NormalizePath(newPath);
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase)) return 0;
        lock (gate)
        {
            int sourceIndex = items.FindIndex(item =>
                string.Equals(item.Path, sourcePath, StringComparison.OrdinalIgnoreCase));
            if (sourceIndex < 0) return 0;
            var source = items[sourceIndex];
            int targetIndex = items.FindIndex(item =>
                string.Equals(item.Path, targetPath, StringComparison.OrdinalIgnoreCase));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var migrated = source with
            {
                Path = targetPath,
                LastOpenedAt = now,
                UpdatedAt = now
            };
            if (targetIndex < 0)
            {
                items[sourceIndex] = migrated;
                return 1;
            }
            items[targetIndex] = migrated;
            items.RemoveAt(sourceIndex);
            return 1;
        }
    }

    public Task<ReadingListSaveResult> SaveAsync()
    {
        ReadingListEntry[] snapshot;
        lock (gate) snapshot = items.ToArray();
        return Task.Run(() => Save(snapshot));
    }

    private ReadingListSaveResult Save(ReadingListEntry[] snapshot)
    {
        var errors = new List<string>();
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(root);
            if (snapshot.Length > MaximumItems)
                throw new InvalidDataException("阅读清单超过 500 条上限。");
            foreach (var item in snapshot) Validate(item);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            if (bytes.LongLength > MaximumBytes) throw new InvalidDataException("阅读清单超过 2 MiB 上限。");
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            temporary = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            errors.Add("阅读清单保存失败：" + ex.Message);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
        return new(DateTimeOffset.UtcNow, errors);
    }

    private (List<ReadingListEntry> Items, string? Warning) Read()
    {
        if (!File.Exists(path)) return ([], null);
        try
        {
            if (new FileInfo(path).Length > MaximumBytes)
                throw new InvalidDataException("阅读清单超过 2 MiB 上限。");
            var loaded = JsonSerializer.Deserialize<List<ReadingListEntry>>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new InvalidDataException("阅读清单为空。");
            if (loaded.Count > MaximumItems)
                throw new InvalidDataException("阅读清单超过 500 条上限。");
            var validated = loaded.Select(Validate).ToList();
            if (validated.Select(item => item.Id).Distinct().Count() != validated.Count
                || validated.Select(item => item.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != validated.Count)
                throw new InvalidDataException("阅读清单含有重复条目。");
            return (validated, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or ArgumentException)
        {
            string? quarantine = null;
            try
            {
                quarantine = path + ".corrupt-" + Guid.NewGuid().ToString("N");
                File.Move(path, quarantine, overwrite: false);
            }
            catch { }
            return ([], "阅读清单损坏，已忽略" + (quarantine is null ? "" : "并隔离") + "：" + ex.Message);
        }
    }

    private bool Update(string normalized, Func<ReadingListEntry, ReadingListEntry> transform)
    {
        int index = items.FindIndex(item =>
            string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        items[index] = transform(items[index]);
        return true;
    }

    private static ReadingListEntry Validate(ReadingListEntry item)
    {
        if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Path) ||
            item.ProgressPercent is < 0 or > 100 ||
            item.SourceAnchor < 0)
            throw new InvalidDataException("阅读清单字段无效。");
        try
        {
            return item with { Path = NormalizePath(item.Path) };
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("阅读清单字段无效。", ex);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("阅读清单路径不能为空。", nameof(path));
        if (path.Contains('\0') || Encoding.UTF8.GetByteCount(path) > 4096
            || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("阅读清单需要有效的本机绝对路径。", nameof(path));
        return Path.GetFullPath(path);
    }
}
