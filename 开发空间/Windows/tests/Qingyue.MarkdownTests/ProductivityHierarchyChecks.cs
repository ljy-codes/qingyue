using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Qingyue.Core;
using Qingyue.Windows;
using DocumentList = System.Windows.Documents.List;

internal static class ProductivityHierarchyChecks
{
    private const string Source = "- 父😀\r\n  - 子\r\n\r\n" +
        "    ## **标题😀**\r\n\r\n    > **引用** 与 `code`\r\n    > *次行*\r\n\r\n" +
        "    | **A** | B |\r\n    | :--- | ---: |\r\n    | 甲\\|乙 | `a|b` |\r\n\r\n" +
        "    **子段**\r\n\r\n  > *父引用*\r\n\r\n" +
        "  | 根 |\r\n  | --- |\r\n  | 值 |\r\n\r\n" +
        "  ### *父标题*\r\n\r\n  父段\r\n\r\n- 尾\r\n\r\n" +
        "| 外 |\r\n| --- |\r\n| 终 |\r\n\r\n# 外标题";

    internal static void ModelRanges()
    {
        var model = ReadingModel.Build(Source, TextKind.Markdown);
        var expected = new (ReadingBlockKind Kind, string Text, string Source)[]
        {
            (ReadingBlockKind.ListItem, "父😀", "- 父😀\r\n"),
            (ReadingBlockKind.ListItem, "子", "  - 子\r\n"),
            (ReadingBlockKind.Heading, "标题😀", "    ## **标题😀**\r\n"),
            (ReadingBlockKind.Quote, "引用 与 code\n次行", "    > **引用** 与 `code`\r\n    > *次行*\r\n"),
            (ReadingBlockKind.Table, "A\tB\n甲|乙\ta|b",
                "    | **A** | B |\r\n    | :--- | ---: |\r\n    | 甲\\|乙 | `a|b` |\r\n"),
            (ReadingBlockKind.Paragraph, "子段", "    **子段**\r\n"),
            (ReadingBlockKind.Quote, "父引用", "  > *父引用*\r\n"),
            (ReadingBlockKind.Table, "根\n值", "  | 根 |\r\n  | --- |\r\n  | 值 |\r\n"),
            (ReadingBlockKind.Heading, "父标题", "  ### *父标题*\r\n"),
            (ReadingBlockKind.Paragraph, "父段", "  父段\r\n"),
            (ReadingBlockKind.ListItem, "尾", "- 尾\r\n"),
            (ReadingBlockKind.Table, "外\n终", "| 外 |\r\n| --- |\r\n| 终 |\r\n"),
            (ReadingBlockKind.Heading, "外标题", "# 外标题")
        };
        Equal(expected.Length, model.Blocks.Count, "all container blocks remain distinct");
        for (int i = 0; i < expected.Length; i++)
        {
            var block = model.Blocks[i];
            Equal(expected[i].Kind, block.Kind, $"block {i} kind");
            Equal(expected[i].Text, block.Text, $"block {i} visible text");
            Equal(Source.IndexOf(expected[i].Source, StringComparison.Ordinal), block.SourceStart,
                $"block {i} original UTF16 start");
            Equal(expected[i].Source, Source.Substring(block.SourceStart, block.SourceLength),
                $"block {i} indentation and CRLF source range");
            Equal(i is >= 2 and <= 9, block.ListContinuation, $"block {i} list ownership");
        }
        Check(model.Headings.Select(h => h.Level).SequenceEqual([2, 3, 1]),
            "heading levels must be independent of owning list depth");
        Equal("父😀\n子\n标题😀\n引用 与 code\n次行\nA\tB\n甲|乙\ta|b\n子段\n父引用\n根\n值\n父标题\n父段\n尾\n外\n终\n外标题",
            model.VisibleText, "authoritative visible text");
    }

    internal static void PreviewContainers()
    {
        const string source = "- 父\n\n  > 引用\n\n  | A | B |\n  | --- | --- |\n  | 甲 | 乙 |\n\n- 尾";
        var document = new MarkdownPreview(source, 16, 680).Document;
        var root = document.Blocks.OfType<DocumentList>().First();
        Equal(1, document.Blocks.Count, "quote and table must not split the root list");
        Equal(2, root.ListItems.Count, "continuation blocks do not create markers");
        var parent = root.ListItems.FirstListItem!;
        Check(parent.Blocks.Select(b => b.GetType()).SequenceEqual([typeof(Paragraph), typeof(Paragraph), typeof(Table)]),
            "the actual ListItem must own a quote paragraph and a real Table");
        var quote = parent.Blocks.OfType<Paragraph>().Last();
        Check(quote.Tag is ReadingBlock { Kind: ReadingBlockKind.Quote }, "semantic quote tag");
        Equal("引用", Text(quote), "quote marker removed");
        var table = parent.Blocks.OfType<Table>().Single();
        Check(ReferenceEquals(parent, table.Parent), "table logical parent");
        Equal(2, table.Columns.Count, "nested table columns");
        Equal("甲", CellText(table, 1, 0), "nested table visible body");
    }

