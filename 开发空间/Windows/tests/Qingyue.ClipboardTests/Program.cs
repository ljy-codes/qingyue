using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Qingyue.Core;
using Qingyue.Windows;
using DocumentList = System.Windows.Documents.List;

// Synthetic IDataObjects and isolated window fixtures only. No system clipboard,
// user's documents/profile, or network endpoint is accessed by this harness.
internal static class Program
{
    private static int passed, failed;
    private static readonly BitmapImage Pixels = DecodedImage();

    [STAThread]
    private static int Main()
    {
        Test("WPF native copy offers RTF and XAML but no HTML", NativeFormats);
        Test("first preview copy adds headings table list and UTF8 CF_HTML while retaining native formats", Structure);
        Test("partial Run copy excludes all unselected text and preserves selection", PartialText);
        Test("partial table copy excludes unselected cells and rows", PartialTable);
        Test("rectangular table copy excludes cells between disjoint selected segments", RectangularTable);
        Test("selected decoded inline image is self contained PNG with intact pixels", InlineImage);
        Test("selecting caption alone never exports the preceding image", CaptionOnly);
        Test("selected block image exports without unselected neighbors", BlockImage);
        Test("whole-document API retains selection and supplies RTF XamlPackage and images", FullDocument);
        Test("whole-document RTF and XamlPackage round trip actual image pixels", NativeImageRoundTrip);
        Test("real native selection copy adds image-bearing RTF for full and partial image selections", NativeSelectedImages);
        Test("WPF default in-memory paste chooses a complete image-bearing selection format", NativeDefaultPaste);
        Test("line breaks code quote emphasis and ordered list semantics survive export", Formatting);
        Test("rectangular selection excludes adjacent image-only cells", RectangularImages);
        Test("unknown UI containers are not exported as missing images", UnknownUi);
        Test("blank table cells retain their column position in HTML and native rich formats", EmptyCells);
        Test("large image presentation is bounded while HTML and RTF keep original pixels", ImagePresentation);
        Test("empty rectangular table selection retains only the selected column", EmptyRectangularCells);
        Test("image dimension and encoded byte limits produce explicit placeholders", ImageResourceLimits);
        Test("disabled empty drag and nested text input copy keep defaults", DefaultBehavior);
        Test("pending and excessive images have explicit placeholders without URI access", MissingAndLimitedImages);
        Test("oversized selected text falls back atomically to native formats", TextLimit);
        Test("复制快照只依赖已冻结资源", ClipboardSnapshotIsSelfContained);
        Test("富文本导出失败仍保留 UnicodeText", RichExportFailureKeepsUnicodeText);
        SharingChecks.Run(Test);
        MainWindowSharingChecks.Run(Test);
        Console.WriteLine($"Clipboard checks: {passed}/{passed + failed} passed");
        return failed == 0 ? 0 : 1;
    }

    private static void NativeFormats()
    {
        var box = Box(new Paragraph(new Run("合成文本")));
        box.SelectAll();
        var data = CreateNativeData(box);
        Check(data.GetDataPresent(DataFormats.UnicodeText, false), "WPF default lacks UnicodeText");
        Check(data.GetDataPresent(DataFormats.Rtf, false), "WPF default lacks RTF");
        Check(data.GetDataPresent(DataFormats.Xaml, false), "WPF default lacks XAML");
        Check(!data.GetDataPresent(DataFormats.Html, false), "WPF baseline unexpectedly has HTML");
    }

    private static void ClipboardSnapshotIsSelfContained()
    {
        var image = Image();
        var box = Box(new Paragraph(new Run("前文😀")), new Paragraph(new InlineUIContainer(image)),
            new Paragraph(new Run("后文")));
        box.SelectAll();
        var result = ReadingClipboard.CreateFullDocumentData(box);
        Check(result is not null, "快照创建失败");
        Check(result!.Data.GetDataPresent(DataFormats.XamlPackage, false), "快照缺少 XamlPackage");
        image.Source = null;
        Check(result.Data.GetDataPresent(DataFormats.XamlPackage, false) &&
            ((string)result.Data.GetData(DataFormats.UnicodeText, false)!).Contains("前文😀"),
            "快照仍依赖已变更的实时控件资源");
    }

