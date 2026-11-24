namespace Qingyue.Core;

/// <summary>Finds inline code spans so literal backtick content is never parsed as markup.</summary>
public static class MarkdownCodeSpan
{
    /// <summary>
    /// Returns true when a token overlaps a code span without covering that span as a
    /// whole. The whole-span exception lets the existing code alternative render its
    /// content, while tokens inside or crossing only one code boundary stay literal.
    /// </summary>
    public static bool Protects(string text, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        int end = start + length;
        int index = 0;
        while (index < text.Length)
        {
            if (text[index] != '`' || Escaped(text, index))
            {
                index++;
                continue;
            }

            int opening = index;
            while (index < text.Length && text[index] == '`') index++;
            int delimiter = index - opening;
            int probe = index;
            int closing = -1;
            while (probe < text.Length)
            {
                if (text[probe] != '`' || Escaped(text, probe))
                {
                    probe++;
                    continue;
                }

                int runStart = probe;
                while (probe < text.Length && text[probe] == '`') probe++;
                if (probe - runStart == delimiter)
                {
                    closing = probe;
                    break;
                }
            }

            if (closing < 0) continue;
            if (start < closing && end > opening && !(start <= opening && end >= closing))
                return true;
            index = closing;
        }
        return false;
    }

    private static bool Escaped(string text, int index)
    {
        int slashes = 0;
        while (index > 0 && text[--index] == '\\') slashes++;
        return slashes % 2 == 1;
    }
}
