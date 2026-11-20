using System.Windows.Media.Imaging;

namespace Qingyue.Windows;

internal readonly record struct MarkdownImageCacheKey(
    string Identity,
    long SourceLength,
    long LastWriteUtcTicks,
    int DecodeWidth,
    int DecodeHeight);

internal sealed record MarkdownImageCacheEntry(
    BitmapSource Bitmap,
    long SourceBytes,
    long DecodedPixels);

internal static class MarkdownImageCache
{
    internal const long MaximumBytes = 64L * 1024 * 1024;
    internal const long MaximumPixels = 48_000_000;

    private static readonly object Gate = new();
    private static readonly Dictionary<MarkdownImageCacheKey, LinkedListNode<(MarkdownImageCacheKey Key,
        MarkdownImageCacheEntry Entry)>> Entries = new();
    private static readonly LinkedList<(MarkdownImageCacheKey Key, MarkdownImageCacheEntry Entry)> Lru = new();
    private static long bytes;
    private static long pixels;

    internal static bool TryGet(MarkdownImageCacheKey key, out MarkdownImageCacheEntry entry)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var node))
            {
                entry = null!;
                return false;
            }

            Lru.Remove(node);
            Lru.AddFirst(node);
            entry = node.Value.Entry;
            return true;
        }
    }

    internal static void Add(MarkdownImageCacheKey key, MarkdownImageCacheEntry entry)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var existing))
            {
                bytes -= existing.Value.Entry.SourceBytes;
                pixels -= existing.Value.Entry.DecodedPixels;
                Lru.Remove(existing);
                Entries.Remove(key);
            }

            var node = Lru.AddFirst((key, entry));
            Entries[key] = node;
            bytes += entry.SourceBytes;
            pixels += entry.DecodedPixels;
            Trim();
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
            Lru.Clear();
            bytes = 0;
            pixels = 0;
        }
    }

    private static void Trim()
    {
        while ((bytes > MaximumBytes || pixels > MaximumPixels) && Lru.Last is { } last)
        {
            Lru.RemoveLast();
            Entries.Remove(last.Value.Key);
            bytes -= last.Value.Entry.SourceBytes;
            pixels -= last.Value.Entry.DecodedPixels;
        }
    }
}
