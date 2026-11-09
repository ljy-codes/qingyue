using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Qingyue.Core;

internal static class FormattingServices
{
    private const int MaxCharacters = 50_000_000;
    private const int MaxDepth = 256;

    internal static string Format(string text, TextKind kind)
    {
        CheckInput(text);
        _ = LanguageCatalog.Get(kind);
        return kind switch
        {
            TextKind.Plain => FormatPlain(text),
            TextKind.Markdown => FormatMarkdown(text),
            TextKind.Json or TextKind.Jsonc => FormatJson(text, kind),
            TextKind.Xml => FormatXml(text),
            _ => throw new InvalidOperationException(
                $"{LanguageCatalog.DisplayName(kind)} 未提供完整格式化，请使用“整理空白”；该操作不进行语法校验。")
        };
    }

    internal static string NormalizeWhitespace(string text)
    {
        CheckInput(text);
        return string.Join('\n', Lf(text).Split('\n').Select(line => line.TrimEnd(' ', '\t')));
    }

    private static string FormatPlain(string text)
    {
        var output = new Output();
        bool previousBlank = false, first = true;
        foreach (string source in Lf(text).Split('\n'))
        {
            var line = source.AsSpan().TrimEnd(" \t");
            bool blank = line.IsEmpty;
            if (blank && previousBlank) continue;
            if (!first) output.Append("\n");
            first = false;
            previousBlank = blank;
            int position = 0, column = 0;
            while (position < line.Length && line[position] is ' ' or '\t')
            {
                column += line[position] == '\t' ? 4 - column % 4 : 1;
                position++;
            }
            output.Spaces(column);
            output.Append(line[position..]);
        }
        return output.ToString();
    }

    private static string FormatMarkdown(string text)
    {
        text = Lf(text);
        var lines = text.Split('\n');
        var protect = new bool[lines.Length];

        // Raw HTML can contain Markdown-looking literal text or block-sensitive blank
        // lines. With no HTML/Markdown parser, leave such documents conservatively intact.
        if (lines.Any(line => line.AsSpan().TrimStart().StartsWith("<"))) return text;

        if (lines[0].TrimStart('\uFEFF').Trim() == "---")
        {
            int end = 1;
            while (end < lines.Length && lines[end].Trim() is not ("---" or "...")) end++;
            Array.Fill(protect, true, 0, Math.Min(end + 1, lines.Length));
        }

        char fence = '\0';
        int fenceLength = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (protect[i]) continue;
            if (fence != '\0')
            {
                protect[i] = true;
                if (TryFence(lines[i], out var marker, out var length, out var closing) &&
                    marker == fence && length >= fenceLength && closing) fence = '\0';
                continue;
            }
            if (TryFence(lines[i], out fence, out fenceLength, out _))
            {
                protect[i] = true;
                continue;
            }
            var value = lines[i].AsSpan().TrimStart();
            int indentation = lines[i].Length - value.Length;
            if (indentation >= 4 || lines[i].AsSpan(0, indentation).Contains('\t') ||
                value.StartsWith(">") || lines[i].Contains('|') ||
                lines[i].Contains("](") || lines[i].Contains("]:"))
                protect[i] = true;
            if (!value.IsEmpty && (All(value.TrimEnd(), '=') || All(value.TrimEnd(), '-')))
            {
                protect[i] = true;
                if (i > 0) protect[i - 1] = true;
            }
        }

        // Padding after a list marker establishes the indentation of continuation
        // blocks. Only normalize independent top-level items without continuations.
        for (int i = 0; i < lines.Length; i++)
        {
            if (!TryList(lines[i], out _, out _)) continue;
            int next = i + 1;
            while (next < lines.Length && string.IsNullOrWhiteSpace(lines[next])) next++;
            if (next < lines.Length && lines[next].Length > 0 && char.IsWhiteSpace(lines[next][0]))
                protect[i] = true;
        }

