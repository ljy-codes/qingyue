using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Qingyue.Core;
using Qingyue.Windows;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        checks++;
        Console.WriteLine("PASS " + message);
    }

    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "qingyue-navigation-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{typeof(MainWindow).Assembly.GetName().Name};component/Theme.xaml")
        });
        int result = 1;
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow(Path.Combine(root, "profile"), restoreSession: false);
                window.Show();
                await window.InitializeAsync();
                var original = window.CurrentTab!;
                string path = Path.Combine(root, "navigation.md");
                File.WriteAllText(path, string.Concat(Enumerable.Range(1, 100).Select(i =>
                    $"# Chapter {i}\n\n" + string.Concat(Enumerable.Repeat($"Section {i} 阅读内容保持位置。\n\n", 8)))));
                var pending = window.OpenPathsAsync([path]);
                var cancel = typeof(MainWindow).GetMethod("CancelLoading", BindingFlags.Instance | BindingFlags.Public);
                Check(cancel is not null, "打开任务提供可取消入口");
                Check(((FrameworkElement)window.FindName("OperationPanel")).Visibility == Visibility.Visible,
                    "打开时立即显示阶段和进度");
                cancel!.Invoke(window, null);
                await pending;
                Check(window.Tabs.Count == 1 && ReferenceEquals(window.CurrentTab, original),
                    "取消读取不替换已有文档");
                Check(!original.File.IsDirty || original.File.Text.Length == 0, "取消不修改已有正文");
                bool uiYielded = false;
                _ = window.Dispatcher.BeginInvoke(() => uiYielded = true, DispatcherPriority.Background);
                await window.OpenPathsAsync([path]);
                await Idle(window);
                var tab = window.CurrentTab!;
                Check(tab.IsReading && uiYielded, "Markdown打开期间允许UI处理队列");
                tab.Preview.ScrollToVerticalOffset(tab.Preview.ExtentHeight * .55);
                await Idle(window);
                double before = tab.Preview.VerticalOffset;
                var capture = typeof(MainWindow).GetMethod("CaptureWorkspaceEntry", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(capture is not null, "会话提供实际视口快照");
                var entry = (WorkspaceEntry)capture!.Invoke(window, [tab])!;
                int viewport = (int?)typeof(WorkspaceEntry).GetProperty("ViewportAnchor")?.GetValue(entry) ?? -1;
                Check(viewport > 100 && tab.Preview.Selection.IsEmpty, "保存屏幕可见段落且不改变复制选区");
                var rendered = tab.Rendered;
                window.ToggleReading();
                Check(!tab.IsReading && tab.Editor.CaretIndex > 100, "切换编辑定位到实际阅读段落");
                Check(tab.Rendered is null, "退出阅读后释放内存中的渲染文档");
                window.ToggleReading();
                await Idle(window);
                Check(tab.Rendered is not null && !ReferenceEquals(rendered, tab.Rendered),
                    "重新进入阅读按当前正文生成渲染文档");
                Check(Math.Abs(before - tab.Preview.VerticalOffset) < 80, "编辑预览往返保持屏幕位置");
                Invoke(window, "ShowSearch", false);
                ((TextBox)window.FindName("SearchQuery")).Text = "Chapter 100";
                await Task.Delay(350);
                await Idle(window);
                Check(tab.Preview.Selection.Text == "Chapter 100", "查找仍自动高亮");
                Invoke(window, "HideSearch");
                await Idle(window);
                Check(Math.Abs(before - tab.Preview.VerticalOffset) < 80, "退出查找回到查找前阅读视口");
                var outline = (ListBox)window.FindName("OutlineList");
                object? NavigationStack() => typeof(EditorTab)
                    .GetProperty("Navigation", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(tab);
                int StackCount() => (int)NavigationStack()!.GetType().GetProperty("Count")!.GetValue(NavigationStack())!;
                var returnButton = (Button)window.FindName("ReturnPositionButton");
                Check(StackCount() == 0 && !returnButton.IsEnabled, "目录跳转前导航栈为空");
                outline.SelectedIndex = outline.Items.Count - 1;
                await Idle(window);
                Check(tab.Preview.VerticalOffset > before + 100, "目录选择跳转到目标章节");
                Check(StackCount() == 1 && returnButton.IsEnabled, "目录跳转按 macOS 压入导航位置");
                Invoke(window, "ReturnToReadingPosition");
                await Idle(window);
                Check(Math.Abs(before - tab.Preview.VerticalOffset) < 80, "支持返回目录跳转前位置");
                Check(StackCount() == 0 && !returnButton.IsEnabled, "返回后导航栈与按钮状态同步");
                outline.SelectedIndex = outline.Items.Count - 1;
                await Idle(window);
                outline.SelectedIndex = 0;
                await Idle(window);
                Check(StackCount() == 2, "连续目录跳转形成后进先出导航栈");
                Invoke(window, "ReturnToReadingPosition");
                await Idle(window);
                Check(StackCount() == 1, "第一次返回只弹出一层导航");
                Invoke(window, "ReturnToReadingPosition");
                await Idle(window);
                Check(StackCount() == 0 && !returnButton.IsEnabled, "第二次返回弹出剩余导航并禁用按钮");
                for (int i = 0; i < 70; i++)
                    Invoke(window, "RememberReadingPosition", tab);
                Check(StackCount() == 64, "导航栈严格限制为 macOS 的 64 项");
                while (StackCount() > 0)
                    Invoke(window, "ReturnToReadingPosition");
                await Idle(window);
                string otherText = string.Join("\n\n", Enumerable.Range(1, 80)
                    .Select(index => $"Other paragraph {index} 阅读内容保持位置。"));
                var other = window.NewDocument(otherText);
                int otherSourceAnchor = otherText.IndexOf("Other paragraph 55", StringComparison.Ordinal);
                other.Editor.CaretIndex = otherSourceAnchor;
                window.ToggleReading();
                await Idle(window);
                other.Preview.ScrollToVerticalOffset(other.Preview.ExtentHeight * .6);
                await Idle(window);
                double otherViewport = other.Preview.VerticalOffset;
                Check(other.IsReading && otherViewport > 100, "第二个标签进入阅读并记录独立视口");
                window.SelectTab(tab);
                await Idle(window);
                Check(Math.Abs(before - tab.Preview.VerticalOffset) < 80, "切换标签保留实际阅读位置");
                Check(File.ReadAllText(path) == tab.File.Text && !tab.File.IsDirty, "导航与取消不改写原件");
                string pendingPath = Path.Combine(root, "cancel-render.md");
                string pendingText = string.Concat(Enumerable.Range(1, 1500).Select(i => $"# Item {i}\n\nParagraph {i}.\n\n"));
                File.WriteAllText(pendingPath, pendingText);
                var rendering = window.OpenPathsAsync([pendingPath]);
                await Until(() => window.CurrentTab?.File.Path == pendingPath);
                var interrupted = window.CurrentTab!;
                window.SelectTab(other);
                await rendering;
                Check(interrupted.IsReading && interrupted.File.Text == pendingText && interrupted.Rendered is null,
                    "排版期间切换标签释放资源且保留原文与阅读意图");
                Check(ReferenceEquals(window.CurrentTab, other), "后台打开完成不抢回用户已切换标签");
                window.SelectTab(interrupted);
                await Until(() => interrupted.Rendered is not null);
                Check(ReferenceEquals(window.CurrentTab, interrupted) && interrupted.IsReading,
                    "切回被中断标签后按需重建阅读视图");
                window.SelectTab(tab);
                await Idle(window);
                var saved = await window.PersistWorkspaceAsync();
                Check(saved.Errors.Count == 0, "含视口的新会话可安全保存");
                window.CloseForShutdown();
                window = new MainWindow(Path.Combine(root, "profile"), enableStartupUpdateCheck: false);
                window.Show();
                await window.InitializeAsync();
                await Idle(window);
                var restored = window.Tabs.Single(t => t.File.Path == path);
                var restoredOther = window.Tabs.Single(t => t.File.Text == otherText);
                Check(ReferenceEquals(window.CurrentTab, restored) && restored.IsReading, "重启恢复活动Markdown阅读标签");
                Check(restoredOther.IsReading && restoredOther.Rendered is null,
                    "重启先保留非活动阅读标签状态并延迟渲染");
                window.SelectTab(restoredOther);
                await Until(() => restoredOther.Rendered is not null);
                await Idle(window);
                Check(restoredOther.IsReading && restoredOther.SourceAnchor == otherSourceAnchor,
                    "选择非活动标签后仍恢复阅读态和精确源锚点");
                Check(Math.Abs(otherViewport - restoredOther.Preview.VerticalOffset) < 100,
                    "选择非活动标签后恢复其独立阅读视口");
                Check(Math.Abs(before - restored.Preview.VerticalOffset) < 100, "重启恢复实际屏幕视口而非旧选区");
                result = 0;
                Console.WriteLine($"Navigation checks: {checks} passed");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                window?.CloseForShutdown();
                app.Shutdown();
                Directory.Delete(root, true);
            }
        };
        app.Run();
        return result;
    }

    private static object? Invoke(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private static async Task Idle(MainWindow window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task Until(Func<bool> ready)
    {
        for (int i = 0; i < 1000; i++)
        {
            if (ready()) return;
            await Task.Delay(5);
        }
        throw new TimeoutException("等待加载阶段超时");
    }
}
