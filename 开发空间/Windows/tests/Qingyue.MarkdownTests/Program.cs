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
        if (args.Length == 2 && args[0] == "--list-marker-audit")
        {
            NestedListChecks.CompareMarkerRendering(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--table-audit")
        {
            // Read-only aggregate diagnostics; never print document text or image data.
            string source = File.ReadAllText(args[1]);
            var model = ReadingModel.Build(source, TextKind.Markdown);
            var preview = new MarkdownPreview(source, 16, 680);
            Console.WriteLine($"Table audit: characters={source.Length}; " +
                $"modelTables={model.Blocks.Count(b => b.Kind == ReadingBlockKind.Table)}; " +
                $"previewTables={preview.Document.Blocks.OfType<Table>().Count()}");
            return 0;
        }
        var tests = new (string Name, Action Run)[]
        {
            ("existing mapping and visible text", Mapping),
            ("block hierarchy and literal code", Blocks),
            ("preview inline styles and inert content", InlinePreview),
            ("Markdown br tags use in-paragraph breaks like macOS build 9", BrBreaks),
            ("preview GFM autolinks and reference links", GfmPreview),
            ("Markdown link scanner matches the macOS safety and reference contract", MarkdownLinksScanner),
            ("preview safe Markdown links are interactive and preserve label styles", MarkdownLinkBehavior),
            ("preview lists and table grid", ListsAndTables),
            ("nested list levels and original CRLF ranges", NestedListChecks.Model),
            ("nested bullet containers and three marker styles", NestedListChecks.Preview),
            ("mixed lists keep parent continuation and inline styles", NestedListChecks.Continuations),
            ("nested fenced code stays literal", NestedListChecks.Fences),
            ("nested reading source and decorated pointer mapping", NestedListChecks.Mapping),
            ("nested list syntax keeps original token offsets", NestedListChecks.Tokens),
            ("consecutive continuations return to their own ancestors", NestedListChecks.AncestorContinuations),
            ("nested fences isolate references and literal images", NestedListChecks.FenceIsolation),
            ("reading list grammar agrees with writing marker widths", NestedListChecks.MarkerGrammar),
            ("productivity nested blocks preserve CRLF source ranges", ProductivityHierarchyChecks.ModelRanges),
            ("productivity quote and real table belong to ListItem", ProductivityHierarchyChecks.PreviewContainers),
            ("productivity blocks return to ancestors with inline styles", ProductivityHierarchyChecks.AncestorsAndStyles),
            ("productivity relative indentation and outside table boundaries", ProductivityHierarchyChecks.ClosingAndRelativeIndent),
            ("productivity nested fences retain literal code semantics", ProductivityHierarchyChecks.CodeSemantics),
            ("productivity nested block navigation and UTF16 mapping", ProductivityHierarchyChecks.Navigation),
#if QINGYUE_HIERARCHY_EXPORT_CHECKS
            ("productivity clipboard and HTML export preserve actual hierarchy", ProductivityHierarchyChecks.ExportedHierarchy),
#endif
            ("preview one-dash table syntax", () => TableSyntaxCompatibility(1)),
            ("preview two-dash table syntax", () => TableSyntaxCompatibility(2)),
            ("preview three-dash table syntax", () => TableSyntaxCompatibility(3)),
            ("preview incomplete table syntax stays text", IncompleteTableSyntax),
            ("preview update, width and font scale", Update),
            ("dynamic theme resource updates", Theme),
            ("editor token ranges and precedence", Tokens),
            ("unicode, incomplete input and long lines", TokenBoundaries),
            ("raw fallback and parser compatibility", Fallback),
            ("ordered marker numeric limits", OrderedMarkerLimits),
            ("successive incomplete edits remain renderable", EditingStates),
            ("parser failures show raw text and recover", ParserFailureRecovery),
            ("markdown outline preserves heading levels and source ranges", Outline),
            ("text diff marks changed lines and keeps line numbers", Diff),
            ("pasted Markdown shows a sequence diagram", MermaidChecks.Preview),
            ("pasted Markdown shows a flowchart", MermaidChecks.FlowchartPreview),
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

    private static void GfmPreview()
    {
        const string input = "[guide]: https://example.test/guide \"指南\"\n\n" +
            "<https://example.test> <mailto:reader@example.test>\n[文档][guide]\n[未解析][missing]";
        var preview = new MarkdownPreview(input, 16, 680);
        string visible = new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text;
        Check(visible.Contains("https://example.test", StringComparison.Ordinal), "HTTP autolink preview text");
        Check(visible.Contains("mailto:reader@example.test", StringComparison.Ordinal), "mailto autolink preview text");
        Check(visible.Contains("文档", StringComparison.Ordinal) &&
            !visible.Contains("[文档][guide]", StringComparison.Ordinal), "reference link preview text");
        Check(visible.Contains("[未解析][missing]", StringComparison.Ordinal), "unresolved reference stays visible");
        Check(!visible.Contains("[guide]:", StringComparison.Ordinal), "reference definition stays hidden");
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
        Check(preview.IsReadOnly && !preview.IsUndoEnabled && preview.IsDocumentEnabled, "read-only preview with interactive content");
        var runs = AllParagraphs(preview.Document.Blocks).SelectMany(p => Runs(p.Inlines)).ToArray();
        var code = runs.Single(r => r.Text == "inline_code");
        Check(code.FontFamily.Source.Contains("Consolas") && code.Background is not null, "inline code style");
        Check(runs.First(r => r.Text == "重点").FontWeight == FontWeights.Bold, "strong style");
        Check(runs.Single(r => r.Text == "斜体").FontStyle == FontStyles.Italic, "emphasis style");
        Check(runs.Single(r => r.Text == "删除").TextDecorations.Count > 0, "strike style");
        var literal = new MarkdownPreview("<script>bad()</script>\n\n![远程](https://invalid.test/p.png)\n\n[链接](file:///x)", 16, 680);
        string text = new TextRange(literal.Document.ContentStart, literal.Document.ContentEnd).Text;
        Check(text.Contains("<script>bad()</script>") && text.Contains("[图片：远程]") && !text.Contains("https://"), "inert text/image");
        Check(!AllInlines(literal.Document.Blocks).Any(inline => inline is Hyperlink) &&
            !AllParagraphs(literal.Document.Blocks).Any(p => p.Inlines.Any(i => i is InlineUIContainer)),
            "unsafe destinations and remote images stay inert");
    }

    private static void MarkdownLinkBehavior()
    {
        const string source = "[guide]: <https://example.com/guide> \"说明\"\n\n" +
            "# [章节][guide] 😀\n\n" +
            "正文 [引用][GUIDE]、[guide][] 和 [guide]。访问 https://swift.org/docs, 或 <https://example.com/a>。\n\n" +
            "- **[重点][guide]**\n\n" +
            "| 链接 | 内容 |\n| --- | --- |\n| [表格][guide] | `https://code.example` |\n\n" +
            "```md\n[onlycode]: https://never.example\n[代码][guide] https://fence.example\n```\n\n" +
            "`[内联][guide] https://inline.example`\n\n" +
            "[危险](javascript:alert(1)) [文件](file:///tmp/a)\n\n## 后续标题\n";
        var preview = new MarkdownPreview(source, 16, 680);
        string visible = new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text;
        var links = AllInlines(preview.Document.Blocks).OfType<Hyperlink>().ToArray();
        string Target(string text) => links
            .Where(link => new TextRange(link.ContentStart, link.ContentEnd).Text.Contains(text, StringComparison.Ordinal))
            .Select(link => link.NavigateUri?.AbsoluteUri)
            .FirstOrDefault() ?? throw new InvalidOperationException("missing link: " + text);

        Equal("https://example.com/guide", Target("引用"), "full reference destination");
        Equal("https://example.com/guide", Target("guide"), "shortcut reference destination");
        Equal("https://swift.org/docs", Target("https://swift.org/docs"), "raw autolink destination");
        Equal("https://example.com/a", Target("https://example.com/a"), "angle autolink destination");
        Equal("https://example.com/guide", Target("重点"), "reference inside bold");
        Equal("https://example.com/guide", Target("表格"), "reference in table");
        Check(visible.Contains("章节 😀") && !visible.Contains("[章节][guide]", StringComparison.Ordinal),
            "reference label is visible in heading");
        Check(visible.Contains("正文 引用、guide 和 guide。", StringComparison.Ordinal),
            "full, collapsed and shortcut references show labels");
        Check(visible.Contains("https://swift.org/docs,", StringComparison.Ordinal),
            "autolink keeps trailing punctuation outside the target");
        Check(!visible.Contains("[guide]:", StringComparison.Ordinal) &&
            visible.Contains("[onlycode]:", StringComparison.Ordinal),
            "only active body reference definitions are hidden");
        var bold = AllInlines(preview.Document.Blocks).OfType<Run>().Single(run => run.Text == "重点");
        Check(bold.FontWeight == FontWeights.Bold, "link label keeps bold style");
        Check(!links.Any(link => new TextRange(link.ContentStart, link.ContentEnd).Text.Contains(
            "https://code.example", StringComparison.Ordinal) ||
            new TextRange(link.ContentStart, link.ContentEnd).Text.Contains("https://fence.example", StringComparison.Ordinal) ||
            new TextRange(link.ContentStart, link.ContentEnd).Text.Contains("https://inline.example", StringComparison.Ordinal)),
            "code and fenced content never become links");
        Check(!links.Any(link => link.NavigateUri?.Scheme is "javascript" or "file"),
            "unsafe protocols are not navigable");
        Check(preview.IsDocumentEnabled, "interactive document enables hyperlink input");

        const string referenceImage = "![猫][pic]\n\n[pic]: images/cat.png\n";
        var extracted = MarkdownImages.Extract(referenceImage);
        Check(extracted.References.Count == 1 &&
            extracted.References[0].Alt == "猫" &&
            extracted.References[0].Target == "images/cat.png",
            "reference image retains its local loading target");
        var imagePreview = new MarkdownPreview(referenceImage, 16, 680);
        string imageText = new TextRange(imagePreview.Document.ContentStart, imagePreview.Document.ContentEnd).Text;
        Check(imageText.Contains("[图片：猫]", StringComparison.Ordinal) &&
            !imageText.Contains("[pic]:", StringComparison.Ordinal),
            "reference image renders its caption and hides the definition");
    }

    private static void MarkdownLinksScanner()
    {
        const string source = "[官网](https://example.com/docs) [参考][guide] https://swift.org\n\n" +
            "[guide]: <https://example.com/guide>\n\n" +
            "`https://ignored.example`\n```\n[代码](https://ignored.example)\n[onlycode]: https://never.example\n```";
        var references = MarkdownLinks.References(source);
        Equal("https://example.com/guide", references["guide"].Destination, "scanner reference destination");
        var links = MarkdownLinks.Occurrences(source);
        Equal(3, links.Count, "scanner finds inline, reference and automatic links");
        Check(links.Select(link => link.Destination).Contains("https://example.com/guide", StringComparer.Ordinal),
            "scanner resolves reference links");
        Check(links.All(link => !link.Destination.Contains("ignored", StringComparison.Ordinal) &&
            !link.Destination.Contains("never", StringComparison.Ordinal)),
            "scanner ignores code, fences and fenced definitions");
        Check(MarkdownLinks.Occurrences("[危险](javascript:alert(1)) <file:///tmp/a>").Count == 0,
            "scanner rejects script and file protocols");
        Check(MarkdownLinks.Occurrences(@"\[guide]" + "\n\n[guide]: https://example.com\n").Count == 0,
            "scanner preserves escaped brackets as literal text");
        Check(MarkdownLinks.Occurrences("    https://indented.example\n").Count == 0,
            "scanner ignores indented code");
        var automatic = MarkdownLinks.Occurrences("访问 https://swift.org/docs, 和 www.example.com。");
        Equal(2, automatic.Count, "scanner keeps www and raw automatic links");
        Equal("https://swift.org/docs", automatic[0].Destination, "raw autolink punctuation");
        Equal("https://www.example.com", automatic[1].Destination, "www autolink scheme");
        Check(!MarkdownLinks.TryCreateNavigationUri("images/cat.png", out _) &&
            MarkdownLinks.TryCreateNavigationUri("mailto:reader@example.test", out _),
            "navigation requires an explicitly allowed scheme");
    }

    private static void BrBreaks()
    {
        const char separator = '\u2028';
        foreach (string tag in new[] { "<br>", "<br/>", "<br />", "<BR>", "<Br/>", "<BR  />", "<br\t/>" })
        {
            string source = "上😀" + tag + "下文";
            var model = ReadingModel.Build(source, TextKind.Markdown);
            Equal("上😀\u2028下文", model.VisibleText, $"ReadingModel br break: {tag}");
            var preview = new MarkdownPreview(source, 16, 680);
            var paragraph = preview.Document.Blocks.OfType<Paragraph>().Single();
            Equal("上😀\u2028下文", string.Concat(Runs(paragraph.Inlines).Select(run => run.Text)),
                $"preview paragraph reuses U+2028: {tag}");
            Check(!paragraph.Inlines.OfType<LineBreak>().Any(),
                $"preview inserted a block-level LineBreak instead of U+2028: {tag}");
            Check(MarkdownSyntax.Tokens(source).Any(token =>
                token.Start == 3 && token.Length == tag.Length), $"editor highlights the br tag: {tag}");
        }

        foreach (string literal in new[]
                 {
                     "`a<br>b`", "``a`<br>b``", "``a`**b<br>c**`d``", "``a`*b<br>c*`d``",
                     "``a`~~b<br>c~~`d``", "\\<br>", "&lt;br&gt;", "<bravo>",
                     "<br onclick=\"x\">", "<br / >", "<br x>"
                 })
        {
            var preview = new MarkdownPreview(literal, 16, 680);
            Check(!Runs(preview.Document.Blocks.OfType<Paragraph>().Single().Inlines)
                .Any(run => run.Text.Contains(separator)), $"literal or code text was interpreted: {literal}");
        }

        var styled = new MarkdownPreview("**重点<br/>下一行** 与 [链接<br>文字](https://example.invalid)", 16, 680);
        var styledRuns = Runs(styled.Document.Blocks.OfType<Paragraph>().Single().Inlines).ToArray();
        var strong = styledRuns.Where(run => run.Text is "重点" or "下一行").ToArray();
        Equal(2, strong.Length, "strong text is split only at the paragraph break");
        Check(strong.All(run => run.FontWeight == FontWeights.Bold), "text on both sides of br keeps strong style");
        var link = styledRuns.Where(run => run.Text is "链接" or "文字").ToArray();
        Equal(2, link.Length, "link label is split only at the paragraph break");
        Check(link.All(run => run.TextDecorations.Count > 0), "text on both sides of br keeps link style");

        const string tableSource =
            "| 问题 | 参考范围 | 推荐项 |\n| --- | :---: | ---: |\n" +
            "| 计划口径 | A. 报单金额😀<br>B. 出库金额<br/>C. 销售数量 | 推荐A<BR />分析 |\n\n## 后续标题";
        var tablePreview = new MarkdownPreview(tableSource, 16, 680);
        var table = tablePreview.Document.Blocks.OfType<Table>().Single();
        Equal(2, table.RowGroups.Single().Rows.Count, "br does not create extra table rows");
        Equal(3, table.RowGroups.Single().Rows[1].Cells.Count, "br does not create extra table cells");
        var cells = table.RowGroups.Single().Rows[1].Cells;
        Equal("A. 报单金额😀\u2028B. 出库金额\u2028C. 销售数量",
            string.Concat(Runs(cells[1].Blocks.OfType<Paragraph>().Single().Inlines).Select(run => run.Text)),
            "table cell keeps both br breaks");
        Equal("推荐A\u2028分析",
            string.Concat(Runs(cells[2].Blocks.OfType<Paragraph>().Single().Inlines).Select(run => run.Text)),
            "second table cell keeps its br");

        var tableModel = ReadingModel.Build(tableSource, TextKind.Markdown);
        var tableReading = RenderedReading.Markdown(tableModel, tableSource, null, 16, 680);
        int separatorAt = tableModel.VisibleText.IndexOf(separator);
        Check(separatorAt >= 0, "table model contains the in-paragraph break");
        Equal(separatorAt, tableReading.OffsetAt(tableReading.PointerAt(separatorAt)),
            "decorated table mapping keeps the br offset");
        var followingHeading = tableModel.Headings.Last();
        Equal(followingHeading.SourceStart, tableModel.DisplayToSource(followingHeading.DisplayStart),
            "heading source mapping after a table br");
        Equal(followingHeading.DisplayStart, tableModel.SourceToDisplay(followingHeading.SourceStart),
            "heading display mapping after a table br");
        Equal(followingHeading.DisplayStart,
            tableReading.OffsetAt(tableReading.PointerAt(followingHeading.DisplayStart)),
            "decorated heading mapping after a table br");

        foreach (string fenced in new[]
                 {
                     "```html\nA<br>B\n```", "~~~\nA<BR/>B\n~~~", "    A<br>B"
                 })
        {
            var model = ReadingModel.Build(fenced, TextKind.Markdown);
            var preview = new MarkdownPreview(fenced, 16, 680);
            Check(!AllParagraphs(preview.Document.Blocks).SelectMany(paragraph => Runs(paragraph.Inlines))
                .Any(run => run.Text.Contains(separator)), "block code keeps br literal");
            if (fenced.StartsWith("    ", StringComparison.Ordinal))
                Equal("A<br>B", model.Blocks.Single(block => block.Kind == ReadingBlockKind.Code).Text,
                    "indented code strips only its four-column marker");
        }
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

    private static void TableSyntaxCompatibility(int dashes)
    {
        string marker = new('-', dashes);
        string source = $"before\n\n| A\\|B | C |\n| {marker} | :{marker}: |\n| x\\|y | `p|q` |\n\n# After";
        var preview = new MarkdownPreview(source, 16, 680);
        var tables = preview.Document.Blocks.OfType<Table>().ToArray();
        Equal(1, tables.Length, $"{dashes}-dash separator produces a real WPF Table");
        var table = tables.Single();
        Equal(2, table.Columns.Count, "escaped pipes do not create extra columns");
        var rows = table.RowGroups.Single().Rows;
        Equal(2, rows.Count, "header and body row");
        Equal("A|B", CellText(rows[0].Cells[0]), "escaped header pipe");
        Equal("x|y", CellText(rows[1].Cells[0]), "escaped body pipe");
        Equal("p|q", CellText(rows[1].Cells[1]), "code span pipe stays in its cell");
        Equal(3, preview.Document.Blocks.Count, "paragraph and heading remain outside the table");
        Equal("before", Text((Paragraph)preview.Document.Blocks.FirstBlock), "preceding paragraph");
        Equal("After", Text((Paragraph)preview.Document.Blocks.LastBlock), "following heading");
        Check(table.Tag is ReadingBlock { Kind: ReadingBlockKind.Table }, "table retains its reading block");
    }

    private static string CellText(TableCell cell) =>
        new TextRange(cell.ContentStart, cell.ContentEnd).Text.TrimEnd('\r', '\n');

    private static void IncompleteTableSyntax()
    {
        foreach (string source in new[]
        {
            "| A | B |", "| A | B |\n| -- | :", "| A | B |\n| : | --- |",
            "| A | B |\n| --x | --- |", "| A | B |\n| -- | -- | -- |"
        })
        {
            var preview = new MarkdownPreview(source, 16, 680);
            Equal(0, preview.Document.Blocks.OfType<Table>().Count(), "incomplete/invalid separators stay text");
            Check(new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text.Contains("A | B"),
                "invalid table keeps its content visible");
        }
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

    private static void Outline()
    {
        const string source = "# 一级\n\n正文\n\n## 二级\n\n### 三级";
        var model = ReadingModel.Build(source, TextKind.Markdown);
        var outline = MarkdownOutline.Build(source, model);
        Equal(3, outline.Count, "outline count");
        Equal(1, outline[0].Level, "outline H1 level");
        Equal(2, outline[1].Level, "outline H2 level");
        Equal(3, outline[2].Level, "outline H3 level");
        Equal("# 一级\n".Length, outline[0].SourceLength, "outline source range");
        Equal("二级", outline[1].Title, "outline visible title");
        Check(outline[2].DisplayStart > outline[1].DisplayStart, "outline display order");
        Check(MarkdownOutline.Build("正文", ReadingModel.Build("正文", TextKind.Markdown)).Count == 0,
            "plain markdown has no outline");
    }

    private static void Diff()
    {
        var diff = TextDiff.Build("a\nsame\nold", "a\nsame\nnew\nadd");
        Check(diff.Any(line => line.Kind == TextDiffLineKind.Equal && line.Text == "same"),
            "diff equal line");
        Check(diff.Count(line => line.Kind == TextDiffLineKind.Changed) == 2,
            "diff changed pair");
        Check(diff.Any(line => line.Kind == TextDiffLineKind.Added && line.Text == "add"),
            "diff added line");
        Check(diff.All(line => line.LeftLine >= 0 && line.RightLine >= 0), "diff line numbers");
        bool rejected = false;
        try { _ = TextDiff.Build(new string('x', TextDiff.MaximumCharacters + 1), ""); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "diff character budget");
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

    private static IEnumerable<Inline> AllInlines(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph)
                foreach (var inline in Flatten(paragraph.Inlines)) yield return inline;
            if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems)
                    foreach (var inline in AllInlines(item.Blocks)) yield return inline;
            if (block is Table table)
                foreach (var group in table.RowGroups)
                    foreach (var row in group.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var inline in AllInlines(cell.Blocks)) yield return inline;
        }
    }

    private static IEnumerable<Inline> Flatten(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
                foreach (var nested in Flatten(span.Inlines)) yield return nested;
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
