using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace Qingyue.Windows;

internal readonly record struct MarkdownImageCacheKey(string ContentDigest)
{
    internal static MarkdownImageCacheKey FromBytes(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));
}

internal sealed record MarkdownImageCacheEntry(
    BitmapSource Bitmap,
    byte[] DecodedPng,
    long SourceBytes,
    long OriginalPixels,
    long DecodedPixels,
    long Cost)
{
    internal long DecodedPngBytes => DecodedPng.LongLength;
}

internal static class MarkdownImageCache
{
    internal const int MaximumCount = 32;
    internal const long MaximumCost = 24L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Dictionary<MarkdownImageCacheKey, LinkedListNode<(MarkdownImageCacheKey Key,
        MarkdownImageCacheEntry Entry)>> Entries = new();
    private static readonly LinkedList<(MarkdownImageCacheKey Key, MarkdownImageCacheEntry Entry)> Lru = new();
    private static long cost;

    internal static int Count
    {
        get { lock (Gate) return Entries.Count; }
    }

    internal static long Cost
    {
        get { lock (Gate) return cost; }
    }

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
                cost -= existing.Value.Entry.Cost;
                Lru.Remove(existing);
                Entries.Remove(key);
            }

            var node = Lru.AddFirst((key, entry));
            Entries[key] = node;
            cost += entry.Cost;
            Trim();
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
            Lru.Clear();
            cost = 0;
        }
    }

    private static void Trim()
    {
        while ((Entries.Count > MaximumCount || cost > MaximumCost) && Lru.Last is { } last)
        {
            Lru.RemoveLast();
            Entries.Remove(last.Value.Key);
            cost -= last.Value.Entry.Cost;
        }
    }
}
