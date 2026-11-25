using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using Qingyue.Windows;

internal static class NativeParityChecks
{
    public static async Task Run(string profile, Action<bool, string> check)
    {
        Directory.CreateDirectory(profile);
        MainWindow? window = null;
        try
        {
            window = new MainWindow(profile, restoreSession: false, enableStartupUpdateCheck: false);
            window.Show();
            await window.InitializeAsync();
            await Pump(window);
            await CheckSelectedTabPixels(window, check);
            await CheckMenuFocusAndSelection(window, check);
            await CheckMenuStability(window, check);
            CheckShortcutUniqueness(window, check);
        }
        finally
        {
            window?.CloseForShutdown();
        }
    }

    private static async Task CheckSelectedTabPixels(MainWindow window, Action<bool, string> check)
    {
        window.ApplyTheme(false);
        window.ApplyTheme(ReaderThemeKind.Celadon);
        var tab = window.CurrentTab ?? window.NewDocument("标签像素测试");
        window.SelectTab(tab);
        window.UpdateLayout();
        await Pump(window);

        var strip = (TabControl)window.FindName("TabStrip");
        var item = strip.ItemContainerGenerator.ContainerFromItem(tab) as TabItem;
        if (item is null)
        {
            item = Descendants(strip).OfType<TabItem>().FirstOrDefault(candidate =>
                ReferenceEquals(candidate.DataContext, tab));
        }
        check(item is { IsSelected: true, ActualWidth: > 20, ActualHeight: > 8 },
            "选中标签已生成真实 TabItem 且完成排版");
        if (item is null || item.ActualWidth < 20 || item.ActualHeight < 8) return;

        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(item.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(item.ActualHeight)),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(item);
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);

