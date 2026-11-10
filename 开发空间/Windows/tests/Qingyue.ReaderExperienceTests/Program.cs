using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Qingyue.Core;
using Qingyue.Windows;

internal static class Program
{
    private static int failures;
    private const string TableSource = "| **名称** | `code` | *右侧* |\n| :--- | :---: | ---: |\n| **粗体** | `a|b` | *斜体* |\n\n表后😀";
    [STAThread]
    private static int Main(string[] args)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        if (args.Contains("--perf")) { Performance(); return 0; }
        Check("unchanged preview retains document, selection, and decoded controls", CachedPreview);
        Check("table retains colon alignment and inline formats", TableFormatting);
        Check("table expansion is horizontal and leaves original table attached", TableExpansion);
        Check("full and selected table export retains real cell structure", TableClipboard);
        Check("image controls stop at the document budget while labels and ranges survive", ImageBudget);
        Check("image menu opens a decoded bitmap viewer without replacing selection", ImageInteraction);
        Check("decoded viewer supports fit, original pixels, bounded zoom and copy data", ImageViewer);
        Check("source projection and reordered table images retain labels and ranges", Projection);
        Check("rich map handles repeated styled text, lists, tables and UTF16", Mapping);
        Check("async construction yields and matches synchronous visible content", () => RunAsync(AsyncSuccess));
        Check("async cancellation discards pending document without touching live document", () => RunAsync(AsyncCancel));
        Check("async pre-cancellation and worker invocation honor contracts", () => RunAsync(AsyncContracts));
        Check("explicit builder boundary still executes for unchanged text", ExplicitBuildBoundary);
        Check("single Run image limit remains visible without building extra image controls", PlainBudgetNotice);
        Check("pending image actions are disabled and cannot open a viewer", PendingImageActions);
        Check("image viewer uses the owner's local and merged theme resources", ImageWindowTheme);
        Check("expanded table uses the owner's theme without moving source nodes", TableWindowTheme);
        Check("expanded star columns have readable actual widths", ExpandedStarColumns);
        Check("expanded pixel columns grow while retaining larger fixed widths", ExpandedPixelColumns);
        Console.WriteLine($"Reader experience: {19 - failures}/19 passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
    }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static string Text(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text;
    private static Table TableOf(MarkdownPreview preview) => preview.Document.Blocks.OfType<Table>().Single();
    private static Paragraph Cell(Table table, int row, int column) => (Paragraph)table.RowGroups[0].Rows[row].Cells[column].Blocks.FirstBlock;
    private static void CachedPreview()
    {
        var preview = new MarkdownPreview("# 标题\n\n**正文** ![图](missing.png)", 16, 680);
        var document = preview.Document;
        preview.Selection.Select(document.Blocks.LastBlock.ContentStart, document.Blocks.LastBlock.ContentEnd);
        var start = preview.Selection.Start;
        string selection = preview.Selection.Text;
        preview.Update(new string("# 标题\n\n**正文** ![图](missing.png)".ToCharArray()));
        Assert(ReferenceEquals(document, preview.Document), "unchanged text rebuilt document");
        Assert(preview.Selection.Text == selection && preview.Selection.Start.CompareTo(start) == 0, "selection moved");
        preview.UpdatePresentation(20, 420);
        preview.Update("# 标题\n\n**正文** ![图](missing.png)");
        Assert(ReferenceEquals(document, preview.Document) && document.FontSize == 20, "presentation invalidated content cache");
        preview.Update("# 已改");
        Assert(!ReferenceEquals(document, preview.Document), "changed source incorrectly reused document");
    }
    private static void ExplicitBuildBoundary()
    {
        var preview = new MarkdownPreview("原文", 16, 680);
        bool propagated = false;
        try { preview.Update("原文", _ => throw new OutOfMemoryException("synthetic; no allocation")); }
        catch (OutOfMemoryException) { propagated = true; }
        Assert(propagated, "content cache bypassed an explicit build boundary");
    }
    private static void PlainBudgetNotice()
    {
        string source = string.Join("\n\n", Enumerable.Range(0, 40).Select(i => $"![图{i}](missing-{i}.png)"));
        var reading = new RenderedReading(ReadingModel.Build(source, TextKind.Markdown), sourceText: source);
        Assert(Controls(reading.Document.Blocks).OfType<TextBlock>().Any(t => t.Text.Contains("32")), "single Run renderer lost limit notice");
    }
    private static void TableFormatting()
    {
        var table = TableOf(new MarkdownPreview(TableSource, 16, 680));
        Assert(Cell(table, 1, 0).TextAlignment == TextAlignment.Left &&
            Cell(table, 1, 1).TextAlignment == TextAlignment.Center &&
            Cell(table, 1, 2).TextAlignment == TextAlignment.Right, "Markdown colon alignment lost");
        Assert(Cell(table, 1, 0).Inlines.OfType<Run>().Any(r => r.FontWeight == FontWeights.Bold), "strong lost");
        Assert(Cell(table, 1, 1).Inlines.OfType<Run>().Any(r => r.Text == "a|b" && r.FontFamily.Source.Contains("Consolas")), "code lost");
        Assert(Cell(table, 1, 2).Inlines.OfType<Run>().Any(r => r.FontStyle == FontStyles.Italic), "emphasis lost");
    }
    private static void TableExpansion()
    {
        var preview = new MarkdownPreview(TableSource, 16, 680);
        var table = TableOf(preview);
        Assert(table.ContextMenu is not null, "table has no expansion entry");
        var item = table.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.Contains("展开"));
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var window = Dispatcher.CurrentDispatcher.Invoke(() => ApplicationWindows().Single(w => w.GetType().Name == "ReaderTableWindow"));
        try
        {
            Assert(ReferenceEquals(table.Parent, preview.Document), "expansion reparented live table");
            var host = Descendants<RichTextBox>(window).Single();
            Assert(host.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto, "expanded table cannot scroll horizontally");
            Assert(host.Document.Blocks.OfType<Table>().Single() != table && host.Document.PageWidth >= 900, "expansion did not create independent wide table");
            Assert(!Text(host.Document).Contains("展开"), "control label leaked into table copy");
            window.Width = 420;
            window.UpdateLayout();
            Assert(host.ExtentWidth > host.ViewportWidth + 100, "narrow expanded table has no horizontal overflow");
            host.ScrollToHorizontalOffset(180);
            window.UpdateLayout();
            Assert(host.HorizontalOffset > 0, "expanded table cannot actually scroll");
        }
        finally { window.Close(); }
    }
    private static IEnumerable<Window> ApplicationWindows() =>
        PresentationSource.CurrentSources.Cast<PresentationSource>().OfType<System.Windows.Interop.HwndSource>()
            .Select(s => s.RootVisual).OfType<Window>();
    private static Window ThemeOwner(UIElement content)
    {
        var owner = new Window { Content = content, Width = 640, Height = 400 };
        var merged = new ResourceDictionary
        {
            ["EditorBrush"] = Brushes.Lavender,
            ["TextBrush"] = Brushes.LightGoldenrodYellow,
            ["PanelBrush"] = Brushes.DarkSlateBlue,
            ["BorderBrush"] = Brushes.SlateGray,
            ["AccentTextBrush"] = Brushes.LightSkyBlue,
            ["SyntaxStringBrush"] = Brushes.PaleGreen,
            ["MutedBrush"] = Brushes.Gray
        };
        merged.MergedDictionaries.Add(new ResourceDictionary { ["NestedThemeValue"] = "nested" });
        owner.Resources.MergedDictionaries.Add(merged);
        owner.Resources["EditorBrush"] = Brushes.MidnightBlue;
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 23d));
        owner.Resources[typeof(Button)] = style;
        return owner;
    }
    private static void ImageWindowTheme()
    {
        var image = new Image { Source = Bitmap() };
        var view = new Border { Child = image };
        ReaderImageWindow.Attach(view, image, "主题图片");
        var owner = ThemeOwner(view);
        try
        {
            owner.Show();
            view.ContextMenu!.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var window = ApplicationWindows().OfType<ReaderImageWindow>().Single();
            Assert(ReferenceEquals(window.Owner, owner), "viewer lost owner");
            Assert(ReferenceEquals(window.Background, Brushes.MidnightBlue) &&
                ReferenceEquals(window.Foreground, Brushes.LightGoldenrodYellow), "viewer lost owner-local palette or resource precedence");
            Assert(Descendants<Button>(window).All(button => button.FontSize == 23), "viewer lost implicit button style");
            Assert((string)window.FindResource("NestedThemeValue") == "nested", "viewer lost nested merged resource");
            window.Resources["EditorBrush"] = Brushes.Black;
            Assert(ReferenceEquals(owner.Resources["EditorBrush"], Brushes.MidnightBlue), "viewer shares mutable resource dictionary with owner");
        }
        finally
        {
            foreach (var window in ApplicationWindows().OfType<ReaderImageWindow>().ToArray()) window.Close();
            owner.Close();
        }
    }
    private static void TableWindowTheme()
    {
        var preview = new MarkdownPreview(TableSource, 16, 680);
        var owner = ThemeOwner(preview);
        try
        {
            owner.Show();
            var table = TableOf(preview);
            table.ContextMenu!.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var window = ApplicationWindows().OfType<ReaderTableWindow>().Single();
            var host = Descendants<RichTextBox>(window).Single();
            Assert(ReferenceEquals(window.Owner, owner), "expanded table lost owner");
            Assert(ReferenceEquals(window.Background, Brushes.MidnightBlue) &&
                ReferenceEquals(host.Background, Brushes.MidnightBlue) &&
                ReferenceEquals(host.Foreground, Brushes.LightGoldenrodYellow), "expanded table lost owner-local palette");
            var clone = host.Document.Blocks.OfType<Table>().Single();
            Assert(ReferenceEquals(clone.RowGroups[0].Rows[0].Background, Brushes.DarkSlateBlue), "expanded header lost themed row background");
            Assert(Cell(clone, 1, 1).Inlines.OfType<Run>().Any(run => ReferenceEquals(run.Foreground, Brushes.PaleGreen)), "expanded code lost theme");
            Assert((string)window.FindResource("NestedThemeValue") == "nested", "expanded table lost merged resources");
            Assert(ReferenceEquals(table.Parent, preview.Document), "theme setup moved source table");
        }
        finally
        {
            foreach (var window in ApplicationWindows().OfType<ReaderTableWindow>().ToArray()) window.Close();
            owner.Close();
        }
    }
    private static void ExpandedStarColumns() => ExpandedColumnWidths(
        new[] { new GridLength(1, GridUnitType.Star), new GridLength(4, GridUnitType.Star), new GridLength(1, GridUnitType.Star) },
        minimumMiddleWidth: 590);
    private static void ExpandedPixelColumns() => ExpandedColumnWidths(
        new[] { new GridLength(48), new GridLength(500), new GridLength(48) },
        minimumMiddleWidth: 490);
    private static void ExpandedColumnWidths(GridLength[] widths, double minimumMiddleWidth)
    {
        var preview = new MarkdownPreview("| A | B | C |\n| --- | --- | --- |\n| 一 | 二 | 三 |", 16, 640);
        var table = TableOf(preview);
        for (int column = 0; column < widths.Length; column++) table.Columns[column].Width = widths[column];
        table.ContextMenu!.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var window = ApplicationWindows().OfType<ReaderTableWindow>().Single();
        try
        {
            window.Width = 420;
            window.UpdateLayout();
            var host = Descendants<RichTextBox>(window).Single();
            var clone = host.Document.Blocks.OfType<Table>().Single();
            double x0 = Cell(clone, 1, 0).ContentStart.GetCharacterRect(LogicalDirection.Forward).X;
            double x1 = Cell(clone, 1, 1).ContentStart.GetCharacterRect(LogicalDirection.Forward).X;
            double x2 = Cell(clone, 1, 2).ContentStart.GetCharacterRect(LogicalDirection.Forward).X;
            Assert(x1 - x0 >= 190, $"expanded first column stays cramped: {x1 - x0:F1}px");
            Assert(x2 - x1 >= minimumMiddleWidth, $"expanded middle column loses source width: {x2 - x1:F1}px");
            Assert(host.ExtentWidth > host.ViewportWidth + 100, "expanded columns have no horizontal overflow");
            host.ScrollToHorizontalOffset(250);
            window.UpdateLayout();
            Assert(host.HorizontalOffset > 0, "expanded columns do not actually scroll");
            Assert(table.Columns.Select(column => column.Width).SequenceEqual(widths), "expansion changed live source widths");
            Console.WriteLine($"expanded {widths[0].GridUnitType}: actual_columns={x1 - x0:F1},{x2 - x1:F1}px; " +
                $"extent={host.ExtentWidth:F1}px; viewport={host.ViewportWidth:F1}px; scrolled={host.HorizontalOffset:F1}px");
        }
        finally { window.Close(); }
    }
    private static void TableClipboard()
    {
        var preview = new MarkdownPreview(TableSource, 16, 680);
        var data = ReadingClipboard.CreateFullDocumentData(preview);
        Assert(data?.Data.GetData(DataFormats.Html) is string full && full.Contains("<table"), "full copy lost table");
        var table = TableOf(preview);
        preview.Selection.Select(Cell(table, 1, 0).ContentStart, Cell(table, 1, 2).ContentEnd);
        var copying = new DataObjectCopyingEventArgs(new DataObject(DataFormats.UnicodeText, preview.Selection.Text), false);
        ReadingClipboard.Attach(preview, () => true);
        preview.RaiseEvent(copying);
        Assert(copying.DataObject.GetData(DataFormats.Html) is string html && html.Contains("<td"), "selection copy lost cells");
    }
    private static void ImageBudget()
    {
        string source = string.Join("\n\n", Enumerable.Range(0, 1000).Select(i => $"![图{i}](missing-{i}.png)"));
        var preview = new MarkdownPreview(source, 16, 680);
        Assert(ImageControls(preview.Document).Count() == 32, "over-budget images still allocate heavy controls");
        Assert(Text(preview.Document).Contains("[图片：图999]"), "over-budget caption lost");
        Assert(Text(preview.Document).Contains("32"), "budget limit must be visible");
        var last = (ReadingBlock)preview.Document.Blocks.LastBlock.Tag;
        Assert(last.SourceStart == source.IndexOf("![图999]", StringComparison.Ordinal), "tail source range changed");
        var model = ReadingModel.Build(source, TextKind.Markdown);
        var plain = new RenderedReading(model, sourceText: source);
        Assert(ImageControls(plain.Document).Count() == 32, "single Run renderer exceeds control budget");
    }
    private static void ImageInteraction()
    {
        var preview = new MarkdownPreview("前文 ![图](missing.png) 后文", 16, 680);
        var view = ImageControls(preview.Document).Single();
        var image = Descendants<Image>(view).Single();
        image.Source = Bitmap();
        image.Visibility = Visibility.Visible;
        preview.Selection.Select(preview.Document.ContentStart, preview.Document.ContentEnd);
        string selected = preview.Selection.Text;
        Assert(view.ContextMenu is not null, "image has no context menu");
        view.ContextMenu!.Items.OfType<MenuItem>().First(i => i.Header.ToString()!.Contains("放大"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var window = ApplicationWindows().Single(w => w.GetType().Name == "ReaderImageWindow");
        try
        {
            Assert(ReferenceEquals(Descendants<Image>(window).Single().Source, image.Source), "viewer redecode or source replacement");
            Assert(preview.Selection.Text == selected, "image action changed reading selection");
        }
        finally { window.Close(); }
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent };
        view.RaiseEvent(click);
        var clickedWindow = ApplicationWindows().Single(w => w.GetType().Name == "ReaderImageWindow");
        try
        {
            Assert(click.Handled && preview.Selection.Text == selected, "image click changed selection or escaped the control");
        }
        finally { clickedWindow.Close(); }
    }
    private static void PendingImageActions()
    {
        var preview = new MarkdownPreview("![未加载](missing.png)", 16, 680);
        var view = ImageControls(preview.Document).Single();
        var menu = view.ContextMenu!;
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert(menu.Items.OfType<MenuItem>().All(item => !item.IsEnabled), "pending image offers enabled actions");
        menu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert(!ApplicationWindows().Any(w => w.GetType().Name == "ReaderImageWindow"), "pending image opened a viewer");
    }
    private static BitmapSource Bitmap()
    {
        var bitmap = BitmapSource.Create(200, 100, 192, 192, PixelFormats.Bgr32, null, new byte[200 * 100 * 4], 200 * 4);
        bitmap.Freeze();
        return bitmap;
    }
    private static void ImageViewer()
    {
        var window = new ReaderImageWindow(Bitmap(), "图片");
        try
        {
            window.Show();
            window.UpdateLayout();
            var image = Descendants<Image>(window).Single();
            var buttons = Descendants<Button>(window).ToArray();
            void Click(string caption) => buttons.Single(b => b.Content.ToString() == caption).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Click("原图");
            Assert(image.Width == 200 && image.Height == 100, "original must use decoded pixel size, independent of DPI");
            Click("放大");
            Assert(image.Width > 200, "zoom in failed");
            Click("缩小");
            Assert(Math.Abs(image.Width - 200) < .01, "zoom out failed");
            Click("适应窗口");
            Assert(double.IsFinite(image.Width) && image.Width > 0, "fit is invalid");
            Assert(buttons.Any(b => b.Content.ToString() == "复制图片"), "image copy action absent");
            Assert(window.CommandBindings.OfType<CommandBinding>().Any(b => b.Command == ApplicationCommands.Copy), "viewer copy does not handle Ctrl+C");
            for (int i = 0; i < 40; i++) Click("放大");
            Assert(image.Width <= 1600, "zoom has no upper bound");
            for (int i = 0; i < 80; i++) Click("缩小");
            Assert(image.Width >= 2, "zoom has no lower bound");
            window.Width = 380;
            window.UpdateLayout();
            var last = buttons[^1];
            Assert(last.TranslatePoint(new Point(last.ActualWidth, 0), window).X <= window.ActualWidth,
                "narrow image window clips copy button");
        }
        finally { window.Close(); }
    }
    private static void Projection()
    {
        const string source = "# 标题\n\n| 一 | 二 |\n| --- | --- |\n| ![后](https://invalid.test/b) ![前](missing.png) | `code` |\n\n![尾](https://invalid.test/c)\n\n末尾";
        var preview = new MarkdownPreview(source, 16, 680);
        string text = Text(preview.Document);
        Assert(text.Contains("[图片：后]") && text.Contains("[图片：前]") && !text.Contains("qyimg"), "image restoration leaked markers");
        var last = (ReadingBlock)preview.Document.Blocks.LastBlock.Tag;
        Assert(last.SourceStart == source.LastIndexOf("末尾", StringComparison.Ordinal) && last.SourceLength == 2, "original projection range wrong");
    }
    private static void Mapping()
    {
        string source = "# 标题😀\n\n" + string.Concat(Enumerable.Repeat("**相同** *内容* ", 500)) +
            "\n\n" + TableSource + "\n\n- **列表起点**\n- 项尾😀";
        var model = ReadingModel.Build(source, TextKind.Markdown);
        var reading = RenderedReading.Markdown(model, source, null, 16, 680);
        foreach (string token in new[] { "标题😀", "相同 内容", "粗体", "a|b", "斜体", "表后😀", "列表起点", "项尾😀" })
        {
            int offset = model.VisibleText.IndexOf(token, StringComparison.Ordinal);
            var start = reading.PointerAt(offset);
            Assert(new TextRange(start, reading.PointerAt(offset + token.Length)).Text == token &&
                reading.OffsetAt(start) == offset, "mapping broken: " + token);
        }
    }
    private static Task<RenderedReading> BuildAsync(ReadingModel model, string source, CancellationToken token)
        => RenderedReading.MarkdownAsync(model, source, null, 16, 680, token);
    private static async Task AsyncSuccess()
    {
        string source = string.Join("\n\n", Enumerable.Range(0, 180).Select(i => $"**段落{i}** 😀")) + "\n\n" + TableSource;
        var model = await Task.Run(() => ReadingModel.Build(source, TextKind.Markdown));
        bool heartbeat = false;
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => heartbeat = true);
        var task = BuildAsync(model, source, CancellationToken.None);
        Assert(!task.IsCompleted, "construction never yielded");
        var reading = await task;
        Assert(heartbeat && reading.Document.Dispatcher.CheckAccess(), "UI heartbeat or thread affinity failed");
        Assert(Text(reading.Document) == Text(RenderedReading.Markdown(model, source, null, 16, 680).Document), "async and sync differ");
        int index = model.VisibleText.IndexOf("段落179", StringComparison.Ordinal);
        Assert(new TextRange(reading.PointerAt(index), reading.PointerAt(index + 5)).Text == "段落179", "async map failed");
    }
    private static async Task AsyncCancel()
    {
        var live = new MarkdownPreview("保持 **选区**", 16, 680);
        live.SelectAll();
        var document = live.Document;
        string selection = live.Selection.Text;
        string source = string.Join("\n\n", Enumerable.Range(0, 200).Select(i => $"段落{i}"));
        var model = ReadingModel.Build(source, TextKind.Markdown);
        using var cts = new CancellationTokenSource();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => cts.Cancel());
        try { await BuildAsync(model, source, cts.Token); throw new InvalidOperationException("cancellation was ignored"); }
        catch (OperationCanceledException) { }
        Assert(ReferenceEquals(document, live.Document) && live.Selection.Text == selection, "cancellation polluted displayed document");
        var result = await BuildAsync(model, source, CancellationToken.None);
        Assert(Text(result.Document).Contains("段落199"), "cancelled build poisoned later build");
    }
    private static async Task AsyncContracts()
    {
        var model = ReadingModel.Build("正文", TextKind.Markdown);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { await BuildAsync(model, "正文", cts.Token); throw new InvalidOperationException("pre-cancel ignored"); }
        catch (OperationCanceledException) { }
        bool rejected = false;
        try { await Task.Run(() => BuildAsync(model, "正文", CancellationToken.None)); }
        catch (Exception ex) when (ex.GetBaseException() is InvalidOperationException) { rejected = true; }
        Assert(rejected, "worker thread silently created WPF document");
    }
    private static void RunAsync(Func<Task> action)
    {
        var task = action();
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    private static IEnumerable<FrameworkElement> ImageControls(FlowDocument document) =>
        Controls(document.Blocks).Where(c => c.GetType().Name == "MarkdownImageView");
    private static IEnumerable<FrameworkElement> Controls(BlockCollection blocks)
    {
        foreach (Block block in blocks)
        {
            if (block is BlockUIContainer { Child: FrameworkElement child }) yield return child;
            if (block is Paragraph p)
                foreach (var inline in p.Inlines.OfType<InlineUIContainer>())
                    if (inline.Child is FrameworkElement control) yield return control;
            if (block is Table table)
                foreach (var group in table.RowGroups)
                    foreach (var row in group.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var control in Controls(cell.Blocks)) yield return control;
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    private static void Performance()
    {
        foreach (int count in new[] { 2000, 4000, 8000 })
        {
            string source = string.Concat(Enumerable.Repeat("**abcdefgh** ", count));
            var model = ReadingModel.Build(source, TextKind.Markdown);
            var document = new MarkdownPreview(source, 16, 680).Document;
            var watch = Stopwatch.StartNew();
            _ = new RichDocumentMap(document, model);
            Console.WriteLine($"map tokens={count} ms={watch.Elapsed.TotalMilliseconds:F2}");
        }
        foreach (int count in new[] { 500, 1000, 2000 })
        {
            string source = string.Join("\n\n", Enumerable.Range(0, count).Select(i => $"![图{i}](https://invalid.test/{i})"));
            var images = MarkdownImages.Extract(source);
            var blocks = ReadingModel.Build(images.Text, TextKind.Markdown).Blocks;
            var watch = Stopwatch.StartNew();
            foreach (var block in blocks) _ = images.RestoreLabels(block.Text);
            Console.Write($"refs count={count} restore_ms={watch.Elapsed.TotalMilliseconds:F2}");
            watch.Restart();
            foreach (var block in blocks) _ = images.OriginalRange(block);
            Console.Write($" ranges_ms={watch.Elapsed.TotalMilliseconds:F2}");
            watch.Restart();
            var preview = new MarkdownPreview(source, 16, 680);
            Console.WriteLine($" build_ms={watch.Elapsed.TotalMilliseconds:F2}");
            watch.Restart();
            var doc = preview.Document;
            preview.Update(source);
            Console.WriteLine($"cache count={count} ms={watch.Elapsed.TotalMilliseconds:F2} reused={ReferenceEquals(doc, preview.Document)}");
        }
        var budget = new MarkdownPreview(string.Join("\n\n", Enumerable.Range(0, 1000).Select(i => $"![图{i}](missing-{i}.png)")), 16, 680);
        Console.WriteLine($"local references=1000 controls={ImageControls(budget.Document).Count()}");
    }
}
