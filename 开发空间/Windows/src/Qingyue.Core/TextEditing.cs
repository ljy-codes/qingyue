namespace Qingyue.Core;

public sealed record TextEditResult(
    int Start,
    int Length,
    string Replacement,
    int SelectionStart,
    int SelectionLength);

/// <summary>Source-preserving plain-text editing with macOS-compatible UTF-16 offsets.</summary>
public static class TextEditing
{
    /// <summary>
    /// Empty selections insert spaces. Nonempty selections indent their complete
    /// lines; a selection ending at the next line's start does not include that line.
    /// </summary>
    public static TextEditResult? Indent(string text, int start, int length, bool remove, int spaces = 2)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!ValidSelection(text, start, length) || spaces <= 0) return null;
        if (!remove && length == 0)
        {
            string whitespace = new(' ', spaces);
            return new(start, 0, whitespace, start + spaces, 0);
        }

        int finalLocation = length == 0 ? start : start + length - 1;
        var first = LineRange(text, start);
        var last = LineRange(text, finalLocation);
        int blockStart = first.Start;
        int blockEnd = last.End;
        string indentation = remove ? "" : new string(' ', spaces);
        var replacement = new System.Text.StringBuilder(blockEnd - blockStart + spaces);
        var edits = new List<PrefixEdit>();
        int cursor = blockStart;

        do
        {
            var line = LineRange(text, cursor);
            int lineLength = line.End - line.Start;
            int removed = 0;
            if (remove && lineLength > 0)
            {
                if (text[cursor] == '\t')
                {
                    removed = 1;
                }
                else
                {
                    while (removed < Math.Min(spaces, lineLength) && text[cursor + removed] == ' ')
                        removed++;
                }
            }
            int inserted = remove ? 0 : spaces;
            if (removed > 0 || inserted > 0)
                edits.Add(new(cursor, removed, inserted));
            replacement.Append(indentation);
            replacement.Append(text, cursor + removed, lineLength - removed);
            cursor = line.End;
        } while (cursor < blockEnd);

        if (edits.Count == 0) return null;
        int selectionStart = Map(start, edits, includeInsertionAtPosition: false);
        int selectionEnd = Map(start + length, edits, includeInsertionAtPosition: false);
        return new(blockStart, blockEnd - blockStart, replacement.ToString(),
            selectionStart, Math.Max(0, selectionEnd - selectionStart));
    }

    private static bool ValidSelection(string text, int start, int length)
    {
        if (start < 0 || length < 0 || start > text.Length || length > text.Length - start) return false;
        int end = start + length;
        return IsBoundary(text, start) && IsBoundary(text, end);
    }

    private static bool IsBoundary(string text, int offset) =>
        offset <= 0 || offset >= text.Length ||
        !char.IsHighSurrogate(text[offset - 1]) || !char.IsLowSurrogate(text[offset]);

    private static (int Start, int End) LineRange(string text, int location)
    {
        int position = Math.Clamp(location, 0, text.Length);
        int start = position;
        // A caret between CR and LF belongs to the line terminated by that pair.
        if (start > 0 && start < text.Length && text[start] == '\n' && text[start - 1] == '\r')
            start--;
        while (start > 0 && text[start - 1] is not '\r' and not '\n') start--;

        int end = position;
        while (end < text.Length && text[end] is not '\r' and not '\n') end++;
        if (end < text.Length)
        {
            if (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n') end += 2;
            else end++;
        }
        return (start, end);
    }

    private static int Map(int position, IReadOnlyList<PrefixEdit> edits, bool includeInsertionAtPosition)
    {
        int delta = 0;
        foreach (var edit in edits)
        {
            if (position < edit.Location) break;
            if (edit.Removed == 0)
            {
                if (position == edit.Location && !includeInsertionAtPosition) break;
                delta += edit.Inserted;
            }
            else if (position < edit.Location + edit.Removed)
            {
                return edit.Location + delta;
            }
            else
            {
                delta += edit.Inserted - edit.Removed;
            }
        }
        return position + delta;
    }

    private sealed record PrefixEdit(int Location, int Removed, int Inserted);
}