    internal static void AncestorsAndStyles()
    {
        var preview = new MarkdownPreview(Source, 16, 680);
        preview.Resources["PanelBrush"] = Brushes.LightGray;
        var root = preview.Document.Blocks.OfType<DocumentList>().First();
        Equal(3, preview.Document.Blocks.Count, "only list, outside table and outside heading at root");
        Equal(2, root.ListItems.Count, "tail returns to the same root list");
        var parent = root.ListItems.FirstListItem!;
        var child = parent.Blocks.OfType<DocumentList>().Single().ListItems.FirstListItem!;
        var childHeading = Tagged(child, ReadingBlockKind.Heading);
        Equal("标题😀", Text(childHeading), "deep heading hides container and ATX syntax");
        Equal(25.6, childHeading.FontSize, "H2 size independent of list depth");
        Check(childHeading.Inlines.OfType<Run>().Any(r => r.Text == "标题😀" && r.FontWeight == FontWeights.Bold),
            "deep heading strong inline style");
        var quote = Tagged(child, ReadingBlockKind.Quote);
        Equal("引用 与 code\n次行", Text(quote), "all nested quote prefixes removed");
        Check(quote.Inlines.OfType<Run>().Any(r => r.Text == "引用" && r.FontWeight == FontWeights.Bold) &&
            quote.Inlines.OfType<Run>().Any(r => r.Text == "code" && r.Background is not null) &&
            quote.Inlines.OfType<Run>().Any(r => r.Text == "次行" && r.FontStyle == FontStyles.Italic),
            "nested quote preserves strong, code and emphasis styles");
        var childTable = child.Blocks.OfType<Table>().Single();
        var header = (Paragraph)childTable.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock;
        Check(header.Inlines.OfType<Run>().Any(r => r.Text == "A" && r.FontWeight == FontWeights.Bold),
            "table source rows preserve strong styling");
        Equal("甲|乙", CellText(childTable, 1, 0), "escaped nested table pipe");
        Equal("a|b", CellText(childTable, 1, 1), "nested code pipe remains one cell");
        var codeCell = (Paragraph)childTable.RowGroups[0].Rows[1].Cells[1].Blocks.FirstBlock;
        Check(codeCell.Inlines.OfType<Run>().Single().Background is not null, "table cell inline code style");
        Equal(TextAlignment.Right, codeCell.TextAlignment, "nested table alignment");
        Equal("子段", Text(Tagged(child, ReadingBlockKind.Paragraph)), "child paragraph stays in child");
        Equal("父引用", Text(Tagged(parent, ReadingBlockKind.Quote)), "quote returns to ancestor");
        Equal("父标题", Text(Tagged(parent, ReadingBlockKind.Heading)), "heading returns to ancestor");
        Equal("父段", Text(Tagged(parent, ReadingBlockKind.Paragraph)), "paragraph follows ancestor blocks");
        Equal("根", CellText(parent.Blocks.OfType<Table>().Single(), 0, 0), "table returns to ancestor");
        preview.UpdatePresentation(20, 520);
        Equal(32d, childHeading.FontSize, "presentation update keeps H2 semantics");
        Check(ReferenceEquals(childHeading.Parent, child), "presentation update retains actual ownership");
    }

