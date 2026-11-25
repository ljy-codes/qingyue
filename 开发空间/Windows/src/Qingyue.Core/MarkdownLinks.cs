using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace Qingyue.Core;

public sealed record MarkdownLinkReference(string Label, string Destination, string? Title);

public readonly record struct MarkdownLinkOccurrence(int Start, int Length, string Label,
    string Destination, string? Title, bool IsImage, bool IsAutomatic);

public readonly record struct MarkdownSourceRange(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// Safe GFM link scanning shared by the reading model and WPF renderer. It follows
/// the macOS MarkdownLinks contract: code and definitions do not create links,
/// reference labels are case/whitespace folded, and only HTTP(S), mailto, relative
/// paths and fragments can be emitted. Navigation callers must additionally require
/// an explicitly supported URI scheme.
/// </summary>
public static class MarkdownLinks
{
    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    private static readonly Regex DefinitionPattern = Pattern(
        @"^[ \t]{0,3}\[([^\]\r\n]+)\]:[ \t]*(?:<([^>\r\n]+)>|([^ \t\r\n]+))(?:[ \t]+(?:[""']([^""']+)[""']|\(([^)]+)\)))?[ \t]*(?:\r?\n|$)");
    private static readonly Regex DefinitionLinePattern = Pattern(
        @"^[ \t]{0,3}\[[^\]\r\n]+\]:[^\r\n]*(?:\r?\n|$)");
    private static readonly Regex InlinePattern = Pattern(
        @"!?\[([^\]\r\n]+)\]\(\s*(?:<([^>\r\n]+)>|([^\s)]+))(?:\s+(?:[""']([^""']+)[""']|\(([^)]+)\)))?\s*\)");
    private static readonly Regex ReferencePattern = Pattern(
        @"!?\[([^\]\r\n]+)\](?:\[([^\]\r\n]*)\])");
    private static readonly Regex ShortcutPattern = Pattern(
        @"!?\[([^\]\r\n]+)\](?![\[(])");
    private static readonly Regex AutomaticPattern = Pattern(
        @"(?<![\w])(?:<((?:https?://|mailto:)[^>\s]+)>|((?:https?://|mailto:)[^\s<>()]+)|((?:www\.)[\w.-]+\.[A-Za-z]{2,}(?:/[^\s<>()]*)?))",
        RegexOptions.IgnoreCase);

    private static Regex Pattern(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, RegexOptions.CultureInvariant | options | RegexOptions.Multiline,
            TimeSpan.FromMilliseconds(350));

    public static IReadOnlyDictionary<string, MarkdownLinkReference> References(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var references = new Dictionary<string, MarkdownLinkReference>(StringComparer.Ordinal);
        foreach (var definition in ReferenceDefinitions(text))
            references.TryAdd(NormalizeReferenceLabel(definition.Reference.Label), definition.Reference);
        return new ReadOnlyDictionary<string, MarkdownLinkReference>(references);
    }

    public static IReadOnlyList<MarkdownLinkOccurrence> Occurrences(string text,
        IReadOnlyDictionary<string, MarkdownLinkReference>? suppliedReferences = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var references = suppliedReferences ?? References(text);
        var ignored = IgnoredRanges(text);
        var result = new List<MarkdownLinkOccurrence>();

        foreach (Match match in InlinePattern.Matches(text))
        {
            if (Ignored(match.Index, match.Length) || IsEscaped(text, match.Index)) continue;
            string rawDestination = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            if (!TryNormalizeDestination(rawDestination, out string destination)) continue;
            string? title = Title(match, text, 4, 5);
            Add(match.Index, match.Length, match.Groups[1].Value, destination, title,
                match.Value.StartsWith("![", StringComparison.Ordinal), automatic: false);
        }

        foreach (Match match in ReferencePattern.Matches(text))
        {
            if (Ignored(match.Index, match.Length) || IsEscaped(text, match.Index) ||
                Overlaps(result, match.Index, match.Length)) continue;
            string label = match.Groups[1].Value;
            string key = match.Groups[2].Length == 0 ? label : match.Groups[2].Value;
            if (!references.TryGetValue(NormalizeReferenceLabel(key), out var reference)) continue;
            Add(match.Index, match.Length, label, reference.Destination, reference.Title,
                match.Value.StartsWith("![", StringComparison.Ordinal), automatic: false);
        }

        foreach (Match match in ShortcutPattern.Matches(text))
        {
            if (Ignored(match.Index, match.Length) || IsEscaped(text, match.Index) ||
                Overlaps(result, match.Index, match.Length)) continue;
            string label = match.Groups[1].Value;
            if (!references.TryGetValue(NormalizeReferenceLabel(label), out var reference)) continue;
            Add(match.Index, match.Length, label, reference.Destination, reference.Title,
                match.Value.StartsWith("![", StringComparison.Ordinal), automatic: false);
        }

        foreach (Match match in AutomaticPattern.Matches(text))
        {
            if (Ignored(match.Index, match.Length) || Overlaps(result, match.Index, match.Length)) continue;
            bool angle = match.Groups[1].Success;
            string raw = angle ? match.Groups[1].Value :
                match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            int start = match.Index;
            int length = match.Length;
            if (!angle)
            {
                raw = raw.TrimEnd('.', ',', ';', ':', '!', '?');
                length = raw.Length;
            }
            if (raw.Length == 0) continue;
            string destinationValue = raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? "https://" + raw : raw;
            if (!TryNormalizeDestination(destinationValue, out string destination)) continue;
            Add(start, length, raw, destination, null, isImage: false, automatic: true);
        }

        result.Sort((left, right) => left.Start.CompareTo(right.Start));
        return result;

        void Add(int start, int length, string label, string destination, string? title,
            bool isImage, bool automatic)
        {
            if (length <= 0 || start < 0 || (long)start + length > text.Length) return;
            result.Add(new(start, length, label, destination, title, isImage, automatic));
        }

        bool Ignored(int start, int length) =>
            ignored.Any(range => Intersects(range.Start, range.Length, start, length));
    }

    public static IReadOnlyList<MarkdownLinkOccurrence> Occurrences(string text,
        IReadOnlyDictionary<string, string> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        var projected = new Dictionary<string, MarkdownLinkReference>(references.Count, StringComparer.Ordinal);
        foreach (var (label, destination) in references)
            projected[label] = new(label, destination, null);
        return Occurrences(text, projected);
    }

    public static IReadOnlyList<MarkdownSourceRange> CodeRanges(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var blocks = new List<MarkdownSourceRange>();
        int lineStart = 0;
        char fenceMarker = '\0';
        int fenceLength = 0;
        int fenceStart = 0;
        while (lineStart < text.Length)
        {
            int lineEnd = lineStart;
            while (lineEnd < text.Length && text[lineEnd] is not '\r' and not '\n') lineEnd++;
            int next = lineEnd;
            if (next < text.Length && text[next] == '\r') next++;
            if (next < text.Length && text[next] == '\n') next++;

            int indent = 0;
            while (lineStart + indent < lineEnd && lineStart + indent - lineStart < 4 &&
                text[lineStart + indent] == ' ') indent++;
            int markerStart = lineStart + indent;
            if (indent <= 3 && markerStart < lineEnd && text[markerStart] is '`' or '~')
            {
                int markerEnd = markerStart;
                char marker = text[markerStart];
                while (markerEnd < lineEnd && text[markerEnd] == marker) markerEnd++;
                int count = markerEnd - markerStart;
                if (count >= 3)
                {
                    if (fenceMarker == '\0')
                    {
                        fenceMarker = marker;
                        fenceLength = count;
                        fenceStart = lineStart;
                    }
                    else if (marker == fenceMarker && count >= fenceLength &&
                        text.AsSpan(markerEnd, lineEnd - markerEnd).Trim().IsEmpty)
                    {
                        blocks.Add(new(fenceStart, next - fenceStart));
                        fenceMarker = '\0';
                    }
                    lineStart = next;
                    continue;
                }
            }

            if (fenceMarker != '\0')
            {
                lineStart = next;
                continue;
            }
            if (indent >= 4 || lineStart < lineEnd && text[lineStart] == '\t')
                blocks.Add(new(lineStart, next - lineStart));
            lineStart = next;
        }
        if (fenceMarker != '\0') blocks.Add(new(fenceStart, text.Length - fenceStart));

        blocks.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = Merge(blocks);
        int blockIndex = 0;
        int index = 0;
        while (index < text.Length)
        {
            while (blockIndex < merged.Count && merged[blockIndex].End <= index) blockIndex++;
            if (blockIndex < merged.Count && merged[blockIndex].Start <= index)
            {
                index = merged[blockIndex].End;
                continue;
            }
            if (text[index] != '`' || IsEscaped(text, index))
            {
                index++;
                continue;
            }
            int opening = index;
            while (index < text.Length && text[index] == '`') index++;
            int count = index - opening;
            int probe = index;
            int closing = -1;
            while (probe < text.Length)
            {
                while (blockIndex < merged.Count && merged[blockIndex].End <= probe) blockIndex++;
                if (blockIndex < merged.Count && merged[blockIndex].Start <= probe)
                {
                    probe = merged[blockIndex].End;
                    continue;
                }
                if (text[probe] != '`' || IsEscaped(text, probe))
                {
                    probe++;
                    continue;
                }
                int runStart = probe;
                while (probe < text.Length && text[probe] == '`') probe++;
                if (probe - runStart == count)
                {
                    closing = probe;
                    break;
                }
            }
            if (closing < 0) continue;
            blocks.Add(new(opening, closing - opening));
            index = closing;
        }

        blocks.Sort((left, right) => left.Start.CompareTo(right.Start));
        return Merge(blocks).AsReadOnly();
    }

    public static string NormalizeReferenceLabel(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        bool whitespace = false;
        foreach (char character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                whitespace = true;
                continue;
            }
            if (whitespace && builder.Length > 0) builder.Append(' ');
            whitespace = false;
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    public static bool TryNormalizeDestination(string value, out string destination)
    {
        destination = value.Trim();
        if (destination.Length == 0) return false;
        if (Uri.TryCreate(destination, UriKind.Absolute, out var uri))
        {
            if (!AllowedSchemes.Contains(uri.Scheme))
            {
                destination = "";
                return false;
            }
            return true;
        }
        if (destination.StartsWith('/') || destination.StartsWith('.') ||
            !destination.Contains(':')) return true;
        destination = "";
        return false;
    }

    public static bool TryCreateNavigationUri(string destination, out Uri? uri) =>
        Uri.TryCreate(destination, UriKind.Absolute, out uri) && uri is not null &&
        AllowedSchemes.Contains(uri.Scheme);

    private static IReadOnlyList<(MarkdownSourceRange Range, MarkdownLinkReference Reference)>
        ReferenceDefinitions(string text)
    {
        var ignored = CodeRanges(text);
        var result = new List<(MarkdownSourceRange, MarkdownLinkReference)>();
        foreach (Match match in DefinitionPattern.Matches(text))
        {
            if (ignored.Any(range => Intersects(range.Start, range.Length, match.Index, match.Length))) continue;
            string label = match.Groups[1].Value.Trim();
            string rawDestination = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            if (!TryNormalizeDestination(rawDestination, out string destination)) continue;
            string? title = Title(match, text, 4, 5);
            result.Add((new(match.Index, match.Length), new(label, destination, title)));
        }
        return result;
    }

    private static List<MarkdownSourceRange> IgnoredRanges(string text)
    {
        var ranges = CodeRanges(text).ToList();
        foreach (Match match in DefinitionLinePattern.Matches(text))
        {
            if (!ranges.Any(range => Intersects(range.Start, range.Length, match.Index, match.Length)))
                ranges.Add(new(match.Index, match.Length));
        }
        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        return Merge(ranges);
    }

    private static List<MarkdownSourceRange> Merge(List<MarkdownSourceRange> ranges)
    {
        var result = new List<MarkdownSourceRange>();
        foreach (var range in ranges)
        {
            if (range.Length <= 0) continue;
            if (result.Count == 0 || range.Start > result[^1].End)
            {
                result.Add(range);
                continue;
            }
            var previous = result[^1];
            int end = Math.Max(previous.End, range.End);
            result[^1] = new(previous.Start, end - previous.Start);
        }
        return result;
    }

    private static string? Title(Match match, string text, int quotedGroup, int parenthesizedGroup)
    {
        Group group = match.Groups[quotedGroup].Success ? match.Groups[quotedGroup] : match.Groups[parenthesizedGroup];
        return group.Success ? text[group.Index..(group.Index + group.Length)] : null;
    }

    private static bool Overlaps(List<MarkdownLinkOccurrence> occurrences, int start, int length) =>
        occurrences.Any(item => Intersects(item.Start, item.Length, start, length));

    private static bool Intersects(int leftStart, int leftLength, int rightStart, int rightLength) =>
        leftStart < (long)rightStart + rightLength && rightStart < (long)leftStart + leftLength;

    private static bool IsEscaped(string text, int index)
    {
        int slashes = 0;
        while (index > 0 && text[--index] == '\\') slashes++;
        return slashes % 2 == 1;
    }
}
