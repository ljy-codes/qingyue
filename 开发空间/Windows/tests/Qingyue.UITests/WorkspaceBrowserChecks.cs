using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Qingyue.Core;

namespace Qingyue.Windows;

internal static class WorkspaceBrowserChecks
{
    public static async Task Run(MainWindow host, string root, Action<bool, string> check)
    {
        string workspace = Path.Combine(root, "workspace-browser");
        string favoriteSource = Path.Combine(root, "workspace-browser-favorites");
        Directory.CreateDirectory(Path.Combine(workspace, "nested"));
        Directory.CreateDirectory(favoriteSource);
        File.WriteAllText(Path.Combine(workspace, "nested", "a.md"), "needle first\n");
        File.WriteAllText(Path.Combine(workspace, "b.txt"), "needle second\n");

        var settings = new ReaderSettings();
        for (int index = 1; index <= 51; index++)
        {
            string path = Path.Combine(favoriteSource, index.ToString("00"));
            Directory.CreateDirectory(path);
            if (index <= 50) settings.FavoriteDirectories.Add(path);
        }

        WorkspaceBrowserWindow? browser = null;
        try
        {
            browser = new WorkspaceBrowserWindow(
                host,
                settings,
                workspace,
                _ => { },
                () => { },
                _ => { });
            browser.Show();
            await Pump(browser);
            var tree = Named<TreeView>(browser, "WorkspaceTree");
            var nameFilter = Named<TextBox>(browser, "WorkspaceNameFilter");
            var typeFilter = Named<TextBox>(browser, "WorkspaceTypeFilter");
            var favoriteButton = Named<Button>(browser, "WorkspaceFavoriteButton");
            var favoritePicker = Named<ComboBox>(browser, "WorkspaceFavoritePicker");
            var status = Named<TextBlock>(browser, "WorkspaceStatus");
            var rescan = Named<Button>(browser, "WorkspaceRescanButton");

            check(browser.Title == "本地工作区" &&
                Nearly(browser.Width, 720) && Nearly(browser.Height, 560) &&
                Nearly(browser.MinWidth, 540) && Nearly(browser.MinHeight, 360) &&
                browser.ResizeMode == ResizeMode.CanResize,
                "本地工作区：标题、尺寸、最小尺寸和可调整大小与 macOS 一致");
            check(AutomationProperties.GetName(tree) == "工作区文件树" &&
                AutomationProperties.GetName(nameFilter) == "工作区文件名过滤" &&
                AutomationProperties.GetName(typeFilter) == "工作区类型过滤",
                "本地工作区：文件树和两个过滤器具有可访问名称");
            await WaitFor(() => status.Text == "显示 2 / 2 个文件",
                () => status.Text);
            await Pump(browser);
            check(tree.Items.Count == 2 &&
                tree.Items.OfType<WorkspaceNode>().Select(node => node.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .SequenceEqual(["b.txt", "nested"]),
                "本地工作区：扫描缓存生成真实目录树，并保留目录层次");

            nameFilter.Text = "a.md";
            await WaitFor(() => status.Text == "显示 1 / 2 个文件", () => status.Text);
            await Pump(browser);
            check(tree.Items.Count == 1 && tree.Items[0] is WorkspaceNode { IsDirectory: true },
                "本地工作区：文件名过滤按相对路径包含匹配，并保留匹配文件的父目录");
            check(tree.Items[0] is WorkspaceNode { IsExpanded: true },
                "本地工作区：存在过滤条件时展开过滤后的目录");

            // Filtering must not rescan the disk. The deleted file remains in
            // the cache until the explicit rescan button is clicked.
            File.Delete(Path.Combine(workspace, "b.txt"));
            nameFilter.Text = "";
            typeFilter.Text = "txt";
            await WaitFor(() => status.Text == "显示 1 / 2 个文件" &&
                tree.Items.Count == 1 &&
                tree.Items[0] is WorkspaceNode { IsDirectory: false }, () => status.Text);
            await Pump(browser);
            check(tree.Items.Count == 1 && !((WorkspaceNode)tree.Items[0]).IsDirectory,
                "本地工作区：类型过滤显示匹配文本文件");

            typeFilter.Text = "";
            await WaitFor(() => status.Text == "显示 2 / 2 个文件" &&
                tree.Items.Count == 2, () => status.Text);
            await Pump(browser);
            check(status.Text == "显示 2 / 2 个文件",
                "本地工作区：过滤只处理扫描缓存，不隐式重扫磁盘");
            rescan.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => status.Text == "显示 1 / 1 个文件", () => status.Text);
            await Pump(browser);
            check(status.Text == "显示 1 / 1 个文件",
                "本地工作区：重新扫描后才更新磁盘变化");

            check(AutomationProperties.GetName(favoritePicker) == "收藏目录" &&
                favoritePicker.Items.Count == 51 &&
                favoriteButton.Content?.ToString() == "收藏目录",
                "本地工作区：收藏目录使用可见下拉入口");
            favoriteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Pump(browser);
            check(settings.FavoriteDirectories.Count == 50 &&
                status.Text.Contains("最多收藏 50 个目录", StringComparison.Ordinal),
                "本地工作区：收藏目录上限与 macOS 一致为 50");

            string opened = "";
            browser.Close();
            browser = NewBrowser(host, settings, workspace, path => opened = path);
            browser.Show();
            await Pump(browser);
            nameFilter = Named<TextBox>(browser, "WorkspaceNameFilter");
            tree = Named<TreeView>(browser, "WorkspaceTree");
            status = Named<TextBlock>(browser, "WorkspaceStatus");
            nameFilter.Text = "a.md";
            await WaitFor(() => status.Text == "显示 1 / 1 个文件", () => status.Text);
            await Pump(browser);
            var folder = tree.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
            check(folder is not null, "本地工作区：过滤后的目录树生成真实控件");
            if (folder is not null)
            {
                folder.IsExpanded = true;
                folder.ApplyTemplate();
                folder.UpdateLayout();
                await Pump(browser);
                var file = FindContainer(folder, folder.Items[0]) as TreeViewItem;
                check(file is not null, "本地工作区：展开目录后生成文件节点控件");
                if (file is not null)
                {
                    file.IsSelected = true;
                    file.Focus();
                    check(tree.SelectedItem is WorkspaceNode selected &&
                        selected.FullPath.EndsWith(Path.Combine("nested", "a.md"),
                            StringComparison.OrdinalIgnoreCase),
                        "本地工作区：双击前文件树选中真实文件节点");
                    tree.RaiseEvent(new MouseButtonEventArgs(
                        Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent,
                        Source = tree
                    });
                }
            }
            check(opened.EndsWith(Path.Combine("nested", "a.md"),
                    StringComparison.OrdinalIgnoreCase),
                "本地工作区：双击文件调用真实打开回调");

            browser.Close();
            browser = null;
            string orderedWorkspace = Path.Combine(root, "workspace-order");
            Directory.CreateDirectory(Path.Combine(orderedWorkspace, "b-dir"));
            File.WriteAllText(Path.Combine(orderedWorkspace, "a-file.md"), "a");
            File.WriteAllText(Path.Combine(orderedWorkspace, "b-dir", "c.md"), "c");
            File.WriteAllText(Path.Combine(orderedWorkspace, "file10.md"), "10");
            File.WriteAllText(Path.Combine(orderedWorkspace, "file2.md"), "2");
            string missingFavorite = Path.Combine(root, "workspace-order-missing");
            var naturalSettings = new ReaderSettings();
            naturalSettings.FavoriteDirectories.Add(favoriteSource);
            naturalSettings.FavoriteDirectories.Add(missingFavorite);
            browser = new WorkspaceBrowserWindow(
                host, naturalSettings, orderedWorkspace, _ => { }, () => { }, _ => { });
            browser.Show();
            await WaitFor(() =>
                Named<TextBlock>(browser, "WorkspaceStatus").Text == "显示 4 / 4 个文件",
                () => Named<TextBlock>(browser, "WorkspaceStatus").Text);
            await Pump(browser);
            tree = Named<TreeView>(browser, "WorkspaceTree");
            status = Named<TextBlock>(browser, "WorkspaceStatus");
            favoritePicker = Named<ComboBox>(browser, "WorkspaceFavoritePicker");
            favoriteButton = Named<Button>(browser, "WorkspaceFavoriteButton");
            typeFilter = Named<TextBox>(browser, "WorkspaceTypeFilter");
            check(tree.Items.OfType<WorkspaceNode>().Select(node => node.Name).SequenceEqual(
                    ["a-file.md", "b-dir", "file2.md", "file10.md"]),
                "本地工作区：文件与目录按 Finder 自然顺序混排，不再强制目录优先");

            typeFilter.Text = "..MD..";
            await WaitFor(() => status.Text == "显示 4 / 4 个文件", () => status.Text);
            await Pump(browser);
            check(status.Text == "显示 4 / 4 个文件",
                "本地工作区：类型过滤去除首尾点号且大小写不敏感");
            typeFilter.Text = "";
            await WaitFor(() => status.Text == "显示 4 / 4 个文件", () => status.Text);

            check(naturalSettings.FavoriteDirectories.Contains(missingFavorite,
                    StringComparer.OrdinalIgnoreCase) &&
                favoritePicker.Items.Cast<WorkspaceFavoriteOption>().Any(option =>
                    string.Equals(option.Path, missingFavorite, StringComparison.OrdinalIgnoreCase)),
                "本地工作区：失效收藏目录保留在下拉列表中，不自动清除");
            favoriteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Pump(browser);
            check(naturalSettings.FavoriteDirectories.Count == 3 &&
                string.Equals(naturalSettings.FavoriteDirectories[^1], orderedWorkspace,
                    StringComparison.OrdinalIgnoreCase),
                "本地工作区：新增收藏追加到末尾，与 macOS 一致");

            favoritePicker.SelectedItem = favoritePicker.Items.Cast<WorkspaceFavoriteOption>().Single(option =>
                string.Equals(option.Path, missingFavorite, StringComparison.OrdinalIgnoreCase));
            await WaitFor(() => status.Text.Contains("不存在", StringComparison.Ordinal),
                () => status.Text);
            check(string.Equals(browser.CurrentRoot, Path.GetFullPath(missingFavorite),
                    StringComparison.OrdinalIgnoreCase) &&
                naturalSettings.FavoriteDirectories.Contains(missingFavorite,
                    StringComparer.OrdinalIgnoreCase),
                "本地工作区：选择失效收藏会执行扫描并显示错误，同时保留收藏");
        }
        finally
        {
            if (browser?.IsVisible == true) browser.Close();
        }
    }

    private static WorkspaceBrowserWindow NewBrowser(
        MainWindow host,
        ReaderSettings settings,
        string workspace,
        Action<string> openFile) =>
        new(host, settings, workspace, _ => { }, () => { }, openFile);

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants(root).OfType<T>().FirstOrDefault(item => item.Name == name)
        ?? throw new InvalidOperationException("未找到控件：" + name);

    private static TreeViewItem? FindContainer(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container)
            return container;
        foreach (var child in parent.Items.OfType<object>())
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(child) is not TreeViewItem childContainer)
                continue;
            var nested = FindContainer(childContainer, item);
            if (nested is not null) return nested;
        }
        return null;
    }

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

    private static bool Nearly(double actual, double expected) =>
        Math.Abs(actual - expected) < 0.5;

    private static async Task Pump(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitFor(Func<bool> condition, Func<string> state)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("等待本地工作区窗口状态超时。当前状态：" + state());
            await Task.Delay(25);
        }
    }
}
