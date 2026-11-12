using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Qingyue.Core;
using Qingyue.Windows;

// Synthetic documents only. File tests use the ignored evidence directory.
internal static class SharingChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("v030 raw Markdown copy keeps original syntax CRLF Unicode and no rich flavors", Markdown);
        test("v030 plain text copy uses rendered document without changing selection", PlainText);
        test("v030 current code copy resolves caret and excludes neighboring blocks", CurrentCode);
        test("v030 current table copy expands a cell and refuses crossing selection", CurrentTable);
        test("v030 HTML export embeds decoded pixels and sanitizes title metadata and resources", Html);
        test("v070 Mermaid export embeds a self-contained PNG and keeps source text", Mermaid);
        test("v070 single image selection supplies a Bitmap clipboard flavor", SingleBitmap);
        test("v070 failed Mermaid export keeps the warning and source without a blank image", MermaidFailure);
        test("v030 HTML image metadata never exposes drive paths with forward slashes", MetadataPaths);
        test("v030 missing failed unsupported and excessive image reports are distinct", ImageReports);
        test("v030 renderer image-limit notice survives export without an invented omission count", ImageLimitNotice);
        test("v030 asynchronous copy waits for generated images on Dispatcher", () => Pump(WaitForImages));
        test("v030 asynchronous copy is cancellable and never publishes partial data", () => Pump(CancelWait));
        test("v030 asynchronous copy refuses a replaced document", () => Pump(ReplacedDocument));
        test("v030 asynchronous copy refuses text changes within the same document", () => Pump(MutatedDocument));
        test("v030 image progress reports pending and ready without a synchronization context", () => Pump(ProgressWithoutContext));
        test("v030 failed images do not consume the pending-image timeout", () => Pump(FailedImages));
        test("v030 pending images time out within five seconds with explicit accounting", () => Pump(Timeout));
        test("v030 atomic HTML writer uses UTF8 and protects all open source paths", () => Pump(AtomicWrite));
        test("v030 atomic HTML writer requires overwrite consent and leaves no cancelled temp files", () => Pump(Overwrite));
        test("v030 atomic HTML writer rejects trailing-dot source aliases", () => Pump(TrailingDot));
        test("v030 portable HTML enforces existing text and image budgets", Budgets);
        test("v030 block context menu is installed once and uses current selection", ContextMenu);
        test("复制快照只依赖已冻结资源", ClipboardSnapshotIsSelfContained);
    }

    private static MethodInfo Api(string name) =>
        typeof(ReadingClipboard).GetMethod(name, BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing ReadingClipboard API: " + name);

    private static object? Invoke(MethodInfo method, params object?[] args)
    {
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }

    private static object? Clip(string name, params object?[] args) => Invoke(Api(name), args);
    private static Type HtmlType => typeof(ReadingClipboard).Assembly.GetType("Qingyue.Windows.ReadingHtmlExport")
        ?? throw new InvalidOperationException("Missing ReadingHtmlExport API");
    private static object? Export(string name, params object?[] args) =>
        Invoke(HtmlType.GetMethod(name) ?? throw new InvalidOperationException("Missing export API: " + name), args);
    private static T Property<T>(object value, string name) =>
        (T)(value.GetType().GetProperty(name)?.GetValue(value)
            ?? throw new InvalidOperationException("Missing result property: " + name));

    private static void Markdown()
    {
        const string source = "# 标题😀\r\n\r\n**粗体** ![a](file:///C:/private/a.png)\r\n```cs\r\nx();\r\n```\r\n";
        var data = (DataObject)Clip("CreateMarkdownData", source)!;
        Check((string)data.GetData(DataFormats.UnicodeText, false)! == source, "Markdown source was normalized or rendered");
        Check((string)data.GetData(DataFormats.Text, false)! == source, "text flavor changed Markdown");
        Check(!data.GetDataPresent(DataFormats.Html, false) && !data.GetDataPresent(DataFormats.Rtf, false),
            "Markdown must paste as source text");
    }

    private static void ClipboardSnapshotIsSelfContained()
    {
        var box = Box(new Paragraph(new Run("快照😀")));
        box.SelectAll();
        var result = ReadingClipboard.CreateFullDocumentData(box);
        Check(result is not null && result.Data.GetDataPresent(DataFormats.UnicodeText, false),
            "快照未冻结 UnicodeText");
    }

    private static void PlainText()
    {
        var run = new Run("标题😀 正文");
        var box = Box(new Paragraph(run), new Paragraph(new Run("第二段")));
        box.Selection.Select(run.ContentStart.GetPositionAtOffset(2)!, run.ContentEnd);
        var start = box.Selection.Start;
        var end = box.Selection.End;
        var data = (DataObject)Clip("CreatePlainTextData", box)!;
        Check((string)data.GetData(DataFormats.UnicodeText, false)! ==
            new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text, "rendered plain text is incomplete");
        Check(!data.GetDataPresent(DataFormats.Html, false), "plain copy advertises rich content");
        Check(box.Selection.Start.CompareTo(start) == 0 && box.Selection.End.CompareTo(end) == 0, "plain copy changed selection");
    }

    private static Paragraph Code(string text) => new(new Run(text))
    { Tag = new ReadingBlock(ReadingBlockKind.Code, text, 0, text.Length, 0) };

    private static void CurrentCode()
    {
        var code = Code("var x = \"<😀>\";\nreturn x;");
        var box = Box(new Paragraph(new Run("前文")), code, new Paragraph(new Run("后文")));
        var caret = ((Run)code.Inlines.FirstInline!).ContentStart.GetPositionAtOffset(3)!;
        box.Selection.Select(caret, caret);
        var result = (ReadingClipboardResult?)Clip("CreateCurrentCodeBlockData", box);
        Check(result is not null, "caret inside code failed to locate block");
        var html = (string)result!.Data.GetData(DataFormats.Html, false)!;
        Check(html.Contains("<pre><code>") && !html.Contains("前文") && !html.Contains("后文"), "code copy escaped its block");
        Check(((string)result.Data.GetData(DataFormats.UnicodeText, false)!).Contains("return x;"), "code copy is only partial");
        Check(box.Selection.IsEmpty && box.Selection.Start.CompareTo(caret) == 0, "code copy moved caret");
        box.Selection.Select(box.Document.ContentStart, caret);
        Check(Clip("CreateCurrentCodeBlockData", box) is null, "cross-block selection copied ambiguous code");
    }

    private static Table Table()
    {
        var table = new Table();
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        foreach (string value in new[] { "标题", "值😀" })
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Run(value))));
            group.Rows.Add(row);
        }
        return table;
    }

    private static void CurrentTable()
    {
        var table = Table();
        var box = Box(new Paragraph(new Run("前文")), table, new Paragraph(new Run("后文")));
        var caret = table.RowGroups[0].Rows[1].Cells[0].ContentStart;
        box.Selection.Select(caret, caret);
        caret = box.Selection.Start;
        var result = (ReadingClipboardResult?)Clip("CreateCurrentTableData", box);
        Check(result is not null, "cell caret failed to find table");
        string html = (string)result!.Data.GetData(DataFormats.Html, false)!;
        Check(html.Contains("<th") && html.Contains("<td") && html.Contains("标题") && html.Contains("值😀"), "table copy was partial");
        Check(!html.Contains("前文") && !html.Contains("后文"), "table copy included neighbors");
        Check(box.Selection.IsEmpty && box.Selection.Start.CompareTo(caret) == 0, "table copy changed selection");
        box.Selection.Select(caret, box.Document.ContentEnd);
        Check(Clip("CreateCurrentTableData", box) is null, "ambiguous table selection accepted");
    }

    private static void Html()
    {
        var image = Image();
        AutomationProperties.SetName(image, @"file:///C:/private/person/photo.png");
        var link = new Hyperlink(new Run("链接文字")) { NavigateUri = new Uri("https://example.invalid/private") };
        var box = Box(new Paragraph(link), Code("<script>alert('x')</script>"), Table(),
            new Paragraph(new InlineUIContainer(new Grid { Children = { image } })));
        var selected = box.Document.ContentStart;
        box.Selection.Select(selected, selected);
        selected = box.Selection.Start;
        var result = Export("CreateDocument", box, @"C:\private\报告😀.md")!;
        string html = Property<string>(result, "Html");
        var document = XDocument.Parse(html);
        Check(document.Root!.Element("head")!.Element("title")!.Value == "报告😀.md", "title leaked a path or lost Unicode");
        Check(document.Descendants("style").Any() && document.Descendants("meta").Any(), "portable document lacks style/metadata");
        Check(!document.Descendants("script").Any() && !document.Descendants("a").Any(), "executable or remote markup escaped whitelist");
        Check(!html.Contains("file:", StringComparison.OrdinalIgnoreCase) && !html.Contains("C:\\") &&
            !html.Contains("https://"), "portable HTML leaked a resource URI/path");
        var img = document.Descendants("img").Single();
        string uri = (string)img.Attribute("src")!;
        Check(uri.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "image isn't embedded");
        using var stream = new MemoryStream(Convert.FromBase64String(uri.Split(',')[1]));
        var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bytes = new byte[8];
        bitmap.CopyPixels(bytes, 8, 0);
        Check(bytes.SequenceEqual(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }), "export lost image pixels");
        Check(box.Selection.IsEmpty && box.Selection.Start.CompareTo(selected) == 0, "HTML export changed selection");
    }

    private static void Mermaid()
    {
        const string source = "sequenceDiagram\nparticipant A as 前端\nparticipant B as 后端\nA->>B: 请求😀";
        Check(MermaidSequence.TryParse(source, out var diagram, out var error), "Mermaid fixture failed: " + error);
        var preview = new MarkdownPreview("```mermaid\n" + source + "\n```", 16, 680);
        Layout(preview, 720);
        var diagramBlock = preview.Document.Blocks.OfType<BlockUIContainer>().Single();
        preview.Selection.Select(diagramBlock.ElementStart, diagramBlock.ElementEnd);
        var result = ReadingClipboard.CreateFullDocumentData(preview);
        Check(result is not null, "Mermaid document export returned null");
        string html = (string)result!.Data.GetData(DataFormats.Html, false)!;
        string text = (string)result.Data.GetData(DataFormats.UnicodeText, false)!;
        Check(html.Contains("data:image/png;base64,", StringComparison.Ordinal),
            "Mermaid HTML did not contain a self-contained PNG: " + HtmlFragment(result.Data));
        Check(text.Contains("sequenceDiagram", StringComparison.Ordinal) &&
            text.Contains("A->>B: 请求😀", StringComparison.Ordinal),
            "Mermaid source text was not retained");
        var image = XDocument.Parse("<root>" + HtmlFragment(result.Data) + "</root>").Descendants("img").Single();
        using var stream = new MemoryStream(Convert.FromBase64String(
            ((string)image.Attribute("src")!).Split(',', 2)[1]));
        var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Check(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "Mermaid PNG is empty");
    }

    private static void SingleBitmap()
    {
        var box = Box(new Paragraph(new InlineUIContainer(Image())));
        box.SelectAll();
        var result = ReadingClipboard.CreateFullDocumentData(box);
        Check(result is not null, "single image export returned null");
        Check(result!.Data.GetDataPresent(DataFormats.Bitmap, false),
            "single image selection did not publish Bitmap");
        Check(result.Data.GetData(DataFormats.Bitmap, false) is BitmapSource bitmap &&
            bitmap.PixelWidth == 2 && bitmap.PixelHeight == 1,
            "Bitmap clipboard flavor did not retain the image pixels");
    }

    private static void MermaidFailure()
    {
        const string source = "sequenceDiagram\nparticipant A\nalt 暂不支持\nA->>B: 原文😀";
        var block = new ReadingBlock(ReadingBlockKind.Code, source, 0, source.Length, 0)
        { CodeLanguage = "mermaid" };
        var panel = new Border
        {
            Tag = block,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "图表暂未显示：语法不受支持" },
                    new TextBox { Text = source, IsReadOnly = true }
                }
            }
        };
        var box = Box(new BlockUIContainer(panel) { Tag = block }, new Paragraph(new Run(source)) { Tag = block });
        box.SelectAll();
        var result = ReadingClipboard.CreateFullDocumentData(box);
        Check(result is not null, "failed Mermaid export returned null");
        string html = (string)result!.Data.GetData(DataFormats.Html, false)!;
        string text = (string)result.Data.GetData(DataFormats.UnicodeText, false)!;
        Check(html.Contains("图表暂未显示：语法不受支持", StringComparison.Ordinal),
            "Mermaid failure warning was dropped from HTML: " + HtmlFragment(result.Data));
        Check(text.Contains("sequenceDiagram", StringComparison.Ordinal) &&
            text.Contains("A->>B: 原文😀", StringComparison.Ordinal),
            "Mermaid failure source was dropped from plain text");
        Check(!html.Contains("data:image/png;base64,", StringComparison.Ordinal),
            "Mermaid failure published a blank image");
    }

    private static void ImageReports()
    {
        var pending = new Image { Visibility = Visibility.Collapsed };
        var failed = new Grid { Children = { new Image { Visibility = Visibility.Collapsed },
            new TextBlock { Text = "图片加载失败：不存在" } } };
        var unsupported = new Image { Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 0, 255 }, 4) };
        var paragraph = new Paragraph(new InlineUIContainer(pending));
        paragraph.Inlines.Add(new InlineUIContainer(failed));
        paragraph.Inlines.Add(new InlineUIContainer(unsupported));
        for (int i = 0; i < 33; i++) paragraph.Inlines.Add(new InlineUIContainer(Image()));
        var result = ReadingClipboard.CreateFullDocumentData(Box(paragraph))!;
        var report = Property<object>(result, "Images");
        Check(Property<int>(report, "Pending") == 1 && Property<int>(report, "Failed") == 1 &&
            Property<int>(report, "Unsupported") == 1 && Property<int>(report, "Limited") == 1, "omission reasons are inaccurate");
        Check(result.EmbeddedImages == 32 && result.OmittedImages == 4, "image report totals are inconsistent");
    }

    private static void MetadataPaths()
    {
        foreach (string path in new[] { "C:/private/photo.png", @"D:\private\photo.png", @"\\server\private\photo.png", "/private/photo.png" })
        {
            var image = Image();
            AutomationProperties.SetName(image, path);
            var result = Export("CreateDocument", Box(new Paragraph(new InlineUIContainer(image))), "合成")!;
            Check(!Property<string>(result, "Html").Contains("private", StringComparison.Ordinal), "image metadata leaked a local path: " + path);
        }
    }

    private static void ImageLimitNotice()
    {
        var box = Box(new Paragraph(new InlineUIContainer(new TextBlock
            { Text = "图片未显示：本篇图片超过 32 张" })));
        var result = Export("CreateDocument", box, "合成")!;
        string html = Property<string>(result, "Html");
        var report = Property<object>(result, "Images");
        Check(html.Contains("本篇图片超过 32 张") && html.Contains("export-warning"),
            "the renderer's image-limit notice disappeared from portable HTML");
        Check(XDocument.Parse(html).Descendants("p").Single(p => (string?)p.Attribute("class") == "export-warning")
            .Value.Contains("部分图片因资源限制未加载，未包含在本次内容中"),
            "the image-limit summary does not explain the omission to the reader");
        Check(Property<bool>(report, "Incomplete") && Property<int>(report, "Omitted") == 0,
            "a count was invented for images without generated controls");
        var copy = ReadingClipboard.CreateFullDocumentData(box)!;
        Check(((string)copy.Data.GetData(DataFormats.Html, false)!).Contains("本篇图片超过 32 张"),
            "full rich copy silently omitted the image-limit notice");
    }

    private static Task CopyAsync(RichTextBox box, CancellationToken token, object? progress = null) =>
        (Task)Clip("CreateFullDocumentDataAsync", box, token, progress)!;

    private static object Result(Task task) => task.GetType().GetProperty("Result")!.GetValue(task)!;

    private static async Task WaitForImages()
    {
        var image = new Image { Visibility = Visibility.Collapsed };
        var box = Box(new Paragraph(new Run("合成")), new Paragraph(new InlineUIContainer(image)));
        var caret = box.Document.ContentStart;
        box.Selection.Select(caret, caret);
        caret = box.Selection.Start;
        int ownerThread = Environment.CurrentManagedThreadId;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => { timer.Stop(); image.Source = Image().Source; image.Visibility = Visibility.Visible; };
        timer.Start();
        var watch = Stopwatch.StartNew();
        var task = CopyAsync(box, CancellationToken.None);
        Check(!task.IsCompleted, "copy didn't wait for pending image");
        await task;
        var result = (ReadingClipboardResult)Result(task);
        Check(result.EmbeddedImages == 1 && result.OmittedImages == 0, "wait didn't export loaded image");
        Check(watch.ElapsedMilliseconds >= 250 && watch.ElapsedMilliseconds < 3000, "wait interval or completion incorrect");
        Check(Environment.CurrentManagedThreadId == ownerThread &&
            box.Selection.Start.CompareTo(caret) == 0 && box.Selection.IsEmpty, "wait changed thread or selection");
    }

    private static async Task CancelWait()
    {
        var box = Box(new Paragraph(new InlineUIContainer(new Image())));
        using var request = new CancellationTokenSource(80);
        var watch = Stopwatch.StartNew();
        await ThrowsAsync<OperationCanceledException>(() => CopyAsync(box, request.Token));
        Check(watch.ElapsedMilliseconds < 1500, "cancellation wasn't prompt");
    }

    private static async Task ReplacedDocument()
    {
        var box = Box(new Paragraph(new InlineUIContainer(new Image())));
        var task = CopyAsync(box, CancellationToken.None);
        box.Document = new FlowDocument(new Paragraph(new Run("新内容")));
        await ThrowsAsync<InvalidOperationException>(async () => await task);
    }

    private static async Task Timeout()
    {
        var box = Box(new Paragraph(new InlineUIContainer(new Image())));
        var watch = Stopwatch.StartNew();
        var task = CopyAsync(box, CancellationToken.None);
        await task;
        var result = (ReadingClipboardResult)Result(task);
        Check(result.EmbeddedImages == 0 && result.OmittedImages == 1, "timeout silently dropped image");
        Check(Property<int>(Property<object>(result, "Images"), "Pending") == 1, "timeout reason isn't pending");
        Check(watch.ElapsedMilliseconds >= 4500 && watch.ElapsedMilliseconds < 6500, "wait isn't bounded at five seconds");
    }

    private static async Task MutatedDocument()
    {
        var run = new Run("旧内容");
        var box = Box(new Paragraph(run), new Paragraph(new InlineUIContainer(new Image())));
        var task = CopyAsync(box, CancellationToken.None);
        run.Text = "新内容";
        await ThrowsAsync<InvalidOperationException>(async () => await task);
    }

    private sealed class WaitProgress : IProgress<ReadingImageWaitProgress>
    {
        internal readonly List<ReadingImageWaitProgress> Reports = [];
        public void Report(ReadingImageWaitProgress value) { lock (Reports) Reports.Add(value); }
    }

    private static async Task ProgressWithoutContext()
    {
        var image = new Image();
        var box = Box(new Paragraph(new InlineUIContainer(image)));
        var progress = new WaitProgress();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        Task task;
        try { task = CopyAsync(box, CancellationToken.None, progress); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        timer.Tick += (_, _) => { timer.Stop(); image.Source = Image().Source; };
        timer.Start();
        await task;
        Check(((ReadingClipboardResult)Result(task)).EmbeddedImages == 1, "copy touched WPF from a worker thread");
        lock (progress.Reports)
        {
            Check(progress.Reports.First().PendingImages == 1 && progress.Reports.Last().PendingImages == 0,
                "progress never reported pending-to-ready transition");
            Check(progress.Reports.Zip(progress.Reports.Skip(1)).All(p => p.Second.Elapsed >= p.First.Elapsed), "elapsed time moved backwards");
        }
    }

    private static async Task FailedImages()
    {
        var box = Box(new Paragraph(new InlineUIContainer(new Grid { Children =
        { new Image { Visibility = Visibility.Collapsed }, new TextBlock { Text = "图片加载失败：合成失败" } } })));
        var watch = Stopwatch.StartNew();
        var task = CopyAsync(box, CancellationToken.None);
        await task;
        Check(watch.ElapsedMilliseconds < 1000, "terminal image failure incorrectly waited five seconds");
        Check(Property<int>(Property<object>(Result(task), "Images"), "Failed") == 1, "failed image wasn't reported");
    }

    private static readonly string Evidence = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "sharing-v030"));

    private static Task Write(object result, string target, string[] protectedPaths, bool overwrite, CancellationToken token) =>
        (Task)Export("WriteAtomicAsync", result, target, protectedPaths, overwrite, token)!;

    private static async Task AtomicWrite()
    {
        Directory.CreateDirectory(Evidence);
        string dir = Path.Combine(Evidence, "atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string source = Path.Combine(dir, "original.md");
        await File.WriteAllTextAsync(source, "# 原文");
        string target = Path.Combine(dir, "export.html");
        var document = Export("CreateDocument", Box(new Paragraph(new Run("标题😀 & 正文"))), "合成报告")!;
        await Write(document, target, [source], false, CancellationToken.None);
        byte[] bytes = await File.ReadAllBytesAsync(target);
        Check(bytes.Length > 3 && bytes[0] != 0xef && Encoding.UTF8.GetString(bytes).Contains("标题😀 &amp; 正文"),
            "file isn't UTF8 without BOM or lost content");
        await ThrowsAsync<IOException>(() => Write(document, Path.Combine(dir, ".", "original.md"),
            [Path.Combine(dir, "other.md"), source.ToUpperInvariant()], true, CancellationToken.None));
        Check(await File.ReadAllTextAsync(source) == "# 原文", "export overwrote open source");
        Check(Directory.GetFiles(dir).Length == 2, "atomic write left a temporary file");
    }

    private static async Task Overwrite()
    {
        Directory.CreateDirectory(Evidence);
        string dir = Path.Combine(Evidence, "overwrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "target.html");
        await File.WriteAllTextAsync(target, "old");
        var document = Export("CreateDocument", Box(new Paragraph(new Run("new😀"))), "合成")!;
        await ThrowsAsync<IOException>(() => Write(document, target, [], false, CancellationToken.None));
        Check(await File.ReadAllTextAsync(target) == "old", "unconfirmed target changed");
        using var request = new CancellationTokenSource();
        request.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => Write(document, target, [], true, request.Token));
        Check(await File.ReadAllTextAsync(target) == "old" && Directory.GetFiles(dir).Length == 1,
            "cancelled write changed target or left temp file");
        await Write(document, target, [], true, CancellationToken.None);
        Check((await File.ReadAllTextAsync(target)).Contains("new😀"), "confirmed overwrite didn't replace target");
        await ThrowsAsync<IOException>(() => Write(document, @"\\example.invalid\share\export.html", [], true, CancellationToken.None));
    }

    private static async Task TrailingDot()
    {
        Directory.CreateDirectory(Evidence);
        string dir = Path.Combine(Evidence, "alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string source = Path.Combine(dir, "source.md");
        await File.WriteAllTextAsync(source, "# source");
        var document = Export("CreateDocument", Box(new Paragraph(new Run("export"))), "合成")!;
        await ThrowsAsync<IOException>(() => Write(document, source + ".", [source], true, CancellationToken.None));
        Check(await File.ReadAllTextAsync(source) == "# source", "trailing dot bypassed original-file protection");
    }

    private static void Budgets()
    {
        Check(Export("CreateDocument", Box(new Paragraph(new Run(new string('x', 500_001)))), "合成") is null,
            "portable HTML ignored body budget");
        var paragraph = new Paragraph();
        for (int i = 0; i < 33; i++) paragraph.Inlines.Add(new InlineUIContainer(Image()));
        var result = Export("CreateDocument", Box(paragraph), "合成")!;
        string html = Property<string>(result, "Html");
        Check(XDocument.Parse(html).Descendants("img").Count() == 32, "portable export ignored image count budget");
        Check(Property<int>(Property<object>(result, "Images"), "Limited") == 1 && html.Contains("图片未"),
            "portable export silently omitted excessive image");
    }

    private static void ContextMenu()
    {
        var code = Code("hello();");
        var box = Box(code, new Paragraph(new Run("正文")));
        ReadingClipboard.Attach(box, () => true);
        ReadingClipboard.Attach(box, () => true);
        Check(box.ContextMenu is not null, "explicit block copy context menu missing");
        var items = box.ContextMenu!.Items.OfType<MenuItem>().ToArray();
        Check(items.Count(item => Equals(item.Header, "复制当前代码块")) == 1 &&
            items.Count(item => Equals(item.Header, "复制当前表格")) == 1, "block commands missing or duplicated");
    }

    private static Image Image()
    {
        var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return new Image { Source = bitmap };
    }

    private static void Layout(FrameworkElement element, double width)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
        element.UpdateLayout();
    }

    private static RichTextBox Box(params Block[] blocks)
    {
        var document = new FlowDocument();
        foreach (var block in blocks) document.Blocks.Add(block);
        return new RichTextBox(document) { IsReadOnly = true, IsUndoEnabled = false };
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static string HtmlFragment(IDataObject data)
    {
        string cf = (string)data.GetData(DataFormats.Html, false)!;
        byte[] bytes = Encoding.UTF8.GetBytes(cf);
        int Offset(string field)
        {
            int start = cf.IndexOf(field + ":", StringComparison.Ordinal) + field.Length + 1;
            return int.Parse(cf.AsSpan(start, 10), CultureInfo.InvariantCulture);
        }
        int start = Offset("StartFragment");
        int end = Offset("EndFragment");
        return Encoding.UTF8.GetString(bytes, start, end - start);
    }

    private static void Pump(Func<Task> action)
    {
        var previous = SynchronizationContext.Current;
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = action();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
