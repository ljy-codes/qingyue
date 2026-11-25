using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qingyue.Core;
using Qingyue.Windows;

internal static class QuickOpenChecks
{
    internal static async Task Run(MainWindow owner, Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(check);

        QuickOpenItem[] items =
        [
            new("recent-path", "config.yaml", "/项目/说明文档/config.yaml", false),
            new("open-path", "notes.md", "/项目/说明文档/notes.md", true),
            new("recent-title", "说明文档.md", "/资料/说明文档.md", false),
            new("open-title", "说明文档.md", "/另一份/说明文档.md", true),
            new("recent-fold", "Café.JSON", "/資料/Ｃａｆｅ́.json", false),
            new("emoji", "😀笔记.md", "/中文/😀笔记.md", false)
        ];

        check(Ids(items, "").SequenceEqual(
                ["open-path", "open-title", "recent-path", "recent-title", "recent-fold", "emoji"]),
            "快速打开算法：空查询稳定分组已打开与最近文件");
        check(Ids(items, "  \t\n").SequenceEqual(Ids(items, "")),
            "快速打开算法：纯空白查询与空查询等价");
        check(Ids(items, "说明文档").SequenceEqual(
                ["open-title", "open-path", "recent-title", "recent-path"]),
            "快速打开算法：已打开优先且标题命中排在仅路径命中之前");
        check(Ids(items, "说明文档.md").SequenceEqual(["open-title", "recent-title"]),
            "快速打开算法：不同路径下的同名文件保持为独立结果");
        check(Ids(items, "另一份").SequenceEqual(["open-title"]),
            "快速打开算法：路径可消除同名文件歧义");
        check(Ids(items, "cafe.json").SequenceEqual(["recent-fold"]),
            "快速打开算法：忽略大小写与组合重音");
        check(Ids(items, "ＣＡＦＥ").SequenceEqual(["recent-fold"]),
            "快速打开算法：全角字符按 NFKD 折叠");
        check(Ids(items, "😀").SequenceEqual(["emoji"]),
            "快速打开算法：Emoji 文件名可搜索");
        check(Ids(items, "说明 项目").SequenceEqual(["open-path", "recent-path"]),
            "快速打开算法：多词可跨标题和路径匹配");
        check(!Ids(items, "does-not-exist").Any(),
            "快速打开算法：无匹配查询返回空结果");
        check(QuickOpenRanker.Filter([], "a").Count == 0,
            "快速打开算法：空数据源是合法输入");
        check(QuickOpenRanker.Filter([items[0], items[0], items[1]], "").Count == 2,
            "快速打开算法：重复身份只显示一次");
        QuickOpenItem[] ranking =
        [
            new("contains", "my-report.txt", "/x", false),
            new("prefix", "report-new.txt", "/x", false),
            new("exact", "report", "/x", false),
            new("path", "other", "/report", false)
        ];
        check(Ids(ranking, "report").SequenceEqual(["exact", "prefix", "contains", "path"]),
            "快速打开算法：文件名精确、前缀、包含、仅路径依序排名");
        check(Ids(items, " md").SequenceEqual(
                ["open-path", "open-title", "recent-title", "emoji"]),
            "快速打开算法：查询去空白后保持稳定源顺序");

        var first = await Start(owner, items);
        var firstSearch = Named<TextBox>(first.Dialog, "QuickOpenSearch");
        var firstList = Named<ListBox>(first.Dialog, "QuickOpenResults");
        check(ReferenceEquals(first.Dialog.Owner, owner), "快速打开窗口绑定当前主窗口");
        check(firstList.Items.Count == 6 && firstList.SelectedIndex == 0,
            "快速打开初始显示全部结果并选中第一项");
        check(AutomationProperties.GetName(firstSearch) == "快速打开搜索" &&
            AutomationProperties.GetName(firstList) == "快速打开文件结果",
            "快速打开搜索框和结果列表提供可访问名称");

        check(RaiseKey(firstSearch, Key.Down) && firstList.SelectedIndex == 1,
            "搜索框按下键移动到下一项");
        check(RaiseKey(firstSearch, Key.Up) && firstList.SelectedIndex == 0,
            "搜索框按上键移动到上一项");
        check(RaiseKey(firstSearch, Key.Up) && firstList.SelectedIndex == 5,
            "搜索框按上键从第一项循环到末项");
        check(RaiseKey(firstSearch, Key.Down) && firstList.SelectedIndex == 0,
            "搜索框按下键从末项循环到第一项");

        firstSearch.Text = "说明文档.md";
        await Idle(first.Dialog);
        check(firstList.Items.Count == 2 && SelectedId(firstList) == "open-title",
            "输入文件名后只显示匹配结果且默认优先已打开文件");
        firstSearch.Text = "md";
        await Idle(first.Dialog);
        check(SelectedId(firstList) == "open-title",
            "过滤结果变化时保留原选中项身份");

        var row = RowAt(firstList, 1);
        var rowTexts = Descendants(row).OfType<TextBlock>().Select(text => text.Text).ToArray();
        check(rowTexts.Contains("说明文档.md") && rowTexts.Contains("已打开") &&
            rowTexts.Contains("/另一份/说明文档.md"),
            "结果行显示标题、来源和完整区分路径");
        check(RaiseKey(firstSearch, Key.Enter), "搜索框 Enter 事件被快速打开接管");
        var firstResult = await Complete(first);
        check(firstResult?.Id == "open-title", "搜索框 Enter 返回当前选中身份");

        var tableEnter = await Start(owner, items);
        var tableEnterList = Named<ListBox>(tableEnter.Dialog, "QuickOpenResults");
        tableEnterList.SelectedIndex = 1;
        check(RaiseKey(tableEnterList, Key.Enter), "结果列表 Enter 事件被快速打开接管");
        var tableEnterResult = await Complete(tableEnter);
        check(tableEnterResult?.Id == "open-title", "结果列表 Enter 返回当前选中身份");

        var cancelled = await Start(owner, items);
        var cancelledSearch = Named<TextBox>(cancelled.Dialog, "QuickOpenSearch");
        check(RaiseKey(cancelledSearch, Key.Escape), "搜索框 Esc 事件被快速打开接管");
        check(await Complete(cancelled) is null, "搜索框 Esc 关闭且不返回文件");

        var tableCancelled = await Start(owner, items);
        var tableCancelledList = Named<ListBox>(tableCancelled.Dialog, "QuickOpenResults");
        tableCancelledList.Focus();
        check(RaiseKey(tableCancelledList, Key.Escape), "结果列表 Esc 事件被快速打开接管");
        check(await Complete(tableCancelled) is null, "结果列表 Esc 关闭且不返回文件");

        var doubleClicked = await Start(owner, items);
        var doubleClickedList = Named<ListBox>(doubleClicked.Dialog, "QuickOpenResults");
        doubleClickedList.SelectedIndex = 1;
        var doubleClickedRow = RowAt(doubleClickedList, 1);
        var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source = doubleClickedRow
        };
        doubleClickedRow.RaiseEvent(doubleClick);
        var doubleClickResult = await Complete(doubleClicked);
        check(doubleClickResult?.Id == "open-title", "结果行双击返回当前选中身份");

