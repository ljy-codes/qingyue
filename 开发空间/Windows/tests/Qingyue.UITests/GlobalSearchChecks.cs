using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Qingyue.Core;

namespace Qingyue.Windows;

internal static class GlobalSearchChecks
{
    private const uint WmCommand = 0x0111;
    private const int IdYes = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public static async Task Run(MainWindow host, string root, Action<bool, string> check)
    {
        check(new GlobalSearchOptions().ContextLines == 0,
            "全局搜索：默认不附加前后文行，与 macOS WorkspaceSearchOptions 一致");
        string profile = Path.Combine(root, "global-search-profile");
        string workspace = Path.Combine(root, "global-search-workspace");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(workspace);
        string alpha = Path.Combine(workspace, "alpha.txt");
        string beta = Path.Combine(workspace, "beta.md");
        string gamma = Path.Combine(workspace, "gamma.txt");
        File.WriteAllText(alpha, "needle alpha");
        File.WriteAllText(beta, "needle beta");
        File.WriteAllText(gamma, "needle gamma");

        MainWindow? window = null;
        GlobalSearchWindow? search = null;
        Window? detail = null;
        try
        {
            window = new MainWindow(profile, restoreSession: false, enableStartupUpdateCheck: false);
            window.Show();
            await window.InitializeAsync();
            await Pump(window);
            var settingsField = typeof(MainWindow).GetField(
                "settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var settings = (ReaderSettings)settingsField.GetValue(window)!;
            settings.WorkspaceRoot = workspace;
            await window.OpenPathsAsync([alpha]);

            int opened = 0;
            search = new GlobalSearchWindow(
                window,
                workspace,
                _ => opened++,
                path => !string.Equals(path, alpha, StringComparison.OrdinalIgnoreCase));
            search.Show();
            await Pump(search);

            var query = Named<TextBox>(search, "GlobalSearchQuery");
            var filter = Named<TextBox>(search, "GlobalSearchFileFilter");
            var replacement = Named<TextBox>(search, "GlobalSearchReplacement");
            var searchButton = Named<Button>(search, "GlobalSearchSearchButton");
            var cancel = Named<Button>(search, "GlobalSearchCancelSearchButton");
            var preview = Named<Button>(search, "GlobalSearchPreviewButton");
            var apply = Named<Button>(search, "GlobalSearchApplyButton");
            var close = Named<Button>(search, "GlobalSearchCloseButton");
            var status = Named<TextBlock>(search, "GlobalSearchStatus");
            var results = Named<ListView>(search, "GlobalSearchResults");

            check(search.Title == "全局搜索 · " + Path.GetFileName(workspace) &&
                Nearly(search.Width, 900) && Nearly(search.Height, 580) &&
                Nearly(search.MinWidth, 680) && Nearly(search.MinHeight, 400) &&
                search.ResizeMode == ResizeMode.CanResize,
                "全局搜索：标题、窗口尺寸、最小尺寸和可调整大小与 macOS 一致");
            check(AutomationProperties.GetName(query) == "工作区搜索内容" &&
                status.Text.Contains("替换必须先预览并确认", StringComparison.Ordinal) &&
                !cancel.IsEnabled,
                "全局搜索：真实窗口具备可访问搜索框、安全提示和取消搜索入口");
            var grid = results.View as GridView;
            check(grid is not null && grid.Columns.Select(column => column.Header?.ToString())
                    .SequenceEqual(["文件", "行 / 列", "正文 / 替换预览"]),
                "全局搜索：结果表使用 macOS 的三列标题");

            var setControlsEnabled = typeof(GlobalSearchWindow).GetMethod(
                "SetControlsEnabled", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(nameof(GlobalSearchWindow), "SetControlsEnabled");
            setControlsEnabled.Invoke(search, [false]);
            check(!query.IsEnabled && !replacement.IsEnabled && !filter.IsEnabled &&
                !Named<CheckBox>(search, "GlobalSearchCaseSensitive").IsEnabled &&
                !Named<CheckBox>(search, "GlobalSearchRegex").IsEnabled &&
                !Named<CheckBox>(search, "GlobalSearchWholeWord").IsEnabled &&
                !searchButton.IsEnabled && !preview.IsEnabled && !apply.IsEnabled && !cancel.IsEnabled,
                "全局搜索：应用替换期间输入、选项和全部操作按钮同时锁定");
            setControlsEnabled.Invoke(search, [true]);
            query.Text = "";
            await Pump(search);

            query.Text = "needle";
            await Pump(search);
            check(!apply.IsEnabled, "全局搜索：仅输入条件不会启用应用替换");
            searchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => status.Text.StartsWith("找到 3 处", StringComparison.Ordinal),
                () => status.Text);
            await Pump(search);
            check(results.ItemsSource is not null &&
                results.Items.Cast<object>().Select(row => RowValue(row, "File"))
                    .SequenceEqual(["alpha.txt", "beta.md", "gamma.txt"]) &&
                results.Items.Cast<object>().Select(row => RowValue(row, "Position"))
                    .All(value => value == "1:1") &&
                results.Items.Cast<object>().Select(row => RowValue(row, "Preview"))
                    .Contains("needle alpha", StringComparer.Ordinal),
                "全局搜索：普通搜索显示相对路径、行列和正文，且不直接改写磁盘");
            check(File.ReadAllText(beta) == "needle beta",
                "全局搜索：普通搜索不修改文件");

            query.Text = " ";
            searchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => status.Text.StartsWith("找到 3 处", StringComparison.Ordinal),
                () => status.Text);
            check(results.Items.Count == 3,
                "全局搜索：纯空格查询可搜索，与 macOS 的 query.isEmpty 判断一致");

            filter.Text = "beta";
            await Pump(search);
            check(results.Items.Count == 0 && !apply.IsEnabled &&
                status.Text.Contains("条件已改变", StringComparison.Ordinal),
                "全局搜索：修改文件名过滤立即清空旧结果和旧应用状态");
            searchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => status.Text.StartsWith("找到 1 处", StringComparison.Ordinal),
                () => status.Text);
            await Pump(search);
            check(results.Items.Count == 1 &&
                RowValue(results.Items[0], "File") == "beta.md",
                "全局搜索：文件名过滤按工作区相对路径包含匹配");

            filter.Text = "";
            query.Text = "needle";
            replacement.Text = "value";
            await Pump(search);
            check(results.Items.Count == 0 && !apply.IsEnabled &&
                status.Text.Contains("条件已改变", StringComparison.Ordinal),
                "全局搜索：修改替换内容立即使旧预览失效");
            bool replacementEnterHandled = RaiseKey(replacement, Key.Enter);
            await Pump(search);
            check(!replacementEnterHandled &&
                status.Text.Contains("条件已改变", StringComparison.Ordinal) &&
                !apply.IsEnabled,
                "全局搜索：替换框 Enter 不会启动预览，与 macOS 一致");
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => status.Text.StartsWith("预览 3 个文件，3 处", StringComparison.Ordinal),
                () => status.Text);
            await Pump(search);
            check(apply.IsEnabled &&
                results.Items.Cast<object>().All(row =>
                    RowValue(row, "Position") == "1 处" &&
                    RowValue(row, "Preview") == "双击查看替换前后内容") &&
                File.ReadAllText(beta) == "needle beta",
                "全局搜索：替换预览按文件汇总，应用前不改写磁盘");

