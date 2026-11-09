using System.Text.RegularExpressions;

namespace Qingyue.Windows;

/// <summary>A foreground-only UTF-16 range within the original editor line.</summary>
public readonly record struct MarkdownColorToken(int Start, int Length, string ResourceKey);

public static class MarkdownSyntax
{
    // Kept compatible with ReadingModel's lightweight inline grammar. No backtracking,
    // document parsing, font changes, or editor mutations on the keystroke path.
    internal static readonly Regex InlinePattern = Pattern(
        @"\\[\\`*{}\[\]()#+\-.!_>|~]|!\[([^\]\r\n]*)\]\([^)\r\n]*\)|\[([^\]\r\n]*)\]\([^)\r\n]*\)|`([^`\r\n]+)`|\*\*([^*\r\n]+)\*\*|__([^_\r\n]+)__|\*([^*\r\n]+)\*|_([^_\r\n]+)_|~~([^~\r\n]+)~~");
    internal static readonly Regex HeadingPattern = Pattern(@"^ {0,3}(#{1,6})(?:[ \t]+|$)(.*)$");
    internal static readonly Regex ListPattern = Pattern(@"^ {0,3}(?:[-+*]|[0-9]{1,9}[.)])[ \t]+");
    internal static readonly Regex QuotePattern = Pattern(@"^ {0,3}>[ \t]?");
    private static readonly Regex FencePattern = Pattern(@"^ {0,3}(`{3,}|~{3,}).*$");

    private static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// Returns sorted, non-overlapping color ranges, including Markdown delimiters.
    /// This is stateless: fence markers are colored; fenced body state belongs to the caller.
    /// Use ResourceKey with FormattedText.SetForegroundBrush only, keeping TextBox metrics.
    /// </summary>
    public static IEnumerable<MarkdownColorToken> Tokens(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length == 0) yield break;
        if (FencePattern.IsMatch(line))
        {
            yield return new(0, line.Length, "SyntaxStringBrush");
            yield break;
        }

        string? baseKey = null;
        int baseEnd = 0, contentStart = 0;
        var heading = HeadingPattern.Match(line);
        if (heading.Success)
        {
            baseKey = "AccentTextBrush";
            baseEnd = line.Length;
            contentStart = heading.Groups[2].Index;
        }
        else
        {
            var marker = QuotePattern.Match(line);
            if (!marker.Success) marker = ListPattern.Match(line);
            if (marker.Success)
            {
                baseKey = "SyntaxKeyBrush";
                baseEnd = contentStart = marker.Length;
            }
        }

        int cursor = 0;
        foreach (Match match in InlinePattern.Matches(line, contentStart))
        {
            if (match.Value[0] == '\\' || IsIntrawordUnderscore(line, match)) continue;
            if (baseKey is not null && cursor < Math.Min(match.Index, baseEnd))
                yield return new(cursor, Math.Min(match.Index, baseEnd) - cursor, baseKey);
            string key = match.Value[0] == '`' ? "SyntaxStringBrush" :
                match.Value[0] == '[' || match.Value.StartsWith("![", StringComparison.Ordinal) ? "SyntaxKeyBrush" : "SyntaxValueBrush";
            yield return new(match.Index, match.Length, key);
            cursor = match.Index + match.Length;
        }
        if (baseKey is not null && cursor < baseEnd)
            yield return new(cursor, baseEnd - cursor, baseKey);
    }

    internal static bool IsIntrawordUnderscore(string text, Match match) =>
        match.Value[0] == '_' &&
        ((match.Index > 0 && char.IsLetterOrDigit(text[match.Index - 1])) ||
         (match.Index + match.Length < text.Length && char.IsLetterOrDigit(text[match.Index + match.Length])));
}
