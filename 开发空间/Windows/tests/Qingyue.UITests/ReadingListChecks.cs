using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Qingyue.Core;

namespace Qingyue.Windows;

internal static class ReadingListChecks
{
    public static async Task Run(MainWindow host, string root, Action<bool, string> check)
    {
        string profile = Path.Combine(root, "reading-list-profile");
        string workspace = Path.Combine(root, "reading-list-workspace");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(workspace);
        string path = Path.Combine(workspace, "阅读清单验证.md");
        string json = Path.Combine(profile, "ReadingList.json");
        MainWindow? window = null;
        Window? readingListWindow = null;
        try
        {
            window = new MainWindow(profile, restoreSession: false, enableStartupUpdateCheck: false);
            window.Show();
            await window.InitializeAsync();
            await Pump(window);

            var tab = window.NewDocument("第一行\n第二行\n第三行");
            check(window.SaveTabToPath(tab, path), "阅读清单：验证文档可保存到工作区");
            Program.ClickAction(window, "addReadingList");
            await WaitFor(() => File.Exists(json));
            await Pump(window);
            check(File.ReadAllText(json).Contains("阅读清单验证.md", StringComparison.Ordinal),
                "阅读清单：加入当前文档会立即写入磁盘");

            var store = Field<ReadingListStore>(window, "readingList");
            string secondPath = Path.Combine(workspace, "未固定文档.txt");
            File.WriteAllText(secondPath, "second");
            store.Upsert(secondPath, 37, 2, isPinned: false);
            window.RefreshReadingList();

            Program.ClickAction(window, "readingList");
            await WaitFor(() => Application.Current.Windows.OfType<Window>()
                .Any(item => item.Title == "阅读清单" && item.IsVisible));
            readingListWindow = Application.Current.Windows.OfType<Window>()
                .First(item => item.Title == "阅读清单" && item.IsVisible);
            await Pump(readingListWindow);

            var items = Named<ListView>(readingListWindow, "ReadingListItems");
            var pin = Named<Button>(readingListWindow, "ReadingListPinButton");
            var read = Named<Button>(readingListWindow, "ReadingListReadButton");
            var remove = Named<Button>(readingListWindow, "ReadingListRemoveButton");
            var open = Named<Button>(readingListWindow, "ReadingListOpenButton");
            var retry = Named<Button>(readingListWindow, "ReadingListRetryButton");
            var status = Named<TextBlock>(readingListWindow, "ReadingListStatus");
            var grid = items.View as GridView;
            check(grid is not null && grid.Columns.Select(column => column.Header?.ToString())
                    .SequenceEqual(["文件", "文件夹", "固定", "状态", "阅读进度"]),
                "阅读清单：五列表头严格匹配 macOS");
            check(items.Items.Count == 2 && items.SelectedIndex == 0,
                "阅读清单：真实窗口显示并选中当前文档");
            check(pin.Content?.ToString() == "取消固定",
                "阅读清单：新加入文档固定到顶部且按钮显示取消固定");
            var orderedEntries = items.ItemsSource.Cast<object>()
                .Select(row => (ReadingListEntry)row.GetType().GetProperty("Entry")!
                    .GetValue(row)!)
                .ToArray();
            check(orderedEntries.Length == 2 &&
                string.Equals(orderedEntries[0].Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) &&
                orderedEntries[0].IsPinned && !orderedEntries[1].IsPinned,
                "阅读清单：固定项优先，其余按更新时间倒序");
            check(status.Text.Contains("2 个文件", StringComparison.Ordinal) &&
                status.Text.Contains("0 个已读", StringComparison.Ordinal),
                "阅读清单：窗口状态显示真实文件数与已读数");

            store.UpdateProgress(path, 17, 2);
            window.RefreshReadingList();
            await Pump(readingListWindow);
            var beforeRead = store.Find(path) ?? throw new InvalidOperationException("阅读清单记录不存在");
            read.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => read.Content?.ToString() == "标为未读");
            await Pump(readingListWindow);
            var afterRead = store.Find(path) ?? throw new InvalidOperationException("阅读清单记录不存在");
            check(afterRead.IsRead &&
                afterRead.ProgressPercent == beforeRead.ProgressPercent &&
                afterRead.SourceAnchor == beforeRead.SourceAnchor,
                "阅读清单：标记已读不改变阅读进度和源位置");
            check(status.Text.Contains("2 个文件", StringComparison.Ordinal) &&
                status.Text.Contains("1 个已读", StringComparison.Ordinal),
                "阅读清单：标记已读后窗口与磁盘状态一致");
            var persistedStore = new ReadingListStore(profile);
            await persistedStore.LoadAsync();
            var persisted = persistedStore.Find(path);
            check(persisted?.IsRead == true,
                "阅读清单：标记已读会立即保存");

            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => items.Items.Count == 1);
            await Pump(readingListWindow);
            check(status.Text.Contains("1 个文件", StringComparison.Ordinal),
                "阅读清单：移除记录后刷新真实文件数");
            check(File.Exists(path), "阅读清单：移除记录不会删除磁盘原文件");
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => items.Items.Count == 0);
            await Pump(readingListWindow);
            check(status.Text.Contains("阅读清单为空", StringComparison.Ordinal),
                "阅读清单：移除最后一条记录后显示空清单提示");

            Program.ClickAction(window, "addReadingList");
            await WaitFor(() => items.Items.Count == 1);
            await Pump(readingListWindow);
            File.Delete(json);
            Directory.CreateDirectory(json);
            read.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => retry.IsEnabled);
            await Pump(readingListWindow);
            check(status.Text.Contains("保存失败", StringComparison.Ordinal) &&
                retry.IsEnabled,
                "阅读清单：保存失败保留内存状态并启用重试");

            Directory.Delete(json);
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => !retry.IsEnabled && File.Exists(json));
            await Pump(readingListWindow);
            check(status.Text.Contains("1 个文件", StringComparison.Ordinal),
                "阅读清单：恢复目录后重试保存成功");

            int tabCount = window.Tabs.Count;
            File.Delete(path);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Pump(readingListWindow);
            check(status.Text.Contains("文件不存在或已移动", StringComparison.Ordinal) &&
                window.Tabs.Count == tabCount,
                "阅读清单：原文件不存在时不打开且显示原因");
        }
        finally
        {
            readingListWindow?.Close();
            window?.CloseForShutdown();
        }
    }

    private static T Field<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().Name, name));

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants(root).OfType<T>().FirstOrDefault(item => item.Name == name)
        ?? throw new InvalidOperationException("未找到控件：" + name);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object? child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependency) continue;
            yield return dependency;
            foreach (var nested in Descendants(dependency)) yield return nested;
        }
        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D) yield break;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (LogicalTreeHelper.GetParent(child) is not null) continue;
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static async Task Pump(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(6))
                throw new TimeoutException("等待阅读清单窗口状态超时。");
            await Task.Delay(25);
        }
    }
}
