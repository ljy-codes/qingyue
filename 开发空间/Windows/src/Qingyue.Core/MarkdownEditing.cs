using System.Text.RegularExpressions;

namespace Qingyue.Core;

public enum MarkdownEditAction
{
    Enter, SoftBreak, Indent, Outdent, Backspace, ToggleTask,
    Bold, Italic, Link, InlineCode, CodeBlock, Heading1, Heading2, Heading3,
    Quote, Unordered, Ordered, Task
}

public sealed record MarkdownEdit(int Start, int Length, string Text, int SelectionStart, int SelectionLength);
public sealed record MarkdownListMarker(int Start, int Length, int Indent, int ContentStart, int Level,
    bool Ordered, int Number, bool Task, bool Checked)
{
    // The list container starts before a task checkbox. Source offsets above
    // stay in UTF-16 characters; this value follows four-column tab stops.
    public int ContentIndent { get; init; }
}

/// <summary>Source-preserving editing operations. Positions always refer to the original UTF-16 text.</summary>
public static class MarkdownEditing
{
    private sealed record Line(int Start, int End, int Next, string Text, bool Code, bool OpensFence);
    private static readonly Regex Marker = new(@"^([ \t]*)([-+*]|[0-9]{1,9}[.)])([ \t]+)(\[[ xX]\][ \t]+)?",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Fence = new(@"^[ \t]*(`{3,}|~{3,})(.*)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex BreaksList = new(@"^ {0,3}(?:#{1,6}(?:[ \t]+|$)|>|(?:[-*_][ \t]*){3,}$|\[[^\]]+\]:)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static IReadOnlyList<MarkdownListMarker> ListMarkers(string text)
    {
        var result = new List<MarkdownListMarker>();
        var depths = new List<int>();
        bool listContext = false;
        bool afterBlank = false;
        foreach (var line in Lines(text))
        {
            int indent = Indentation(line.Text);
            if (string.IsNullOrWhiteSpace(line.Text)) { afterBlank = true; continue; }
            if (line.Code || BreaksList.IsMatch(line.Text))
            {
                if (indent == 0) { listContext = false; depths.Clear(); }
                afterBlank = false;
                continue;
            }
            var match = Marker.Match(line.Text);
            if (!match.Success || indent >= 4 && !listContext)
            {
                // An unseparated plain paragraph can be a lazy list continuation.
                if (afterBlank && indent == 0)
                { listContext = false; depths.Clear(); }
                afterBlank = false;
                continue;
            }
            afterBlank = false;
            while (depths.Count > 0 && depths[^1] > indent) depths.RemoveAt(depths.Count - 1);
            if (depths.Count == 0 || depths[^1] < indent) depths.Add(indent);
            string marker = match.Groups[2].Value;
            bool ordered = char.IsAsciiDigit(marker[0]);
            result.Add(new(line.Start, match.Length, indent, line.Start + match.Length, depths.Count - 1,
                ordered, ordered ? int.Parse(marker[..^1]) : 1,
                match.Groups[4].Success, match.Groups[4].Success && match.Groups[4].Value[1] != ' ')
            {
                ContentIndent = Columns(line.Text.AsSpan(0,
                    match.Groups[4].Success ? match.Groups[4].Index : match.Length))
            });
            listContext = true;
        }
        return result;
    }

    public static MarkdownEdit? Apply(string text, int start, int length, MarkdownEditAction action)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (start < 0 || length < 0 || start > text.Length - length) throw new ArgumentOutOfRangeException(nameof(start));
        // Never split a surrogate pair when a toolbar operates on a programmatic selection.
        if (!SearchEngine.IsBoundary(text, start) || !SearchEngine.IsBoundary(text, start + length)) return null;
        var lines = Lines(text);
        int index = lines.FindLastIndex(l => l.Start <= start);
        var line = lines[Math.Max(index, 0)];
        var markers = ListMarkers(text);
        var owners = Owners(lines, markers, out var indentedCode);
        var marker = markers.LastOrDefault(m => m.Start == line.Start);
        var owner = owners[index];
        bool inCode = line.Code || indentedCode[index];
        if (inCode) return null;
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\r') ? "\r" : "\n";
        switch (action)
        {
            case MarkdownEditAction.Enter:
            case MarkdownEditAction.SoftBreak:
                if (marker is null)
                {
                    // An explicit continuation line retains the nearest item's indentation.
                    if (owner is null || string.IsNullOrWhiteSpace(line.Text) ||
                        Indentation(line.Text) < owner.ContentIndent) return null;
                    string continuation = newline + (action == MarkdownEditAction.SoftBreak
                        ? new string(' ', owner.ContentIndent) : NextPrefix(text, owner));
                    if (action == MarkdownEditAction.SoftBreak) continuation = "  " + continuation;
                    return new(start, length, continuation, start + continuation.Length, 0);
                }
                if (start < marker.ContentStart) return null;
                if (action == MarkdownEditAction.SoftBreak)
                {
                    string insertion = "  " + newline + new string(' ', marker.ContentIndent);
                    return new(start, length, insertion, start + insertion.Length, 0);
                }
                if (length == 0 && string.IsNullOrWhiteSpace(text[marker.ContentStart..line.End]))
                {
                    if (marker.Indent > 0) return ChangeIndent(text, start, length, lines, index, markers, owners, false);
                    return new(line.Start, line.End - line.Start, "", line.Start, 0);
                }
                string next = NextPrefix(text, marker);
                string value = newline + next;
                return new(start, length, value, start + value.Length, 0);
            case MarkdownEditAction.Indent:
            case MarkdownEditAction.Outdent:
                if (marker is null) return null;
                return ChangeIndent(text, start, length, lines, index, markers, owners, action == MarkdownEditAction.Indent);
            case MarkdownEditAction.Backspace:
                if (marker is null || length != 0 || start != marker.ContentStart) return null;
                if (marker.Indent > 0) return ChangeIndent(text, start, length, lines, index, markers, owners, false);
                return new(line.Start, marker.Length, "", line.Start, 0);
            case MarkdownEditAction.ToggleTask:
                if (marker is not { Task: true }) return null;
                int check = line.Start + Marker.Match(line.Text).Groups[4].Index + 1;
                return new(check, 1, marker.Checked ? " " : "x", start, length);
            case MarkdownEditAction.Bold: return Wrap(text, start, length, "**", "**", "加粗文字");
            case MarkdownEditAction.Italic: return Wrap(text, start, length, "*", "*", "斜体文字");
            case MarkdownEditAction.InlineCode: return Wrap(text, start, length, "`", "`", "代码");
            case MarkdownEditAction.Link:
                string label = length == 0 ? "链接文字" : text.Substring(start, length);
                return new(start, length, "[" + label + "](https://)", start + label.Length + 3, 8);
            case MarkdownEditAction.CodeBlock:
                string selected = text.Substring(start, length);
                int longest = 2, run = 0;
                foreach (char c in selected) { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
                string fence = new('`', longest + 1);
                string prefix = (start > line.Start ? newline : "") + fence + newline;
                string suffix = newline + fence + (start + length < text.Length && text[start + length] is not '\r' and not '\n' ? newline : "");
                return new(start, length, prefix + selected + suffix, start + prefix.Length, length);
            default:
                return BlockPrefix(text, start, length, lines, action, indentedCode);
        }
    }

    private static MarkdownEdit Wrap(string text, int start, int length, string before, string after, string placeholder)
    {
        if (start >= before.Length && start + length + after.Length <= text.Length &&
            text.AsSpan(start - before.Length, before.Length).SequenceEqual(before) &&
            text.AsSpan(start + length, after.Length).SequenceEqual(after))
            return new(start - before.Length, length + before.Length + after.Length,
                text.Substring(start, length), start - before.Length, length);
        string value = length == 0 ? placeholder : text.Substring(start, length);
        return new(start, length, before + value + after, start + before.Length, value.Length);
    }

    private static MarkdownEdit? ChangeIndent(string text, int start, int length, List<Line> lines,
        int first, IReadOnlyList<MarkdownListMarker> markers, MarkdownListMarker?[] owners, bool indent)
    {
        var root = markers.Last(m => m.Start == lines[first].Start);
        var parent = markers.LastOrDefault(m => m.Start < root.Start && m.Indent < root.Indent);
        var sibling = markers.LastOrDefault(m => m.Start < root.Start && m.Indent == root.Indent &&
            (parent is null || m.Start > parent.Start));
        if (indent && sibling is null) return null;
        if (indent && lines.Any(l => l.Start > sibling!.Start && l.Start < root.Start &&
            !string.IsNullOrWhiteSpace(l.Text) && Indentation(l.Text) <= root.Indent &&
            !markers.Any(m => m.Start == l.Start))) return null;
        int change = indent
            ? sibling!.ContentIndent - sibling.Indent
            : root.Indent - (parent?.Indent ?? 0);
        change = Math.Max(0, change);
        int last = first;
        int selectionEnd = start + length;
        while (last + 1 < lines.Count && lines[last + 1].Start < selectionEnd) last++;
        // A selected parent owns its nested items and continuation paragraphs.
        int depth = markers.Where(m => m.Start >= lines[first].Start && m.Start <= lines[last].Start)
            .Min(m => m.Indent);
        // Carry every descendant of a selected parent, including unselected
        // siblings of the last selected child and its fenced code/blank lines.
        for (int next = last + 1; next < lines.Count; next++)
        {
            if (string.IsNullOrWhiteSpace(lines[next].Text)) continue;
            if (owners[next] is not { } owner || owner.Start < lines[first].Start || owner.Indent < depth ||
                markers.Any(m => m.Start == lines[next].Start && m.Indent <= depth)) break;
            last = next;
        }
        var edits = new List<(int Start, int Length, string Text)>();
        for (int i = first; i <= last; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i].Text)) continue;
            if (indent && !lines[i].Text.TakeWhile(c => c is ' ' or '\t').Contains('\t'))
                edits.Add((lines[i].Start, 0, new string(' ', change)));
            else if (indent)
            {
                int leading = lines[i].Text.TakeWhile(c => c is ' ' or '\t').Count();
                edits.Add((lines[i].Start, leading, new string(' ', Indentation(lines[i].Text) + change)));
            }
            else
            {
                int removed = 0, columns = 0;
                while (removed < lines[i].Text.Length && columns < change &&
                    lines[i].Text[removed] is ' ' or '\t')
                {
                    columns += lines[i].Text[removed] == '\t' ? 4 - columns % 4 : 1;
                    removed++;
                }
                if (removed > 0) edits.Add((lines[i].Start, removed, new string(' ', Math.Max(0, columns - change))));
            }
        }
        return Transform(text, start, length, edits);
    }