            var firstRow = results.ItemContainerGenerator.ContainerFromIndex(0) as UIElement
                ?? results.Items[0] as UIElement;
            if (firstRow is null) throw new InvalidOperationException("全局搜索预览行不存在。");
            results.SelectedIndex = 0;
            results.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Control.MouseDoubleClickEvent,
                Source = results
            });
            detail = await WaitForWindow(
                () => Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(item => item.Title == "替换预览 · alpha.txt" && item.IsVisible));
            await Pump(detail);
            var detailText = Named<TextBox>(detail, "GlobalSearchReplacementDetailText");
            check(Nearly(detail.Width, 720) && Nearly(detail.Height, 500) &&
                detailText.Text.Contains("替换前：\nneedle alpha", StringComparison.Ordinal) &&
                detailText.Text.Contains("替换后：\nvalue alpha", StringComparison.Ordinal),
                $"全局搜索：双击替换预览显示 720×500 的前后内容详情" +
                $"（实际 {detail.Width:0.##}×{detail.Height:0.##}，" +
                $"内容 {detailText.Text[..Math.Min(detailText.Text.Length, 80)].Replace("\n", "\\n")}）");
            detail.Close();
            detail = null;

            var previewType = typeof(GlobalSearchReplacementPreview);
            var previewConstructor = previewType.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(DocumentFile), typeof(int), typeof(string)],
                modifiers: null) ?? throw new MissingMethodException(previewType.FullName, ".ctor");
            string clusterBoundaryOriginal = new string('a', 99_999) + "😀z";
            string clusterBoundaryReplacement = new string('b', 99_999) + "😀z";
            var boundaryPreview = (GlobalSearchReplacementPreview)previewConstructor.Invoke(
                [new DocumentFile(clusterBoundaryOriginal), 1, clusterBoundaryReplacement]);
            Invoke(search, "ShowReplacementDetail", boundaryPreview);
            detail = await WaitForWindow(
                () => Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(item => item.Title == "替换预览 · (未保存)" && item.IsVisible));
            await Pump(detail);
            detailText = Named<TextBox>(detail, "GlobalSearchReplacementDetailText");
            string boundaryText = detailText.Text;
            int originalStart = boundaryText.IndexOf("替换前：\n", StringComparison.Ordinal) + "替换前：\n".Length;
            int originalEnd = boundaryText.IndexOf("\n\n替换后：\n", originalStart, StringComparison.Ordinal);
            string displayedOriginal = boundaryText[originalStart..originalEnd];
            check(displayedOriginal.Contains("😀", StringComparison.Ordinal) &&
                !displayedOriginal.Contains('\uFFFD') &&
                !displayedOriginal.Contains('z'),
                "全局搜索：替换详情按 macOS 字符边界截断，不切断 Emoji 代理对");
            check(detailText.TextWrapping == TextWrapping.Wrap &&
                detailText.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled &&
                detail.MinWidth == 0 && detail.MinHeight == 0,
                "全局搜索：替换详情按 macOS 纵向换行与自由缩放，不额外设置最小尺寸");
            detail.Close();
            detail = null;

            var confirmation = ClickNextMessageBoxYes();
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await confirmation;
            await WaitFor(
                () => status.Text.StartsWith("已替换 2 个文件；跳过 1", StringComparison.Ordinal),
                () => status.Text);
            await Pump(search);
            check(File.ReadAllText(alpha) == "needle alpha" &&
                File.ReadAllText(beta) == "value beta" &&
                File.ReadAllText(gamma) == "value gamma",
                "全局搜索：已打开标签文件跳过，未打开文件按预览替换");
            check(status.Text.Contains("失败/冲突 0", StringComparison.Ordinal),
                "全局搜索：部分跳过时显示真实成功与跳过数量");

            File.WriteAllText(beta, "needle beta");
            File.WriteAllText(gamma, "needle gamma");
            query.Text = "needle";
            filter.Text = "";
            replacement.Text = "value";
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => status.Text.StartsWith("预览 3 个文件，3 处", StringComparison.Ordinal),
                () => status.Text);
            await Pump(search);
            File.WriteAllText(gamma, "externally changed");
            var conflictConfirmation = ClickNextMessageBoxYes();
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await conflictConfirmation;
            await WaitFor(() => status.Text.Contains("失败/冲突 1", StringComparison.Ordinal),
                () => status.Text + " " + (status.ToolTip?.ToString() ?? ""));
            await Pump(search);
            check(File.ReadAllText(gamma) == "externally changed" &&
                (status.ToolTip?.ToString() ?? "").Contains(
                    "磁盘文件已被其他程序修改", StringComparison.Ordinal),
                "全局搜索：外部修改冲突被拒绝，不覆盖磁盘且明确报告失败");

            query.Text = "needle";
            searchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => status.Text.StartsWith("找到", StringComparison.Ordinal),
                () => status.Text);
            await Pump(search);
            if (results.Items.Count > 0)
            {
                results.SelectedIndex = 0;
                var row = results.ItemContainerGenerator.ContainerFromIndex(0) as UIElement
                    ?? results.Items[0] as UIElement;
                if (row is not null)
                {
                    results.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent,
                        Source = results
                    });
                }
            }
            await Pump(search);
            check(opened == 1, "全局搜索：普通结果双击调用真实打开定位回调");

            string locationFile = Path.Combine(root, "global-search-location.txt");
            File.WriteAllText(locationFile, "needle cursor");
            await window.OpenPathsAsync([locationFile]);
            var locationTab = window.CurrentTab!;
            await InvokeAsync(window, "OpenGlobalSearchMatchAsync", new GlobalSearchMatch(
                locationFile, 1, 1, "needle cursor", "", "", "needle", "",
                SourceStart: 0, SourceLength: 6));
            await Pump(window);
            check(locationTab.Editor.SelectionLength == 0 && locationTab.Editor.CaretIndex == 0,
                "全局搜索：双击结果只放置光标，不选中整段匹配");
            locationTab.Editor.SelectedText = "X";
            check(locationTab.Editor.Text == "Xneedle cursor",
                "全局搜索：定位后输入字符会插入而不是替换匹配文本");
            locationTab.Editor.Undo();
            Invoke(window, "CloseTab", locationTab, false, false);

            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => !search.IsVisible, () => "窗口仍可见");
        }
        finally
        {
            detail?.Close();
            if (search?.IsVisible == true) search.Close();
            window?.CloseForShutdown();
        }
    }

    private static string RowValue(object row, string property) =>
        row.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)?.GetValue(row)
            as string ?? throw new InvalidOperationException($"全局搜索行缺少属性：{property}");

    private static bool RaiseKey(FrameworkElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("全局搜索控件没有可用的窗口源。");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
            Source = target
        };
        target.RaiseEvent(args);
        return args.Handled;
    }

    private static async Task InvokeAsync(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
                candidate.Name == name && candidate.GetParameters().Length == arguments.Length)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        if (method.Invoke(target, arguments) is Task task) await task;
    }

    private static object? Invoke(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
                candidate.Name == name && candidate.GetParameters().Length == arguments.Length)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        return method.Invoke(target, arguments);
    }

    private static Task ClickNextMessageBoxYes() => Task.Run(async () =>
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(8))
        {
            var handle = FindWindow(null, "确认全局替换");
            if (handle != IntPtr.Zero)
            {
                SendMessage(handle, WmCommand, new IntPtr(IdYes), IntPtr.Zero);
                return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("等待全局替换确认框超时。");
    });

    private static async Task<Window> WaitForWindow(Func<Window?> find)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (find() is { } window) return window;
            await Task.Delay(25);
        }
        throw new TimeoutException("等待全局搜索窗口超时。");
    }

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

    private static bool Nearly(double actual, double expected) =>
        Math.Abs(actual - expected) < 0.5;

    private static async Task Pump(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitFor(Func<bool> condition, Func<string>? state = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("等待全局搜索窗口状态超时。" +
                    (state is null ? "" : " 当前状态：" + state()));
            await Task.Delay(25);
        }
    }
}
