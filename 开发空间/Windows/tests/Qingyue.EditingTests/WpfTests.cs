using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Qingyue.Core;
using Qingyue.Windows;
using Path = System.IO.Path;

internal static partial class Program
{
    static partial void RunWpfTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "qingyue-editing-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Qingyue.EditingTests;component/Theme.xaml", UriKind.Relative)
        });
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow(Path.Combine(root, "profile"), restoreSession: false);
                window.Show();
                await window.InitializeAsync();
                typeof(MainWindow).GetMethod("InitializeEditingInteractions", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, null);
                await Idle(window);

                Test("自动命名空文档仍是占位文档，手动改名后恢复资格保留", () =>
                {
                    var empty = window.CurrentTab!;
                    Equal(empty.File.Title, "新文件 1");
                    Equal(empty.IsEmpty, true);
                    window.RenameTab(empty, "手动空文档");
                    Equal(empty.IsEmpty, false);
                    window.RenameTab(empty, "新文件 1");
                    Equal(empty.IsEmpty, false);
                    var restoredEmpty = new EditorTab(DocumentFile.Restore(empty.File.CaptureSnapshot()));
                    Equal(restoredEmpty.IsEmpty, false);
                });
                Test("标签空白双击新建，双击现有标签与关闭按钮不被新建处理截获", () =>
                {
                    var row = (Border)window.FindName("TabRow");
                    var strip = (TabControl)window.FindName("TabStrip");
                    int before = window.Tabs.Count;
                    Equal(window.HandleTabEditingPress(row, 2, new Point()), true);
                    Equal(window.Tabs.Count, before + 1);
                    window.UpdateLayout();
                    var item = (TabItem)strip.ItemContainerGenerator.ContainerFromIndex(0);
                    Equal(window.HandleTabEditingPress(item, 2, new Point()), false);
                    Equal(window.Tabs.Count, before + 1);
                    var close = Descendants<Button>(item).First();
                    Equal(window.HandleTabEditingPress(close, 2, new Point()), false);
                    Equal(window.Tabs.Count, before + 1);
                    Equal(window.HandleTabEditingPress(row, 1, new Point()), false);
                });
                Test("拖动排序保持同一个TextBox及选中文档，支持向左右移动", () =>
                {
                    window.NewDocument("third");
                    window.UpdateLayout();
                    var strip = (TabControl)window.FindName("TabStrip");
                    var first = window.Tabs[0];
                    var editor = first.Editor;
                    var lastItem = (TabItem)strip.ItemContainerGenerator.ContainerFromIndex(window.Tabs.Count - 1);
                    double right = lastItem.TranslatePoint(new Point(lastItem.ActualWidth, 0), strip).X + 10;
                    Equal(window.TryReorderEditingTab(first, new Point(right, 5)), true);
                    Equal(window.Tabs[^1], first);
                    Equal(window.CurrentTab, first);
                    Equal(first.Editor, editor);
                    window.UpdateLayout();
                    Equal(window.TryReorderEditingTab(first, new Point(-10, 5)), true);
                    Equal(window.Tabs[0], first);
                });
                var tab = window.NewDocument(new string('中', 220) + "，连续片段。\n第二逻辑行");
                await Idle(window);
                Test("WPF双击整段中文，三击跨软换行选择完整逻辑行", () =>
                {
                    var rect = tab.Editor.GetRectFromCharacterIndex(80);
                    Equal(tab.SelectTextAt(new Point(rect.Left + 1, rect.Top + rect.Height / 2), 2), true);
                    Equal(tab.Editor.SelectedText, new string('中', 220));
                    tab.SelectTextAt(new Point(rect.Left + 1, rect.Top + rect.Height / 2), 3);
                    Equal(tab.Editor.SelectedText, new string('中', 220) + "，连续片段。");
                });

                string path = Path.Combine(root, "baseline.txt");
                File.WriteAllText(path, "a\nb\nc");
                await window.OpenPathsAsync([path]);
                var disk = window.CurrentTab!;
                await Idle(window);
                disk.Editor.BeginChange();
                try
                {
                    disk.Editor.Select(2, 1);
                    disk.Editor.SelectedText = "B";
                }
                finally { disk.Editor.EndChange(); }
                await WaitForChanges(disk);
                Test("WPF磁盘修改显示橙条与未保存tooltip", () =>
                {
                    ChangesInTab(disk, (2, LineChangeKind.Unsaved, false));
                    var markers = Descendants<Rectangle>(disk.EditorSurface).Where(r => r.ToolTip is string).ToArray();
                    Equal(markers.Length, 1);
                    Equal(((SolidColorBrush)markers[0].Fill).Color, ((SolidColorBrush)window.FindResource("UnsavedBrush")).Color);
                    if (!markers[0].ToolTip.ToString()!.Contains("未保存")) throw new Exception("缺少非颜色提示");
                    window.Resources["UnsavedBrush"] = Brushes.Magenta;
                    Equal(((SolidColorBrush)markers[0].Fill).Color, Brushes.Magenta.Color);
                    window.Resources.Remove("UnsavedBrush");
                    Equal(disk.Editor.CanUndo, true);
                });
                window.SaveTabToPath(disk, path);
                await WaitForChanges(disk);
                Test("WPF成功保存后橙转绿", () =>
                {
                    ChangesInTab(disk, (2, LineChangeKind.Saved, false));
                    var marker = Descendants<Rectangle>(disk.EditorSurface).Single(r => r.ToolTip is string);
                    Equal(((SolidColorBrush)marker.Fill).Color, ((SolidColorBrush)window.FindResource("SavedBrush")).Color);
                    if (!marker.ToolTip.ToString()!.Contains("已保存")) throw new Exception("缺少已保存提示");
                });
                disk.Editor.Undo();
                await WaitForChanges(disk);
                Test("WPF保存后撤销恢复初始原文仍显示橙条", () =>
                {
                    Equal(disk.Editor.Text.Replace("\r\n", "\n"), "a\nb\nc");
                    ChangesInTab(disk, (2, LineChangeKind.Unsaved, false));
                });
                window.SaveTabToPath(disk, path);
                await WaitForChanges(disk);
                Test("WPF将原始内容再次保存后全部标记清除", () => ChangesInTab(disk));
                disk.Editor.Select(2, 0);
                disk.Editor.SelectedText = "new\n";
                await WaitForChanges(disk);
                disk.Editor.Undo();
                await WaitForChanges(disk);
                Test("WPF插入后撤销到最近保存内容清除橙条", () => ChangesInTab(disk));

                var savedOrder = window.Tabs.Where(t => !t.IsEmpty).Select(t => t.File.Id).ToArray();
                await window.PersistWorkspaceAsync();
                var restored = await new WorkspaceStore(Path.Combine(root, "profile")).LoadAsync();
                Test("重排后的标签顺序真实持久化并可恢复", () =>
                {
                    if (!restored.Documents.Select(d => d.File.Id).SequenceEqual(savedOrder))
                        throw new Exception("持久化标签顺序不一致");
                });
                var snapshot = disk.File.CaptureSnapshot() with { Text = "a\nrestored\nc" };
                var recovered = new EditorTab(DocumentFile.Restore(snapshot));
                var host = new Window { Content = recovered.EditorSurface, Width = 500, Height = 200 };
                host.Show();
                await WaitForChanges(recovered);
                Test("恢复草稿以快照磁盘基线标橙且不伪造已保存绿条", () =>
                    ChangesInTab(recovered, (2, LineChangeKind.Unsaved, false)));
                recovered.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
                await Idle(host);
                Test("文本双击不截获滚动条的点击", () =>
                {
                    var scroll = Descendants<ScrollBar>(recovered.Editor).First(s => s.Orientation == Orientation.Vertical);
                    var point = scroll.TranslatePoint(new Point(scroll.ActualWidth / 2, scroll.ActualHeight / 2), recovered.Editor);
                    Equal(recovered.SelectTextAt(point, 2), false);
                });
                host.Close();
                await RunMarkdownWpfTests(window);
            }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL WPF测试流程\n" + ex); }
            finally
            {
                window?.CloseForShutdown();
                app.Shutdown();
                Directory.Delete(root, recursive: true);
            }
        };
        app.Run();
    }

    private static async Task Idle(Window window) =>
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);

    private static async Task WaitForChanges(EditorTab tab)
    {
        // Include the debounce timer and the worker's return to the dispatcher.
        await Task.Delay(350);
        await tab.Editor.Dispatcher.InvokeAsync(() => tab.Editor.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }

    private static async Task RunMarkdownWpfTests(MainWindow window)
    {
        var tab = window.NewDocument("- 中文");
        tab.AutomaticKind = false;
        tab.Kind = TextKind.Markdown;
        await Idle(window);
        Test("WPF Markdown单次命令撤销重做且保存为原始标记", () =>
        {
            tab.Editor.CaretIndex = 4;
            var writer = typeof(EditorTab).GetProperty("MarkdownWriter",
                BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(tab)
                ?? throw new Exception("编辑器尚未绑定Markdown命令");
            var execute = writer.GetType().GetMethod("Execute")!;
            Equal(execute.Invoke(writer, [MarkdownEditAction.Enter]), true);
            Equal(tab.Editor.Text.ReplaceLineEndings("\n"), "- 中文\n- ");
            tab.Editor.Undo();
            Equal(tab.Editor.Text, "- 中文");
            tab.Editor.Redo();
            Equal(tab.Editor.Text.ReplaceLineEndings("\n"), "- 中文\n- ");
            tab.Editor.Undo();
            tab.Editor.Select(2, 2);
            execute.Invoke(writer, [MarkdownEditAction.Bold]);
            Equal(tab.Editor.SelectedText, "中文");
            Equal(tab.Editor.Text, "- **中文**");
            tab.Editor.Undo();
            Equal(tab.Editor.Text, "- 中文");
        });
        Test("WPF Markdown之外保留原生输入行为", () =>
        {
            tab.Kind = TextKind.Plain;
            var writer = typeof(EditorTab).GetProperty("MarkdownWriter",
                BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(tab)
                ?? throw new Exception("缺少编辑命令");
            Equal(writer.GetType().GetMethod("Execute")!.Invoke(writer, [MarkdownEditAction.Enter]), false);
            Equal(tab.Editor.Text, "- 中文");
        });
        tab.Kind = TextKind.Markdown;
        window.SelectTab(tab);
        Test("WPF中文组合输入期间不执行Markdown结构命令", () =>
        {
            string before = tab.Editor.Text;
            var composition = new TextComposition(InputManager.Current, tab.Editor, "中");
            tab.Editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            Equal(tab.MarkdownWriter.Execute(MarkdownEditAction.Enter), false);
            Equal(tab.Editor.Text, before);
            tab.Editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            tab.Editor.CaretIndex = tab.Editor.Text.Length;
            Equal(tab.MarkdownWriter.Execute(MarkdownEditAction.Enter), true);
            tab.Editor.Undo();
            Equal(tab.Editor.Text, before);
        });
        Test("WPF Markdown编写工具栏操作保留选区", () =>
        {
            var toolbar = window.FindName("MarkdownWritingToolbar") as FrameworkElement
                ?? throw new Exception("缺少Markdown编写工具栏");
            Equal(toolbar.Visibility, Visibility.Visible);
            tab.Editor.Select(2, 2);
            var bold = (Button)window.FindName("MarkdownBoldButton");
            bold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(tab.Editor.Text, "- **中文**");
            Equal(tab.Editor.SelectedText, "中文");
            tab.Editor.Undo();
        });
        typeof(MainWindow).GetMethod("ToggleMarkdownPreview", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, null);
        await Idle(window);
        Test("WPF Markdown分屏双向定位与过时预览保护", () =>
        {
            var preview = (MarkdownPreview)typeof(MainWindow).GetField("markdownPreview",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            bool Locate(string name) => (bool)(typeof(MainWindow).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(window, null)
                ?? throw new Exception("缺少分屏定位：" + name));
            Equal(Locate("LocateMarkdownPreview"), true);
            preview.CaretPosition = preview.Document.ContentStart;
            Equal(Locate("LocateMarkdownEditor"), true);
            tab.Editor.AppendText("\n新行");
            Equal(Locate("LocateMarkdownEditor"), false);
        });
        tab.Editor.Text = "-";
        await Idle(window);
        tab.Editor.Select(1, 0);
        tab.Editor.SelectedText = " ";
        await Task.Delay(100);
        await Idle(window);
        Test("WPF Markdown横线后输入空格立即激活列表显示", () =>
        {
            Equal(tab.Editor.Text, "- ");
            Equal(tab.Editor.Foreground, Brushes.Transparent);
            Equal(tab.MarkdownWriter.Markers.Count, 1);
        });
        window.Width = 1280; window.Height = 850;
        window.ApplyTheme(false);
        const string sample = "# Markdown 编写\n\n- 第一阶段\n  - 梳理业务流程\n    - 明确输入和输出\n    - 整理异常情况\n  - 每周总结一次\n- 第二阶段\n\n- [ ] 检查待办事项\n- [x] 已完成验收\n\n1. 收集资料\n2. 整理笔记\n\n> 编写和阅读都能看清层次。\n";
        tab.Editor.Text = sample;
        typeof(MainWindow).GetMethod("UpdateMarkdownPreview", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [tab]);
        tab.Editor.CaretIndex = sample.IndexOf("- 第一", StringComparison.Ordinal) + 2;
        await Task.Delay(100);
        await Idle(window);
        Test("WPF Markdown自然绘制显示即时圆点且不改变源码", () =>
        {
            var syntax = tab.EditorSurface.Children.OfType<JsonSyntaxLayer>().Single();
            Equal(tab.Editor.Foreground, Brushes.Transparent);
            if (VisualTreeHelper.GetDrawing(syntax) is not { } drawing)
                throw new Exception("实际覆盖层没有绘制内容");
            static IEnumerable<Drawing> Drawings(DrawingGroup group) => group.Children
                .SelectMany(d => d is DrawingGroup child ? Drawings(child) : [d]);
            Equal(Drawings(drawing).OfType<GeometryDrawing>().Any(d => d.Geometry is EllipseGeometry), true);
            Equal(tab.Editor.Text.ReplaceLineEndings("\n"), sample);
        });
        if (Environment.GetEnvironmentVariable("QINGYUE_MARKDOWN_SCREENSHOTS") is { Length: > 0 } screenshotRoot)
        {
            Directory.CreateDirectory(screenshotRoot);
            Screenshot(window, Path.Combine(screenshotRoot, "markdown-writing-light.png"));
            window.ApplyTheme(true);
            await Task.Delay(100);
            await Idle(window);
            Screenshot(window, Path.Combine(screenshotRoot, "markdown-writing-dark.png"));
        }
        await RunMarkdownNavigationChecks(window);
    }

    private static void Screenshot(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void ChangesInTab(EditorTab tab, params (int, LineChangeKind, bool)[] expected)
    {
        var actual = tab.LineChanges.Select(c => (c.Line, c.Kind, c.IsDeletion)).ToArray();
        if (!actual.SequenceEqual(expected))
            throw new Exception($"期望 [{string.Join(", ", expected)}]，实际 [{string.Join(", ", actual)}]");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject element) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
