using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Qingyue.Core;
using Qingyue.Windows;
using DocumentList = System.Windows.Documents.List;

internal static class NestedListChecks
{
    internal static void Model()
    {
        foreach (string indent in new[] { "  ", "    ", "\t" })
        {
            string[] lines = ["- 父😀", indent + "- **子**", indent + indent + "- 孙",
                indent + "- 同层", "- 同根"];
            string source = string.Join("\r\n", lines) + "\r\n";
            var model = ReadingModel.Build(source, TextKind.Markdown);
            Check(model.Blocks.Select(b => b.Kind).All(k => k == ReadingBlockKind.ListItem),
                "indented markers must remain list items");
            Check(model.Blocks.Select(b => b.Level).SequenceEqual([0, 1, 2, 1, 0]),
                "two spaces, four spaces and tabs must each form zero-based parent/child levels");
            int start = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                var block = model.Blocks[i];
                Check(block.SourceStart == start && block.SourceLength == lines[i].Length + 2,
                    "list range must retain indentation and CRLF");
                Check(source.Substring(block.SourceStart, block.SourceLength) == lines[i] + "\r\n",
                    "source range must address the original string");
                start += lines[i].Length + 2;
            }
            Check(model.VisibleText == "父😀\n子\n孙\n同层\n同根", "visible list text must exclude markers");
        }
    }

    internal static void Preview()
    {
        foreach (string indent in new[] { "  ", "    ", "\t" })
        {
            var preview = new MarkdownPreview($"- 父\n{indent}- 子\n{indent}{indent}- 孙\n- 同根", 16, 680);
            var root = preview.Document.Blocks.OfType<DocumentList>().Single();
            Check(root.MarkerStyle == TextMarkerStyle.Disc && root.ListItems.Count == 2, "root disc list");
            var child = root.ListItems.FirstListItem!.Blocks.OfType<DocumentList>().SingleOrDefault();
            Check(child is { MarkerStyle: TextMarkerStyle.Circle }, "child must be a real nested circle list");
            var grandchild = child!.ListItems.FirstListItem!.Blocks.OfType<DocumentList>().SingleOrDefault();
            Check(grandchild is { MarkerStyle: TextMarkerStyle.Box }, "grandchild must use the observed solid box marker");
            Check(preview.Document.Blocks.Count == 1, "children must not leak into document root");
            preview.UpdatePresentation(20, 520);
            Check(child.MarkerStyle == TextMarkerStyle.Circle &&
                grandchild!.MarkerStyle == TextMarkerStyle.Box, "typography updates must retain solid nested markers");
        }
    }

    internal const string Mixed = "- 父 **重点**\r\n  3. **有序**\r\n     - [x] `任务`\r\n" +
        "     **有序续段**\r\n\r\n  父级 [续段](https://example.test)\r\n  - 兄弟子项\r\n- 尾项";

    internal static void Continuations()
    {
        var model = ReadingModel.Build(Mixed, TextKind.Markdown);
        Check(model.Blocks.Select(b => b.Level).SequenceEqual([0, 1, 2, 1, 0, 1, 0]),
            "continuation must belong to the matching ancestor, not the last child");
        Check(model.Blocks.Select(b => b.ListContinuation).SequenceEqual([false, false, false, true, true, false, false]),
            "continuations must be explicit without creating list markers");
        var document = new MarkdownPreview(Mixed, 16, 680).Document;
        var root = document.Blocks.OfType<DocumentList>().Single();
        var parent = root.ListItems.FirstListItem!;
        Check(root.ListItems.Count == 2, "continuation must not create a new root marker");
        var ordered = parent.Blocks.OfType<DocumentList>().Single(l => l.MarkerStyle == TextMarkerStyle.Decimal);
        Check(ordered.StartIndex == 3 && ordered.ListItems.Count == 1, "ordered child numbering");
        var orderedItem = ordered.ListItems.FirstListItem!;
        var task = orderedItem.Blocks.OfType<DocumentList>().Single();
        Check(task.MarkerStyle == TextMarkerStyle.None && ParagraphText(task.ListItems.FirstListItem!.Blocks.FirstBlock) == "☑ 任务",
            "task checkbox and nested body");
        var orderedContinuation = orderedItem.Blocks.OfType<Paragraph>().Last();
        Check(ParagraphText(orderedContinuation) == "有序续段" &&
            orderedContinuation.Inlines.OfType<Run>().Single().FontWeight == FontWeights.Bold,
            "ordered continuation must keep inline source styling");
        var parentContinuation = parent.Blocks.OfType<Paragraph>().Last();
        Check(ParagraphText(parentContinuation) == "父级 续段" &&
            parentContinuation.Inlines.OfType<Hyperlink>().Any(link =>
                link.NavigateUri?.OriginalString == "https://example.test" &&
                link.Inlines.OfType<Run>().Any(run => run.Text == "续段" && run.TextDecorations.Count > 0)),
            "parent continuation must keep an interactive link after dedenting");
        Check(parent.Blocks.OfType<DocumentList>().Count() == 2, "mixed child types stay inside the same parent");
    }

    internal static void Fences()
    {
        const string source = "- 父\n    ```md\n    - 假子项\n      1. 假孙项\n    ```\n    - 真子项\n" +
            "\n~~~md\n- 根级假项\n~~~\n- 尾项";
        var model = ReadingModel.Build(source, TextKind.Markdown);
        Check(model.Blocks.Count(b => b.Kind == ReadingBlockKind.ListItem) == 3, "fenced markers are literal");
        var codes = model.Blocks.Where(b => b.Kind == ReadingBlockKind.Code).ToArray();
        Check(codes.Length == 2 && codes[0].Text == "- 假子项\n  1. 假孙项" && codes[1].Text == "- 根级假项",
            "nested fence must strip only its parent content indentation");
        Check(source.Substring(codes[0].SourceStart, codes[0].SourceLength).StartsWith("    ```md\n"),
            "nested code source range retains original indentation");
        var document = new MarkdownPreview(source, 16, 680).Document;
        var root = document.Blocks.OfType<DocumentList>().First();
        Check(root.ListItems.FirstListItem!.Blocks.OfType<Paragraph>().Any(p =>
            p.Tag is ReadingBlock { Kind: ReadingBlockKind.Code } && ParagraphText(p) == codes[0].Text),
            "nested code belongs to the parent list item");
    }

    internal static void Mapping()
    {
        string source = Mixed + "\r\n  - 图 ![标签😀](https://example.test/a.png)";
        var model = ReadingModel.Build(source, TextKind.Markdown);
        var rendered = RenderedReading.Markdown(model, source, null, 16, 680);
        Check(rendered.Document.Blocks.OfType<DocumentList>().First().ListItems.FirstListItem!
            .Blocks.OfType<DocumentList>().Any(), "reader must reuse the nested preview");
        int previous = -1;
        for (int i = 0; i <= source.Length; i++)
        {
            int display = model.SourceToDisplay(i);
            Check(display >= previous && Boundary(model.VisibleText, display), "source mapping monotone and Unicode safe");
            previous = display;
        }
        previous = -1;
        for (int i = 0; i <= model.VisibleText.Length; i++)
        {
            int offset = model.DisplayToSource(i);
            Check(offset >= previous && Boundary(source, offset), "display mapping monotone and Unicode safe");
            previous = offset;
            if (Boundary(model.VisibleText, i))
                Check(rendered.OffsetAt(rendered.PointerAt(i)) == i, "nested decorated pointer roundtrip at " + i);
        }
        var tagged = Paragraphs(rendered.Document.Blocks).Select(p => p.Tag).OfType<ReadingBlock>().ToArray();
        Check(tagged.Select(b => (b.SourceStart, b.SourceLength)).SequenceEqual(
            model.Blocks.Select(b => (b.SourceStart, b.SourceLength))), "image projection must retain original block offsets");
    }

    internal static void Tokens()
    {
        foreach (string prefix in new[] { "    - ", "\t- ", "        12. " })
        {
            string source = prefix + "**重点**";
            var tokens = MarkdownSyntax.Tokens(source).ToArray();
            Check(tokens.Any(t => t.Start == 0 && t.Length == prefix.Length && t.ResourceKey == "SyntaxKeyBrush"),
                "nested marker token retains indentation");
            Check(tokens.Any(t => t.Start == prefix.Length && t.Length == 6), "inline token retains source offset");
        }
    }

    internal static void AncestorContinuations()
    {
        const string source = "- 父\n  - 子\n    **子续段**\n  *父续段*\n\n段外\n\n- 新根";
        var model = ReadingModel.Build(source, TextKind.Markdown);
        Check(model.Blocks.Select(b => b.Level).SequenceEqual([0, 1, 1, 0, 0, 0]),
            "consecutive dedented paragraphs must return to their actual ancestors");
        Check(model.Blocks[2].ListContinuation && model.Blocks[3].ListContinuation &&
            !model.Blocks[4].ListContinuation, "a blank followed by unindented prose must end the list");
        var document = new MarkdownPreview(source, 16, 680).Document;
        var root = document.Blocks.OfType<DocumentList>().First();
        var parent = root.ListItems.FirstListItem!;
        Check(ParagraphText(parent.Blocks.OfType<Paragraph>().Last()) == "父续段", "parent continuation container");
        var child = parent.Blocks.OfType<DocumentList>().Single().ListItems.FirstListItem!;
        Check(ParagraphText(child.Blocks.OfType<Paragraph>().Last()) == "子续段", "child continuation container");
    }

    internal static void FenceIsolation()
    {
        const string source = "- 父\n    ```md\n  [fake]: https://example.test\n" +
            "    ![原样](https://example.test/code.png)\n    ```\n\n[未定义][fake]";
        var model = ReadingModel.Build(source, TextKind.Markdown);
        Check(!model.ReferenceDefinitions.ContainsKey("fake") &&
            model.Blocks.Last().Text == "[未定义][fake]", "nested code definitions must not escape the fence");
        var code = model.Blocks.Single(b => b.Kind == ReadingBlockKind.Code);
        var preview = new MarkdownPreview(source, 16, 680);
        var paragraph = Paragraphs(preview.Document.Blocks).Single(p =>
            p.Tag is ReadingBlock { Kind: ReadingBlockKind.Code });
        Check(ParagraphText(paragraph) == code.Text &&
            code.Text.Contains("![原样](https://example.test/code.png)"), "nested code image syntax must remain literal");
    }

    internal static void MarkerGrammar()
    {
        var cases = new (string Source, int[] Levels)[]
        {
            ("- 父\n  - 子\n    - 孙\n  父续段\n- 尾", [0, 1, 2, 0]),
            ("- 父\n    - 子\n        - 孙\n    父续段\n- 尾", [0, 1, 2, 0]),
            ("- 父\n\t- 子\n\t\t- 孙\n\t父续段\n- 尾", [0, 1, 2, 0]),
            ("10. 父\n    - [ ] 子\n      4) **孙**\n         **孙续段**\n" +
                "      子续段\n\n    父续段\n11. 尾", [0, 1, 2, 0]),
            ("- [x] 父\n  10. 子\n      + 孙\n      子续段\n  父续段\n- 尾", [0, 1, 2, 0]),
            ("- 父\n  - 子\n    ```md\n    - 假孙项\n    ```\n    - 真孙项\n- 尾", [0, 1, 2, 0]),
            ("    - 独立代码\n\n\t10. 独立代码\n\n- 真列表", [0])
        };
        foreach (var (source, levels) in cases)
        {
            var model = ReadingModel.Build(source, TextKind.Markdown);
            var items = model.Blocks.Where(b => b.Kind == ReadingBlockKind.ListItem).ToArray();
            var markers = MarkdownEditing.ListMarkers(source);
            Check(items.Select(b => b.Level).SequenceEqual(levels), "reader levels: " + source);
            Check(markers.Select(m => m.Level).SequenceEqual(levels), "writing marker levels: " + source);
            Check(items.Select(b => (b.SourceStart, b.Level)).SequenceEqual(
                markers.Select(m => (m.Start, m.Level))), "reading and writing source positions: " + source);
            foreach (var item in items)
                Check(MarkdownSyntax.ListPattern.IsMatch(source.Substring(item.SourceStart, item.SourceLength)),
                    "preview must consume the same source marker");
        }
        const string ordered = "10. 父\n    - [ ] 子\n      4) **孙**\n         **孙续段**\n" +
            "      子续段\n\n    父续段\n11. 尾";
        var blocks = ReadingModel.Build(ordered, TextKind.Markdown).Blocks;
        Check(blocks.Select(b => b.Level).SequenceEqual([0, 1, 2, 2, 1, 0, 0]) &&
            blocks.Select(b => b.ListContinuation).SequenceEqual([false, false, false, true, true, true, false]),
            "two-digit ordered parents must own four-space continuations");
        var root = new MarkdownPreview(ordered, 16, 680).Document.Blocks.OfType<DocumentList>().Single();
        Check(root.MarkerStyle == TextMarkerStyle.Decimal && root.StartIndex == 10 && root.ListItems.Count == 2,
            "two-digit ordered parent numbering");
        var parent = root.ListItems.FirstListItem!;
        var child = parent.Blocks.OfType<DocumentList>().Single().ListItems.FirstListItem!;
        Check(ParagraphText(parent.Blocks.OfType<Paragraph>().Last()) == "父续段" &&
            ParagraphText(child.Blocks.OfType<Paragraph>().Last()) == "子续段",
            "ordered and task continuations must remain in their parent items");
    }

    internal static void CompareMarkerRendering(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (int fontSize in new[] { 16, 24 })
        {
            var grid = new Grid { Width = 720, Height = 330, Background = Brushes.White };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            foreach (var (style, column) in new[] { (TextMarkerStyle.Square, 0), (TextMarkerStyle.Box, 1) })
            {
                var panel = new DockPanel { Margin = new Thickness(10) };
                var label = new TextBlock
                {
                    Text = $"{style} | Microsoft YaHei UI {fontSize}",
                    Foreground = Brushes.Black, FontSize = 16, Margin = new Thickness(0, 0, 0, 10)
                };
                DockPanel.SetDock(label, Dock.Top);
                panel.Children.Add(label);
                var preview = new MarkdownPreview("- 父 Parent\n  - 子 Child\n    - 孙 Third", fontSize, 330);
                preview.Resources["TextBrush"] = Brushes.Black;
                preview.Resources["EditorBrush"] = Brushes.White;
                var grandchild = preview.Document.Blocks.OfType<DocumentList>().Single()
                    .ListItems.FirstListItem!.Blocks.OfType<DocumentList>().Single()
                    .ListItems.FirstListItem!.Blocks.OfType<DocumentList>().Single();
                grandchild.MarkerStyle = style;
                panel.Children.Add(preview);
                Grid.SetColumn(panel, column);
                grid.Children.Add(panel);
            }
            grid.Measure(new Size(720, 330));
            grid.Arrange(new Rect(0, 0, 720, 330));
            grid.UpdateLayout();
            var bitmap = new RenderTargetBitmap(720, 330, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(grid);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(directory, $"square-box-{fontSize}.png");
            using var output = File.Create(path);
            encoder.Save(output);
            Console.WriteLine("Rendered marker comparison: " + path);
        }
    }

    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph) yield return paragraph;
            if (block is DocumentList list)
                foreach (var item in list.ListItems)
                    foreach (var child in Paragraphs(item.Blocks)) yield return child;
        }
    }

    private static string ParagraphText(Block block) =>
        string.Concat(Flatten(((Paragraph)block).Inlines).Select(run => run.Text));

    private static IEnumerable<Run> Flatten(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run) yield return run;
            if (inline is Span span)
                foreach (var nested in Flatten(span.Inlines)) yield return nested;
        }
    }

    private static bool Boundary(string text, int offset) => offset <= 0 || offset >= text.Length ||
        !char.IsHighSurrogate(text[offset - 1]) || !char.IsLowSurrogate(text[offset]);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
