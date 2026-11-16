using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;

namespace Qingyue.Core;

public sealed class ReadingModel
{
    private const int MaximumHeadings = 500;
    private readonly string source;
    private readonly bool identity;
    public string VisibleText { get; }
    public IReadOnlyList<ReadingBlock> Blocks { get; }
    public IReadOnlyList<ReadingHeading> Headings { get; }
    public IReadOnlyDictionary<string, string> ReferenceDefinitions { get; }

    private ReadingModel(string text, bool identity, List<ReadingBlock> blocks, List<ReadingHeading> headings,
        IReadOnlyDictionary<string, string>? referenceDefinitions = null)
    {
        source = text;
        this.identity = identity;
        Blocks = blocks.AsReadOnly();
        Headings = headings.AsReadOnly();
        ReferenceDefinitions = referenceDefinitions ??
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
        VisibleText = identity ? text : string.Join("\n", blocks.Select(block => block.Text));
    }

    public static ReadingModel Build(string text, TextKind kind)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!PreviewLimits.CanRender(text, kind))
            return Raw(text, []);
        if (kind != TextKind.Markdown)
            return Raw(text, ExtractHeadings(text, kind));
        return Markdown(text);
    }

    private static ReadingModel Raw(string text, List<ReadingHeading> headings) =>
        new(text, true, [new(ReadingBlockKind.Paragraph, text, 0, text.Length, 0)], headings);

    // Block-level UTF-16 mapping, not an arbitrary-character source map.
    // Hidden syntax and whitespace are interpolated within their owning block.
    // Both directions clamp, remain monotone and snap to the start of a surrogate pair.
    public int SourceToDisplay(int offset)
    {
        offset = Snap(source, offset);
        if (identity) return offset;
        if (offset == 0 || Blocks.Count == 0) return 0;
        if (offset == source.Length) return VisibleText.Length;
        int index = FindBlock(offset, display: false);
        if (index < 0) return 0;
        var block = Blocks[index];
        int within = Math.Clamp(offset - block.SourceStart, 0, block.SourceLength);
        int mapped = block.DisplayStart + (block.SourceLength == 0 ? 0 :
            (int)((long)within * block.Text.Length / block.SourceLength));
        return Snap(VisibleText, mapped);
    }

    public int DisplayToSource(int offset)
    {
        offset = Snap(VisibleText, offset);
        if (identity) return offset;
        if (offset == 0) return 0;
        if (offset == VisibleText.Length) return source.Length;
        int index = FindBlock(offset, display: true);
        if (index < 0) return 0;
        var block = Blocks[index];
        int within = Math.Clamp(offset - block.DisplayStart, 0, block.Text.Length);
        int mapped = block.SourceStart + (block.Text.Length == 0 ? 0 :
            (int)((long)within * block.SourceLength / block.Text.Length));
        return Snap(source, mapped);
    }

    private int FindBlock(int offset, bool display)
    {
        int low = 0, high = Blocks.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            int start = display ? Blocks[middle].DisplayStart : Blocks[middle].SourceStart;
            if (start <= offset) low = middle + 1;
            else high = middle - 1;
        }
        return high;
    }

    private static int Snap(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        return SearchEngine.IsBoundary(text, offset) ? offset : offset - 1;
    }

    private sealed record Line(string Text, int Start, int End);
    private static List<Line> Lines(string text)
    {
        var lines = new List<Line>();
        int start = 0;
        while (start < text.Length)
        {
            int end = start;
            while (end < text.Length && text[end] is not '\r' and not '\n') end++;
            int next = end;
            if (next < text.Length && text[next++] == '\r' && next < text.Length && text[next] == '\n') next++;
            lines.Add(new(text[start..end], start, next));
            start = next;
        }
        return lines;
    }

    // Non-backtracking internal syntax recognizers have linear worst-case scanning.
    private static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(350));
    private static readonly Regex Atx = Pattern(@"^ {0,3}(#{1,6})(?:[ \t]+|$)(.*)$");
    private static readonly Regex Fence = Pattern(@"^ {0,3}(`{3,}|~{3,})(.*)$");
    private static readonly Regex List = Pattern(@"^(?<indent>[ \t]*)(?:[-+*]|[0-9]{1,9}[.)])[ \t]+(?<body>.*)$");
    private static readonly Regex Quote = Pattern(@"^ {0,3}>[ \t]?(.*)$");
    private static readonly Regex Task = Pattern(@"^\[([ xX])\][ \t]+(.*)$");
    private static readonly Regex TableSeparator = Pattern(@"^:?-+:?$");
    private static readonly Regex Chapter = Pattern(@"^(?:第[零〇一二三四五六七八九十百千万两0-9０-９]+[章节卷部篇回](?:.*)|[序终尾]章(?:.*)|Chapter[ \t]+[0-9]+(?:.*))$");
    private static readonly Regex IniSection = Pattern(@"^\[([^\]\r\n]+)\](?:[ \t]*[;#].*)?$");
    private static readonly Regex InlineToken = Pattern(
        @"\\[\\`*{}\[\]()#+\-.!_>|~]|!\[([^\]\r\n]*)\]\([^)\r\n]*\)|\[([^\]\r\n]*)\]\([^)\r\n]*\)|`([^`\r\n]+)`|\*\*([^*\r\n]+)\*\*|__([^_\r\n]+)__|\*([^*\r\n]+)\*|_([^_\r\n]+)_|~~([^~\r\n]+)~~|<((?:https?://|mailto:)[^<>\s]+)>|\[([^\]\r\n]*)\]\[([^\]\r\n]*)\]");
    private static readonly Regex ReferenceDefinition = Pattern(
        @"^ {0,3}\[([^\]\r\n]+)\]:[ \t]*(?:<([^>\r\n]+)>|(\S+))(?:[ \t]+(?:""([^""\r\n]*)""|'([^'\r\n]*)'|\(([^)\r\n]*)\)))?[ \t]*$");

    private static ReadingModel Markdown(string text)
    {
        var lines = Lines(text);
        var references = CollectReferenceDefinitions(lines);
        var blocks = new List<ReadingBlock>();
        var headings = new List<ReadingHeading>();
        var listParents = new List<(int Indent, int ContentIndent)>();
        bool afterBlank = false;
        int display = 0;
        void Add(ReadingBlockKind kind, string value, int first, int last, int level = 0,
            bool listContinuation = false)
        {
            if (blocks.Count > 0) display++;
            var block = new ReadingBlock(kind, value, lines[first].Start,
                lines[last].End - lines[first].Start, display, level) { ListContinuation = listContinuation };
            blocks.Add(block);
            if (kind == ReadingBlockKind.Heading && headings.Count < MaximumHeadings)
                headings.Add(new(value, block.SourceStart, display, level));
            display += value.Length;
        }

        for (int i = 0; i < lines.Count;)
        {
            string line = lines[i].Text;
            if (string.IsNullOrWhiteSpace(line)) { afterBlank = true; i++; continue; }
            bool separated = afterBlank;
            afterBlank = false;
            int first = i;
            int indent = Indentation(line);
            int owner = listParents.FindLastIndex(parent => indent >= parent.ContentIndent);
            int contentIndent = owner < 0 ? 0 : listParents[owner].ContentIndent;
            var fence = Fence.Match(StripIndent(line, contentIndent));
            if (fence.Success)
            {
                listParents.RemoveRange(owner + 1, listParents.Count - owner - 1);
                string marker = fence.Groups[1].Value;
                var content = new List<string>();
                i++;
                while (i < lines.Count && !ClosesFence(StripIndent(lines[i].Text, contentIndent), marker))
                    content.Add(StripIndent(lines[i++].Text, indent));
                if (i < lines.Count) i++;
                Add(ReadingBlockKind.Code, string.Join("\n", content), first, i - 1,
                    Math.Max(0, owner), listContinuation: owner >= 0);
                var info = fence.Groups[2].Value.AsSpan().Trim();
                int languageEnd = info.IndexOfAny(' ', '\t');
                blocks[^1] = blocks[^1] with
                {
                    CodeLanguage = (languageEnd < 0 ? info : info[..languageEnd]).ToString()
                };
                continue;
            }
            var list = List.Match(line);
            if (list.Success && !IsSeparator(line) && (indent <= 3 || listParents.Count > 0))
            {
                while (listParents.Count > 0 && indent < listParents[^1].Indent)
                    listParents.RemoveAt(listParents.Count - 1);
                if (listParents.Count > 0 && indent == listParents[^1].Indent)
                    listParents.RemoveAt(listParents.Count - 1);
                // Depth follows actual ancestors, so both two- and four-space
                // indentation (and tab stops) add one level, never phantom items.
                listParents.Add((indent, ColumnAt(line, list.Groups["body"].Index)));
                string value = list.Groups["body"].Value;
                var task = Task.Match(value);
                value = task.Success
                    ? (task.Groups[1].Value == " " ? "☐ " : "☑ ") + Inline(task.Groups[2].Value, references)
                    : Inline(value, references);
                Add(ReadingBlockKind.ListItem, value, i, i, listParents.Count - 1);
                i++;
                continue;
            }
            // Indentation selects the nearest owning ancestor, not necessarily
            // the most recent child. A nonblank lazy paragraph may continue it.
            if (owner < 0 && !separated && listParents.Count > 0 && !IsSpecial(line) &&
                !(i + 1 < lines.Count && LooksLikeTable(line, lines[i + 1].Text)))
                owner = listParents.Count - 1;
            if (owner >= 0 && !IsSpecial(line.TrimStart(' ', '\t')))
            {
                listParents.RemoveRange(owner + 1, listParents.Count - owner - 1);
                var continuation = new List<string> { Inline(line.TrimStart(' ', '\t'), references) };
                i++;
                while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i].Text) &&
                    !IsSpecial(lines[i].Text.TrimStart(' ', '\t')))
                {
                    int nextIndent = Indentation(lines[i].Text);
                    int nextOwner = listParents.FindLastIndex(parent => nextIndent >= parent.ContentIndent);
                    if (nextOwner >= 0 && nextOwner != owner) break;
                    if (i + 1 < lines.Count && LooksLikeTable(lines[i].Text, lines[i + 1].Text)) break;
                    continuation.Add(Inline(lines[i++].Text.TrimStart(' ', '\t'), references));
                }
                Add(ReadingBlockKind.Paragraph, string.Join("\n", continuation), first, i - 1,
                    owner, listContinuation: true);
                continue;
            }
            listParents.Clear();
            if (TryReferenceDefinition(line, out _, out _))
            {
                i++;
                continue;
            }
            var atx = Atx.Match(line);
            if (atx.Success)
            {
                var title = atx.Groups[2].Value.TrimEnd();
                int trailing = title.Length;
                while (trailing > 0 && title[trailing - 1] == '#') trailing--;
                if (trailing == 0 || trailing < title.Length && char.IsWhiteSpace(title[trailing - 1]))
                    title = title[..trailing].TrimEnd();
                Add(ReadingBlockKind.Heading, Inline(title, references), i, i, atx.Groups[1].Length);
                i++;
                continue;
            }
            if (i + 1 < lines.Count && SetextLevel(lines[i + 1].Text) is int level && level > 0 &&
                !IsSpecial(line))
            {
                Add(ReadingBlockKind.Heading, Inline(line.Trim(), references), i, i + 1, level);
                i += 2;
                continue;
            }
            if (IsSeparator(line))
            {
                Add(ReadingBlockKind.Separator, "────────", i, i);
                i++;
                continue;
            }
            if (TryTable(lines, i, references, out int last, out string table))
            {
                Add(ReadingBlockKind.Table, table, i, last);
                i = last + 1;
                continue;
            }
            var quote = Quote.Match(line);
            if (quote.Success)
            {
                var content = new List<string>();
                do
                {
                    content.Add(Inline(quote.Groups[1].Value, references));
                    i++;
                    quote = i < lines.Count ? Quote.Match(lines[i].Text) : Match.Empty;
                } while (quote.Success);
                Add(ReadingBlockKind.Quote, string.Join("\n", content), first, i - 1);
                continue;
            }
            var paragraph = new List<string> { Inline(line, references) };
            i++;
            while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i].Text) && !IsSpecial(lines[i].Text))
            {
                if (i + 1 < lines.Count && SetextLevel(lines[i + 1].Text) > 0) break;
                if (i + 1 < lines.Count && LooksLikeTable(lines[i].Text, lines[i + 1].Text)) break;
                paragraph.Add(Inline(lines[i++].Text, references));
            }
            Add(ReadingBlockKind.Paragraph, string.Join("\n", paragraph), first, i - 1);
        }
        return new(text, false, blocks, headings, references);
    }

    private static int Indentation(string line)
    {
        int end = 0;
        while (end < line.Length && line[end] is ' ' or '\t') end++;
        return ColumnAt(line, end);
    }

    private static int ColumnAt(string line, int end)
    {
        int column = 0;
        for (int i = 0; i < end; i++)
            column = line[i] == '\t' ? column + 4 - column % 4 : column + 1;
        return column;
    }

    private static string StripIndent(string line, int columns)
    {
        int i = 0, column = 0;
        while (i < line.Length && column < columns && line[i] is ' ' or '\t')
        {
            column = line[i++] == '\t' ? column + 4 - column % 4 : column + 1;
        }
        return column > columns ? new string(' ', column - columns) + line[i..] : line[i..];
    }

    private static IReadOnlyDictionary<string, string> CollectReferenceDefinitions(List<Line> lines)
    {
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        string? marker = null;
        foreach (var line in lines)
        {
            // This pass has no list tree yet. Conservatively isolate indented
            // fences too, so definitions in a parent item's code cannot escape.
            string content = line.Text.TrimStart(' ', '\t');
            if (marker is not null)
            {
                if (ClosesFence(content, marker)) marker = null;
                continue;
            }
            var fence = Fence.Match(content);
            if (fence.Success)
            {
                marker = fence.Groups[1].Value;
                continue;
            }
            if (TryReferenceDefinition(line.Text, out string key, out string target))
                references[key] = target;
        }
        return new ReadOnlyDictionary<string, string>(references);
    }

    private static bool TryReferenceDefinition(string line, out string key, out string target)
    {
        key = "";
        target = "";
        var match = ReferenceDefinition.Match(line);
        if (!match.Success) return false;
        key = NormalizeReferenceKey(match.Groups[1].Value);
        target = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
        if (key.Length == 0 || target.Length == 0 ||
            !Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto"))
        {
            key = "";
            target = "";
            return false;
        }
        return true;
    }

    private static string NormalizeReferenceKey(string value)
    {
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

    private static bool IsSpecial(string line) =>
        Fence.IsMatch(line) || Atx.IsMatch(line) || List.IsMatch(line) || Quote.IsMatch(line) ||
        IsSeparator(line) || TryReferenceDefinition(line, out _, out _);

    private static bool ClosesFence(string line, string marker)
    {
        int indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        if (indent > 3) return false;
        int end = indent;
        while (end < line.Length && line[end] == marker[0]) end++;
        return end - indent >= marker.Length && string.IsNullOrWhiteSpace(line[end..]);
    }

    private static int SetextLevel(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return 0;
        return trimmed.All(c => c == '=') ? 1 : trimmed.All(c => c == '-') ? 2 : 0;
    }

    private static bool IsSeparator(string line)
    {
        char marker = '\0';
        int count = 0;
        foreach (char value in line)
        {
            if (value is ' ' or '\t') continue;
            if (value is not '-' and not '*' and not '_') return false;
            if (marker != '\0' && marker != value) return false;
            marker = value;
            count++;
        }
        return count >= 3;
    }

    private static string Inline(string text, IReadOnlyDictionary<string, string> references, int depth = 0)
    {
        if (depth >= 8) return text;
        return InlineToken.Replace(text, match =>
        {
            if (match.Value[0] == '\\') return match.Value[1..];
            if (match.Groups[9].Success) return match.Groups[9].Value;
            if (match.Groups[10].Success)
            {
                string referenceKey = match.Groups[11].Value;
                if (referenceKey.Length == 0) referenceKey = match.Groups[10].Value;
                return references.ContainsKey(NormalizeReferenceKey(referenceKey))
                    ? Inline(match.Groups[10].Value, references, depth + 1)
                    : match.Value;
            }
            if (match.Value.StartsWith("![")) return "[图片：" + Inline(match.Groups[1].Value, references, depth + 1) + "]";
            if (match.Value[0] == '[') return Inline(match.Groups[2].Value, references, depth + 1);
            if (match.Value[0] == '`') return match.Groups[3].Value;
            if (match.Value[0] == '_' &&
                ((match.Index > 0 && char.IsLetterOrDigit(text[match.Index - 1])) ||
                 (match.Index + match.Length < text.Length && char.IsLetterOrDigit(text[match.Index + match.Length]))))
                return match.Value;
            for (int group = 4; group <= 8; group++)
                if (match.Groups[group].Success) return Inline(match.Groups[group].Value, references, depth + 1);
            return match.Value;
        });
    }

    private static List<string>? Cells(string line)
    {
        var value = line.Trim();
        if (!value.Contains('|')) return null;
        int start = value.StartsWith('|') ? 1 : 0;
        int end = value.Length;
        if (end > start && value[end - 1] == '|')
        {
            int slashes = 0;
            for (int j = end - 2; j >= start && value[j] == '\\'; j--) slashes++;
            if (slashes % 2 == 0) end--;
        }
        var cells = new List<string>();
        int cellStart = start, ticks = 0;
        for (int i = start; i < end; i++)
        {
            if (value[i] == '\\') { i++; continue; }
            if (value[i] == '`')
            {
                int run = 1;
                while (i + run < end && value[i + run] == '`') run++;
                ticks = ticks == run ? 0 : ticks == 0 ? run : ticks;
                i += run - 1;
            }
            else if (value[i] == '|' && ticks == 0)
            {
                cells.Add(value[cellStart..i].Trim());
                if (cells.Count >= 32) return null;
                cellStart = i + 1;
            }
        }
        cells.Add(value[cellStart..end].Trim());
        return cells;
    }

    private static bool LooksLikeTable(string first, string second)
    {
        var header = Cells(first);
        var separator = Cells(second);
        return header is not null && separator is not null && header.Count == separator.Count &&
            separator.All(cell => TableSeparator.IsMatch(cell));
    }

    private static bool TryTable(List<Line> lines, int first,
        IReadOnlyDictionary<string, string> references, out int last, out string text)
    {
        last = first;
        text = "";
        if (first + 1 >= lines.Count || !LooksLikeTable(lines[first].Text, lines[first + 1].Text)) return false;
        var header = Cells(lines[first].Text)!;
        var rows = new List<List<string>> { header };
        int next = first + 2;
        while (next < lines.Count && !string.IsNullOrWhiteSpace(lines[next].Text))
        {
            if (IsSpecial(lines[next].Text)) break;
            var row = Cells(lines[next].Text);
            if (row is null || row.Count != header.Count) break;
            if ((rows.Count + 1) * header.Count > 1024) return false;
            rows.Add(row);
            next++;
        }
        last = next - 1;
        text = string.Join("\n", rows.Select(row => string.Join("\t", row.Select(cell => Inline(cell, references)))));
        return true;
    }

    private static List<ReadingHeading> ExtractHeadings(string text, TextKind kind)
    {
        var headings = new List<ReadingHeading>();
        if (kind is TextKind.Json or TextKind.Jsonc)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
                {
                    AllowTrailingCommas = kind == TextKind.Jsonc,
                    CommentHandling = kind == TextKind.Jsonc ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
                    MaxDepth = 256
                });
                int previousByte = 0, sourceOffset = 0;
                while (headings.Count < MaximumHeadings && reader.Read())
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    int tokenByte = checked((int)reader.TokenStartIndex);
                    sourceOffset += Encoding.UTF8.GetCharCount(bytes.AsSpan(previousByte, tokenByte - previousByte));
                    previousByte = tokenByte;
                    headings.Add(new(reader.GetString() ?? "", sourceOffset, sourceOffset, Math.Max(1, reader.CurrentDepth)));
                }
            }
            catch (JsonException) { /* Invalid edited documents still display verbatim. */ }
            return headings;
        }
        foreach (var line in Lines(text))
        {
            if (headings.Count == MaximumHeadings) break;
            var trimmed = line.Text.Trim();
            if (kind == TextKind.Plain && Chapter.IsMatch(trimmed))
                headings.Add(new(trimmed, line.Start, line.Start));
            else if (kind == TextKind.Ini)
            {
                var section = IniSection.Match(trimmed);
                if (section.Success) headings.Add(new(section.Groups[1].Value, line.Start, line.Start));
            }
            else if (kind == TextKind.Yaml && line.Text.Length > 0 && !char.IsWhiteSpace(line.Text[0]) &&
                line.Text[0] is not '#' and not '-' and not '{' and not '[')
            {
                int colon = YamlKeyEnd(line.Text);
                if (colon > 0)
                {
                    string key = line.Text[..colon].Trim();
                    if (key.Length > 1 && (key[0] == '"' && key[^1] == '"' || key[0] == '\'' && key[^1] == '\''))
                        key = key[1..^1];
                    headings.Add(new(key, line.Start, line.Start));
                }
            }
        }
        return headings;
    }

    private static int YamlKeyEnd(string text)
    {
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char value = text[i];
            if (value == '\\' && quote == '"') { i++; continue; }
            if (value is '\'' or '"')
            {
                if (quote == value) quote = '\0';
                else if (quote == '\0') quote = value;
            }
            if (quote == '\0' && value == ':' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]))) return i;
        }
        return -1;
    }
}
