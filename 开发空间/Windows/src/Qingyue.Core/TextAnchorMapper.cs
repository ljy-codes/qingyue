namespace Qingyue.Core;

public static class TextAnchorMapper
{
    public static int Map(string oldText, string newText, int oldAnchor)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        if (newText.Length == 0) return 0;
        int anchor = Math.Clamp(oldAnchor, 0, oldText.Length);
        if (oldText.Length == 0) return 0;
        if (oldText == newText) return Math.Min(anchor, newText.Length);

        int lineStart = oldText.LastIndexOf('\n', Math.Max(0, anchor - 1)) + 1;
        int lineEnd = oldText.IndexOf('\n', anchor);
        if (lineEnd < 0) lineEnd = oldText.Length;
        string line = oldText[lineStart..lineEnd];
        if (line.Length > 0)
        {
            int occurrence = FindClosestLine(newText, line, anchor, oldText.Length);
            if (occurrence >= 0) return Math.Clamp(occurrence + Math.Min(anchor - lineStart, line.Length), 0, newText.Length);
        }

        int prefix = CommonPrefix(oldText, newText);
        int suffix = CommonSuffix(oldText, newText, prefix);
        if (anchor <= prefix) return Math.Min(anchor, newText.Length);
        if (anchor >= oldText.Length - suffix)
            return Math.Clamp(newText.Length - (oldText.Length - anchor), 0, newText.Length);

        return Math.Clamp((int)Math.Round(anchor * (double)newText.Length / oldText.Length), 0, newText.Length);
    }

    private static int FindClosestLine(string text, string line, int oldAnchor, int oldLength)
    {
        int target = (int)Math.Round(oldAnchor * (double)text.Length / oldLength);
        int best = -1;
        int bestDistance = int.MaxValue;
        int cursor = 0;
        while (cursor <= text.Length - line.Length)
        {
            int found = text.IndexOf(line, cursor, StringComparison.Ordinal);
            if (found < 0) break;
            bool atLineBoundary = (found == 0 || text[found - 1] == '\n')
                && (found + line.Length == text.Length || text[found + line.Length] == '\n');
            if (atLineBoundary)
            {
                int distance = Math.Abs(found - target);
                if (distance < bestDistance) { best = found; bestDistance = distance; }
            }
            cursor = found + Math.Max(1, line.Length);
        }
        return best;
    }

    private static int CommonPrefix(string left, string right)
    {
        int length = Math.Min(left.Length, right.Length);
        int index = 0;
        while (index < length && left[index] == right[index]) index++;
        return index;
    }

    private static int CommonSuffix(string left, string right, int prefix)
    {
        int max = Math.Min(left.Length, right.Length) - prefix;
        int count = 0;
        while (count < max && left[left.Length - 1 - count] == right[right.Length - 1 - count]) count++;
        return count;
    }
}
