using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Qingyue.Windows;

internal static class MenuParityChecks
{
    public static async Task RunAsync(MainWindow window, Action<bool, string> check)
    {
        var menu = (Menu)window.FindName("MainMenu");
        var groups = menu.Items.OfType<MenuItem>().ToArray();
        check(groups.Select(item => StripMnemonic(item.Header?.ToString())).SequenceEqual(
                ["文件", "编辑", "搜索", "视图", "标签", "帮助"]),
            "主菜单六组与 macOS 顺序一致");

        var file = groups[0];
        CheckPath(file, check,
            ["打开工作区…"],
            "文件菜单包含打开工作区");
        var rename = Direct(file, "重命名…");
        check(rename is not null && string.IsNullOrEmpty(rename.InputGestureText),
            "文件菜单重命名不占快捷键，与 macOS 一致");
        check(window.InputBindings.OfType<KeyBinding>().All(binding => binding.Key != Key.F2),
            "窗口没有 F2 重命名快捷键，与 macOS 一致");
        CheckPath(file, check,
            ["重新载入磁盘文件"],
            "文件菜单包含重新载入磁盘文件");
        CheckPath(file, check,
            ["恢复选项", "当前文件草稿恢复开关"],
            "文件菜单包含当前文件草稿恢复开关");
        CheckPath(file, check,
            ["恢复选项", "恢复详情…"],
            "文件菜单包含恢复详情");

        var edit = groups[1];
        CheckPath(edit, check, ["JSON 工具", "整理 JSON"], "编辑菜单包含 JSON 工具");
        foreach (var title in new[]
        {
            "插入表格行", "删除表格行", "插入表格列", "删除表格列",
            "左对齐当前列", "居中当前列", "右对齐当前列",
            "切换任务状态", "粘贴 TSV 为表格", "插入图片…"
        })
            CheckPath(edit, check, ["Markdown 编辑", title], "Markdown 编辑包含：" + title);
        foreach (var title in new[]
        {
            "添加当前批注…", "查看当前文档批注…",
            "导出 Markdown 阅读简报…", "导出 HTML 阅读简报…"
        })
            CheckPath(edit, check, ["批注与阅读成果", title], "批注与阅读成果包含：" + title);

        var search = groups[2];
        CheckDirect(search, check, "添加 / 移除当前位置书签", "搜索菜单包含书签切换");
        CheckDirect(search, check, "当前文档书签", "搜索菜单包含当前文档书签");
        CheckDirect(search, check, "全局搜索工作区…", "搜索菜单使用工作区全局搜索文案");

        var view = groups[3];
        foreach (var title in new[]
        {
            "阅读外观与设置…", "阅读清单…",
            "加入当前文档到阅读清单", "标记当前文档已读"
        })
            CheckDirect(view, check, title, "视图菜单包含：" + title);
        var widthMenu = Direct(view, "阅读正文宽度");
        var widthItems = widthMenu?.Items.OfType<MenuItem>().ToArray() ?? [];
        check(widthItems.Select(item => item.Tag as string).SequenceEqual(
                ["width:560", "width:720", "width:900", "width:5000"]) &&
            widthItems.Select(item => StripMnemonic(item.Header?.ToString())).SequenceEqual(
                ["窄（短行阅读）", "适中（推荐）", "宽（展示更多）", "铺满窗口"]),
            "阅读正文宽度：四档值与文案严格匹配 macOS");
        CheckPath(view, check, ["文档视图", "分屏编辑"], "文档视图包含分屏编辑");
        CheckPath(view, check, ["文档视图", "JSON 结构"], "文档视图包含 JSON 结构");

        var tabs = groups[4];
        CheckDirect(tabs, check, "重命名当前标签…", "标签菜单包含重命名");
        CheckDirect(tabs, check, "关闭其他标签", "标签菜单包含关闭其他标签");
        CheckPath(tabs, check, ["切换到标签", "标签 1"], "标签菜单包含标签 1");
        CheckPath(tabs, check, ["切换到标签", "标签 8"], "标签菜单包含标签 8");
        CheckPath(tabs, check, ["切换到标签", "最后一个标签"], "标签菜单包含最后一个标签");

        var help = groups[5];
        CheckDirect(help, check, "检查 Windows 更新…", "帮助菜单包含检查 Windows 更新");
        CheckDirect(help, check, "Windows 使用说明", "帮助菜单包含 Windows 使用说明");
        check(help.Items.OfType<MenuItem>()
                .Select(item => StripMnemonic(item.Header?.ToString()))
                .SequenceEqual(["检查 Windows 更新…", "Windows 使用说明", "打开与阅读诊断…", "关于轻阅"]),
            "帮助菜单四项顺序与 macOS 一致");

        var current = window.CurrentTab;
        check(current is not null, "菜单契约测试存在当前文档");
        if (current is null) return;

        CheckAction(file, check, "openWorkspace", true, "打开工作区入口绑定真实动作");
        CheckAction(file, check, "reload", false, "未命名文档禁用重新载入");
        CheckAction(edit, check, "formatJson", false, "非 JSON 文档禁用整理 JSON");
        CheckAction(edit, check, "insertMarkdownImage", true,
            "活动普通文档仍启用插入图片，真实动作内部再校验 Markdown 编辑态");
        CheckAction(edit, check, "showAnnotations", true, "查看批注入口绑定真实动作");
        CheckAction(view, check, "readingList", true, "阅读清单入口绑定真实动作");
        CheckAction(tabs, check, "tab:1", true, "标签 1 入口绑定真实动作");
        CheckAction(help, check, "update", true, "Windows 更新入口绑定真实动作");

        var sidebar = Direct(view, "显示 / 隐藏侧栏");
        bool sidebarVisible = window.FindName("Sidebar") is Border border &&
            border.Visibility == Visibility.Visible;
        check(sidebar?.IsChecked == sidebarVisible,
            "侧栏菜单勾选态与实际可见态一致");

        var tabHeader = Descendants((DependencyObject)window.FindName("TabStrip"))
            .OfType<FrameworkElement>()
            .FirstOrDefault(item => item is StackPanel && ReferenceEquals(item.DataContext, current));
        check(tabHeader?.ContextMenu is not null, "标签右键菜单可打开");
        if (tabHeader?.ContextMenu is { } tabMenu)
        {
            tabMenu.PlacementTarget = tabHeader;
            tabMenu.DataContext = current;
            tabMenu.IsOpen = true;
            await window.Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var tabItems = tabMenu.Items.OfType<MenuItem>().ToArray();
            check(tabItems.Select(item => StripMnemonic(item.Header?.ToString())).SequenceEqual(
                    ["重命名…", "关闭此标签", "关闭其他标签", "关闭全部标签",
                        "向左移动", "向右移动", "切换此文件草稿恢复", "复制路径",
                        "在资源管理器中显示"]),
                "标签右键九项标题与顺序严格匹配 macOS");
            check(tabItems.Select(item => item.Tag as string).SequenceEqual(
                    ["rename", "closeTab", "closeOthers", "closeAll", "moveLeft",
                        "moveRight", "toggleCurrentDraft", "copyPath", "revealPath"]),
                "标签右键九项动作与 macOS 一一对应");
            check(tabItems.Single(item => Equals(item.Tag, "copyPath")).IsEnabled ==
                    (current.File.Path is not null) &&
                tabItems.Single(item => Equals(item.Tag, "revealPath")).IsEnabled ==
                    (current.File.Path is not null),
                "标签右键路径项按文件是否存在启用");
            check(!tabItems.Single(item => Equals(item.Tag, "closeOthers")).IsEnabled,
                "单标签时关闭其他标签禁用");
            check(!tabItems.Single(item => Equals(item.Tag, "moveLeft")).IsEnabled &&
                !tabItems.Single(item => Equals(item.Tag, "moveRight")).IsEnabled,
                "单标签时左右移动禁用");
            var recovery = tabItems.Single(item => Equals(item.Tag, "toggleCurrentDraft"));
            check(recovery.IsCheckable && recovery.IsChecked == current.RecoverDraft,
                "标签右键草稿恢复项显示当前勾选态");
            tabMenu.IsOpen = false;
        }

        check(window.InputBindings.OfType<KeyBinding>().Any(binding =>
                binding.Key == Key.R &&
                binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) &&
            window.InputBindings.OfType<KeyBinding>().Any(binding =>
                binding.Key == Key.J &&
                binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)),
            "重新载入与整理 JSON 的真实快捷键已绑定");