    private static string NextPrefix(string text, MarkdownListMarker marker)
    {
        var match = Marker.Match(text[marker.Start..]);
        return match.Groups[1].Value +
            (marker.Ordered ? Math.Min(999_999_999, marker.Number + 1) + match.Groups[2].Value[^1..] : match.Groups[2].Value) +
            " " + (marker.Task ? "[ ] " : "");
    }

    private static MarkdownListMarker?[] Owners(List<Line> lines, IReadOnlyList<MarkdownListMarker> markers,
        out bool[] indentedCode)
    {
        var result = new MarkdownListMarker?[lines.Count];
        indentedCode = new bool[lines.Count];
        var byStart = markers.ToDictionary(m => m.Start);
        var parents = new List<MarkdownListMarker>();
        bool paragraphOpen = false, wasCode = false;
        MarkdownListMarker? codeOwner = null;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            int indent = Indentation(line.Text);
            if (line.Code)
            {
                if (!wasCode || line.OpensFence)
                    codeOwner = parents.LastOrDefault(m => m.ContentIndent <= indent);
                result[i] = codeOwner;
                if (codeOwner is null) parents.Clear();
                wasCode = true;
                paragraphOpen = false;
                continue;
            }
            wasCode = false;
            if (string.IsNullOrWhiteSpace(line.Text)) { paragraphOpen = false; continue; }
            if (byStart.TryGetValue(line.Start, out var marker))
            {
                while (parents.Count > 0 && parents[^1].Indent >= marker.Indent) parents.RemoveAt(parents.Count - 1);
                parents.Add(marker);
                result[i] = marker;
                paragraphOpen = !string.IsNullOrWhiteSpace(line.Text[(marker.ContentStart - line.Start)..]);
            }
            else
            {
                var owner = parents.LastOrDefault(m => m.ContentIndent <= indent);
                bool table = line.Text.Contains('|') && i + 1 < lines.Count &&
                    lines[i + 1].Text.Contains('-') && lines[i + 1].Text.All(c => c is '|' or '-' or ':' or ' ' or '\t');
                bool startsBlock = BreaksList.IsMatch(line.Text) || table;
                if (owner is null && paragraphOpen && !startsBlock)
                    owner = parents.LastOrDefault();
                // Indented code cannot interrupt an existing paragraph. Conversely,
                // a closed fence/blank line cannot make an external paragraph lazy.
                indentedCode[i] = !paragraphOpen && indent >= (owner?.ContentIndent ?? 0) + 4;
                result[i] = owner;
                if (owner is null) parents.Clear();
                else while (parents.Count > 0 && parents[^1].Start > owner.Start) parents.RemoveAt(parents.Count - 1);
                paragraphOpen = !startsBlock && !indentedCode[i];
            }
        }
        return result;
    }

    private static MarkdownEdit? BlockPrefix(string text, int start, int length, List<Line> lines,
        MarkdownEditAction action, bool[] indentedCode)
    {
        string prefix = action switch
        {
            MarkdownEditAction.Heading1 => "# ", MarkdownEditAction.Heading2 => "## ",
            MarkdownEditAction.Heading3 => "### ", MarkdownEditAction.Quote => "> ",
            MarkdownEditAction.Unordered => "- ", MarkdownEditAction.Ordered => "1. ",
            MarkdownEditAction.Task => "- [ ] ", _ => ""
        };
        if (prefix.Length == 0) return null;
        var edits = new List<(int Start, int Length, string Text)>();
        int number = 1;
        for (int index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Start > start + Math.Max(0, length - 1) || line.End < start) continue;
            if (line.Code || indentedCode[index]) continue;
            if (length > 0 && string.IsNullOrWhiteSpace(line.Text)) continue;
            int leading = line.Text.TakeWhile(c => c is ' ' or '\t').Count();
            string rest = line.Text[leading..];
            int remove = 0;
            if (action is MarkdownEditAction.Heading1 or MarkdownEditAction.Heading2 or MarkdownEditAction.Heading3)
            {
                int hashes = rest.TakeWhile(c => c == '#').Count();
                if (hashes is >= 1 and <= 6 && (hashes == rest.Length || rest[hashes] == ' '))
                    remove = hashes + (hashes < rest.Length ? 1 : 0);
            }
            else if (action == MarkdownEditAction.Quote && rest.StartsWith("> ", StringComparison.Ordinal)) remove = 2;
            else if (action is MarkdownEditAction.Unordered or MarkdownEditAction.Ordered or MarkdownEditAction.Task)
            {
                var match = Marker.Match(line.Text);
                if (match.Success) remove = match.Length - leading;
            }
            string value = action == MarkdownEditAction.Ordered ? number++ + ". " : prefix;
            if (remove > 0 && line.Text.AsSpan(leading, remove).SequenceEqual(value)) value = "";
            edits.Add((line.Start + leading, remove, value));
        }
        return Transform(text, start, length, edits);
    }

    private static MarkdownEdit? Transform(string text, int start, int length,
        List<(int Start, int Length, string Text)> edits)
    {
        if (edits.Count == 0) return null;
        int Map(int position)
        {
            int delta = 0;
            foreach (var edit in edits.OrderBy(e => e.Start))
            {
                if (position < edit.Start) break;
                if (position < edit.Start + edit.Length) return edit.Start + delta + edit.Text.Length;
                delta += edit.Text.Length - edit.Length;
            }
            return position + delta;
        }
        int selectionStart = Map(start), selectionEnd = Map(start + length);
        int begin = edits.Min(e => e.Start), end = edits.Max(e => e.Start + e.Length);
        string replacement = text[begin..end];
        foreach (var edit in edits.OrderByDescending(e => e.Start))
            replacement = replacement.Remove(edit.Start - begin, edit.Length).Insert(edit.Start - begin, edit.Text);
        return new(begin, end - begin, replacement, selectionStart, Math.Max(0, selectionEnd - selectionStart));
    }

    private static int Indentation(string line)
    {
        int end = 0;
        while (end < line.Length && line[end] is ' ' or '\t') end++;
        return Columns(line.AsSpan(0, end));
    }

    private static int Columns(ReadOnlySpan<char> text)
    {
        int columns = 0;
        foreach (char c in text) columns += c == '\t' ? 4 - columns % 4 : 1;
        return columns;
    }

    private static List<Line> Lines(string text)
    {
        var lines = new List<Line>();
        string? fence = null;
        for (int start = 0; start <= text.Length;)
        {
            int end = start;
            while (end < text.Length && text[end] is not '\r' and not '\n') end++;
            int next = end;
            if (next < text.Length && text[next++] == '\r' && next < text.Length && text[next] == '\n') next++;
            string value = text[start..end];
            var match = Fence.Match(value);
            bool code = fence is not null || match.Success;
            bool opensFence = fence is null && match.Success &&
                (match.Groups[1].Value[0] != '`' || !match.Groups[2].Value.Contains('`'));
            if (opensFence)
                fence = match.Groups[1].Value;
            else if (fence is not null && match.Success && match.Groups[1].Value[0] == fence[0] &&
                match.Groups[1].Length >= fence.Length && string.IsNullOrWhiteSpace(match.Groups[2].Value))
                fence = null;
            lines.Add(new(start, end, next, value, code, opensFence));
            if (next == text.Length && end == text.Length) break;
            start = next;
        }
        return lines;
    }
}
