using System.Net;
using System.Text;
using System.Globalization;

namespace Qingyue.Core;

public sealed record ReadingBriefInput(
    string Title,
    string? SourcePath,
    TextKind Kind,
    string SourceText,
    int ProgressPercent,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ReadingHeading> Headings,
    IReadOnlyList<DocumentAnnotation> Annotations);

public static class ReadingBriefExporter
{
    public const int MaximumBytes = 8 * 1024 * 1024;

    public static string BuildMarkdown(ReadingBriefInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var builder = new StringBuilder();
        builder.Append("# 阅读简报：").Append(EscapeInline(input.Title.Trim())).Append('\n');
        builder.Append('\n');
        builder.Append("- 来源：").Append(input.SourcePath is null
            ? "未保存文档"
            : CodeSpan(input.SourcePath)).Append('\n');
        builder.Append("- 生成时间：")
            .Append(FormatDate(input.GeneratedAt)).Append('\n');
        builder.Append("- 阅读进度：")
            .Append(Math.Clamp(input.ProgressPercent, 0, 100)).Append('%').Append('\n');
        builder.Append("- 批注数量：").Append(input.Annotations.Count).Append('\n');

        foreach (var group in GroupByChapter(input))
        {
            builder.Append('\n').Append("## ").Append(EscapeInline(group.Title)).Append('\n');
            foreach (var item in group.Items)
            {
                builder.Append('\n');
                builder.Append("### 引文 · 第 ")
                    .Append(LineNumber(input.SourceText, item.SourceStart))
                    .Append(" 行 · ")
                    .Append(AnchorStateText(item.AnchorState)).Append('\n');
                AppendMarkdownQuote(builder, item.Quote);

                if (item.Note is { Length: > 0 })
                {
                    builder.Append('\n').Append("**备注**").Append('\n').Append('\n');
                    AppendMarkdownQuote(builder, item.Note);
                }

                if (item.Tags.Count > 0)
                {
                    builder.Append("**标签** ")
                        .Append(string.Join(" ", item.Tags.Select(CodeSpan)))
                        .Append('\n');
                }
                EnsureSize(builder);
            }
        }

        EnsureSize(builder);
        return builder.ToString();
    }

    public static string BuildHtml(ReadingBriefInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        static string Encode(string value) => WebUtility.HtmlEncode(value);

        var builder = new StringBuilder();
        builder.Append("<!DOCTYPE html>\n")
            .Append("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>阅读简报：").Append(Encode(input.Title)).Append("</title>\n")
            .Append("<style>body{margin:2rem auto;max-width:860px;padding:0 1.5rem;font:16px/1.75 'Microsoft YaHei UI','Segoe UI',sans-serif;color:#29241e;background:#fffdf8}blockquote{margin:0;padding:.5rem 0 .5rem 1rem;border-left:3px solid #b76e3b;background:#f7efe3;white-space:normal}code{background:#f2eadf;padding:.1rem .3rem;border-radius:3px}.state{color:#8a5a2b}.meta{color:#665b4f}article{margin:1.5rem 0}</style>\n")
            .Append("</head><body>")
            .Append("<h1>阅读简报：").Append(Encode(input.Title)).Append("</h1>\n")
            .Append("<p class=\"meta\">来源：")
            .Append(input.SourcePath is null ? "未保存文档" : Encode(input.SourcePath))
            .Append("<br>生成时间：")
            .Append(Encode(FormatDate(input.GeneratedAt)))
            .Append("<br>阅读进度：")
            .Append(Math.Clamp(input.ProgressPercent, 0, 100)).Append("%")
            .Append("<br>批注数量：").Append(input.Annotations.Count)
            .Append("</p>\n");

        foreach (var group in GroupByChapter(input))
        {
            builder.Append("<h2>").Append(Encode(group.Title)).Append("</h2>\n");
            foreach (var item in group.Items)
            {
                builder.Append("<article><p class=\"state\">第 ")
                    .Append(LineNumber(input.SourceText, item.SourceStart))
                    .Append(" 行 · ")
                    .Append(Encode(AnchorStateText(item.AnchorState)))
                    .Append("</p>")
                    .Append("<blockquote>")
                    .Append(Encode(item.Quote).ReplaceLineEndings("<br>"))
                    .Append("</blockquote>");

                if (item.Note is { Length: > 0 })
                {
                    builder.Append("<p><strong>备注</strong><br>")
                        .Append(Encode(item.Note).ReplaceLineEndings("<br>"))
                        .Append("</p>");
                }

                if (item.Tags.Count > 0)
                {
                    builder.Append("<p><strong>标签</strong> ")
                        .Append(string.Join(" ", item.Tags.Select(tag =>
                            "<code>" + Encode(tag) + "</code>")))
                        .Append("</p>");
                }

                builder.Append("</article>\n");
                EnsureSize(builder);
            }
        }

        builder.Append("</body></html>\n");
        EnsureSize(builder);
        return builder.ToString();
    }

    public static string AnchorStateText(AnnotationAnchorState state) => state switch
    {
        AnnotationAnchorState.Exact => "位置准确",
        AnnotationAnchorState.Shifted => "已自动跟随正文移动",
        AnnotationAnchorState.Uncertain => "位置可能已变化，请核对",
        AnnotationAnchorState.Missing => "原文中未找到这段引文",
        _ => "位置状态未知"
    };

    private static IReadOnlyList<(string Title, IReadOnlyList<DocumentAnnotation> Items)> GroupByChapter(
        ReadingBriefInput input)
    {
        var annotations = input.Annotations
            .OrderBy(item => item.SourceStart)
            .ThenBy(item => item.CreatedAt)
            .ToArray();
        var headings = input.Headings
            .OrderBy(item => item.SourceStart)
            .ToArray();

        var groups = new List<(string Title, List<DocumentAnnotation> Items)>();
        int previousHeading = -2;
        int headingIndex = -1;
        foreach (var annotation in annotations)
        {
            while (headingIndex + 1 < headings.Length
                   && headings[headingIndex + 1].SourceStart <= annotation.SourceStart)
            {
                headingIndex++;
            }

            string title = headingIndex >= 0 ? headings[headingIndex].Title : "开篇";
            if (headingIndex == previousHeading)
            {
                groups[^1].Items.Add(annotation);
            }
            else
            {
                groups.Add((title, [annotation]));
                previousHeading = headingIndex;
            }
        }

        return groups
            .Select(group => (group.Title, (IReadOnlyList<DocumentAnnotation>)group.Items))
            .ToArray();
    }

    private static void AppendMarkdownQuote(StringBuilder builder, string value)
    {
        foreach (string line in value.ReplaceLineEndings("\n").Split('\n'))
            builder.Append("> ").Append(EscapeMarkdownUserText(line)).Append('\n');
    }

    private static int LineNumber(string text, int offset)
    {
        int end = Math.Clamp(offset, 0, text.Length);
        int line = 1;
        for (int i = 0; i < end; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private static string EscapeInline(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("#", "\\#", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);

    private static string EscapeMarkdownUserText(string value) =>
        EscapeInline(value).Replace("`", "\\`", StringComparison.Ordinal);

    private static string CodeSpan(string value)
    {
        string text = value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        int longest = 0;
        int run = 0;
        foreach (char character in text)
        {
            run = character == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        string delimiter = new('`', longest + 1);
        return delimiter + " " + text + " " + delimiter;
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static void EnsureSize(StringBuilder builder)
    {
        if (Encoding.UTF8.GetByteCount(builder.ToString()) > MaximumBytes)
            throw new InvalidOperationException(
                "阅读简报超过 8 MiB 上限，请减少批注数量或备注长度。");
    }
}