        foreach (var codec in new[] { "Utf8", "Utf8Bom", "Utf16LE", "Utf16BE", "Gb18030" })
        {
            var item = Find(file, "reopen:" + codec);
            check(item is not null && !item.IsEnabled,
                "未命名文档禁用按编码重新打开：" + codec);
        }
    }

    private static void CheckDirect(MenuItem parent, Action<bool, string> check, string title, string name)
    {
        var item = Direct(parent, title);
        check(item is not null, name);
    }

    private static void CheckPath(MenuItem parent, Action<bool, string> check, string[] path, string name)
    {
        MenuItem? current = parent;
        foreach (var title in path)
            current = current is null ? null : Direct(current, title);
        check(current is not null, name);
    }

    private static void CheckAction(MenuItem parent, Action<bool, string> check,
        string tag, bool expected, string name)
    {
        var item = Find(parent, tag);
        check(item is not null && item.IsEnabled == expected, name);
    }

    private static MenuItem? Direct(MenuItem parent, string title) =>
        parent.Items.OfType<MenuItem>()
            .FirstOrDefault(item => string.Equals(
                StripMnemonic(item.Header?.ToString()), title, StringComparison.Ordinal));

    private static MenuItem? Find(ItemsControl parent, string tag)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            if (Equals(item.Tag, tag)) return item;
            var nested = Find(item, tag);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static IEnumerable<MenuItem> AllMenuItems(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var nested in AllMenuItems(item)) yield return nested;
        }
    }

    private static string StripMnemonic(string? value) =>
        (value ?? "").Replace("(_F)", "", StringComparison.Ordinal)
            .Replace("(_E)", "", StringComparison.Ordinal)
            .Replace("(_S)", "", StringComparison.Ordinal)
            .Replace("(_V)", "", StringComparison.Ordinal)
            .Replace("(_T)", "", StringComparison.Ordinal)
            .Replace("(_H)", "", StringComparison.Ordinal);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