    internal static void ClosingAndRelativeIndent()
    {
        foreach (string indent in new[] { "  ", "    ", "\t" })
        {
            string source = "- 父\n\n" + indent + "## **内标题**\n\n" +
                indent + "> *内引用*\n\n" +
                indent + "| A | B |\n" + indent + "| --- | --- |\n" + indent + "| x | y |\n" +
                "| 外 | 行 |\n| --- | --- |\n| 终 | 值 |\n\n- 新";
            var model = ReadingModel.Build(source, TextKind.Markdown);
            var tables = model.Blocks.Where(b => b.Kind == ReadingBlockKind.Table).ToArray();
            Equal(2, tables.Length, "dedented table closes container without needing a blank");
            Equal("A\tB\nx\ty", tables[0].Text, "nested table must not consume outside rows");
            Equal("外\t行\n终\t值", tables[1].Text, "outside table keeps its header");
            var document = new MarkdownPreview(source, 16, 680).Document;
            var parent = document.Blocks.OfType<DocumentList>().First().ListItems.FirstListItem!;
            Equal("内标题", Text(Tagged(parent, ReadingBlockKind.Heading)), "relative heading indentation");
            Equal("内引用", Text(Tagged(parent, ReadingBlockKind.Quote)), "relative quote indentation");
            Equal(1, parent.Blocks.OfType<Table>().Count(), "real nested table for spaces and tab stops");
            Equal(1, document.Blocks.OfType<Table>().Count(), "outside table belongs to document");
            Equal(2, document.Blocks.OfType<DocumentList>().Count(), "outside table terminates the previous list");
        }
        const string setext = "- 父\n  - 子\n\n    **子标题**\n    ===\n\n  父标题\n  ---\n\n段外";
        var setextModel = ReadingModel.Build(setext, TextKind.Markdown);
        Check(setextModel.Headings.Select(h => h.Level).SequenceEqual([1, 2]), "nested setext heading semantics");
        var setextParent = new MarkdownPreview(setext, 16, 680).Document.Blocks.OfType<DocumentList>()
            .Single().ListItems.FirstListItem!;
        Equal("父标题", Text(Tagged(setextParent, ReadingBlockKind.Heading)), "setext returns to ancestor");
    }

    internal static void CodeSemantics()
    {
        const string source = "- 父\r\n  - 子\r\n\r\n    ```md\r\n      # literal **code**\r\n" +
            "    > quote\r\n    | a | b |\r\n    ```\r\n\r\n" +
            "  ## 父标题\r\n\r\n  ```text\r\n    parent indent\r\n  ```\r\n\r\n" +
            "```text\r\n    outside indent\r\n```\r\n\r\n- 新";
        var model = ReadingModel.Build(source, TextKind.Markdown);
        var codes = model.Blocks.Where(b => b.Kind == ReadingBlockKind.Code).ToArray();
        Check(codes.Select(b => b.Text).SequenceEqual([
            "  # literal **code**\n> quote\n| a | b |", "  parent indent", "    outside indent"]),
            "strip only fence indentation; code syntax and remaining indentation stay literal");
        Check(codes.Select(b => b.CodeLanguage).SequenceEqual(["md", "text", "text"]), "code languages retained");
        var document = new MarkdownPreview(source, 16, 680).Document;
        var parent = document.Blocks.OfType<DocumentList>().First().ListItems.FirstListItem!;
        var child = parent.Blocks.OfType<DocumentList>().Single().ListItems.FirstListItem!;
        Equal(codes[0].Text, Text(Tagged(child, ReadingBlockKind.Code)), "child fence stays in its item");
        Equal(codes[1].Text, Text(Tagged(parent, ReadingBlockKind.Code)), "fence after ancestor heading stays in ancestor");
        Equal(codes[2].Text, Text(document.Blocks.OfType<Paragraph>().Single(p =>
            p.Tag is ReadingBlock { Kind: ReadingBlockKind.Code })), "outside code indentation preserved");
    }