        var output = new List<string>();
        for (int i = 0; i < lines.Length;)
        {
            if (lines[i].Length == 0)
            {
                int end = i + 1;
                while (end < lines.Length && lines[end].Length == 0) end++;
                bool safe = i > 0 && end < lines.Length &&
                    !protect[i - 1] && !protect[end] &&
                    !TryList(lines[i - 1], out _, out _) &&
                    !protect.AsSpan(i, end - i).Contains(true);
                int count = safe ? 1 : end - i;
                for (int j = 0; j < count; j++) output.Add("");
                i = end;
                continue;
            }
            string line = lines[i];
            if (!protect[i])
            {
                int indent = 0;
                while (indent < line.Length && line[indent] == ' ') indent++;
                int end = indent;
                while (end < line.Length && line[end] == '#') end++;
                if (indent <= 3 && end - indent is >= 1 and <= 6 &&
                    end < line.Length && line[end] is ' ' or '\t')
                {
                    int content = end;
                    while (content < line.Length && line[content] is ' ' or '\t') content++;
                    if (content < line.Length) line = line[..end] + " " + line[content..];
                }
                else if (TryList(line, out int markerEnd, out int contentStart))
                    line = line[..markerEnd] + " " + line[contentStart..];
            }
            output.Add(line);
            i++;
        }
        return string.Join('\n', output);
    }

    private static bool TryFence(string line, out char marker, out int length, out bool closing)
    {
        marker = '\0';
        length = 0;
        closing = false;
        int position = 0;
        // Includes fences inside list/quote containers, whose literal body must also survive.
        while (position < line.Length && line[position] is ' ' or '\t' or '>' or '-' or '+' or '*' or '.' or ')' or >= '0' and <= '9')
            position++;
        if (position == line.Length || line[position] is not ('`' or '~')) return false;
        char candidate = line[position];
        int start = position;
        while (position < line.Length && line[position] == candidate) position++;
        length = position - start;
        if (length < 3) { length = 0; return false; }
        marker = candidate;
        closing = line.AsSpan(position).Trim().IsEmpty;
        return true;
    }

    private static bool TryList(string line, out int markerEnd, out int contentStart)
    {
        markerEnd = contentStart = 0;
        if (line.Length < 3) return false;
        if (line[0] is '-' or '+' or '*') markerEnd = 1;
        else
        {
            while (markerEnd < line.Length && line[markerEnd] is >= '0' and <= '9') markerEnd++;
            if (markerEnd is < 1 or > 9 || markerEnd >= line.Length || line[markerEnd] is not ('.' or ')')) return false;
            markerEnd++;
        }
        contentStart = markerEnd;
        while (contentStart < line.Length && line[contentStart] is ' ' or '\t') contentStart++;
        if (contentStart == markerEnd || contentStart == line.Length ||
            contentStart - markerEnd > 4 || line.AsSpan(markerEnd, contentStart - markerEnd).Contains('\t')) return false;
        if (line[0] is '-' or '*')
        {
            int markers = 0;
            bool separator = true;
            foreach (char character in line)
                if (character == line[0]) markers++;
                else if (character is not (' ' or '\t')) separator = false;
            if (separator && markers >= 3) return false;
        }
        return true;
    }

    private static bool All(ReadOnlySpan<char> value, char marker)
    {
        foreach (char character in value)
            if (character != marker) return false;
        return true;
    }

    private static string FormatJson(string text, TextKind kind)
    {
        text = Lf(text);
        try
        {
            // Validate without dictionaries or numeric conversions. Emit original tokens below,
            // so even escaped/lone surrogate strings and duplicate keys retain their spelling.
            using (JsonDocument.Parse(text, TextServices.JsonOptions(kind))) { }
            var output = new Output();
            int depth = 0;
            bool lineStart = true;
            char previous = '\0';

            void NewLine()
            {
                if (!lineStart) output.Append("\n");
                lineStart = true;
            }
            void Write(ReadOnlySpan<char> token)
            {
                if (lineStart) output.Spaces(depth * 2);
                output.Append(token);
                lineStart = false;
            }

            foreach (var (start, length, tokenKind) in Tokens(text))
            {
                var token = text.AsSpan(start, length);
                switch (tokenKind)
                {
                    case '{':
                    case '[':
                        if (previous is '{' or '[') NewLine();
                        Write(token);
                        depth++;
                        break;
                    case '}':
                    case ']':
                        depth--;
                        if (previous is not ('{' or '[')) NewLine();
                        Write(token);
                        break;
                    case ',':
                        Write(token);
                        NewLine();
                        break;
                    case ':':
                        Write(": ");
                        break;
                    case '/':
                        // Moving comments to their own lines never lets // swallow a comma,
                        // colon or closing bracket. Comment contents and order remain intact.
                        NewLine();
                        Write(token);
                        NewLine();
                        break;
                    default:
                        if (previous is '{' or '[') NewLine();
                        Write(token);
                        break;
                }
                previous = tokenKind;
            }
            return output.ToString();
        }
        catch (JsonException error)
        {
            string label = kind == TextKind.Jsonc ? "JSONC" : "JSON";
            throw new FormatException($"{label} 格式化失败：请先修复语法。{error.Message}", error);
        }
    }

    private static IEnumerable<(int Start, int Length, char Kind)> Tokens(string text)
    {
        for (int i = 0; i < text.Length;)
        {
            if (text[i] is ' ' or '\t' or '\n' or '\r') { i++; continue; }
            int start = i;
            char kind = text[i++];
            if (kind == '"')
            {
                while (i < text.Length)
                {
                    char character = text[i++];
                    if (character == '\\') i++;
                    else if (character == '"') break;
                }
            }
            else if (kind == '/')
            {
                if (text[i++] == '/')
                    while (i < text.Length && text[i] != '\n') i++;
                else
                {
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    i += 2;
                }
            }
            else if (kind is not ('{' or '}' or '[' or ']' or ',' or ':'))
            {
                while (i < text.Length && text[i] is not (' ' or '\t' or '\n' or '\r' or '{' or '}' or '[' or ']' or ',' or ':' or '/'))
                    i++;
            }
            yield return (start, i - start, kind);
        }
    }

    private static string FormatXml(string text)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(Lf(text)), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxCharacters
            });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            long addedWhitespace = 0;
            if (document.Root is { } root) Indent(root, 0);

            var output = new Output();
            if (document.Declaration is { } declaration)
            {
                output.Append(declaration.ToString());
                if (document.FirstNode is not XText) output.Append("\n");
            }
            using (var writer = XmlWriter.Create(new OutputWriter(output), new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Indent = false,
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Entitize
            }))
                document.WriteTo(writer);
            return output.ToString();

            void Indent(XElement element, int depth)
            {
                if (depth >= MaxDepth) throw new FormatException("XML 格式化失败：嵌套超过 256 层。");
                if ((string?)element.Attribute(XNamespace.Xml + "space") == "preserve") return;
                var nodes = element.Nodes().ToArray();
                // Whitespace adjacent to inline children may itself be text. Only treat
                // newline-bearing whitespace as existing indentation; preserve other text.
                if (nodes.Any(node => node is XText t &&
                    (t is XCData || !string.IsNullOrWhiteSpace(t.Value) || !t.Value.Contains('\n')))) return;
                var children = nodes.Where(node => node is not XText).ToArray();
                if (children.Length == 0) return;
                foreach (var child in children.OfType<XElement>()) Indent(child, depth + 1);
                foreach (var whitespace in nodes.OfType<XText>()) whitespace.Remove();
                foreach (var child in children)
                    child.AddBeforeSelf(new XText(Indentation(depth + 1)));
                element.Add(new XText(Indentation(depth)));
            }

            string Indentation(int depth)
            {
                addedWhitespace += 1 + depth * 2;
                if (text.Length + addedWhitespace > MaxCharacters)
                    throw new FormatException("XML 格式化输出超过 5000 万字符上限，请缩小内容。");
                return "\n" + new string(' ', depth * 2);
            }
        }
        catch (XmlException error)
        {
            throw new FormatException($"XML 格式化失败：第 {error.LineNumber} 行，第 {error.LinePosition} 列。{error.Message}", error);
        }
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static void CheckInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxCharacters)
            throw new FormatException("格式化输入超过 5000 万字符上限，请缩小内容。");
    }

    private sealed class Output
    {
        private readonly StringBuilder builder = new();
        public void Append(ReadOnlySpan<char> value)
        {
            Ensure(value.Length);
            builder.Append(value);
        }
        public void Spaces(int count)
        {
            Ensure(count);
            builder.Append(' ', count);
        }
        private void Ensure(int count)
        {
            if (count > MaxCharacters - builder.Length)
                throw new FormatException("格式化输出超过 5000 万字符上限，请缩小内容。");
        }
        public override string ToString() => builder.ToString();
    }

    private sealed class OutputWriter(Output output) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            Span<char> buffer = stackalloc char[1];
            buffer[0] = value;
            output.Append(buffer);
        }
        public override void Write(char[] buffer, int index, int count) => output.Append(buffer.AsSpan(index, count));
        public override void Write(string? value) => output.Append(value.AsSpan());
        public override void Write(ReadOnlySpan<char> value) => output.Append(value);
    }
}