        Color panel = ((SolidColorBrush)window.FindResource("PanelBrush")).Color;
        Color accent = ((SolidColorBrush)window.FindResource("AccentBrush")).Color;
        int bodyTotal = 0, bodyPanel = 0, bodyAccent = 0;
        int left = width / 4, right = Math.Max(left + 1, width * 3 / 4);
        int top = Math.Max(1, height / 4), bottom = Math.Max(top + 1, height * 3 / 5);
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            Color pixel = PixelAt(pixels, width, x, y);
            bodyTotal++;
            if (SameRgb(pixel, panel)) bodyPanel++;
            if (SameRgb(pixel, accent)) bodyAccent++;
        }

        int edgeTotal = 0, edgeAccent = 0;
        int edgeTop = Math.Max(0, height - 4);
        for (int y = edgeTop; y < height; y++)
        for (int x = 1; x < width - 1; x++)
        {
            edgeTotal++;
            if (SameRgb(PixelAt(pixels, width, x, y), accent)) edgeAccent++;
        }

        check(bodyPanel > bodyTotal * 0.5,
            "选中标签主体实际渲染为 PanelBrush，而非整块强调色");
        check(bodyAccent == 0,
            "选中标签中部不渲染 AccentBrush");
        check(edgeAccent > edgeTotal * 0.35,
            "选中标签底部存在连续 2px 强调色边（实际 " +
            $"{edgeAccent}/{edgeTotal} 像素）");
    }

    private static async Task CheckMenuFocusAndSelection(MainWindow window, Action<bool, string> check)
    {
        var document = window.NewDocument("菜单焦点测试 alpha beta gamma");
        window.SelectTab(document);
        window.Activate();
        await Pump(window);
        document.Editor.Focus();
        Keyboard.Focus(document.Editor);
        document.Editor.Select(6, 4);
        await Pump(window);
        check(ReferenceEquals(Keyboard.FocusedElement, document.Editor),
            "菜单测试准备：源文编辑区获得真实键盘焦点（实际：" +
            $"{Keyboard.FocusedElement?.GetType().Name ?? "null"}）");

        var menu = (Menu)window.FindName("MainMenu");
        var file = menu.Items.OfType<MenuItem>().First();
        file.IsSubmenuOpen = true;
        await Pump(window);
        check(file.IsSubmenuOpen, "文件根菜单可通过真实 WPF 状态打开");
        check(document.Editor.SelectionStart == 6 && document.Editor.SelectionLength == 4,
            "文件菜单打开期间编辑区选区保持不变");

        file.IsSubmenuOpen = false;
        await Pump(window);
        check(!file.IsSubmenuOpen, "文件根菜单可关闭");
        check(ReferenceEquals(Keyboard.FocusedElement, document.Editor),
            "关闭菜单后键盘焦点回到原源文编辑区");
        check(document.Editor.SelectionStart == 6 && document.Editor.SelectionLength == 4,
            "关闭菜单后编辑区选区保持不变");

        var recent = (MenuItem)window.FindName("RecentMenu");
        file.IsSubmenuOpen = true;
        recent.IsSubmenuOpen = true;
        await Pump(window);
        check(recent.IsSubmenuOpen, "最近文件子菜单可真实打开");
        int firstCount = recent.Items.Count;
        string firstSnapshot = Snapshot(recent);
        recent.IsSubmenuOpen = false;
        file.IsSubmenuOpen = false;
        await Pump(window);
        file.IsSubmenuOpen = true;
        recent.IsSubmenuOpen = true;
        await Pump(window);
        check(recent.Items.Count == firstCount && Snapshot(recent) == firstSnapshot,
            "最近文件菜单重复打开后不累加重复项");
        recent.IsSubmenuOpen = false;
        file.IsSubmenuOpen = false;
        await Pump(window);

        file.IsSubmenuOpen = true;
        await Pump(window);
        var reopen = MenuItemByTag(file, "reopen:Utf8");
        check(reopen is { IsEnabled: false },
            "未命名文档的按编码重新打开子项处于禁用态");
        file.IsSubmenuOpen = false;
        await Pump(window);
    }

    private static async Task CheckMenuStability(MainWindow window, Action<bool, string> check)
    {
        var menu = (Menu)window.FindName("MainMenu");
        var roots = menu.Items.OfType<MenuItem>().ToArray();
        var before = roots.ToArray();
        int countBefore = Descendants(menu).OfType<MenuItem>().Count();

        window.ApplyTheme(false);
        window.ApplyTheme(ReaderThemeKind.SeaSalt);
        window.ApplyTheme(true);
        window.ApplyTheme(ReaderThemeKind.Coffee);
        await Pump(window);
        var after = menu.Items.OfType<MenuItem>().ToArray();
        check(after.Length == 6 && after.Zip(before).All(pair => ReferenceEquals(pair.First, pair.Second)),
            "切换主题不重建六个根菜单控件");
        check(Descendants(menu).OfType<MenuItem>().Count() == countBefore,
            "切换主题不重复添加菜单项");

        before[0].IsSubmenuOpen = true;
        await Pump(window);
        check(Descendants(menu).OfType<MenuItem>().Count() == countBefore,
            "菜单实际打开刷新后不重复添加菜单项");
        before[0].IsSubmenuOpen = false;
        await Pump(window);
    }

    private static void CheckShortcutUniqueness(MainWindow window, Action<bool, string> check)
    {
        var bindings = window.InputBindings.OfType<KeyBinding>()
            .Select(binding => (binding.Key, binding.Modifiers)).ToArray();
        check(bindings.Distinct().Count() == bindings.Length,
            "窗口快捷键手势全部唯一");

        var menu = (Menu)window.FindName("MainMenu");
        var menuItems = AllMenuItems(menu).ToArray();
        var gestureTexts = menuItems
            .Select(item => item.InputGestureText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        check(gestureTexts.Distinct(StringComparer.OrdinalIgnoreCase).Count() == gestureTexts.Length,
            "菜单显示的快捷键文本全部唯一");

        // This is the complete Windows command binding set corresponding to
        // ReaderMenus.swift plus the two documented Windows platform mappings
        // (Ctrl+Q quit and Ctrl+, settings). Standard Cut/Copy/Paste/SelectAll
        // and text undo remain native WPF TextBox commands.
        var expectedBindings = new (Key Key, ModifierKeys Modifiers, string Action)[]
        {
            (Key.N, ModifierKeys.Control, "new"),
            (Key.O, ModifierKeys.Control, "open"),
            (Key.S, ModifierKeys.Control, "save"),
            (Key.S, ModifierKeys.Control | ModifierKeys.Shift, "saveAs"),
            (Key.S, ModifierKeys.Control | ModifierKeys.Alt, "saveAll"),
            (Key.Q, ModifierKeys.Control, "quit"),
            (Key.W, ModifierKeys.Control, "close"),
            (Key.W, ModifierKeys.Control | ModifierKeys.Shift, "closeAll"),
            (Key.F, ModifierKeys.Control, "find"),
            (Key.H, ModifierKeys.Control | ModifierKeys.Shift, "replace"),
            (Key.Z, ModifierKeys.Control | ModifierKeys.Shift, "redo"),
            (Key.R, ModifierKeys.Control, "reading"),
            (Key.R, ModifierKeys.Control | ModifierKeys.Shift, "reload"),
            (Key.J, ModifierKeys.Control | ModifierKeys.Shift, "formatJson"),
            (Key.T, ModifierKeys.Control | ModifierKeys.Shift, "theme"),
            (Key.OemComma, ModifierKeys.Control, "appearance"),
            (Key.D, ModifierKeys.Control, "bookmark"),
            (Key.B, ModifierKeys.Control, "sidebar"),
            (Key.L, ModifierKeys.Control, "goto"),
            (Key.F, ModifierKeys.Control | ModifierKeys.Shift, "focus"),
            (Key.F, ModifierKeys.Control | ModifierKeys.Alt, "format"),
            (Key.O, ModifierKeys.Control | ModifierKeys.Shift, "outline"),
            (Key.P, ModifierKeys.Control | ModifierKeys.Shift, "quick"),
            (Key.G, ModifierKeys.Control, "next"),
            (Key.G, ModifierKeys.Control | ModifierKeys.Shift, "previous"),
            (Key.Left, ModifierKeys.Alt, "returnPosition"),
            (Key.OemCloseBrackets, ModifierKeys.Control | ModifierKeys.Shift, "nextTab"),
            (Key.OemOpenBrackets, ModifierKeys.Control | ModifierKeys.Shift, "previousTab"),
            (Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift, "fontUp"),
            (Key.Add, ModifierKeys.Control, "fontUp"),
            (Key.OemMinus, ModifierKeys.Control, "fontDown"),
            (Key.Subtract, ModifierKeys.Control, "fontDown")
        };
        expectedBindings = expectedBindings.Concat(Enumerable.Range(1, 9)
            .Select(index => (Key.D0 + index, ModifierKeys.Control, "tab:" + index))).ToArray();
        var expectedSignatures = expectedBindings
            .Select(item => GestureSignature(item.Key, item.Modifiers))
            .ToHashSet(StringComparer.Ordinal);
        var actualSignatures = bindings
            .Select(item => GestureSignature(item.Key, item.Modifiers))
            .ToHashSet(StringComparer.Ordinal);
        check(actualSignatures.SetEquals(expectedSignatures),
            "窗口快捷键集合与 macOS 契约及平台白名单完全一致；多出=" +
            string.Join(",", actualSignatures.Except(expectedSignatures)) +
            "；缺少=" + string.Join(",", expectedSignatures.Except(actualSignatures)));
        foreach (var item in expectedBindings)
        {
            check(bindings.Contains((item.Key, item.Modifiers)),
                $"关键快捷键 {item.Action} 映射到 {item.Modifiers}+{item.Key}");
        }

        // Every command shown in a menu must have exactly the same shortcut
        // string as its macOS counterpart. Actions not listed here must show
        // no shortcut at all, which prevents invented accelerators such as
        // ToggleTask's former Ctrl+Shift+Space.
        var expectedGestures = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["new"] = "Ctrl+N",
            ["open"] = "Ctrl+O",
            ["quick"] = "Ctrl+Shift+P",
            ["save"] = "Ctrl+S",
            ["saveAs"] = "Ctrl+Shift+S",
            ["saveAll"] = "Ctrl+Alt+S",
            ["reload"] = "Ctrl+Shift+R",
            ["close"] = "Ctrl+W",
            ["closeAll"] = "Ctrl+Shift+W",
            ["quit"] = "Ctrl+Q",
            ["undo"] = "Ctrl+Z",
            ["redo"] = "Ctrl+Shift+Z",
            ["format"] = "Ctrl+Alt+F",
            ["formatJson"] = "Ctrl+Shift+J",
            ["find"] = "Ctrl+F",
            ["replace"] = "Ctrl+Shift+H",
            ["next"] = "Ctrl+G",
            ["previous"] = "Ctrl+Shift+G",
            ["goto"] = "Ctrl+L",
            ["outline"] = "Ctrl+Shift+O",
            ["bookmark"] = "Ctrl+D",
            ["returnPosition"] = "Alt+←",
            ["reading"] = "Ctrl+R",
            ["focus"] = "Ctrl+Shift+F",
            ["sidebar"] = "Ctrl+B",
            ["appearance"] = "Ctrl+,",
            ["theme"] = "Ctrl+Shift+T",
            ["fontUp"] = "Ctrl++",
            ["fontDown"] = "Ctrl+-",
            ["nextTab"] = "Ctrl+Shift+]",
            ["previousTab"] = "Ctrl+Shift+["
        };
        for (int index = 1; index <= 9; index++) expectedGestures["tab:" + index] = "Ctrl+" + index;

        foreach (var (action, gesture) in expectedGestures)
        {
            var shown = menuItems.Where(item => Equals(item.Tag, action))
                .Select(item => item.InputGestureText)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            check(shown.SequenceEqual([gesture]),
                $"菜单 {action} 仅显示 macOS 等价快捷键 {gesture}（实际：{string.Join("|", shown)}）");
        }
        foreach (string action in menuItems
                     .Where(item => item.Tag is string && !string.IsNullOrWhiteSpace(item.InputGestureText))
                     .Select(item => (string)item.Tag)
                     .Distinct(StringComparer.Ordinal))
        {
            check(expectedGestures.ContainsKey(action),
                $"菜单动作 {action} 没有 macOS 快捷键时不得显示快捷键");
        }
        check(MenuItemByTag(menu, "ToggleTask")?.InputGestureText == string.Empty,
            "切换任务状态与 macOS 一致，不显示快捷键");

        for (int index = 1; index <= 9; index++)
            check(bindings.Contains((Key.D0 + index, ModifierKeys.Control)),
                $"标签 {index} 的 Ctrl+数字快捷键已绑定");
        check(bindings.All(binding => binding.Key != Key.F2),
            "窗口无 F2 重命名快捷键");

        // Prove the accepted WPF gestures execute the intended action rather
        // than only inspecting their declarations. Ctrl+B must reach the
        // app-level sidebar command even while a Markdown editor has focus.
        var sidebarBinding = window.InputBindings.OfType<KeyBinding>().Single(binding =>
            binding.Key == Key.B && binding.Modifiers == ModifierKeys.Control);
        var settings = window.GetType().GetField("settings",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(window)!;
        bool sidebar = (bool)settings.GetType().GetProperty("Sidebar")!.GetValue(settings)!;
        sidebarBinding.Command.Execute(null);
        check((bool)settings.GetType().GetProperty("Sidebar")!.GetValue(settings)! != sidebar,
            "Ctrl+B 的真实 WPF 命令切换到侧栏，而不是被 Markdown 编辑区截获为加粗");
        sidebarBinding.Command.Execute(null);
        check((bool)settings.GetType().GetProperty("Sidebar")!.GetValue(settings)! == sidebar,
            "Ctrl+B 的第二次执行恢复侧栏状态");

        var fontUpBinding = window.InputBindings.OfType<KeyBinding>().Single(binding =>
            binding.Key == Key.OemPlus &&
            binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
        var fontDownBinding = window.InputBindings.OfType<KeyBinding>().Single(binding =>
            binding.Key == Key.OemMinus && binding.Modifiers == ModifierKeys.Control);
        double fontSize = (double)settings.GetType().GetProperty("FontSize")!.GetValue(settings)!;
        fontUpBinding.Command.Execute(null);
        check((double)settings.GetType().GetProperty("FontSize")!.GetValue(settings)! == fontSize + 1,
            "Ctrl++ 的真实 WPF 手势增大字号");
        fontDownBinding.Command.Execute(null);
        check((double)settings.GetType().GetProperty("FontSize")!.GetValue(settings)! == fontSize,
            "Ctrl+- 的真实 WPF 手势恢复字号");
        check(!bindings.Contains((Key.OemPlus, ModifierKeys.Control)),
            "Ctrl+= 不会伪装成 macOS 的 Ctrl++");

        var forbidden = new (Key Key, ModifierKeys Modifiers, string Description)[]
        {
            (Key.H, ModifierKeys.Control, "Ctrl+H 替换别名"),
            (Key.Y, ModifierKeys.Control, "Ctrl+Y 重做别名"),
            (Key.Tab, ModifierKeys.Control, "Ctrl+Tab 标签别名"),
            (Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+Tab 标签别名"),
            (Key.F3, ModifierKeys.None, "F3 匹配导航别名"),
            (Key.F3, ModifierKeys.Shift, "Shift+F3 匹配导航别名"),
            (Key.A, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+A 批注别名")
        };
        foreach (var item in forbidden)
            check(!bindings.Contains((item.Key, item.Modifiers)),
                $"窗口未绑定 macOS 契约之外的 {item.Description}");
        check(MenuItemByTag(menu, "annotate")?.InputGestureText == string.Empty,
            "批注菜单与 macOS 一致，不显示快捷键");
    }

    private static string GestureSignature(Key key, ModifierKeys modifiers) =>
        ((int)modifiers).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
        ((int)key).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<MenuItem> AllMenuItems(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var nested in AllMenuItems(item)) yield return nested;
        }
    }

    private static MenuItem? MenuItemByTag(ItemsControl parent, string tag)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            if (Equals(item.Tag, tag)) return item;
            var nested = MenuItemByTag(item, tag);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static string Snapshot(ItemsControl items) =>
        string.Join("\n", items.Items.OfType<MenuItem>().Select(item =>
            $"{item.Header}|{item.Tag}|{item.IsEnabled}|{item.IsCheckable}|{item.IsChecked}"));

    private static Color PixelAt(byte[] pixels, int width, int x, int y)
    {
        int offset = (y * width + x) * 4;
        return Color.FromArgb(pixels[offset + 3], pixels[offset + 2],
            pixels[offset + 1], pixels[offset]);
    }

    private static bool SameRgb(Color first, Color second) =>
        first.R == second.R && first.G == second.G && first.B == second.B;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static async Task Pump(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
