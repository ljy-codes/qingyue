using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Qingyue.Core;
using Qingyue.Windows;

internal static partial class Program
{
    private static void ResizeChecks()
    {
        Test("full reading applies its gutter once after wide narrow maximize restore", FullReadingResize);
        Test("split preview reflows at the real text viewport after wide narrow restore", SplitPreviewResize);
        Test("rich content and search pointers survive repeated resize without rebuilding", RichContentResize);
        Test("500k body reflows without replacing the document or source mapping", LargeBodyResize);
    }

    private static void FullReadingResize()
    {
        string source = "# Resize\n\n" + new string('文', 200) + "\n\n尾段";
        var model = ReadingModel.Build(source, TextKind.Markdown);
        var reading = RenderedReading.Markdown(model, source, null, 16, 680);
        var host = new RichTextBox
        {
            Document = reading.Document, Padding = new Thickness(80, 28, 80, 60), BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        host.SizeChanged += (_, _) => reading.UpdateLayout(host, 680);
        host.Loaded += (_, _) => reading.UpdateLayout(host, 680);
        reading.UpdateLayout(host, 680);
        foreach (double width in new[] { 1000d, 1600, 420, 1000, 1600, 1000 })
        {
            Layout(host, width);
            double margin = width switch { 1000 => 160, 1600 => 460, _ => 24 };
            var paragraph = reading.Document.Blocks.OfType<Paragraph>().ElementAt(1);
            double left = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).Left;
            Console.WriteLine($"  full width {width}: gutter {margin}, text left {left}, viewport {host.ViewportWidth}");
            Check(Math.Abs(left - margin) < 3,
                $"全宽阅读重复留白：窗口 {width}，单侧预期 {margin}，实际文字起点 {left}");
            Check(host.Padding == new Thickness(0), "控件 Padding 又叠加到了文档留白");
            Check(host.ViewportWidth - reading.Document.PagePadding.Left - reading.Document.PagePadding.Right > 300,
                "宽窄切换压缩了正文宽度");
        }
    }

    private static void SplitPreviewResize()
    {
        var preview = new MarkdownPreview(new string('文', 200), 16, 680);
        foreach (double width in new[] { 1000d, 1600, 420, 1000, 1600, 1000 })
        {
            Layout(preview, width);
            var paragraph = (Paragraph)preview.Document.Blocks.FirstBlock;
            double left = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).Left;
            double margin = preview.Document.PagePadding.Left;
            Console.WriteLine($"  split width {width}: gutter {margin}, text left {left}, viewport {preview.ViewportWidth}");
            Check(Math.Abs(left - margin) < 3, $"分屏重复留白：{width} / {left} / {margin}");
        }
    }

    private static void RichContentResize()
    {
        string source = "# 标题\n\n图前文字 ![比例图](" + DataUri + ") 图后文字😀\n\n" +
            "| 参数 | 说明 |\n| --- | --- |\n| key | 表格后文 |\n\n" +
            "- **列表起点** 后文\n\n```mermaid\nsequenceDiagram\nA->>B: /api/tasks/approve\n```\n\n尾段";
        var model = ReadingModel.Build(source, TextKind.Markdown);
        var reading = RenderedReading.Markdown(model, source, null, 16, 680);
        var host = new RichTextBox
        {
            Document = reading.Document, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var document = reading.Document;
        host.SizeChanged += (_, _) => reading.UpdateLayout(host, 680);
        reading.UpdateLayout(host, 680);
        var image = AssertLoaded(document, "resize 内嵌图片");
        var bitmap = image.Source;
        var blocks = document.Blocks.ToArray();
        double wideImage = 0, narrowImage = 0;
        foreach (double width in new[] { 1000d, 1600, 320, 1000 })
        {
            Layout(host, width);
            Check(ReferenceEquals(document, reading.Document) && document.Blocks.SequenceEqual(blocks),
                "resize 重新构建了富文档");
            Check(ReferenceEquals(image.Source, bitmap), "resize 重新解码了图片");
            Check(image.ActualWidth <= width - 48 && Math.Abs(image.ActualWidth / image.ActualHeight - 2) < .01,
                $"resize 图片超出可用宽度或比例改变：{width} / {image.ActualWidth}x{image.ActualHeight}");
            if (width == 320) narrowImage = image.ActualWidth;
            else if (width == 1000 && wideImage == 0) wideImage = image.ActualWidth;
            else if (width == 1000) Check(image.ActualWidth > narrowImage, "恢复窗口后图片没有重新变宽");
            foreach (string token in new[] { "图前文字", "[图片：比例图]", "图后文字😀", "key",
                "表格后文", "列表起点", "/api/tasks/approve", "尾段" })
            {
                int offset = model.VisibleText.IndexOf(token, StringComparison.Ordinal);
                var start = reading.PointerAt(offset);
                Check(new TextRange(start, reading.PointerAt(offset + token.Length)).Text == token &&
                    reading.OffsetAt(start) == offset, "resize 破坏查找映射：" + token);
            }
        }
        Check(wideImage > narrowImage, "宽窄窗口没有改变图片尺寸");
    }

    private static void LargeBodyResize()
    {
        string source = string.Concat(Enumerable.Repeat(new string('文', 498) + "\n\n", 999)) +
            new string('文', 492) + "尾部定位😀";
        var model = ReadingModel.Build(source, TextKind.Markdown);
        var reading = RenderedReading.Markdown(model, source, null, 16, 680);
        var document = reading.Document;
        var host = new RichTextBox
        {
            Document = document, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var pointer = reading.PointerAt(model.VisibleText.IndexOf("尾部定位", StringComparison.Ordinal));
        host.SizeChanged += (_, _) => reading.UpdateLayout(host, 680);
        reading.UpdateLayout(host, 680);
        foreach (double width in new[] { 1000d, 420, 1600, 1000 })
        {
            Layout(host, width);
            Check(ReferenceEquals(document, reading.Document), "500k 正文 resize 替换了文档");
            Check(reading.OffsetAt(pointer) == model.VisibleText.IndexOf("尾部定位", StringComparison.Ordinal),
                "500k 正文 resize 改变了源映射");
        }
        foreach (double fontSize in new[] { 20d, 12, 16 })
        {
            reading.UpdatePresentation(host, fontSize, 680);
            Check(ReferenceEquals(document, reading.Document) &&
                reading.OffsetAt(pointer) == model.VisibleText.IndexOf("尾部定位", StringComparison.Ordinal),
                "500k 正文呈现更新改变了文档或源映射");
        }
    }
}