        var empty = await Start(owner, []);
        var emptySearch = Named<TextBox>(empty.Dialog, "QuickOpenSearch");
        var emptyList = Named<ListBox>(empty.Dialog, "QuickOpenResults");
        check(emptyList.Items.Count == 0 && emptySearch.Text.Length == 0,
            "空数据源显示空结果状态");
        check(RaiseKey(emptySearch, Key.Enter), "空结果 Enter 事件被安全接管");
        await Idle(empty.Dialog);
        check(empty.Dialog.IsVisible && !empty.Result.IsCompleted,
            "空结果 Enter 不关闭窗口也不返回文件");
        RaiseKey(emptySearch, Key.Escape);
        check(await Complete(empty) is null, "空结果仍可通过 Esc 取消");

        var ime = await Start(owner, items);
        var imeSearch = Named<TextBox>(ime.Dialog, "QuickOpenSearch");
        var composition = new TextComposition(InputManager.Current, imeSearch, "中");
        imeSearch.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent
        });
        check(!RaiseKey(imeSearch, Key.Enter), "中文输入法组合态不抢占 Enter");
        check(ime.Dialog.IsVisible && !ime.Result.IsCompleted,
            "中文输入法组合态 Enter 不打开当前文件");
        imeSearch.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = TextCompositionManager.PreviewTextInputEvent
        });
        RaiseKey(imeSearch, Key.Escape);
        check(await Complete(ime) is null, "中文输入法组合态结束后仍可取消快速打开");

        var nonModal = await StartAsync(owner, items);
        var nonModalSearch = Named<TextBox>(nonModal.Dialog, "QuickOpenSearch");
        check(nonModal.Dialog.IsVisible && owner.IsEnabled,
            "快速打开使用非模态窗口，不阻塞主窗口，与 macOS NSPanel 一致");
        nonModalSearch.Text = "notes.md";
        await Idle(nonModal.Dialog);
        check(RaiseKey(nonModalSearch, Key.Enter),
            "非模态快速打开仍由 Enter 选择当前文件");
        check((await Complete(nonModal))?.Id == "open-path",
            "非模态快速打开选择后返回真实文件身份");
    }

    private static async Task<Started> Start(MainWindow owner, IReadOnlyList<QuickOpenItem> items)
    {
        var result = new TaskCompletionSource<QuickOpenItem?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { result.TrySetResult(QuickOpenDialog.Show(owner, items)); }
            catch (Exception ex) { result.TrySetException(ex); }
        }));

        var dialog = await WaitFor(() => Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(window => ReferenceEquals(window.Owner, owner) &&
                window.Title == "快速打开" && window.IsVisible));
        await Idle(dialog);
        return new Started(dialog, result.Task);
    }

    private static async Task<Started> StartAsync(MainWindow owner, IReadOnlyList<QuickOpenItem> items)
    {
        var result = QuickOpenDialog.ShowAsync(owner, items);
        var dialog = await WaitFor(() => Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(window => ReferenceEquals(window.Owner, owner) &&
                window.Title == "快速打开" && window.IsVisible));
        await Idle(dialog);
        return new Started(dialog, result);
    }

    private static async Task<QuickOpenItem?> Complete(Started started)
    {
        if (await Task.WhenAny(started.Result, Task.Delay(TimeSpan.FromSeconds(2))) != started.Result)
        {
            if (started.Dialog.IsVisible) started.Dialog.Close();
            throw new TimeoutException("快速打开窗口未在预期时间内完成。");
        }
        return await started.Result;
    }

    private static async Task<T> WaitFor<T>(Func<T?> value)
        where T : class
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (value() is { } result) return result;
            await Task.Delay(25);
        }
        throw new TimeoutException("等待快速打开窗口超时。");
    }

    private static async Task Idle(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static T Named<T>(Window dialog, string name)
        where T : FrameworkElement =>
        Descendants(dialog).OfType<T>().FirstOrDefault(element => element.Name == name)
        ?? throw new InvalidOperationException("快速打开控件不存在：" + name);

    private static ListBoxItem RowAt(ListBox list, int index) =>
        list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem
        ?? list.Items[index] as ListBoxItem
        ?? throw new InvalidOperationException("快速打开结果行不存在：" + index);

    private static string? SelectedId(ListBox list) =>
        (list.SelectedItem as ListBoxItem)?.Tag is QuickOpenItem item ? item.Id : null;

    private static string[] Ids(IEnumerable<QuickOpenItem> items, string query) =>
        QuickOpenRanker.Filter(items, query).Select(item => item.Id).ToArray();

    private static bool RaiseKey(FrameworkElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("快速打开控件没有可用的窗口源。");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
            Source = target
        };
        target.RaiseEvent(args);
        return args.Handled;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index)))
                yield return child;
        }
    }

    private sealed record Started(Window Dialog, Task<QuickOpenItem?> Result);
}
