namespace Qingyue.Core;

public enum TextDiffLineKind
{
    Equal,
    Added,
    Removed,
    Changed
}

public sealed record TextDiffLine(
    TextDiffLineKind Kind,
    string Text,
    int LeftLine,
    int RightLine);

public static class TextDiff
{
    public const int MaximumCharacters = 2_000_000;
    public const int MaximumLines = 100_000;

    public static IReadOnlyList<TextDiffLine> Build(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Length > MaximumCharacters || right.Length > MaximumCharacters)
            throw new ArgumentException("差异文件不能超过 200 万个 UTF-16 单元。");

        var a = Lines(left);
        var b = Lines(right);
        if (a.Length > MaximumLines || b.Length > MaximumLines)
            throw new ArgumentException("差异文件不能超过 10 万行。");

        // Use a forward exact-line index instead of an O(left*right) matrix.
        // It keeps memory linear and gives stable, readable hunks for documents
        // where lines are mostly unique. Adjacent unmatched lines are combined
        // into Changed below.
        var positions = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (int i = 0; i < b.Length; i++)
            (positions.TryGetValue(b[i], out var queue)
                ? queue
                : positions[b[i]] = new Queue<int>()).Enqueue(i);

        var ordered = new List<TextDiffLine>(a.Length + b.Length);
        int rightCursor = 0;
        for (int leftIndex = 0; leftIndex < a.Length; leftIndex++)
        {
            int match = -1;
            if (positions.TryGetValue(a[leftIndex], out var candidates))
            {
                while (candidates.Count > 0 && candidates.Peek() < rightCursor) candidates.Dequeue();
                if (candidates.Count > 0) match = candidates.Dequeue();
            }
            if (match < 0)
            {
                ordered.Add(new(TextDiffLineKind.Removed, a[leftIndex], leftIndex + 1, rightCursor));
                continue;
            }
            while (rightCursor < match)
                ordered.Add(new(TextDiffLineKind.Added, b[rightCursor], leftIndex + 1, rightCursor++ + 1));
            ordered.Add(new(TextDiffLineKind.Equal, a[leftIndex], leftIndex + 1, match + 1));
            rightCursor = match + 1;
        }
        while (rightCursor < b.Length)
            ordered.Add(new(TextDiffLineKind.Added, b[rightCursor], a.Length, ++rightCursor));

        var result = new List<TextDiffLine>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i + 1 < ordered.Count &&
                ordered[i].Kind == TextDiffLineKind.Removed &&
                ordered[i + 1].Kind == TextDiffLineKind.Added)
            {
                var removed = ordered[i++];
                var added = ordered[i];
                result.Add(new(TextDiffLineKind.Changed, removed.Text, removed.LeftLine, added.RightLine));
                result.Add(new(TextDiffLineKind.Changed, added.Text, removed.LeftLine, added.RightLine));
            }
            else result.Add(ordered[i]);
        }
        return result.AsReadOnly();
    }

    private static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        return text.ReplaceLineEndings("\n").Split('\n');
    }
}
