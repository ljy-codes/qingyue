using System.Net;
using System.Text;

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
        builder.Append("# 阅读简报：").AppendLine(EscapeInline(input.Title.Trim()));
        builder.AppendLine();
        builder.Append("- 来源：").AppendLine(input.SourcePath is null
            ? "未保存文档"
            : "`" + input.SourcePath.Replace("`", "\\`", StringComparison.Ordinal) + "`");
        builder.Append("- 生成时间：")
            .AppendLine(input.GeneratedAt.ToString("yyyy-MM-dd HH:mm zzz"));
        builder.Append("- 阅读进度：")
            .Append(Math.Clamp(input.ProgressPercent, 0, 100)).AppendLine("%");
        builder.Append("- 批注数量：").AppendLine(input.Annotations.Count.ToString());

        foreach (var group in GroupByChapter(input))
        {
            builder.AppendLine().Append("## ").AppendLine(EscapeInline(group.Title));
            foreach (var item in group.Items)
            {
                builder.AppendLine();
                builder.Append("### 引文 · 第 ")
                    .Append(LineNumber(input.SourceText, item.SourceStart))
                    .Append(" 行 · ")
                    .AppendLine(AnchorStateText(item.AnchorState));
                AppendMarkdownQuote(builder, item.Quote);

                if (!string.IsNullOrWhiteSpace(item.Note))
                {
                    builder.AppendLine().AppendLine("**备注**").AppendLine();
                    AppendMarkdownQuote(builder, item.Note);
                }

                if (item.Tags.Count > 0)
                {
                    builder.Append("**标签** ").AppendLine(string.Join(" ",
                        item.Tags.Select(tag => "`" + tag.Replace("`", "\\`",
                            StringComparison.Ordinal) + "`")));
                }
            }
        }

        string result = builder.ToString();
        EnsureSize(result);
        return result;
    }

    public static string BuildHtml(ReadingBriefInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        static string Encode(string value) => WebUtility.HtmlEncode(value);

        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html>")
            .AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">")
            .AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">")
            .AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>阅读简报：").Append(Encode(input.Title)).AppendLine("</title>")
            .AppendLine("<style>body{margin:2rem auto;max-width:860px;padding:0 1.5rem;font:16px/1.75 'Microsoft YaHei UI','Segoe UI',sans-serif;color:#29241e;background:#fffdf8}blockquote{margin:0;padding:.5rem 0 .5rem 1rem;border-left:3px solid #b76e3b;background:#f7efe3;white-space:normal}code{background:#f2eadf;padding:.1rem .3rem;border-radius:3px}.state{color:#8a5a2b}.meta{color:#665b4f}article{margin:1.5rem 0}</style>")
            .AppendLine("</head><body>")
            .Append("<h1>阅读简报：").Append(Encode(input.Title)).AppendLine("</h1>")
            .Append("<p class=\"meta\">来源：")
            .Append(input.SourcePath is null ? "未保存文档" : Encode(input.SourcePath))
            .Append("<br>生成时间：")
            .Append(Encode(input.GeneratedAt.ToString("yyyy-MM-dd HH:mm zzz")))
            .Append("<br>阅读进度：")
            .Append(Math.Clamp(input.ProgressPercent, 0, 100)).Append("%")
            .Append("<br>批注数量：").Append(input.Annotations.Count)
            .AppendLine("</p>");

        foreach (var group in GroupByChapter(input))
        {
            builder.Append("<h2>").Append(Encode(group.Title)).AppendLine("</h2>");
            foreach (var item in group.Items)
            {
                builder.Append("<article><p class=\"state\">第 ")
                    .Append(LineNumber(input.SourceText, item.SourceStart))
                    .Append(" 行 · ")
                    .Append(Encode(AnchorStateText(item.AnchorState)))
                    .AppendLine("</p>")
                    .Append("<blockquote>")
                    .Append(Encode(item.Quote).ReplaceLineEndings("<br>"))
                    .AppendLine("</blockquote>");

                if (!string.IsNullOrWhiteSpace(item.Note))
                {
                    builder.Append("<p><strong>备注</strong><br>")
                        .Append(Encode(item.Note).ReplaceLineEndings("<br>"))
                        .AppendLine("</p>");
                }

                if (item.Tags.Count > 0)
                {
                    builder.Append("<p><strong>标签</strong> ")
                        .Append(string.Join(" ", item.Tags.Select(tag =>
                            "<code>" + Encode(tag) + "</code>")))
                        .AppendLine("</p>");
                }

                builder.AppendLine("</article>");
            }
        }

        builder.AppendLine("</body></html>");
        string result = builder.ToString();
        EnsureSize(result);
        return result;
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

        return annotations
            .GroupBy(item =>
            {
                var heading = headings.LastOrDefault(candidate =>
                    candidate.SourceStart <= item.SourceStart);
                return heading?.Title ?? "开篇";
            })
            .Select(group => (group.Key,
                (IReadOnlyList<DocumentAnnotation>)group.ToArray()))
            .ToArray();
    }

    private static void AppendMarkdownQuote(StringBuilder builder, string value)
    {
        foreach (string line in value.ReplaceLineEndings("\n").Split('\n'))
            builder.Append("> ").AppendLine(EscapeMarkdownUserText(line));
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

    private static void EnsureSize(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaximumBytes)
            throw new InvalidOperationException(
                "阅读简报超过 8 MiB 上限，请减少批注数量或备注长度。");
    }
}
