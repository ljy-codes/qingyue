using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Qingyue.Core;
using Qingyue.Windows;

internal static class Program
{
    private const string Sample = "# 阅读标题😀\r\n\r\n## 二级标题\r\n\r\n### 三级标题\r\n\r\n" +
        "正文含 **重点**、*斜体*、~~删除~~、`inline_code` 与 [链接](https://example.test)。\r\n\r\n" +
        "> 引用内容\r\n> 保留 `code` 与 **重点**\r\n\r\n" +
        "- 第一项\r\n- 第二项\r\n- [x] 已完成\r\n\r\n3. 有序条目\r\n4. 下一条\r\n\r\n" +
        "```cs\r\nvar literal = \"**不是强调**\";\r\n// 中文😀\r\n```\r\n\r\n" +
        "| 名称 | 值 |\r\n| --- | --- |\r\n| 中文 | 😀 |\r\n| a\\|b | `code` |\r\n\r\n---\r\n";

    [STAThread]
    private static int Main(string[] args)
    {
        var tests = new (string Name, Action Run)[]
        {
            ("existing mapping and visible text", Mapping),
            ("block hierarchy and literal code", Blocks),
            ("preview inline styles and inert content", InlinePreview),
            ("preview lists and table grid", ListsAndTables),
            ("preview update, width and font scale", Update),
            ("dynamic theme resource updates", Theme),
            ("editor token ranges and precedence", Tokens),
            ("unicode, incomplete input and long lines", TokenBoundaries),
            ("raw fallback and parser compatibility", Fallback),
            ("ordered marker numeric limits", OrderedMarkerLimits),
            ("successive incomplete edits remain renderable", EditingStates),
            ("parser failures show raw text and recover", ParserFailureRecovery),
            ("pasted Markdown shows a sequence diagram", MermaidChecks.Preview),
            ("Mermaid fences, literal code and failure recovery", MermaidChecks.FencesAndFallback),
            ("Mermaid reader preserves search and source mapping", MermaidChecks.ReadingMapping),
            ("Mermaid has visible geometry and overflow scrolling", MermaidChecks.Layout),
            ("Mermaid document budget keeps all source", MermaidChecks.DocumentBudget)
        };
        int failed = 0;
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
        }
        if (args.Length == 2 && args[0] == "--screenshots") Screenshots(args[1]);
        Console.WriteLine($"Markdown checks: {tests.Length - failed}/{tests.Length} passed");
        return failed == 0 ? 0 : 1;
    }

    private static void Mapping()
    {
        foreach (var (source, kind) in new[]
        {
            ("甲\n乙\n\n丙😀", TextKind.Plain),
            ("甲😀\r\n乙\n", TextKind.Json),
            (Sample, TextKind.Markdown),
            ("", TextKind.Markdown), ("\n\n", TextKind.Markdown),
            ("```\n\n```\n\n#\n\n😀\n===\n", TextKind.Markdown)
        })
        {
            var model = ReadingModel.Build(source, kind);
            var rendered = new RenderedReading(model);
            var baselineRun = new Run(model.VisibleText);
            var baselineDocument = new FlowDocument(new Paragraph(baselineRun));
            int previous = -1;
            for (int offset = 0; offset <= source.Length; offset++)
            {
                int display = model.SourceToDisplay(offset);
                Check(display >= previous && Boundary(model.VisibleText, display), "source mapping monotone/boundary");
                previous = display;
            }
            previous = -1;
            for (int offset = 0; offset <= model.VisibleText.Length; offset++)
            {
                int mapped = model.DisplayToSource(offset);
                Check(mapped >= previous && Boundary(source, mapped), "display mapping monotone/boundary");
                previous = mapped;
                if (Boundary(model.VisibleText, offset))
                {
                    // WPF normalizes a pointer inside a CRLF pair even in an untouched Run.
                    // Compare raw CRLF with that original behavior, not an impossible caret.
                    int expected = model.VisibleText.Contains('\r')
                        ? new TextRange(baselineRun.ContentStart, baselineRun.ContentStart.GetPositionAtOffset(offset)!).Text.Length
                        : offset;
                    Equal(expected, rendered.OffsetAt(rendered.PointerAt(offset)), $"{kind} pointer roundtrip at {offset}");
                }
            }
            Equal(0, rendered.OffsetAt(rendered.PointerAt(-10)), "pointer lower clamp");
            Equal(model.VisibleText.Length, rendered.OffsetAt(rendered.PointerAt(int.MaxValue)), "pointer upper clamp");
            Equal(Math.Max(1, model.Blocks.Count), rendered.Document.Blocks.Count, "unchanged block structure");
            foreach (var paragraph in rendered.Document.Blocks.Cast<Paragraph>())
                Check(paragraph.Inlines.Count == 1 && paragraph.Inlines.FirstInline is Run, "one mapping run per block");
            if (kind != TextKind.Markdown) Equal(source, model.VisibleText, "identity text");
        }
        const string raw = "# 标题😀\n\n**加粗**与*斜体*、`代码`、[链接](https://example.test)";
        Equal("标题😀\n加粗与斜体、代码、链接", ReadingModel.Build(raw, TextKind.Markdown).VisibleText, "markdown visible text");
    }

    private static void Blocks()
    {
        var document = new RenderedReading(ReadingModel.Build(Sample, TextKind.Markdown)).Document;
        var headings = document.Blocks.OfType<Paragraph>().Take(3).ToArray();
        Check(headings[0].FontSize > headings[1].FontSize && headings[1].FontSize > headings[2].FontSize &&
            headings[2].FontSize > document.FontSize, "H1/H2/H3 size hierarchy");
        Check(headings[0].FontWeight.ToOpenTypeWeight() > headings[2].FontWeight.ToOpenTypeWeight(), "heading weight hierarchy");
        Check(headings[0].Margin.Top > headings[2].Margin.Top, "heading spacing hierarchy");
        var code = document.Blocks.OfType<Paragraph>().Single(p => Text(p).StartsWith("var literal"));
        Check(code.Padding.Left >= 12 && code.BorderThickness.Left > 0, "code panel");
        Equal("var literal = \"**不是强调**\";\n// 中文😀", Text(code), "literal fenced code");
    }

    private static void InlinePreview()
    {
        var preview = new MarkdownPreview(Sample, 16, 680);
        preview.Resources["PanelBrush"] = Brushes.LightGray;
        Check(preview.IsReadOnly && !preview.IsUndoEnabled && !preview.IsDocumentEnabled, "passive read-only preview");
        var runs = AllParagraphs(preview.Document.Blocks).SelectMany(p => Runs(p.Inlines)).ToArray();
        var code = runs.Single(r => r.Text == "inline_code");
        Check(code.FontFamily.Source.Contains("Consolas") && code.Background is not null, "inline code style");
        Check(runs.First(r => r.Text == "重点").FontWeight == FontWeights.Bold, "strong style");
        Check(runs.Single(r => r.Text == "斜体").FontStyle == FontStyles.Italic, "emphasis style");
        Check(runs.Single(r => r.Text == "删除").TextDecorations.Count > 0, "strike style");
        var literal = new MarkdownPreview("<script>bad()</script>\n\n![远程](https://invalid.test/p.png)\n\n[链接](file:///x)", 16, 680);
        string text = new TextRange(literal.Document.ContentStart, literal.Document.ContentEnd).Text;
        Check(text.Contains("<script>bad()</script>") && text.Contains("[图片：远程]") && !text.Contains("https://"), "inert text/image");
        Check(!AllParagraphs(literal.Document.Blocks).Any(p => p.Inlines.Any(i => i is Hyperlink or InlineUIContainer)), "no navigation/media");
    }

    private static void ListsAndTables()
    {
        var preview = new MarkdownPreview(Sample, 16, 680);
        var table = preview.Document.Blocks.OfType<Table>().Single();
        Equal(2, table.Columns.Count, "table columns");
        Equal(3, table.RowGroups.Single().Rows.Count, "table rows");
        Check(table.RowGroups.Single().Rows[0].FontWeight == FontWeights.SemiBold, "table header");
        var lists = preview.Document.Blocks.OfType<System.Windows.Documents.List>().ToArray();
        Check(lists.Any(l => l.MarkerStyle == TextMarkerStyle.Disc && l.ListItems.Count == 2), "bullet group");
        Check(lists.Any(l => l.MarkerStyle == TextMarkerStyle.Decimal && l.StartIndex == 3 && l.ListItems.Count == 2), "ordered start");
        Check(lists.Any(l => l.MarkerStyle == TextMarkerStyle.None), "task keeps check mark");
        var zero = new MarkdownPreview("0) 零起点\n1) 下一项", 16, 680);
        Check(new TextRange(zero.Document.ContentStart, zero.Document.ContentEnd).Text.Contains("0) 零起点"), "zero ordered marker preserved");
    }

    private static void Update()
    {
        var preview = new MarkdownPreview("# 标题", 20, 520);
        Equal(20d, preview.Document.FontSize, "base font");
        Equal(40d, ((Paragraph)preview.Document.Blocks.FirstBlock).FontSize, "scaled H1");
        preview.Measure(new Size(1000, 700));
        preview.Arrange(new Rect(0, 0, 1000, 700));
        preview.UpdateLayout();
        Check(preview.Document.PagePadding.Left > 100, "reading width centered");
        double textLeft = ((Paragraph)preview.Document.Blocks.FirstBlock).ContentStart.GetCharacterRect(LogicalDirection.Forward).Left;
        Check(Math.Abs(textLeft - preview.Document.PagePadding.Left) < 20, "reading gutter applied once");
        preview.Update("## 新标题\n\n新的 `code`");
        Check(!new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text.Contains("旧标题"), "replaced document");
        Equal("新标题", Text((Paragraph)preview.Document.Blocks.FirstBlock), "updated text");
        Check(preview.IsReadOnly && preview.Document.FontSize == 20, "update keeps options");
        preview.Measure(new Size(320, 700));
        preview.Arrange(new Rect(0, 0, 320, 700));
        preview.UpdateLayout();
        Check(preview.Document.PagePadding.Left >= 16 && preview.Document.PagePadding.Left < 40, "narrow viewport");
    }

    private static void Theme()
    {
        var preview = new MarkdownPreview("# 标题\n\n`code`", 16, 680);
        var heading = (Paragraph)preview.Document.Blocks.FirstBlock;
        preview.Resources["AccentTextBrush"] = Brushes.Red;
        Equal(Brushes.Red, heading.Foreground, "initial dynamic resource");
        preview.Resources["AccentTextBrush"] = Brushes.Blue;
        Equal(Brushes.Blue, heading.Foreground, "theme switch");
        var code = Runs(((Paragraph)preview.Document.Blocks.LastBlock).Inlines).Single();
        preview.Resources["PanelBrush"] = Brushes.Beige;
        Equal(Brushes.Beige, code.Background, "inline code theme");
    }

    private static void Tokens()
    {
        const string line = "## 标题 **重点** `code` [链接](https://a.test)";
        var tokens = MarkdownSyntax.Tokens(line).ToArray();
        Check(tokens.Any(t => t.ResourceKey == "AccentTextBrush"), "heading color");
        Check(tokens.Any(t => line.Substring(t.Start, t.Length) == "**重点**" && t.ResourceKey == "SyntaxValueBrush"), "strong token");
        Check(tokens.Any(t => line.Substring(t.Start, t.Length) == "`code`" && t.ResourceKey == "SyntaxStringBrush"), "code token");
        Check(tokens.Any(t => line.Substring(t.Start, t.Length) == "[链接](https://a.test)" && t.ResourceKey == "SyntaxKeyBrush"), "link token");
        var code = MarkdownSyntax.Tokens("`**literal** [x](y)`").ToArray();
        Equal(1, code.Length, "code excludes nested syntax");
        Equal(0, MarkdownSyntax.Tokens(@"a_b_c \*literal\* #not-heading").Count(), "escaped and intraword markers");
        Equal("SyntaxStringBrush", MarkdownSyntax.Tokens("```cs").Single().ResourceKey, "fence marker");
        Equal(0, MarkdownSyntax.Tokens("ordinary words").Count(), "plain text no tokens");
    }

    private static void TokenBoundaries()
    {
        foreach (string line in new[] { "", "#", "#not-heading", "### 😀 `中文` **😀**", "[未完成](", "`", "**", "![图](x)",
            "> `code`", "- [x] **任务**", "```` code", new string('*', 30_000), new string('[', 30_000) })
        {
            int previous = 0;
            foreach (var token in MarkdownSyntax.Tokens(line))
            {
                Check(token.Start >= previous && token.Length > 0 && token.Start + token.Length <= line.Length, "ordered non-overlap ranges");
                Check(Boundary(line, token.Start) && Boundary(line, token.Start + token.Length), "UTF16 token boundaries");
                previous = token.Start + token.Length;
            }
        }
        var clock = Stopwatch.StartNew();
        _ = MarkdownSyntax.Tokens(new string('[', 200_000) + " **end**").ToArray();
        Check(clock.Elapsed < TimeSpan.FromSeconds(3), "bounded scan time");
    }

    private static void Fallback()
    {
        foreach (string source in new[] { "", "\n\n", @"\*literal\* **`nested`** __bold__ foo_bar_baz",
            "# 标题 ##\n\n段落  \n第二行", "Setext\n===\n", "> **quote**\r\n> `code`",
            "- [ ] *task*\n- [X] done", new string('文', 200_001) + " **literal**" })
        {
            var expected = new RenderedReading(ReadingModel.Build(source, TextKind.Markdown)).Document;
            var actual = new MarkdownPreview(source, 16, 680).Document;
            // Samples contain no tables/lists whose semantic containers add text-range separators.
            if (!actual.Blocks.Any(b => b is Table or System.Windows.Documents.List))
                Equal(new TextRange(expected.ContentStart, expected.ContentEnd).Text,
                    new TextRange(actual.ContentStart, actual.ContentEnd).Text, "preview text compatibility");
        }
    }

    private static void OrderedMarkerLimits()
    {
        var preview = new MarkdownPreview("", 16, 680);
        foreach (string digits in new[] { "0", "000000000", "1", "999999999", "2147483647", "2147483648", new string('9', 5_000) })
        {
            string source = $"{digits}) 数字边界\n\n# 后续标题";
            preview.Update(source);
            string visible = new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text;
            Check(visible.Contains("数字边界") && visible.Contains("后续标题"), "numeric boundary retains content");
            if (digits.Length > 9) Check(visible.Contains(digits + ")"), "unsupported marker stays literal");
        }
    }

    private static void EditingStates()
    {
        var preview = new MarkdownPreview("", 16, 680);
        int updates = 0;
        foreach (string source in new[]
        {
            "1) **编辑** [链接](https://example.test)\n2) `code`\n- [x] 任务\n> 引用\n",
            "| 表头 | 内容 |\n| --- | :---: |\n| `a|b` | **值** |\n",
            "# 标题😀\r\n\r\n~~~md\r\n# literal\r\n~~~\r\n尾段",
            "![图片](file:///x) <script>x</script>\n\0\uD800\uDC00\uD800\uDC00\uD800\n**未闭合",
        })
        {
            for (int end = 0; end <= source.Length; end++)
            {
                preview.Update(source[..end]);
                Check(preview.Document is not null && preview.IsReadOnly, "every insertion prefix is safe");
                updates++;
            }
            for (int end = source.Length; end >= 0; end -= 3)
            {
                preview.Update(source[..end]);
                updates++;
            }
        }
        var random = new Random(82127);
        const string alphabet = "abc 中文#>*_~`[]()!|\\.-0123456789 \t\r\n";
        for (int sample = 0; sample < 150; sample++)
        {
            var chars = new char[random.Next(0, 220)];
            for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
            preview.Update(new string(chars));
            updates++;
        }
        preview.Measure(new Size(400, 500));
        preview.Arrange(new Rect(0, 0, 400, 500));
        preview.UpdateLayout();
        Console.WriteLine($"  exercised {updates} insertion/deletion/random updates");
    }

    private static void ParserFailureRecovery()
    {
        var preview = new MarkdownPreview("# 原预览", 18, 680);
        const string raw = "214748364800000000000) **尚未完成** [链接](\n原文😀";
        foreach (Exception failure in new Exception[]
        {
            new RegexMatchTimeoutException(), new FormatException(), new OverflowException(),
            new InvalidOperationException(), new ArgumentOutOfRangeException(), new IndexOutOfRangeException()
        })
        {
            preview.Update(raw, _ => throw failure);
            var paragraph = preview.Document.Blocks.OfType<Paragraph>().Single();
            Equal(raw, paragraph.Inlines.OfType<Run>().Single().Text, "failure fallback preserves original source");
            Check(preview.IsReadOnly && preview.Document.FontSize == 18, "fallback keeps preview settings");
            preview.Update("# 解析恢复\n\n`code`");
            Equal("解析恢复", Text((Paragraph)preview.Document.Blocks.FirstBlock), "later edits retry rich rendering");
        }
        var before = preview.Document;
        bool propagated = false;
        try { preview.Update(raw, _ => throw new OutOfMemoryException("simulated; no allocation")); }
        catch (OutOfMemoryException) { propagated = true; }
        Check(propagated && ReferenceEquals(before, preview.Document), "fatal runtime failures are not disguised as content errors");
    }

    private static void Screenshots(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (bool dark in new[] { false, true })
        {
            var preview = new MarkdownPreview(Sample, 16, 680);
            preview.Resources["TextBrush"] = new SolidColorBrush(dark ? Color.FromRgb(228, 232, 238) : Color.FromRgb(35, 43, 53));
            preview.Resources["EditorBrush"] = new SolidColorBrush(dark ? Color.FromRgb(26, 29, 35) : Colors.White);
            preview.Resources["PanelBrush"] = new SolidColorBrush(dark ? Color.FromRgb(36, 41, 49) : Color.FromRgb(242, 245, 248));
            preview.Resources["BorderBrush"] = new SolidColorBrush(dark ? Color.FromRgb(74, 83, 96) : Color.FromRgb(201, 211, 222));
            preview.Resources["MutedBrush"] = new SolidColorBrush(dark ? Color.FromRgb(173, 184, 199) : Color.FromRgb(86, 102, 121));
            preview.Resources["AccentTextBrush"] = new SolidColorBrush(dark ? Color.FromRgb(141, 190, 255) : Color.FromRgb(27, 86, 153));
            preview.Resources["SyntaxKeyBrush"] = preview.Resources["AccentTextBrush"];
            preview.Resources["SyntaxStringBrush"] = new SolidColorBrush(dark ? Color.FromRgb(160, 210, 173) : Color.FromRgb(39, 105, 58));
            preview.Resources["SyntaxValueBrush"] = new SolidColorBrush(dark ? Color.FromRgb(224, 184, 247) : Color.FromRgb(121, 66, 149));
            preview.Measure(new Size(900, 1300));
            preview.Arrange(new Rect(0, 0, 900, 1300));
            preview.UpdateLayout();
            var bitmap = new RenderTargetBitmap(900, 1300, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(preview);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, dark ? "markdown-dark.png" : "markdown-light.png"));
            encoder.Save(output);
        }
    }

    private static IEnumerable<Paragraph> AllParagraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph p) yield return p;
            if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems)
                    foreach (var paragraph in AllParagraphs(item.Blocks)) yield return paragraph;
            if (block is Table table)
                foreach (var group in table.RowGroups)
                    foreach (var row in group.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var paragraph in AllParagraphs(cell.Blocks)) yield return paragraph;
        }
    }

    private static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run) yield return run;
            if (inline is Span span)
                foreach (var child in Runs(span.Inlines)) yield return child;
        }
    }

    private static string Text(Paragraph p) => new TextRange(p.ContentStart, p.ContentEnd).Text;
    private static bool Boundary(string text, int offset) => offset >= 0 && offset <= text.Length &&
        !(offset > 0 && offset < text.Length && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]));
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
    private static void Equal<T>(T expected, T actual, string reason) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{reason}: expected {expected}, got {actual}");
}
