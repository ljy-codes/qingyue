using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Qingyue.Windows;

internal static class MainWindowSharingChecks
{
    private static readonly string Evidence = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "sharing-v030"));

    internal static void Run(Action<string, Action> test)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Qingyue;component/Theme.xaml")
        });
        try
        {
            test("v030 MainWindow exports cached image pixels and preserves focus selection scroll and original",
                () => Pump(ExportPreservesReading));
            test("v030 MainWindow refuses stale full-width reading data before exporting",
                () => Pump(StaleReading));
            test("v030 MainWindow protects original opened through a directory junction from real-path overwrite",
                () => Pump(SourceJunction));
            test("v030 MainWindow async export immediately includes pending split input",
                () => Pump(SplitExportLatest));
            test("v030 MainWindow async copy refreshes pending split input before its cancellable wait",
                () => Pump(SplitCopyLatest));
            test("v030 MainWindow stable split sharing preserves document focus selection and scroll",
                () => Pump(StableSplitSharing));
        }
        finally { application.Shutdown(); }
    }

    private static async Task WithWindow(Func<MainWindow, string, string, Task> action)
    {
        string root = Path.Combine(Evidence, "window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pixels = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixels));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        string markdown = "# 合成分享标题\n\n![合成图](data:image/png;base64," +
            Convert.ToBase64String(stream.ToArray()) + ")\n\n" +
            string.Concat(Enumerable.Range(0, 40).Select(i => $"正文第 {i} 段，中文与😀。\n\n"));
        string source = Path.Combine(root, "合成原文.md");
        await File.WriteAllTextAsync(source, markdown);
        var window = new MainWindow(Path.Combine(root, "isolated-profile"), restoreSession: false)
        {
            ShowInTaskbar = false, Width = 960, Height = 700
        };
        try
        {
            window.Show();
            await window.InitializeAsync();
            await window.OpenPathsAsync([source]);
            await Idle(window);
            await action(window, root, source);
        }
        finally { window.CloseForShutdown(); }
    }

    private static Task ExportPreservesReading() => WithWindow(async (window, root, source) =>
    {
        var tab = window.CurrentTab!;
        var preview = tab.Preview;
        preview.Focus();
        await Idle(window);
        int offset = tab.Reading!.VisibleText.IndexOf("正文第 3", StringComparison.Ordinal);
        preview.Selection.Select(tab.Rendered!.PointerAt(offset), tab.Rendered.PointerAt(offset + 4));
        preview.ScrollToVerticalOffset(140);
        await Idle(window);
        var document = preview.Document;
        var selectionStart = preview.Selection.Start;
        var selectionEnd = preview.Selection.End;
        var focus = Keyboard.FocusedElement;
        double vertical = preview.VerticalOffset, horizontal = preview.HorizontalOffset;
        Check(focus is not null && vertical > 0, "fixture did not establish focus and scroll");
        string output = Path.Combine(root, "portable.html");
        await window.ExportReadingHtmlToPathAsync(output);
        Check(File.Exists(output), "window export failed: " + Status(window));
        byte[] bytes = await File.ReadAllBytesAsync(output);
        string html = Encoding.UTF8.GetString(bytes);
        var exported = XDocument.Parse(html);
        Check(exported.Descendants("h1").Single().Value == "合成分享标题" &&
            exported.Descendants("img").Single().Attribute("src")!.Value.StartsWith("data:image/png;base64,"),
            "the hosted export lost heading or cached pixels");
        Check(!html.Contains("file:", StringComparison.OrdinalIgnoreCase) && !html.Contains(root) &&
            !exported.Descendants("p").Any(p => (string?)p.Attribute("class") == "export-warning"),
            "hosted export leaked a path or missed a loaded image");
        Check(ReferenceEquals(document, preview.Document) && ReferenceEquals(focus, Keyboard.FocusedElement) &&
            preview.Selection.Start.CompareTo(selectionStart) == 0 && preview.Selection.End.CompareTo(selectionEnd) == 0 &&
            Math.Abs(vertical - preview.VerticalOffset) < .1 && Math.Abs(horizontal - preview.HorizontalOffset) < .1,
            "export changed document, focus, selection or scroll");
        string original = await File.ReadAllTextAsync(source);
        await window.ExportReadingHtmlToPathAsync(source, overwrite: true);
        Check(await File.ReadAllTextAsync(source) == original && Status(window).Contains("不能覆盖"),
            "window export did not protect the opened original");
        using var request = new CancellationTokenSource();
        request.Cancel();
        string cancelled = Path.Combine(root, "cancelled.html");
        await window.ExportReadingHtmlToPathAsync(cancelled, cancellationToken: request.Token);
        Check(!File.Exists(cancelled) && Status(window).Contains("已取消"), "window cancellation published a file");
    });

    private static Task StaleReading() => WithWindow(async (window, root, _) =>
    {
        var tab = window.CurrentTab!;
        tab.File.Text += "\n当前原文已变化。";
        string output = Path.Combine(root, "stale.html");
        await window.ExportReadingHtmlToPathAsync(output);
        Check(!File.Exists(output) && Status(window).Contains("未完成"),
            "stale full-width preview was exported as current source");
    });

    private static async Task<RichTextBox> Split(MainWindow window)
    {
        typeof(MainWindow).GetMethod("ExecuteAction", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, ["markdownPreview"]);
        await Idle(window);
        Check(!window.CurrentTab!.IsReading, "fixture did not enter split editing");
        return (RichTextBox)typeof(MainWindow).GetField("markdownPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
    }

    private static Task SplitExportLatest() => WithWindow(async (window, root, _) =>
    {
        var preview = await Split(window);
        window.CurrentTab!.Editor.AppendText("\n异步导出必须立即包含的新正文。");
        string output = Path.Combine(root, "split-latest.html");
        // No Dispatcher yield: the debounce timer must not refresh the fixture first.
        await window.ExportReadingHtmlToPathAsync(output);
        Check(File.Exists(output), "immediate split export failed: " + Status(window));
        Check(XDocument.Parse(await File.ReadAllTextAsync(output)).Descendants("body").Single().Value
            .Contains("异步导出必须立即包含的新正文。"), "async export used the previous split content");
        Check(new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text
            .Contains("异步导出必须立即包含的新正文。"), "async export did not refresh the pending split");
    });

    private static Task SplitCopyLatest() => WithWindow(async (window, _, _) =>
    {
        var preview = await Split(window);
        window.CurrentTab!.Editor.AppendText("\n异步复制必须立即包含的新正文。");
        using var request = new CancellationTokenSource();
        var copy = window.CopyReadingDocumentAsync(request.Token);
        // Cancel before pumping the Dispatcher, so the real API never writes the system clipboard.
        request.Cancel();
        bool refreshed = new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text
            .Contains("异步复制必须立即包含的新正文。");
        await copy;
        Check(refreshed, "async copy refused pending split input instead of synchronously refreshing it");
        Check(Status(window).Contains("已取消全文复制"), "async copy did not reach its cancellable image wait");
    });

    private static Task StableSplitSharing() => WithWindow(async (window, root, _) =>
    {
        var preview = await Split(window);
        preview.Focus();
        var paragraph = preview.Document.Blocks.OfType<Paragraph>().First(p =>
            new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("正文第 3 段"));
        preview.Selection.Select(paragraph.ContentStart, paragraph.ContentEnd);
        preview.ScrollToVerticalOffset(140);
        await Idle(window);
        var document = preview.Document;
        var start = preview.Selection.Start;
        var end = preview.Selection.End;
        var focus = Keyboard.FocusedElement;
        double vertical = preview.VerticalOffset, horizontal = preview.HorizontalOffset;
        Check(!preview.Selection.IsEmpty && vertical > 0, "fixture did not establish split selection and scroll");
        string output = Path.Combine(root, "stable-split.html");
        await window.ExportReadingHtmlToPathAsync(output);
        Check(File.Exists(output), "stable split export failed: " + Status(window));
        using var request = new CancellationTokenSource();
        var copy = window.CopyReadingDocumentAsync(request.Token);
        request.Cancel();
        await copy;
        Check(Status(window).Contains("已取消全文复制"), "stable split copy did not reach its cancellable wait");
        Check(ReferenceEquals(document, preview.Document) && ReferenceEquals(focus, Keyboard.FocusedElement) &&
            preview.Selection.Start.CompareTo(start) == 0 && preview.Selection.End.CompareTo(end) == 0 &&
            Math.Abs(vertical - preview.VerticalOffset) < .1 && Math.Abs(horizontal - preview.HorizontalOffset) < .1,
            "stable split sharing rebuilt the document or changed focus, selection or scroll");
    });

    private static async Task SourceJunction()
    {
        string root = Path.Combine(Evidence, "source-junction-" + Guid.NewGuid().ToString("N"));
        string real = Path.Combine(root, "real");
        string alias = Path.Combine(root, "alias");
        Directory.CreateDirectory(real);
        string source = Path.Combine(real, "source.md");
        byte[] original = Encoding.UTF8.GetBytes("# 合成联接原文\n\n必须逐字节保留。\n");
        await File.WriteAllBytesAsync(source, original);
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "/c", "mklink", "/J", alias, real }) start.ArgumentList.Add(argument);
        using (var process = Process.Start(start)!)
        {
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Check(process.ExitCode == 0, "synthetic junction creation failed: " + output + error);
        }
        Check((File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0 &&
            File.ReadAllBytes(Path.Combine(alias, "source.md")).SequenceEqual(original),
            "fixture is not an alias of the synthetic source");
        var window = new MainWindow(Path.Combine(root, "isolated-profile"), restoreSession: false)
        { ShowInTaskbar = false };
        try
        {
            window.Show();
            await window.InitializeAsync();
            await window.OpenPathsAsync([Path.Combine(alias, "source.md")]);
            await Idle(window);
            await window.ExportReadingHtmlToPathAsync(source, overwrite: true);
            Check((await File.ReadAllBytesAsync(source)).SequenceEqual(original) && Status(window).Contains("目录联接"),
                "export through the real path overwrote the source opened through its directory junction");
            Check(!Directory.GetFiles(real, ".qingyue-html-*.tmp").Any(), "blocked alias export left a temporary file");
        }
        finally { window.CloseForShutdown(); }
    }

    private static string Status(MainWindow window) => ((TextBlock)window.FindName("StatusText")).Text;
    private static Task Idle(MainWindow window) =>
        window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static void Pump(Func<Task> action)
    {
        var previous = SynchronizationContext.Current;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
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
