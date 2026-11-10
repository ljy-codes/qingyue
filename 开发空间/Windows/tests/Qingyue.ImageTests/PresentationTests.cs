using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Qingyue.Core;
using Qingyue.Windows;

internal static partial class Program
{
    private static void PresentationChecks()
    {
        Test("presentation synchronizes existing heading code table and image caption styles", PresentationStyles);
        Test("split presentation keeps the document selection scroll and decoded image", SplitPresentation);
        Test("full and single Run presentation keep selection scroll and search mapping", ReadingPresentation);
        Test("presentation settings remain active for later text updates", PresentationThenText);
    }

    private static string PresentationSource => "# 一级\n\n## 二级\n\n### 三级\n\n#### 四级\n\n##### 五级\n\n###### 六级\n\n" +
        "```csharp\ncode sample\n```\n\n| 序号 | 说明 |\n| --- | --- |\n| 1 | 内容 |\n\n" +
        "- **列表起点** 后文\n\n图前 ![测试图注](" + DataUri + ") 图后\n\n" +
        "```mermaid\nsequenceDiagram\nA->>B: /api/tasks/approve\n```\n\n" +
        string.Concat(Enumerable.Range(0, 100).Select(i => $"段落{i} " + new string('文', 60) + "\n\n"));

    private static void Present(MarkdownPreview preview, double fontSize, double width)
        => preview.UpdatePresentation(fontSize, width);

