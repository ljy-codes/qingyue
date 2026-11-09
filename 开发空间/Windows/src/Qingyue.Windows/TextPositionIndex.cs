namespace Qingyue.Windows;

/// <summary>
/// Immutable UTF-16 offset index for raw editor text and its LF-normalized source.
/// Construction is O(n); all queries use binary searches and allocate no memory.
/// </summary>
public sealed class TextPositionIndex
{
    private readonly int _rawLength;
    private readonly int _sourceLength;
    private readonly int[] _crlfRawEnds;
    private readonly int[] _crlfSourceEnds;
    private readonly int[] _sourceLineStarts;

    public TextPositionIndex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _rawLength = text.Length;
        var rawEnds = new List<int>();
        var sourceEnds = new List<int>();
        var lineStarts = new List<int> { 0 };
        int source = 0;
        for (int raw = 0; raw < text.Length; raw++)
        {
            source++;
            if (text[raw] == '\r')
            {
                if (raw + 1 < text.Length && text[raw + 1] == '\n')
                {
                    raw++;
                    rawEnds.Add(raw + 1);
                    sourceEnds.Add(source);
                }
                lineStarts.Add(source);
            }
            else if (text[raw] == '\n') lineStarts.Add(source);
        }
        _sourceLength = source;
        _crlfRawEnds = rawEnds.ToArray();
        _crlfSourceEnds = sourceEnds.ToArray();
        _sourceLineStarts = lineStarts.ToArray();
    }

    /// <summary>
    /// Clamp to raw bounds and map to LF source. A position between CR and LF
    /// maps to the same source boundary as the position after the whole pair.
    /// </summary>
    public int ToSource(int rawOffset)
    {
        int raw = Math.Clamp(rawOffset, 0, _rawLength);
        return raw - UpperBound(_crlfRawEnds, raw);
    }

    /// <summary>Clamp to source bounds; the inverse boundary is after a whole CRLF.</summary>
    public int ToEditor(int sourceOffset)
    {
        int source = Math.Clamp(sourceOffset, 0, _sourceLength);
        return source + UpperBound(_crlfSourceEnds, source);
    }

    /// <summary>Return 1-based logical line and UTF-16 column, including the final empty line.</summary>
    public (int Line, int Column) Location(int rawOffset)
    {
        int source = ToSource(rawOffset);
        int line = UpperBound(_sourceLineStarts, source);
        return (line, source - _sourceLineStarts[line - 1] + 1);
    }

    /// <summary>Return the 1-based logical line, independent of visual word wrapping.</summary>
    public int LogicalLineAt(int rawOffset) => UpperBound(_sourceLineStarts, ToSource(rawOffset));

    // Count entries <= value. Also handles empty arrays and repeated boundaries.
    private static int UpperBound(int[] values, int value)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (values[middle] <= value) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
