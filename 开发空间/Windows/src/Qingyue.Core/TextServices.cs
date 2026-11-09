using System.Text.Json;
using System.Xml;

namespace Qingyue.Core;

public static class TextServices
{
    public static TextKind DetectKind(string? path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (LanguageCatalog.TryDetectPath(path, out var kind)) return kind;
        return DetectContent(text, Path.GetExtension(path ?? "").Equals(".config", StringComparison.OrdinalIgnoreCase));
    }

    private static TextKind DetectContent(string text, bool configuration)
    {
        var prefix = text.AsSpan(0, Math.Min(text.Length, 4096)).Trim();
        if (!prefix.IsEmpty && prefix[0] == '\uFEFF') prefix = prefix[1..].TrimStart();
        if (prefix.StartsWith("#!"))
        {
            var end = prefix.IndexOfAny('\r', '\n');
            var interpreter = prefix[..(end < 0 ? prefix.Length : end)].ToString();
            if (interpreter.Contains("python", StringComparison.OrdinalIgnoreCase)) return TextKind.Python;
            if (interpreter.Contains("pwsh", StringComparison.OrdinalIgnoreCase)) return TextKind.PowerShell;
            if (interpreter.Contains("node", StringComparison.OrdinalIgnoreCase)) return TextKind.JavaScript;
            if (interpreter.Contains("sh", StringComparison.OrdinalIgnoreCase)) return TextKind.Shell;
        }
        if (prefix.StartsWith("<")) return TextKind.Xml;
        if (prefix.StartsWith("{")) return TextKind.Json;
        if (prefix.StartsWith("["))
        {
            int close = prefix.IndexOf(']');
            // Prefer a valid JSON array; otherwise a bracket-only first line is an INI section.
            if (Diagnostic(prefix.ToString(), TextKind.Json) is null) return TextKind.Json;
            if (close > 1 && (close + 1 == prefix.Length || prefix[close + 1] is '\r' or '\n'))
                return TextKind.Ini;
            return TextKind.Json;
        }
        if (prefix.StartsWith("//") || prefix.StartsWith("/*")) return TextKind.Jsonc;
        return configuration ? DetectConfiguration(prefix.ToString()) : DetectUntitled(prefix.ToString());
    }

    private static TextKind DetectUntitled(string text)
    {
        bool previousContent = false;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var value = line.Trim();
            int hashes = 0;
            while (hashes < value.Length && value[hashes] == '#') hashes++;
            if (hashes is >= 1 and <= 6 && (hashes == value.Length || char.IsWhiteSpace(value[hashes])))
                return TextKind.Markdown;
            if (value.StartsWith("```") || value.StartsWith("~~~")) return TextKind.Markdown;
            if (previousContent && !value.IsEmpty)
            {
                bool equal = true, dash = true;
                foreach (char character in value) { equal &= character == '='; dash &= character == '-'; }
                if (equal || dash) return TextKind.Markdown;
            }
            previousContent = !value.IsEmpty;
        }
        return TextKind.Plain;
    }

    private static TextKind DetectConfiguration(string text)
    {
        if (text.AsSpan().TrimStart().StartsWith("<")) return TextKind.Xml;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var value = line.Trim();
            if (value.IsEmpty || value[0] is '#' or ';') continue;
            int equal = value.IndexOf('='), colon = value.IndexOf(':');
            if (value[0] == '[' || equal >= 0 && (colon < 0 || equal < colon)) return TextKind.Ini;
            if (colon > 0) return TextKind.Yaml;
        }
        return TextKind.Plain;
    }

    public static string? Diagnostic(string text, TextKind kind)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            switch (kind)
            {
                case TextKind.Json:
                case TextKind.Jsonc:
                    using (JsonDocument.Parse(text, JsonOptions(kind))) { }
                    break;
                case TextKind.Xml:
                    using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = 50_000_000
                    }))
                        while (reader.Read()) { }
                    break;
                case TextKind.Yaml:
                    int number = 0;
                    foreach (var line in text.AsSpan().EnumerateLines())
                    {
                        number++;
                        foreach (var character in line)
                        {
                            if (character == '\t') return $"YAML 第 {number} 行缩进含 Tab，请改用空格；此检查仅检查缩进，不是完整语法校验。";
                            if (character != ' ') break;
                        }
                    }
                    break;
            }
            return null;
        }
        catch (JsonException error)
        {
            return $"JSON 语法错误：第 {(error.LineNumber ?? 0) + 1} 行，第 {(error.BytePositionInLine ?? 0) + 1} 字节。{error.Message}";
        }
        catch (XmlException error)
        {
            return $"XML 语法错误：第 {error.LineNumber} 行，第 {error.LinePosition} 列。{error.Message}";
        }
    }

    internal static JsonDocumentOptions JsonOptions(TextKind kind) => new()
    {
        AllowTrailingCommas = kind == TextKind.Jsonc,
        CommentHandling = kind == TextKind.Jsonc ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
        MaxDepth = 256
    };

    /// <summary>Formats supported kinds, returning LF text. Use NormalizeWhitespace explicitly for other kinds.</summary>
    public static string Format(string text, TextKind kind) => FormattingServices.Format(text, kind);

    /// <summary>Explicit cleanup only: LF and trailing spaces/tabs. Indentation and blank-line counts survive.
    /// This is not syntax-aware and can change whitespace inside multiline literals.</summary>
    public static string NormalizeWhitespace(string text) => FormattingServices.NormalizeWhitespace(text);

    public static string FormatJson(string text) => Format(text, TextKind.Json);
}