    private static void RichExportFailureKeepsUnicodeText()
    {
        var document = new FlowDocument();
        for (int i = 0; i < 21_000; i++)
            document.Blocks.Add(new Paragraph(new Run("Unicode😀保留")));
        var box = new RichTextBox { Document = document };
        box.SelectAll();
        ReadingClipboard.Attach(box, () => true);
        var result = ReadingClipboard.CreateFullDocumentData(box);
        Check(result is not null, "富文本导出失败后没有返回降级数据");
        Check(result!.Data.GetDataPresent(DataFormats.UnicodeText, false),
            "富文本导出失败后没有保留 UnicodeText");
        Check(!result.Data.GetDataPresent(DataFormats.Html, false) &&
            !result.Data.GetDataPresent(DataFormats.Rtf, false) &&
            !result.Data.GetDataPresent(DataFormats.XamlPackage, false),
            "富文本导出失败后仍发布了不完整的富文本格式");
    }

    private static void Structure()
    {
        var (box, _, _, _) = Fixture();
        box.SelectAll();
        var data = Copy(box);
        string html = Fragment(data);
        var root = XDocument.Parse("<root>" + html + "</root>").Root!;
        Check(root.Element("h2")?.Value == "标题😀 & <合成>", "heading structure/text missing");
        var table = root.Element("table")!;
        Check(table.Descendants("tr").Count() == 2, "table row structure missing");
        Check(table.Descendants("th").Select(e => e.Value).SequenceEqual(["列一", "列二"]), "table headings lost");
        Check(table.Descendants("td").Select(e => e.Value).SequenceEqual(["值一", "值二"]), "table cells flattened");
        Check(root.Element("ul")?.Elements("li").Select(e => e.Value).SequenceEqual(["项目一", "项目二"]) == true,
            "list structure lost: " + html);
        Check((string)data.GetData(DataFormats.UnicodeText, false)! == box.Selection.Text, "plain selection changed");
        Check(((string)data.GetData(DataFormats.Rtf, false)!).Contains("\\pict"), "selected image missing from RTF");
        Check(!ReferenceEquals(data.GetData(DataFormats.XamlPackage, false), Package), "image selection kept stale XamlPackage");
        Check(!html.Contains("file:", StringComparison.OrdinalIgnoreCase) && !html.Contains("SourceURL"),
            "local document path exposed");
    }

    private static void PartialText()
    {
        var run = new Run("不可复制前文 选中😀&<> 不可复制后文");
        var box = Box(new Paragraph(run));
        int offset = run.Text.IndexOf("选中", StringComparison.Ordinal);
        box.Selection.Select(run.ContentStart.GetPositionAtOffset(offset)!,
            run.ContentStart.GetPositionAtOffset(offset + "选中😀&<>".Length)!);
        var start = box.Selection.Start;
        string html = Fragment(Copy(box));
        Check(XDocument.Parse("<root>" + html + "</root>").Root!.Value == "选中😀&<>", "partial text escaped incorrectly");
        Check(!html.Contains("不可复制"), "unselected text leaked");
        Check(box.Selection.Start.CompareTo(start) == 0 && box.Selection.Text == "选中😀&<>", "selection mutated");
    }

    private static void PartialTable()
    {
        var (box, _, table, _) = Fixture();
        var selected = (Run)((Paragraph)table.RowGroups[0].Rows[1].Cells[1].Blocks.FirstBlock).Inlines.FirstInline!;
        box.Selection.Select(selected.ContentStart, selected.ContentEnd);
        string html = Fragment(Copy(box));
        var root = XDocument.Parse("<root>" + html + "</root>").Root!;
        Check(root.Descendants("td").Single().Value == "值二", "selected cell missing");
        Check(root.Value == "值二", "unselected cells or surrounding paragraphs leaked");
    }

    private static void RectangularTable()
    {
        var table = new Table();
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        foreach (string[] values in new[] { new[] { "A1", "B1", "C1" }, new[] { "A2", "B2", "C2" } })
        {
            var row = new TableRow();
            foreach (string value in values) row.Cells.Add(new TableCell(new Paragraph(new Run(value))));
            group.Rows.Add(row);
        }
        var box = Box(table);
        box.Selection.Select(group.Rows[0].Cells[1].ContentStart, group.Rows[1].Cells[1].ContentEnd);
        Check(box.Selection.Text.Replace("\t", "").Trim() == "B1B2", "fixture is not a rectangular WPF selection");
        string html = Fragment(Copy(box));
        var cells = XDocument.Parse("<root>" + html + "</root>").Descendants()
            .Where(e => e.Name.LocalName is "td" or "th").Select(e => e.Value).ToArray();
        Check(cells.SequenceEqual(["B1", "B2"]), "rectangular selection leaked neighboring cells: " + string.Join(",", cells));
    }