    internal static void Navigation()
    {
        var model = ReadingModel.Build(Source, TextKind.Markdown);
        var rendered = RenderedReading.Markdown(model, Source, null, 16, 680);
        Check(rendered.Document.Blocks.OfType<DocumentList>().First().ListItems.FirstListItem!
            .Blocks.OfType<DocumentList>().Single().ListItems.FirstListItem!.Blocks.OfType<Table>().Any(),
            "reader navigation must use the real nested preview");
        int previous = -1;
        for (int i = 0; i <= Source.Length; i++)
        {
            int offset = model.SourceToDisplay(i);
            Check(offset >= previous && Boundary(model.VisibleText, offset), "source mapping monotone and UTF16 safe");
            previous = offset;
        }
        previous = -1;
        for (int i = 0; i <= model.VisibleText.Length; i++)
        {
            int offset = model.DisplayToSource(i);
            Check(offset >= previous && Boundary(Source, offset), "display mapping monotone and UTF16 safe");
            previous = offset;
            if (Boundary(model.VisibleText, i))
                Equal(i, rendered.OffsetAt(rendered.PointerAt(i)), $"decorated nested block pointer roundtrip at {i}");
        }
        foreach (var heading in model.Headings)
        {
            var paragraph = rendered.PointerAt(heading.DisplayStart).Paragraph!;
            Check(paragraph.Tag is ReadingBlock block && block.SourceStart == heading.SourceStart &&
                block.Level == heading.Level, "outline navigation retains source and heading level");
        }
    }

#if QINGYUE_HIERARCHY_EXPORT_CHECKS
    // The normal Markdown harness does not link clipboard/export production files.
    // An isolated source-linked build can include those two unchanged files and
    // define this symbol, without adding an application dependency to the harness.
    internal static void ExportedHierarchy()
    {
        string source = Source.Replace("    **子段**\r\n",
            "    ```md\r\n      **literal**\r\n    ```\r\n\r\n    **子段**\r\n", StringComparison.Ordinal);
        var preview = new MarkdownPreview(source, 16, 680);
        var root = preview.Document.Blocks.OfType<DocumentList>().First();
        var child = root.ListItems.FirstListItem!.Blocks.OfType<DocumentList>().Single().ListItems.FirstListItem!;
        var heading = Tagged(child, ReadingBlockKind.Heading);
        preview.Selection.Select(heading.ContentStart, heading.ContentEnd);
        int selectionStart = preview.Document.ContentStart.GetOffsetToPosition(preview.Selection.Start);
        int selectionEnd = preview.Document.ContentStart.GetOffsetToPosition(preview.Selection.End);

        var exported = ReadingHtmlExport.CreateDocument(preview, "层次");
        Check(exported is not null, "real HTML export succeeds");
        var html = System.Xml.Linq.XDocument.Parse(exported!.Html);
        AssertHtmlHierarchy(html.Root!.Element("body")!.Element("div")!);
        var copied = ReadingClipboard.CreateFullDocumentData(preview);
        Check(copied is not null && copied.DegradationReason is null, "real clipboard formats succeed without degradation");
        string cfHtml = (string)copied!.Data.GetData(DataFormats.Html, false)!;
        const string startMarker = "<!--StartFragment-->", endMarker = "<!--EndFragment-->";
        int start = cfHtml.IndexOf(startMarker, StringComparison.Ordinal) + startMarker.Length;
        int end = cfHtml.IndexOf(endMarker, StringComparison.Ordinal);
        Check(start >= startMarker.Length && end > start, "clipboard contains a real CF_HTML fragment");
        AssertHtmlHierarchy(System.Xml.Linq.XElement.Parse(cfHtml[start..end]));
        Equal(selectionStart, preview.Document.ContentStart.GetOffsetToPosition(preview.Selection.Start),
            "full clipboard/export preserves selection start");
        Equal(selectionEnd, preview.Document.ContentStart.GetOffsetToPosition(preview.Selection.End),
            "full clipboard/export preserves selection end");
    }

    private static void AssertHtmlHierarchy(System.Xml.Linq.XElement fragment)
    {
        var root = fragment.Elements("ul").Single();
        Equal(2, root.Elements("li").Count(), "exported root list items");
        var parent = root.Elements("li").First();
        var child = parent.Element("ul")!.Elements("li").Single();
        Equal("标题😀", child.Element("h2")!.Value, "exported child heading stays inside child li");
        Check(child.Element("h2")!.Descendants("strong").Any(), "exported heading strong markup");
        // Existing clipboard HTML exports Run emphasis, but not inline-code tags.
        Check(child.Element("blockquote")!.Descendants("strong").Any(e => e.Value == "引用") &&
            child.Element("blockquote")!.Descendants("em").Any(e => e.Value == "次行"),
            "exported child quote retains inline markup");
        Equal("引用 与 code\n次行", child.Element("blockquote")!.Value, "exported inline code text retained");
        Equal("甲|乙", child.Element("table")!.Elements("tr").Last().Elements("td").First().Value,
            "exported actual nested Table keeps escaped cell text");
        Equal("  **literal**", child.Element("pre")!.Element("code")!.Value,
            "exported fenced code retains literal syntax and indentation");
        Equal("父引用", parent.Element("blockquote")!.Value, "exported quote returns to parent li");
        Equal("父标题", parent.Element("h2")!.Value, "exported ancestor heading uses the macOS h2 contract");
        Equal("根", parent.Element("table")!.Elements("tr").First().Elements("td").Single().Value,
            "exported ancestor table");
        Equal(1, fragment.Elements("table").Count(), "outside table stays outside ul");
        Equal("外标题", fragment.Element("h2")!.Value, "outside heading uses the macOS h2 contract");
    }
#endif

    private static Paragraph Tagged(ListItem item, ReadingBlockKind kind) =>
        item.Blocks.OfType<Paragraph>().Single(p => p.Tag is ReadingBlock block && block.Kind == kind);
    private static string Text(Paragraph paragraph) =>
        new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.TrimEnd('\r', '\n');
    private static string CellText(Table table, int row, int column) =>
        Text((Paragraph)table.RowGroups[0].Rows[row].Cells[column].Blocks.FirstBlock);
    private static bool Boundary(string text, int offset) =>
        !(offset > 0 && offset < text.Length && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]));
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Equal<T>(T expected, T actual, string message) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");
}
