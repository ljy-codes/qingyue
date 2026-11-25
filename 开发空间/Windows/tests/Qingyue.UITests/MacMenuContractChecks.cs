using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Qingyue.Core;

namespace Qingyue.Windows;

internal static class MacMenuContractChecks
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        await CheckDefaultValidation(root, check);
        await CheckModalAndRestoreValidation(root, check);
        await CheckRecentMenuContract(root, check);
        await CheckUndoRedoValidation(root, check);
        await CheckPathAndTabValidation(root, check);
    }

    private static async Task CheckDefaultValidation(string root, Action<bool, string> check)
    {
        MainWindow? window = null;
        try
        {
            window = await OpenWindow(Path.Combine(root, "menu-default"));
            var tab = window.CurrentTab ?? throw new InvalidOperationException("缺少初始文档");
            Invoke(window, "RefreshMenuState");

            foreach (var action in new[]
            {
                "openWorkspace", "readingList", "appearance", "theme", "fontUp",
                "fontDown", "nextTab", "previousTab", "closeOthers",
                "insertMarkdownImage", "markRead", "addReadingList",
                "returnPosition", "markdownPreview", "structure", "bookmark", "lines"
            })
                check(Menu(window, action)?.IsEnabled == true,
                    "macOS 菜单契约：活动普通文档启用 " + action);

            foreach (var action in new[] { "reload", "copyPath", "reveal", "reopen:Utf8" })
                check(Menu(window, action)?.IsEnabled == false,
                    "macOS 菜单契约：无路径文档禁用 " + action);
            check(Menu(window, "formatJson")?.IsEnabled == false,
                "macOS 菜单契约：普通文档禁用整理 JSON");

            tab.Kind = TextKind.Jsonc;
            Invoke(window, "RefreshMenuState");
            check(Menu(window, "formatJson")?.IsEnabled == false,
                "macOS 菜单契约：JSONC 不启用仅限 JSON 的整理命令");
            tab.Kind = TextKind.Json;
            Invoke(window, "RefreshMenuState");
            check(Menu(window, "formatJson")?.IsEnabled == true,
                "macOS 菜单契约：JSON 编辑态启用整理 JSON");
            tab.IsReading = true;
            Invoke(window, "RefreshMenuState");
            check(Menu(window, "formatJson")?.IsEnabled == false,
                "macOS 菜单契约：JSON 阅读态禁用整理 JSON");
            tab.IsReading = false;

            check(Menu(window, "tab:1")?.IsEnabled == true &&
                Menu(window, "tab:2")?.IsEnabled == false &&
                Menu(window, "tab:9")?.IsEnabled == true,
                "macOS 菜单契约：单标签启用首标签与最后标签");
            check(Menu(window, "moveLeft")?.IsEnabled == false &&
                Menu(window, "moveRight")?.IsEnabled == false,
                "macOS 菜单契约：单标签左右移动均禁用");

            foreach (var action in new[] { "sidebar", "reading", "focus", "wrap", "toggleCurrentDraft" })
            {
                var item = Menu(window, action);
                check(item?.IsCheckable == true, "macOS 菜单勾选态：" + action + " 可勾选");
            }
            foreach (var action in new[] { "lines", "bookmark", "markRead" })
            {
                var item = Menu(window, action);
                check(item?.IsCheckable == false && item.IsChecked == false,
                    "macOS 菜单勾选态：" + action + " 不显示勾选");
            }

            var tabs = Field<ObservableCollection<EditorTab>>(window, "tabs");
            tabs.Clear();
            Invoke(window, "RefreshCurrent");
            check(window.CurrentTab is null, "macOS 菜单契约：可构造无活动文档状态");
            foreach (var action in new[]
            {
                "new", "openWorkspace", "readingList", "appearance", "theme",
                "fontUp", "fontDown", "nextTab", "previousTab", "closeOthers",
                "insertMarkdownImage", "markRead", "addReadingList",
                "returnPosition", "markdownPreview", "structure"
            })
                check(Menu(window, action)?.IsEnabled == false,
                    "macOS 菜单契约：无活动文档禁用 " + action);
            check(Menu(window, "recoveryDetails")?.IsEnabled == true,
                "macOS 菜单契约：无活动文档仍允许恢复详情");
            check(Menu(window, "tab:9")?.IsEnabled == false,
                "macOS 菜单契约：无活动文档禁用最后标签");
        }
        finally
        {
            window?.CloseForShutdown();
        }
    }

    private static async Task CheckModalAndRestoreValidation(string root, Action<bool, string> check)
    {
        MainWindow? window = null;
        try
        {
            window = await OpenWindow(Path.Combine(root, "menu-modal"));
            var settings = Field<ReaderSettings>(window, "settings");
            SetField(window, "modalDepth", 1);
            Invoke(window, "RefreshMenuState");
            check(Menu(window, "help")?.IsEnabled == true &&
                Menu(window, "recoveryDetails")?.IsEnabled == true,
                "macOS 菜单契约：模态期间仍允许使用说明与恢复详情");
            foreach (var action in new[]
            {
                "new", "open", "save", "reading", "theme", "appearance",
                "formatJson", "update", "about"
            })
                check(Menu(window, action)?.IsEnabled == false,
                    "macOS 菜单契约：模态期间禁用 " + action);
            check(AllCommandMenuItems(window).All(item => !item.IsEnabled),
                "macOS 菜单契约：模态期间禁用标准编辑命令");

            settings.Recent.Clear();
            settings.Recent.Add(Path.Combine(root, "modal-recent.md"));
            Invoke(window, "ExecuteAction", "clearRecent");
            await Pump(window);
            check(settings.Recent.Count == 0,
                "macOS 菜单契约：模态期间清除最近记录仍可执行");

            var themeBinding = window.InputBindings.OfType<KeyBinding>().Single(binding =>
                binding.Key == Key.T &&
                binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
            bool dark = settings.Dark;
            check(!themeBinding.Command.CanExecute(null),
                "macOS 菜单契约：模态期间快捷键 CanExecute 为假");
            themeBinding.Command.Execute(null);
            await Pump(window);
            check(settings.Dark == dark,
                "macOS 菜单契约：模态期间快捷键不能绕过菜单禁用");

            SetField(window, "modalDepth", 0);
            SetField(window, "restoringWorkspace", true);
            Invoke(window, "RefreshMenuState");
            foreach (var action in new[] { "help", "recoveryDetails", "new", "theme" })
                check(Menu(window, action)?.IsEnabled == false,
                    "macOS 菜单契约：恢复会话期间禁用 " + action);
            check(!themeBinding.Command.CanExecute(null),
                "macOS 菜单契约：恢复会话期间快捷键不可执行");

            SetField(window, "restoringWorkspace", false);
            SetField(window, "exitInProgress", true);
            Invoke(window, "RefreshMenuState");
            check(Menu(window, "help")?.IsEnabled == false &&
                Menu(window, "recoveryDetails")?.IsEnabled == false &&
                Menu(window, "quit")?.IsEnabled == false,
                "macOS 菜单契约：退出流程期间禁用全部菜单命令");
        }
        finally
        {
            if (window is not null)
            {
                SetField(window, "exitInProgress", false);
                SetField(window, "modalDepth", 0);
                SetField(window, "restoringWorkspace", false);
                window.CloseForShutdown();
            }
        }
    }

    private static async Task CheckUndoRedoValidation(string root, Action<bool, string> check)
    {
        MainWindow? window = null;
        try
        {
            window = await OpenWindow(Path.Combine(root, "menu-undo"));
            var tab = window.CurrentTab ?? throw new InvalidOperationException("缺少初始文档");
            tab.Editor.Text = "alpha";
            tab.Editor.CaretIndex = tab.Editor.Text.Length;
            tab.Editor.Focus();
            tab.Editor.AppendText(" beta");
            await Pump(window);
            Invoke(window, "RefreshMenuState");
            check(Menu(window, "undo")?.IsEnabled == true &&
                Menu(window, "redo")?.IsEnabled == false,
                "macOS 菜单契约：编辑后仅启用匹配的撤销命令");

            Program.ClickAction(window, "undo");
            await Pump(window);
            check(tab.Editor.Text == "alpha",
                "macOS 菜单契约：撤销菜单实际作用于当前源文编辑区");
            Invoke(window, "RefreshMenuState");
            check(Menu(window, "redo")?.IsEnabled == true,
                "macOS 菜单契约：撤销后启用重做");

            window.ToggleReading();
            await Pump(window);
            Invoke(window, "RefreshMenuState");
            check(Menu(window, "undo")?.IsEnabled == false &&
                Menu(window, "redo")?.IsEnabled == false,
                "macOS 菜单契约：阅读模式且无字段编辑器时禁用源文撤销重做");

            Program.ClickAction(window, "find");
            await Pump(window);
            var query = (TextBox)window.FindName("SearchQuery");
            query.Text = "query";
            query.CaretIndex = query.Text.Length;
            query.Focus();
            query.AppendText("!");
            await Pump(window);
            Invoke(window, "RefreshMenuState");
            check(Menu(window, "undo")?.IsEnabled == true,
                "macOS 菜单契约：阅读模式中的查找字段编辑器保留撤销");
        }
        finally
        {
            window?.CloseForShutdown();
        }
    }

    private static async Task CheckRecentMenuContract(string root, Action<bool, string> check)
    {
        MainWindow? window = null;
        try
        {
            string profile = Path.Combine(root, "menu-recent");
            window = await OpenWindow(profile);
            Directory.CreateDirectory(profile);
            string path = Path.Combine(profile, "recent.md");
            await File.WriteAllTextAsync(path, "# recent");

            var settings = Field<ReaderSettings>(window, "settings");
            settings.Recent.Clear();
            settings.Recent.Add(path);
            settings.RecentPositions.Clear();
            settings.RecentPositions[path] = new RecentPosition(
                1, 2, 3, 4, 5, 6, 7, IsReading: false);
            Invoke(window, "RefreshRecent");

            var recentMenu = (MenuItem)window.FindName("RecentMenu");
            var clear = recentMenu.Items.OfType<MenuItem>()
                .FirstOrDefault(item => Equals(item.Tag, "clearRecent"));
            check(clear?.Header?.ToString() == "清除最近文件与位置",
                "macOS 菜单契约：最近文件子菜单提供清除入口");
            check(clear is not null && clear.IsEnabled,
                "macOS 菜单契约：清除最近文件与位置可执行");

            Program.ClickAction(window, "clearRecent");
            await Pump(window);
            check(settings.Recent.Count == 0 && settings.RecentPositions.Count == 0,
                "macOS 菜单契约：清除入口同时清空最近文件与阅读位置");
            check(recentMenu.Items.OfType<MenuItem>().Any(item =>
                    Equals(item.Tag, "clearRecent") &&
                    Equals(item.Header?.ToString(), "清除最近文件与位置")),
                "macOS 菜单契约：清除后最近菜单仍保留可再次清除的入口");
        }
        finally
        {
            window?.CloseForShutdown();
        }
    }

    private static async Task CheckPathAndTabValidation(string root, Action<bool, string> check)
    {
        MainWindow? window = null;
        try
        {
            string folder = Path.Combine(root, "menu-tabs");
            Directory.CreateDirectory(folder);
            window = await OpenWindow(Path.Combine(folder, "profile"));
            var first = window.CurrentTab ?? throw new InvalidOperationException("缺少初始文档");
            string path = Path.Combine(folder, "first.txt");
            check(window.SaveTabToPath(first, path),
                "macOS 菜单契约：准备有路径的活动文档");
            Invoke(window, "RefreshMenuState");
            foreach (var action in new[] { "reload", "copyPath", "reveal", "reopen:Utf8" })
                check(Menu(window, action)?.IsEnabled == true,
                    "macOS 菜单契约：有路径文档启用 " + action);

            var second = window.NewDocument("second");
            Invoke(window, "RefreshMenuState");
            check(Menu(window, "nextTab")?.IsEnabled == true &&
                Menu(window, "previousTab")?.IsEnabled == true &&
                Menu(window, "closeOthers")?.IsEnabled == true,
                "macOS 菜单契约：标签切换与关闭其他不要求多标签");
            check(Menu(window, "tab:1")?.IsEnabled == true &&
                Menu(window, "tab:2")?.IsEnabled == true &&
                Menu(window, "tab:3")?.IsEnabled == false &&
                Menu(window, "tab:9")?.IsEnabled == true,
                "macOS 菜单契约：编号标签按实际数量启用");
            check(Menu(window, "moveLeft")?.IsEnabled == true &&
                Menu(window, "moveRight")?.IsEnabled == false,
                "macOS 菜单契约：最后标签可左移且不可右移");
            window.SelectTab(first);
            Invoke(window, "RefreshMenuState");
            check(Menu(window, "moveLeft")?.IsEnabled == false &&
                Menu(window, "moveRight")?.IsEnabled == true,
                "macOS 菜单契约：首标签可右移且不可左移");

            var tabs = Field<ObservableCollection<EditorTab>>(window, "tabs");
            tabs.Remove(second);
            window.SelectTab(first);
            Invoke(window, "RefreshMenuState");
            check(Menu(window, "nextTab")?.IsEnabled == true &&
                Menu(window, "previousTab")?.IsEnabled == true &&
                Menu(window, "closeOthers")?.IsEnabled == true,
                "macOS 菜单契约：单标签仍启用下一项、上一项与关闭其他");
        }
        finally
        {
            window?.CloseForShutdown();
        }
    }

    private static async Task<MainWindow> OpenWindow(string profile)
    {
        Directory.CreateDirectory(profile);
        var window = new MainWindow(profile, restoreSession: false, enableStartupUpdateCheck: false);
        window.Show();
        await window.InitializeAsync();
        await Pump(window);
        return window;
    }

    private static MenuItem? Menu(MainWindow window, string action) =>
        Find((ItemsControl)window.FindName("MainMenu"), action);

    private static MenuItem? Find(ItemsControl parent, string action)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            if (Equals(item.Tag, action)) return item;
            var nested = Find(item, action);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static MenuItem[] AllCommandMenuItems(MainWindow window) =>
        All((ItemsControl)window.FindName("MainMenu"))
            .Where(item => item.Command is not null)
            .ToArray();

    private static IEnumerable<MenuItem> All(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var nested in All(item))
                yield return nested;
        }
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(target, value);

    private static T Field<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(target) ?? throw new MissingFieldException(target.GetType().Name, name));

    private static object? Invoke(object target, string name, params object?[] arguments)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(candidate => candidate.Name == name &&
                candidate.GetParameters().Length == arguments.Length)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        return method.Invoke(target, arguments);
    }

    private static async Task Pump(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