    private static void Present(RenderedReading reading, RichTextBox host, double fontSize, double width)
        => reading.UpdatePresentation(host, fontSize, width);

    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph) yield return paragraph;
            if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems)
                    foreach (var child in Paragraphs(item.Blocks)) yield return child;
            if (block is Table table)
                foreach (var group in table.RowGroups)
                    foreach (var row in group.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var child in Paragraphs(cell.Blocks)) yield return child;
        }
    }

    private static void PresentationStyles()
    {
        var preview = new MarkdownPreview(PresentationSource, 16, 680);
        var document = preview.Document;
        var blocks = document.Blocks.ToArray();
        var paragraphs = Paragraphs(document.Blocks).ToArray();
        var runs = paragraphs.SelectMany(p => p.Inlines.OfType<Run>()).ToArray();
        var listParagraph = paragraphs.Single(p => p.Tag is ReadingBlock { Kind: ReadingBlockKind.ListItem });
        var listMargin = listParagraph.Margin;
        var caption = runs.Single(r => r.Text == "[图片：测试图注]");
        foreach (double fontSize in new[] { 20d, 12, 32, 16 })
        {
            Present(preview, fontSize, 680);
            foreach (var heading in paragraphs.Where(p => p.Tag is ReadingBlock { Kind: ReadingBlockKind.Heading }))
            {
                int level = ((ReadingBlock)heading.Tag).Level;
                double factor = level switch { 1 => 2, 2 => 1.6, 3 => 1.35, 4 => 1.2, 5 => 1.1, _ => 1 };
                Check(Math.Abs(heading.FontSize - fontSize * factor) < .001,
                    $"字号 {fontSize} 时 H{level} 仍为 {heading.FontSize}，应为 {fontSize * factor}");
                Check(Math.Abs(heading.LineHeight - heading.FontSize * 1.4) < .001, "标题行高未跟随字号");
            }
            var code = paragraphs.First(p => p.Tag is ReadingBlock { Kind: ReadingBlockKind.Code });
            Check(Math.Abs(code.FontSize - fontSize * .9) < .001 &&
                Math.Abs(code.LineHeight - fontSize * 1.6) < .001, "代码字号或行高没有更新");
            Check(Math.Abs(document.Blocks.OfType<Table>().Single().FontSize - fontSize * .9) < .001,
                "表格字号没有更新");
            Check(Math.Abs(caption.FontSize - Math.Max(11, fontSize * .8)) < .001, "图注字号或小字号下限错误");
            var diagram = document.Blocks.OfType<BlockUIContainer>().Single().Child;
            Check(Math.Abs(((FrameworkElement)diagram).LayoutTransform.Value.M11 - fontSize / 16) < .001,
                "Mermaid 没有同步缩放文字和图形几何");
            Check(listParagraph.Margin == listMargin, "富列表重复应用了普通段落缩进");
            Check(ReferenceEquals(document, preview.Document) && document.Blocks.SequenceEqual(blocks) &&
                paragraphs.SelectMany(p => p.Inlines.OfType<Run>()).SequenceEqual(runs), "呈现设置重建了文档或 Run");
        }
    }

    private static void AssertPositionPreserved(RichTextBox host, Action update, string context)
    {
        var document = host.Document;
        var paragraph = Paragraphs(document.Blocks).First(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("段落40"));
        host.Selection.Select(paragraph.ContentStart, paragraph.ContentEnd);
        string selected = host.Selection.Text;
        var start = host.Selection.Start;
        var end = host.Selection.End;
        host.ScrollToVerticalOffset(840);
        host.UpdateLayout();
        double offset = host.VerticalOffset;
        Check(offset > 100, context + "：测试没有形成有效滚动位置");
        update();
        host.UpdateLayout();
        host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
        Check(ReferenceEquals(document, host.Document), context + "：文档被替换");
        Check(host.Selection.Text == selected && host.Selection.Start.CompareTo(start) == 0 &&
            host.Selection.End.CompareTo(end) == 0, context + "：选区丢失或移动");
        Check(Math.Abs(host.VerticalOffset - offset) < 2,
            $"{context}：滚动位置丢失，{offset} -> {host.VerticalOffset}");
    }

    private static void SplitPresentation()
    {
        var preview = new MarkdownPreview(PresentationSource, 16, 680);
        Layout(preview, 1000);
        var image = AssertLoaded(preview.Document, "presentation");
        var bitmap = image.Source;
        foreach (var setting in new[] { (20d, 420d), (12d, 800d), (16d, 680d), (16d, 680d) })
        {
            AssertPositionPreserved(preview, () => Present(preview, setting.Item1, setting.Item2), "分屏呈现");
            Check(Math.Abs(preview.Document.PagePadding.Left - Math.Max(24, (1000 - setting.Item2) / 2)) < 1,
                "分屏阅读宽度没有更新");
            Check(ReferenceEquals(image.Source, bitmap), "呈现设置重新解码了图片");
        }
    }

    private static void ReadingPresentation()
    {
        string source = PresentationSource;
        var model = ReadingModel.Build(source, TextKind.Markdown);
        foreach (bool rich in new[] { true, false })
        {
            var reading = rich ? RenderedReading.Markdown(model, source, null, 16, 680) :
                new RenderedReading(model);
            var host = new RichTextBox
            {
                Document = reading.Document, BorderThickness = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            reading.UpdateLayout(host, 680);
            Layout(host, 1000);
            foreach (var setting in new[] { (20d, 420d), (12d, 800d), (16d, 680d) })
            {
                AssertPositionPreserved(host, () => Present(reading, host, setting.Item1, setting.Item2), rich ? "完整富阅读" : "单 Run 阅读");
                var heading = reading.Document.Blocks.OfType<Paragraph>().First();
                Check(Math.Abs(heading.FontSize - setting.Item1 * 2) < .001, "完整阅读标题字号没有同步");
                if (!rich)
                    Check(reading.Document.Blocks.OfType<Paragraph>().All(p => p.Inlines.Count == 1 && p.Inlines.FirstInline is Run),
                        "呈现更新破坏了单 Run 阅读约束");
                foreach (string token in new[] { "一级", "列表起点", "[图片：测试图注]", "图后", "/api/tasks/approve", "段落99" })
                {
                    int offset = model.VisibleText.IndexOf(token, StringComparison.Ordinal);
                    var start = reading.PointerAt(offset);
                    Check(new TextRange(start, reading.PointerAt(offset + token.Length)).Text == token &&
                        reading.OffsetAt(start) == offset, "呈现设置破坏映射：" + token);
                }
            }
        }
    }

    private static void PresentationThenText()
    {
        var preview = new MarkdownPreview("# 原文", 16, 680);
        Layout(preview, 1000);
        Present(preview, 20, 420);
        preview.Update("# 新文\n\n正文");
        Check(preview.FontSize == 20 && preview.Document.FontSize == 20 &&
            ((Paragraph)preview.Document.Blocks.FirstBlock).FontSize == 40, "后续正文更新丢失新字号");
        Layout(preview, 1000);
        Check(Math.Abs(preview.Document.PagePadding.Left - 290) < 1, "后续正文更新丢失新阅读宽度");
    }
}