    private static void InlineImage()
    {
        var (box, _, _, inline) = Fixture();
        box.Selection.Select(inline.ElementStart, inline.ElementEnd);
        string html = Fragment(Copy(box));
        var image = XDocument.Parse("<root>" + html + "</root>").Descendants("img").Single();
        Check(!html.Contains("标题") && !html.Contains("图注") && !html.Contains("列一"), "image selection leaked neighbors");
        Check(image.Attribute("alt")?.Value == "合成红蓝图", "image alt missing");
        byte[] png = Convert.FromBase64String(image.Attribute("src")!.Value["data:image/png;base64,".Length..]);
        var frame = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Check(frame.PixelWidth == 2 && frame.PixelHeight == 1, "exported image dimensions changed");
        var pixels = new byte[8];
        new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 8, 0);
        Check(pixels.SequenceEqual(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }), "exported image pixels corrupted");
    }

    private static void CaptionOnly()
    {
        var (box, _, _, inline) = Fixture();
        var caption = (Run)inline.NextInline!;
        box.Selection.Select(caption.ContentStart, caption.ContentEnd);
        string html = Fragment(Copy(box));
        Check(!html.Contains("<img") && html.Contains("图注"), "caption selection included unselected image");
    }

    private static void BlockImage()
    {
        var block = new BlockUIContainer(new Border { Child = Image() });
        var box = Box(new Paragraph(new Run("前文")), block, new Paragraph(new Run("后文")));
        box.Selection.Select(block.ElementStart, block.ElementEnd);
        string html = Fragment(Copy(box));
        Check(html.Contains("<img") && !html.Contains("前文") && !html.Contains("后文"), "block image range incorrect");
    }

    private static void FullDocument()
    {
        var (box, heading, _, _) = Fixture();
        var run = (Run)heading.Inlines.FirstInline!;
        box.Selection.Select(run.ContentStart, run.ContentStart.GetPositionAtOffset(2)!);
        string selected = box.Selection.Text;
        var start = box.Selection.Start;
        var result = ReadingClipboard.CreateFullDocumentData(box);
        Check(result is not null, "full-document copy returned no data");
        Check(result!.EmbeddedImages == 1 && result.OmittedImages == 0, "full-document image accounting incorrect");
        Check(Fragment(result.Data).Contains("<img"), "full-document image missing");
        Check(result.Data.GetDataPresent(DataFormats.Rtf, false) && result.Data.GetDataPresent(DataFormats.XamlPackage, false),
            "full-document native rich formats missing");
        Check(((string)result.Data.GetData(DataFormats.UnicodeText, false)!).Contains("值二"), "full-document plain text incomplete");
        Check(box.Selection.Text == selected && box.Selection.Start.CompareTo(start) == 0, "copy-all changed user's selection");
    }

    private static void NativeImageRoundTrip()
    {
        var (box, _, _, _) = Fixture();
        var result = ReadingClipboard.CreateFullDocumentData(box)!;
        string rtf = (string)result.Data.GetData(DataFormats.Rtf, false)!;
        Check(rtf.Contains("\\pict"), "RTF is advertised but has no embedded picture");
        foreach (string format in new[] { DataFormats.Rtf, DataFormats.XamlPackage })
        {
            using Stream stream = format == DataFormats.Rtf
                ? new MemoryStream(Encoding.ASCII.GetBytes(rtf))
                : new MemoryStream(((MemoryStream)result.Data.GetData(format, false)!).ToArray());
            var document = new FlowDocument();
            new TextRange(document.ContentStart, document.ContentEnd).Load(stream, format);
            var images = document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineUIContainer>())
                .Select(ui => ui.Child).OfType<Image>().ToArray();
            Check(images.Length == 1, format + " round trip lost wrapped image");
            CheckPixels((BitmapSource)images[0].Source, format);
        }
    }

    private static void NativeSelectedImages()
    {
        foreach (bool all in new[] { true, false })
        {
            var (box, _, _, inline) = Fixture();
            if (all) box.SelectAll(); else box.Selection.Select(inline.ElementStart, inline.ElementEnd);
            var baseline = CreateNativeData(box);
            Check(!((string)baseline.GetData(DataFormats.Rtf, false)!).Contains("\\pict"),
                "baseline changed: wrapped Grid image already exported by WPF");
            string? plain = null;
            object? package = null;
            DataObject.AddCopyingHandler(box, (_, e) =>
            {
                plain = (string)e.DataObject.GetData(DataFormats.UnicodeText, false)!;
                package = e.DataObject.GetData(DataFormats.XamlPackage, false);
            });
            ReadingClipboard.Attach(box, () => true);
            var native = CreateNativeData(box);
            string rtf = (string)native.GetData(DataFormats.Rtf, false)!;
            Check(rtf.Contains("\\pict"), "selection RTF lost image; HTML alone cannot satisfy an RTF-preferring target");
            var restored = new FlowDocument();
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rtf));
            new TextRange(restored.ContentStart, restored.ContentEnd).Load(stream, DataFormats.Rtf);
            var image = restored.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineUIContainer>())
                .Select(ui => ui.Child).OfType<Image>().Single();
            CheckPixels((BitmapSource)image.Source, "selected RTF");
            string text = new TextRange(restored.ContentStart, restored.ContentEnd).Text;
            Check(all ? text.Contains("值二") : !text.Contains("图注") && !text.Contains("标题") && !text.Contains("值二"),
                "safe selected RTF leaked unselected text");
            Check((string)native.GetData(DataFormats.UnicodeText, false)! == plain, "native plain text changed");
            Check(!ReferenceEquals(native.GetData(DataFormats.XamlPackage, false), package), "image selection kept stale native package");
            Check(Fragment(native).Contains("<img"), "selection HTML image missing");
            Check(native.GetDataPresent(DataFormats.Bitmap, false) == !all,
                all ? "mixed document selection advertised a misleading Bitmap" :
                "single image selection did not advertise Bitmap through Attach");
        }
    }

    private static void NativeDefaultPaste()
    {
        foreach (bool all in new[] { true, false })
        {
            var (box, _, _, image) = Fixture();
            if (all) box.SelectAll(); else box.Selection.Select(image.ElementStart, image.ElementEnd);
            ReadingClipboard.Attach(box, () => true);
            var data = CreateNativeData(box);
            Check(!data.GetDataPresent(DataFormats.Xaml, false), "stale standalone XAML remains selectable despite lacking images");
            var target = new RichTextBox();
            var editor = typeof(TextBoxBase).GetProperty("TextEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
            var type = typeof(RichTextBox).Assembly.GetType("System.Windows.Documents.TextEditorCopyPaste")!;
            bool pasted = (bool)type.GetMethod("_DoPaste", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(null, [editor, data, false])!;
            Check(pasted, "WPF default in-memory paste failed");
            var pictures = target.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineUIContainer>())
                .Select(ui => ui.Child).OfType<Image>().ToArray();
            Check(pictures.Length == 1, "WPF chose a rich format which silently dropped the image");
            CheckPixels((BitmapSource)pictures[0].Source, "default WPF paste");
            string text = new TextRange(target.Document.ContentStart, target.Document.ContentEnd).Text;
            Check(all ? text.Contains("值二") : !text.Contains("图注") && !text.Contains("标题"),
                "default paste leaked unselected text");
        }
    }

    private static IDataObject CreateNativeData(RichTextBox box)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var editor = typeof(TextBoxBase).GetProperty("TextEditor", flags)!.GetValue(box);
        var type = typeof(RichTextBox).Assembly.GetType("System.Windows.Documents.TextEditorCopyPaste")!;
        return (IDataObject)type.GetMethod("_CreateDataObject",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [editor, false])!;
    }

    private static void Formatting()
    {
        var paragraph = new Paragraph(new Run("前"));
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(new Bold(new Run("粗")));
        paragraph.Inlines.Add(new Italic(new Run("斜")));
        var code = new Paragraph(new Run("if (a < b)\n  保留空格"))
            { Tag = new ReadingBlock(ReadingBlockKind.Code, "", 0, 0, 0) };
        var quote = new Paragraph(new Run("引用")) { Tag = new ReadingBlock(ReadingBlockKind.Quote, "", 0, 0, 0) };
        var list = new DocumentList { MarkerStyle = TextMarkerStyle.Decimal, StartIndex = 5 };
        list.ListItems.Add(new ListItem(new Paragraph(new Run("五"))));
        list.ListItems.Add(new ListItem(new Paragraph(new Run("六"))));
        var box = Box(paragraph, code, quote, list);
        box.SelectAll();
        var root = XElement.Parse("<root>" + Fragment(Copy(box)) + "</root>");
        Check(root.Descendants("br").Count() == 1, "explicit line break lost");
        Check(root.Descendants("strong").Single().Value == "粗" && root.Descendants("em").Single().Value == "斜", "emphasis lost");
        Check(root.Element("pre")?.Element("code")?.Value == "if (a < b)\n  保留空格", "code changed");
        Check(root.Element("blockquote")?.Value == "引用", "quote structure lost");
        Check(root.Element("ol")?.Attribute("start")?.Value == "5"
            && root.Element("ol")?.Value == "五六", "ordered list contains duplicate native markers");
    }

    private static void RectangularImages()
    {
        var table = new Table();
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (int i = 0; i < 2; i++)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Run("B" + i))));
            row.Cells.Add(new TableCell(new Paragraph(new InlineUIContainer(Image()))));
            group.Rows.Add(row);
        }
        var box = Box(table);
        box.Selection.Select(group.Rows[0].Cells[0].ContentStart, group.Rows[1].Cells[0].ContentEnd);
        string html = Fragment(Copy(box));
        Check(!html.Contains("<img"), "rectangular selection leaked unselected image");
    }

    private static void UnknownUi()
    {
        var box = Box(new BlockUIContainer(new Grid { Children = { new TextBlock { Text = "图形" } } }),
            new Paragraph(new Run("diagram source")));
        box.SelectAll();
        string html = Fragment(Copy(box));
        Check(!html.Contains("图片未") && html.Contains("diagram source"), "non-image UI mislabeled as missing image");
    }

    private static void EmptyCells()
    {
        var table = new Table();
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        foreach (string[] values in new[] { new[] { "左", "中", "右" }, new[] { "", "值", "" } })
        {
            var row = new TableRow();
            foreach (string value in values) row.Cells.Add(new TableCell(new Paragraph(new Run(value))));
            group.Rows.Add(row);
        }
        var box = Box(table);
        box.SelectAll();
        var root = XElement.Parse("<root>" + Fragment(Copy(box)) + "</root>");
        Check(root.Descendants("tr").Last().Elements("td").Select(e => e.Value).SequenceEqual(["", "值", ""]),
            "empty selected cells disappeared and moved data into the wrong column");
        var result = ReadingClipboard.CreateFullDocumentData(box)!;
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes((string)result.Data.GetData(DataFormats.Rtf, false)!));
        var document = new FlowDocument();
        new TextRange(document.ContentStart, document.ContentEnd).Load(stream, DataFormats.Rtf);
        var restored = document.Blocks.OfType<Table>().Single();
        Check(restored.RowGroups[0].Rows.Last().Cells.Count == 3, "RTF lost blank table columns");
    }

    private static void ImagePresentation()
    {
        byte[] pixels = new byte[1600 * 800 * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var source = BitmapSource.Create(1600, 800, 96, 96, PixelFormats.Bgra32, null, pixels, 1600 * 4);
        var bitmap = Decode(source);
        foreach (bool measured in new[] { false, true })
        {
            var image = new Image { Source = bitmap };
            if (measured)
            {
                image.Width = 320;
                image.Height = 160;
                image.Measure(new Size(320, 160));
                image.Arrange(new Rect(0, 0, 320, 160));
            }
            var box = Box(new Paragraph(new InlineUIContainer(image)));
            var result = ReadingClipboard.CreateFullDocumentData(box)!;
            var img = XElement.Parse("<root>" + Fragment(result.Data) + "</root>").Descendants("img").Single();
            int width = (int)img.Attribute("width")!, height = (int)img.Attribute("height")!;
            Check(width == (measured ? 320 : 720) && height == width / 2, "HTML uses raw bitmap dimensions or changes aspect");
            var png = Convert.FromBase64String(img.Attribute("src")!.Value["data:image/png;base64,".Length..]);
            var decoded = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Check(decoded.PixelWidth == 1600 && decoded.PixelHeight == 800, "HTML resampled source pixels");
            using var rtf = new MemoryStream(Encoding.ASCII.GetBytes((string)result.Data.GetData(DataFormats.Rtf, false)!));
            var restored = new FlowDocument();
            new TextRange(restored.ContentStart, restored.ContentEnd).Load(rtf, DataFormats.Rtf);
            var richImage = restored.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineUIContainer>())
                .Select(c => c.Child).OfType<Image>().Single();
            Check(Math.Abs(richImage.Width - width) < 1 && Math.Abs(richImage.Height - height) < 1,
                "RTF image presentation overflows or changes aspect");
            Check(((BitmapSource)richImage.Source).PixelWidth == 1600, "RTF resampled source pixels");
        }
    }

    private static void EmptyRectangularCells()
    {
        var table = new Table();
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (int i = 0; i < 2; i++)
        {
            var row = new TableRow();
            foreach (string value in new[] { "A" + i, "", "" })
                row.Cells.Add(new TableCell(new Paragraph(new Run(value))));
            group.Rows.Add(row);
        }
        var box = Box(table);
        box.Selection.Select(group.Rows[0].Cells[1].ContentStart, group.Rows[1].Cells[1].ContentEnd);
        var root = XElement.Parse("<root>" + Fragment(Copy(box)) + "</root>");
        Check(root.Descendants("tr").Count() == 2 && root.Descendants("tr").All(r => r.Elements().Count() == 1),
            "empty rectangular copy included an unselected adjacent column or lost selected empty cells: " + root);
    }

    private static void ImageResourceLimits()
    {
        foreach (bool dimensions in new[] { true, false })
        {
            int width = dimensions ? 4097 : 1100, height = dimensions ? 1 : 1100;
            var pixels = new byte[width * height * 4];
            if (!dimensions) new Random(127).NextBytes(pixels);
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var box = Box(new Paragraph(new InlineUIContainer(new Image { Source = Decode(source) })));
            var result = ReadingClipboard.CreateFullDocumentData(box);
            Check(result is { EmbeddedImages: 0, OmittedImages: 1 }, "image resource budget bypassed");
            string html = Fragment(result!.Data);
            Check(!html.Contains("<img") && html.Contains("图片未"), "resource-limited image has no explicit placeholder");
        }
    }

    private static void CheckPixels(BitmapSource bitmap, string context)
    {
        Check(bitmap.PixelWidth == 2 && bitmap.PixelHeight == 1, context + " dimensions changed");
        var pixels = new byte[8];
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 8, 0);
        Check(pixels.SequenceEqual(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }), context + " pixels changed");
    }

    private static void DefaultBehavior()
    {
        var box = Box(new Paragraph(new Run("源码 **原样**")));
        box.SelectAll();
        var data = NativeData(box);
        ReadingClipboard.Attach(box, () => false);
        box.RaiseEvent(new DataObjectCopyingEventArgs(data, false));
        Check(!data.GetDataPresent(DataFormats.Html, false), "disabled preview changed defaults");
        ReadingClipboard.Attach(box, () => true);
        var drag = NativeData(box);
        box.RaiseEvent(new DataObjectCopyingEventArgs(drag, true));
        Check(!drag.GetDataPresent(DataFormats.Html, false), "drag export unexpectedly changed");
        box.Selection.Select(box.Document.ContentStart, box.Document.ContentStart);
        var empty = NativeData(box);
        box.RaiseEvent(new DataObjectCopyingEventArgs(empty, false));
        Check(!empty.GetDataPresent(DataFormats.Html, false), "empty selection exported whole document");
        var input = new TextBox { Text = "搜索文字" };
        box.Document.Blocks.Add(new Paragraph(new InlineUIContainer(input)));
        box.SelectAll();
        var child = NativeData(box);
        input.RaiseEvent(new DataObjectCopyingEventArgs(child, false));
        Check(!child.GetDataPresent(DataFormats.Html, false), "nested text input copy was intercepted");
    }

    private static void MissingAndLimitedImages()
    {
        var paragraph = new Paragraph(new InlineUIContainer(new Grid { Children = { new Image() } }));
        // A URI string on metadata is never used as an image-loading instruction.
        paragraph.Tag = @"\\unreachable.invalid\never-open.png";
        paragraph.Inlines.Add(new Run("[图片：尚未加载]"));
        for (int i = 0; i < 33; i++) paragraph.Inlines.Add(new InlineUIContainer(Image()));
        var box = Box(paragraph);
        box.SelectAll();
        string html = Fragment(Copy(box));
        var root = XDocument.Parse("<root>" + html + "</root>").Root!;
        Check(root.Descendants("img").Count() == 32, "image count budget not enforced");
        Check(root.Value.Contains("图片未") && !html.Contains("unreachable"), "omitted images have no explicit boundary");
    }

    private static void TextLimit()
    {
        var box = Box(new Paragraph(new Run(new string('x', 500_001))));
        box.SelectAll();
        var data = Copy(box);
        Check(!data.GetDataPresent(DataFormats.Html, false), "excessive text exported");
        Check((string)data.GetData(DataFormats.Rtf, false)! == "{\\rtf1 native}", "limit damaged native fallback");
    }

    private static readonly MemoryStream Package = new([1, 2, 3]);

    private static DataObject NativeData(RichTextBox box)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, box.Selection.Text, false);
        data.SetData(DataFormats.Rtf, "{\\rtf1 native}", false);
        data.SetData(DataFormats.XamlPackage, Package, false);
        return data;
    }

    private static DataObject Copy(RichTextBox box)
    {
        var data = NativeData(box);
        ReadingClipboard.Attach(box, () => true);
        box.RaiseEvent(new DataObjectCopyingEventArgs(data, false));
        return data;
    }

    private static string Fragment(IDataObject data)
    {
        Check(data.GetDataPresent(DataFormats.Html, false), "CF_HTML missing; target falls back to flattened text");
        string cf = (string)data.GetData(DataFormats.Html, false);
        byte[] bytes = Encoding.UTF8.GetBytes(cf);
        int Offset(string field)
        {
            int start = cf.IndexOf(field + ":", StringComparison.Ordinal) + field.Length + 1;
            return int.Parse(cf.AsSpan(start, 10), CultureInfo.InvariantCulture);
        }
        int htmlStart = Offset("StartHTML"), htmlEnd = Offset("EndHTML");
        int start = Offset("StartFragment"), end = Offset("EndFragment");
        Check(htmlStart < start && start < end && end < htmlEnd && htmlEnd == bytes.Length, "invalid UTF8 byte offsets");
        Check(Encoding.UTF8.GetString(bytes, htmlStart, 6) == "<html>", "StartHTML does not point to HTML");
        Check(Encoding.UTF8.GetString(bytes, htmlEnd - 7, 7) == "</html>", "EndHTML incorrect");
        Check(Encoding.UTF8.GetString(bytes, start - 20, 20) == "<!--StartFragment-->", "StartFragment marker incorrect");
        Check(Encoding.UTF8.GetString(bytes, end, 18) == "<!--EndFragment-->", "EndFragment marker incorrect");
        var fragment = XElement.Parse(Encoding.UTF8.GetString(bytes, start, end - start));
        Check(fragment.Name == "div", "CF_HTML fragment must have a single outer div");
        return string.Concat(fragment.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)));
    }

    private static (RichTextBox Box, Paragraph Heading, Table Table, InlineUIContainer Image) Fixture()
    {
        var heading = new Paragraph(new Run("标题😀 & <合成>"))
        {
            Tag = new ReadingBlock(ReadingBlockKind.Heading, "", 0, 0, 0, 2)
        };
        var table = new Table();
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        foreach (string[] values in new[] { new[] { "列一", "列二" }, new[] { "值一", "值二" } })
        {
            var row = new TableRow();
            foreach (string value in values) row.Cells.Add(new TableCell(new Paragraph(new Run(value))));
            group.Rows.Add(row);
        }
        var list = new DocumentList();
        list.ListItems.Add(new ListItem(new Paragraph(new Run("项目一"))));
        list.ListItems.Add(new ListItem(new Paragraph(new Run("项目二"))));
        var inline = new InlineUIContainer(new Grid { Children = { Image() } });
        var caption = new Paragraph(inline);
        caption.Inlines.Add(new Run("图注"));
        return (Box(heading, table, list, caption), heading, table, inline);
    }

    private static Image Image()
    {
        var image = new Image { Source = Pixels };
        AutomationProperties.SetName(image, "合成红蓝图");
        return image;
    }

    private static BitmapImage DecodedImage()
    {
        var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, 8);
        return Decode(bitmap);
    }

    private static BitmapImage Decode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static RichTextBox Box(params Block[] blocks)
    {
        var document = new FlowDocument();
        foreach (var block in blocks) document.Blocks.Add(block);
        return new RichTextBox(document) { IsReadOnly = true, IsUndoEnabled = false };
    }

    private static void Test(string name, Action run)
    {
        try { run(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
