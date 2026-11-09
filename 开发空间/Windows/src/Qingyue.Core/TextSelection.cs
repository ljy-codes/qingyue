namespace Qingyue.Core;

public readonly record struct TextSelectionRange(int Start, int Length);

/// <summary>Selections use raw UTF-16 editor offsets and never depend on visual wrapping.</summary>
public static class TextSelection
{
    public static TextSelectionRange LogicalLineAt(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        int position = Math.Clamp(offset, 0, text.Length);
        // Both halves of a CRLF belong to the preceding logical line.
        if (position < text.Length && text[position] == '\n' && position > 0 && text[position - 1] == '\r') position--;
        int start = position, end = position;
        while (start > 0 && text[start - 1] is not '\n' and not '\r') start--;
        while (end < text.Length && text[end] is not '\n' and not '\r') end++;
        return new(start, end - start);
    }

    public static TextSelectionRange SegmentAt(string text, int offset)
    {
        var line = LogicalLineAt(text, offset);
        if (line.Length == 0) return line;
        int position = Math.Clamp(offset, line.Start, line.Start + line.Length - 1);
        if (char.IsLowSurrogate(text[position]) && position > line.Start && char.IsHighSurrogate(text[position - 1])) position--;
        int start = position, end = position + 1;
        if (char.IsHighSurrogate(text[position]) && end < text.Length && char.IsLowSurrogate(text[end])) end++;
        if (char.IsPunctuation(text[position]) || char.IsSymbol(text[position])) return new(start, end - start);
        bool whitespace = char.IsWhiteSpace(text[position]);
        bool SameSegment(char value) => whitespace ? char.IsWhiteSpace(value)
            : !char.IsWhiteSpace(value) && !char.IsPunctuation(value) && !char.IsSymbol(value);
        while (start > line.Start && SameSegment(text[start - 1])) start--;
        while (end < line.Start + line.Length && SameSegment(text[end])) end++;
        return new(start, end - start);
    }
}
