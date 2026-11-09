using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Qingyue.Core;
using Qingyue.Windows;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "qingyue-ui-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Qingyue;component/Theme.xaml")
        });
        int checks = 0, result = 1;
        void Check(bool valid, string name)
        {
            if (!valid) throw new Exception(name);
            checks++;
            Console.WriteLine("PASS " + name);
        }
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                var started = Stopwatch.StartNew();
                JsonSyntaxChecks.Check(Check);
                window = new MainWindow(Path.Combine(root, "profile"), restoreSession: false);
                window.Show();
                await window.InitializeAsync();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Build2Checks.CheckEntryPoints(window, Check);
                var first = window.CurrentTab!;
                first.Editor.Text = "文档一 😀\n独立撤销";
                first.Editor.CaretIndex = first.Editor.Text.Length;
                first.Editor.AppendText("追加");
                var second = window.NewDocument("文档二\n第二行");
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check(second.LineNumbers.Text.Contains('2'), "标签首次显示即生成多行行号");
                second.Editor.CaretIndex = second.Editor.Text.Length;
                second.Editor.AppendText("改变");
                second.Editor.Undo();
                Check(second.Editor.Text == "文档二\n第二行", "第二标签独立撤销");
                Check(first.Editor.Text.EndsWith("追加"), "撤销不影响第一标签");
                var numbered = window.NewDocument("甲\n\n乙\n\n丙");
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check(numbered.LineNumbers.Text == "1\n2\n3\n4\n5", "初次打开连续空行的行号不重复");
                window.SelectTab(first);
                Check(ReferenceEquals(window.CurrentTab?.Editor, first.Editor), "切回标签保留原编辑器");

                string filePath = Path.Combine(root, "中文配置.json");
                Check(window.SaveTabToPath(first, filePath), "窗口保存到中文路径");
                first.Editor.AppendText("\n未保存内容");
                window.RenameTab(first, "重命名.json");
                Check(first.File.Title == "重命名.json", "窗口更新重命名标签");
                Check(!File.ReadAllText(first.File.Path!).Contains("未保存内容"), "重命名不写回脏正文");
                Check(first.File.IsDirty && first.Editor.Text.Contains("未保存内容"), "重命名保留未保存编辑");

                var markdown = window.NewDocument("# 阅读标题\n\n这是 **中文** 与 😀。\n\n## 第二章\n- 一项\n");
                window.ToggleReading();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check(markdown.IsReading, "进入原生阅读视图");
                Check(markdown.Reading?.Headings.Count == 2, "阅读目录生成");
                Check(markdown.Reading?.VisibleText.Contains("中文") == true, "阅读显示中文");
                Check(markdown.Preview.IsReadOnly, "阅读页面不允许修改原文");
                Check(markdown.Rendered!.PointerAt(0).GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward).Left >= 20,
                    "首次阅读排版保留正文边距");
                window.ToggleReading();
                Check(!markdown.IsReading && markdown.Editor.Text.Contains("**中文**"), "返回编辑保留原始 Markdown");

                window.ToggleSidebar();
                Check(window.FindName("SidebarToggle") is Button { IsVisible: true }, "收起侧栏后仍有展开按钮");
                window.ToggleSidebar();
                window.ToggleFocus();
                Check(window.FindName("FocusButton") is Button { IsVisible: true }, "专注模式保留退出入口");
                window.ToggleFocus();
                await window.PersistWorkspaceAsync();
                var store = new WorkspaceStore(Path.Combine(root, "profile"));
                var restored = await store.LoadAsync();
                Check(restored.Documents.Any(x => x.File.Text.Contains("未保存内容")), "窗口草稿可恢复脏正文");
                Check(restored.Documents.Any(x => x.File.Text.StartsWith("# 阅读标题")), "未命名 Markdown 草稿可恢复");

                var countBefore = window.Tabs.Count;
                await window.OpenPathsAsync([first.File.Path!]);
                Check(window.Tabs.Count == countBefore, "同一路径打开不会重复标签");

                window.SelectTab(markdown);
                markdown.Editor.AppendText("\n## 新增目录\n正文\n");
                await WaitFor(() => window.FindName("OutlineList") is ListBox { Items.Count: 3 });
                Check(((ListBox)window.FindName("OutlineList")).Items.Count == 3, "编辑后目录异步刷新");
                window.SelectTab(markdown);
                window.ToggleReading();
                int headingOffset = markdown.Reading!.Headings[1].DisplayStart;
                var headingPointer = markdown.Rendered!.PointerAt(headingOffset);
                markdown.Preview.Selection.Select(headingPointer, headingPointer);
                int sourceAnchor = markdown.Reading.DisplayToSource(headingOffset);
                var persisted = await window.PersistWorkspaceAsync();
                Check(persisted.Errors.Count == 0, "更新草稿与会话成功：" + string.Join(';', persisted.Errors));
                window.CloseForShutdown();
                window = new MainWindow(Path.Combine(root, "profile"));
                window.Show();
                await window.InitializeAsync();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                markdown = window.CurrentTab!;
                Check(markdown.IsReading && markdown.Reading!.DisplayToSource(
                    markdown.Rendered!.OffsetAt(markdown.Preview.Selection.Start)) == sourceAnchor, "重启恢复实际阅读位置");
                ((ComboBox)window.FindName("KindCombo")).SelectedItem = TextKind.Plain;
                Check(markdown.IsReading && markdown.Rendered!.OffsetAt(markdown.Preview.Selection.Start) == sourceAnchor, "阅读中切类型保持源位置");
                ((ComboBox)window.FindName("KindCombo")).SelectedItem = TextKind.Markdown;
                ClickAction(window, "find");
                ((TextBox)window.FindName("SearchQuery")).Text = "中文";
                await WaitFor(() => ((TextBlock)window.FindName("SearchCount")).Text == "1 处匹配");
                ClickAction(window, "next");
                Check(markdown.IsReading && markdown.Preview.Selection.Text == "中文", "阅读查找选中实际可见文字");
                ClickAction(window, "hideFind");

                var searchTab = window.NewDocument("x x x x x x x x x x x");
                ClickAction(window, "find");
                ((TextBox)window.FindName("SearchQuery")).Text = "x";
                ((TextBox)window.FindName("ReplacementText")).Text = new string('a', 1_000_000);
                await WaitFor(() => ((TextBlock)window.FindName("SearchCount")).Text == "11 处匹配");
                Check(true, "仅查找不受隐藏替换内容上限影响");
                ((TextBox)window.FindName("ReplacementText")).Text = "";
                ClickAction(window, "hideFind");
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                searchTab.Editor.Text = "甲\r\n中文 😀 中文";
                ClickAction(window, "replace");
                ((TextBox)window.FindName("SearchQuery")).Text = "中文";
                ((TextBox)window.FindName("ReplacementText")).Text = "值$";
                await WaitFor(() => ((TextBlock)window.FindName("SearchCount")).Text == "2 处匹配");
                ClickAction(window, "replaceAll");
                Check(searchTab.File.Text == "甲\n值$ 😀 值$", "CRLF源文替换偏移与字面替换正确");
                searchTab.Editor.Undo();
                Check(searchTab.File.Text == "甲\n中文 😀 中文", "全部替换可一次撤销");
                ClickAction(window, "hideFind");
                var jsonTab = window.NewDocument("{\"n\":9007199254740993,\"e\":1e+400}");
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                ClickAction(window, "format");
                Check(jsonTab.File.Text.Contains("9007199254740993") && jsonTab.File.Text.Contains("1e+400") && jsonTab.File.Text.Contains('\n'), "窗口JSON格式化保留大整数与指数");
                jsonTab.Editor.Undo();
                Check(jsonTab.File.Text == "{\"n\":9007199254740993,\"e\":1e+400}", "窗口格式化可撤销");
                await Build2Checks.CheckFormatting(window, Check);
                var namedEmpty = window.NewDocument();
                window.RenameTab(namedEmpty, "空文档.md");
                var namedResult = await window.PersistWorkspaceAsync();
                Check(namedResult.Errors.Count == 0 && (await new WorkspaceStore(Path.Combine(root, "profile")).LoadAsync())
                    .Documents.Any(d => d.File.Title == "空文档.md"), "未命名空文档起名后可恢复名称");

                window.Width = 1100;
                window.Height = 780;
                window.SelectTab(markdown);
                if (!markdown.IsReading) window.ToggleReading();
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var artifacts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
                Directory.CreateDirectory(artifacts);
                SaveScreenshot(window, Path.Combine(artifacts, "windows-build1-dark.png"));
                window.ToggleReading();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, Path.Combine(artifacts, "windows-build1-dark-edit.png"));
                window.ToggleReading();
                await VerifyComboContrast(window, "dark", artifacts, Check);
                window.ApplyTheme(false);
                window.Width = 840;
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                SaveScreenshot(window, Path.Combine(artifacts, "windows-build1-light-narrow.png"));
                await VerifyComboContrast(window, "light", artifacts, Check);
                // Existing popup containers must update too, including while the list is open.
                var kindCombo = (ComboBox)window.FindName("KindCombo");
                kindCombo.IsDropDownOpen = true;
                window.ApplyTheme(true);
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check(ItemsHaveContrast(kindCombo), "列表展开时切换深色主题仍可读");
                window.ApplyTheme(false);
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check(ItemsHaveContrast(kindCombo), "列表展开时切换浅色主题仍可读");
                kindCombo.IsDropDownOpen = false;
                Check(window.ActualWidth >= 820, "窄窗口保持最小可用宽度");
                var exitWindow = new MainWindow(Path.Combine(root, "exit-profile"), restoreSession: false);
                try
                {
                    exitWindow.Show();
                    await exitWindow.InitializeAsync();
                    exitWindow.NewDocument("需要放弃的测试内容");
                    _ = exitWindow.Dispatcher.BeginInvoke(() =>
                    {
                        var dialog = app.Windows.OfType<Window>().First(w => w.Owner == exitWindow && w.Title == "处理未保存的修改");
                        var discard = FindVisualButton(dialog, b => Equals(b.Content, "放弃"))!;
                        discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    exitWindow.Close();
                    await WaitFor(() => !exitWindow.IsVisible);
                    var discarded = await new WorkspaceStore(Path.Combine(root, "exit-profile")).LoadAsync();
                    Check(discarded.Documents.Count == 0, "退出放弃当前标签可关闭且不复活草稿");
                }
                finally { if (exitWindow.IsVisible) exitWindow.CloseForShutdown(); }
                using var process = Process.GetCurrentProcess();
                File.WriteAllText(Path.Combine(artifacts, "ui-results.json"), JsonSerializer.Serialize(new
                {
                    checks, elapsedMs = started.ElapsedMilliseconds,
                    workingSetBytes = process.WorkingSet64,
                    notes = "WPF真实控件流程；非人工输入法或屏幕阅读器验收。"
                }, new JsonSerializerOptions { WriteIndented = true }));
                result = 0;
                Console.WriteLine($"WPF 流程检查：{checks} 项通过。");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                window?.CloseForShutdown();
                app.Shutdown(result);
            }
        };
        app.Run();
        try { Directory.Delete(root, true); } catch { }
        return result;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("等待界面状态超时。");
            await Task.Delay(25);
        }
    }

    private static async Task VerifyComboContrast(MainWindow window, string theme, string artifacts, Action<bool, string> check)
    {
        var tab = window.CurrentTab!;
        bool wasReading = tab.IsReading;
        if (wasReading)
        {
            window.ToggleReading();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        var displayedForeground = tab.Editor.Foreground is SolidColorBrush { Color.A: 0 }
            ? (Brush)window.FindResource("TextBrush") : tab.Editor.Foreground;
        check(Contrast(displayedForeground, tab.Editor.Background) >= 4.5,
            $"{theme} 可见编辑正文对比度至少 4.5:1");
        check(new[] { "SyntaxKeyBrush", "SyntaxStringBrush", "SyntaxValueBrush", "SyntaxCommentBrush" }
            .All(key => Contrast((Brush)window.FindResource(key), tab.Editor.Background) >= 4.5),
            $"{theme} 语法分色对比度至少 4.5:1");
        check(Contrast(tab.LineNumbers.Foreground, BackgroundAt(tab.LineNumbers)) >= 4.5,
            $"{theme} 行号对比度至少 4.5:1");
        if (wasReading)
        {
            window.ToggleReading();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(Contrast(tab.Preview.Foreground, tab.Preview.Background) >= 4.5,
                $"{theme} 可见阅读正文对比度至少 4.5:1");
        }
        foreach (string name in new[] { "KindCombo", "CodecCombo", "EndingCombo" })
        {
            var combo = (ComboBox)window.FindName(name);
            var selection = combo.SelectedItem;
            combo.Focus();
            combo.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
            check(popup is { IsOpen: true, Child: FrameworkElement { ActualHeight: > 0 } },
                $"{theme} {name} 可展开且列表已排版");
            SaveVisualScreenshot((FrameworkElement)popup.Child, Path.Combine(artifacts, $"combo-{theme}-{name}.png"));
            check(ItemsHaveContrast(combo), $"{theme} {name} 所有选项文字对比度至少 4.5:1");
            var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex((combo.SelectedIndex + 1) % combo.Items.Count);
            item.Focus();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(item.IsKeyboardFocusWithin && Contrast(item.Foreground, BackgroundAt(item)) >= 4.5,
                $"{theme} {name} 键盘焦点选项清晰");
            combo.SelectedItem = item.Content;
            check(Equals(combo.SelectedItem, item.Content), $"{theme} {name} 仍可选择选项");
            combo.SelectedItem = selection;
            combo.IsDropDownOpen = false;
            check(!popup.IsOpen, $"{theme} {name} 可关闭列表");
            var toggle = combo.Template.FindName("DropDownToggle", combo) as Control;
            check(Contrast(combo.Foreground, BackgroundAt(toggle ?? combo)) >= 4.5,
                $"{theme} {name} 关闭区域文字对比度至少 4.5:1");
        }
    }

    private static bool ItemsHaveContrast(ComboBox combo) =>
        Enumerable.Range(0, combo.Items.Count).All(i =>
            combo.ItemContainerGenerator.ContainerFromIndex(i) is ComboBoxItem item &&
            Contrast(item.Foreground, BackgroundAt(item)) >= 4.5);

    private static Brush BackgroundAt(DependencyObject element)
    {
        // Read the rendered template first: item state backgrounds live on its Border.
        if (element is Control control && VisualTreeHelper.GetChildrenCount(control) > 0 &&
            VisualTreeHelper.GetChild(control, 0) is Border frame &&
            frame.Background is SolidColorBrush { Color.A: 255 } frameBrush)
            return frameBrush;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            Brush? brush = current switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                Control parent => parent.Background,
                _ => null
            };
            if (brush is SolidColorBrush { Color.A: 255 }) return brush;
        }
        throw new Exception("未找到控件实际不透明背景。");
    }

    private static double Contrast(Brush foreground, Brush background)
    {
        static double Luminance(Brush brush)
        {
            var color = ((SolidColorBrush)brush).Color;
            static double Linear(byte value) => value / 255.0 <= 0.04045
                ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + 0.055) / 1.055, 2.4);
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        double first = Luminance(foreground), second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static void ClickAction(MainWindow window, string action)
    {
        MenuItem? Find(ItemsControl parent)
        {
            foreach (var item in parent.Items.OfType<MenuItem>())
            {
                if (Equals(item.Tag, action)) return item;
                var result = Find(item);
                if (result is not null) return result;
            }
            return null;
        }
        var menu = Find((Menu)window.FindName("MainMenu"));
        if (menu is not null) { menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); return; }
        var target = FindVisualButton(window, b => Equals(b.Tag, action)) ?? throw new Exception("操作不存在：" + action);
        target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static Button? FindVisualButton(DependencyObject parent, Func<Button, bool> matches)
    {
        if (parent is Button button && matches(button)) return button;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindVisualButton(VisualTreeHelper.GetChild(parent, i), matches);
            if (found is not null) return found;
        }
        return null;
    }

    internal static void SaveScreenshot(Window window, string path)
    {
        SaveVisualScreenshot((FrameworkElement)window.Content, path);
    }

    private static void SaveVisualScreenshot(FrameworkElement client, string path)
    {
        var target = new RenderTargetBitmap((int)client.ActualWidth, (int)client.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(client);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(target));
        using var output = File.Create(path);
        png.Save(output);
    }
}
