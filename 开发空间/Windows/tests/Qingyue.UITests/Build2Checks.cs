using System.Reflection;
using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Qingyue.Core;
using Qingyue.Windows;

internal static class Build2Checks
{
    public static async Task CheckLayout(MainWindow owner, string profileRoot, Action<bool, string> check)
    {
        MainWindow? window = null;
        try
        {
            window = new MainWindow(Path.Combine(profileRoot, "layout-profile"),
                restoreSession: false, enableStartupUpdateCheck: false);
            window.Show();
            await window.InitializeAsync();
            await PumpLayout(window);

            var open = Named<Button>(window, "OpenButton");
            var save = Named<Button>(window, "SaveButton");
            var reading = Named<Button>(window, "ReadingButton");
            var focus = Named<Button>(window, "FocusButton");
            var settings = Named<Button>(window, "SettingsButton");
            var mainMenu = Named<FrameworkElement>(window, "MainMenu");
            var brand = Named<Border>(window, "BrandHeader");
            var tabRow = Named<FrameworkElement>(window, "TabRow");
            var sidebar = Named<Border>(window, "Sidebar");
            var contextToolbar = Named<FrameworkElement>(window, "ContextToolbar");
            var footer = Named<FrameworkElement>(window, "Footer");
            var statusControls = Named<FrameworkElement>(window, "EditorStatusControls");
            var sidebarToggle = Named<Button>(window, "SidebarToggle");

            check(new[] { open, save, reading, focus, settings }.All(button => button.IsVisible),
                "布局：品牌区保留打开、保存、阅读、专注、设置五个主操作");
            var actionButtons = Descendants(brand).OfType<Button>()
                .Where(button => button.IsVisible && button.Content is string label && !string.IsNullOrEmpty(label))
                .OrderBy(button => BoundsIn(button, (FrameworkElement)window.Content).Left)
                .ToArray();
            var actionLabels = actionButtons.Select(button => button.Content?.ToString()).ToArray();
            check(actionLabels.SequenceEqual(["打开文件", "保存", "阅读模式", "专注", "设置"]),
                "布局：五个主操作按 macOS 顺序排列（实际：" + string.Join(" / ", actionLabels) + "）");
            check(settings.Tag as string == "appearance" &&
                AutomationProperties.GetName(settings) == "阅读外观与编辑设置",
                "布局：设置按钮接入现有阅读外观设置入口");
            check(ColorsEqual(reading.Background, (Brush)window.FindResource("AccentBrush")),
                "布局：阅读主按钮使用当前主题强调色");
            check(ColorsEqual(reading.Foreground, (Brush)window.FindResource("AccentForegroundBrush")),
                "布局：阅读主按钮使用强调色前景");
            check(Nearly(sidebarToggle.ActualWidth, 30),
                "布局：侧栏按钮保持 30 像素宽");
            check(Descendants(brand).OfType<TextBlock>().Any(text =>
                    text.Text == "轻阅" && Nearly(text.FontSize, 18)),
                "布局：品牌标题使用 18 像素字号");
            check(Descendants(brand).OfType<TextBlock>().All(text =>
                    text.Text != "本机文本 · 阅读与编辑"),
                "布局：紧凑品牌区不再显示旧副标题");
            check(ReferenceEquals(VisualTreeHelper.GetParent(mainMenu), VisualTreeHelper.GetParent(brand)) &&
                Nearly(BoundsIn(mainMenu, (FrameworkElement)window.Content).Top,
                    BoundsIn(brand, (FrameworkElement)window.Content).Top),
                "布局：菜单与品牌头共享同一顶部行");
            check(Nearly(sidebar.ActualWidth, 218),
                "布局：普通侧栏宽度为 218 像素");
            check(footer.IsVisible && statusControls.IsVisible,
                "布局：底栏和三个文档状态控件始终显示");

            var kindCombo = Named<ComboBox>(window, "KindCombo");
            var codecCombo = Named<ComboBox>(window, "CodecCombo");
            var endingCombo = Named<ComboBox>(window, "EndingCombo");
            CheckFooter(window, footer, [kindCombo, codecCombo, endingCombo], check);

            settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await PumpLayout(window);
            var settingsDialog = Application.Current.Windows.OfType<Window>()
                .FirstOrDefault(candidate => ReferenceEquals(candidate.Owner, window) &&
                    candidate.Title == "阅读外观与编辑设置");
            check(settingsDialog is { IsVisible: true },
                "布局：点击设置按钮真实打开非模态阅读外观窗口");
            if (settingsDialog is not null)
            {
                settingsDialog.UpdateLayout();
                await settingsDialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var cards = Descendants(settingsDialog).OfType<UniformGrid>()
                    .FirstOrDefault(element => element.Name == "ThemeCards");
                var readingPreview = Descendants(settingsDialog).OfType<Border>()
                    .FirstOrDefault(element => element.Name == "ReadingPreview");
                var codePreview = Descendants(settingsDialog).OfType<Border>()
                    .FirstOrDefault(element => element.Name == "CodePreview");
                var follow = Descendants(settingsDialog).OfType<CheckBox>()
                    .FirstOrDefault(element => element.Name == "FollowSystemCheck");
                var dark = Descendants(settingsDialog).OfType<CheckBox>()
                    .FirstOrDefault(element => element.Name == "DarkAppearanceCheck");
                var cardButtons = cards?.Children.OfType<Button>().ToArray() ?? [];
                var scrolls = Descendants(settingsDialog).OfType<ScrollViewer>().ToArray();
                var readingScroll = scrolls.FirstOrDefault(scroll =>
                    AutomationProperties.GetName(scroll) == "阅读模式预览滚动区域");
                var codeScroll = scrolls.FirstOrDefault(scroll =>
                    AutomationProperties.GetName(scroll) == "编辑模式预览滚动区域");
                var fontPicker = Descendants(settingsDialog).OfType<ComboBox>()
                    .FirstOrDefault(element => element.Name == "ReadingFontPicker");
                var fontChoices = fontPicker?.Items.Cast<object>().ToArray() ?? [];
                var fontLabels = fontChoices
                    .Select(item => item.GetType().GetProperty("Label")?.GetValue(item)?.ToString())
                    .ToArray();
                check(Nearly(settingsDialog.Width, 900) &&
                    Nearly(settingsDialog.MinWidth, 820) &&
                    Nearly(settingsDialog.MinHeight, 620) &&
                    settingsDialog.ResizeMode == ResizeMode.CanResize &&
                    Nearly(settingsDialog.Height, 740),
                    "布局：设置窗口为 900×740、最小 820×620 且可调整大小");
                check(cards?.Children.Count == 7 &&
                    cardButtons.Length == 7 &&
                    cardButtons.All(card => Nearly(card.Width, 176) && Nearly(card.Height, 78)),
                    "布局：七张主题卡严格为 176×78");
                check(readingPreview is not null &&
                    codePreview is not null &&
                    readingScroll is not null &&
                    codeScroll is not null &&
                    readingScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto &&
                    codeScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                    "布局：阅读与编辑预览各自提供独立垂直滚动区域");
                check(readingPreview is not null &&
                    Descendants(readingPreview).OfType<TextBlock>().All(text =>
                        !text.Text.Contains("# ", StringComparison.Ordinal) &&
                        !text.Text.Contains("> ", StringComparison.Ordinal)),
                    "布局：阅读预览不暴露 Markdown 井号或引用源标记");
                check(fontLabels.SequenceEqual(["微软雅黑（苹方替代）", "宋体", "系统字体"]),
                    "布局：Windows 阅读字体明确提供苹方替代、宋体、系统字体三项");
                var mainSettings = Field<ReaderSettings>(window, "settings");
                var simSun = fontChoices.Single(item =>
                    item.GetType().GetProperty("Label")?.GetValue(item)?.ToString() == "宋体");
                fontPicker!.SelectedItem = simSun;
                await settingsDialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                check(mainSettings.FontFamily == "SimSun",
                    "布局：修改阅读字体后立即同步到主窗口设置");
                var restore = Descendants(settingsDialog).OfType<Button>()
                    .FirstOrDefault(element => element.Name == "RestoreDefaultsButton");
                restore?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await settingsDialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                check(restore is not null &&
                    mainSettings.Theme == "classic" &&
                    mainSettings.FollowSystem &&
                    Nearly(mainSettings.FontSize, 14) &&
                    Nearly(mainSettings.LineSpacing, 6) &&
                    Nearly(mainSettings.ParagraphSpacing, 13) &&
                    Nearly(mainSettings.ReadingWidth, 680),
                    "布局：恢复默认值匹配 macOS classic / 跟随系统 / 14 / 6 / 13 / 680");
                check(readingPreview is not null &&
                    codePreview is not null &&
                    readingScroll is not null &&
                    codeScroll is not null &&
                    follow is not null &&
                    dark is not null &&
                    dark.IsEnabled == !(follow.IsChecked == true),
                    "布局：设置窗口保留双预览与系统外观跟随联动");
                settingsDialog.Close();
            }
            await PumpLayout(window);

            ((ComboBox)window.FindName("KindCombo")).SelectedItem = TextKind.Plain;
            await PumpLayout(window);
            if (!window.CurrentTab!.IsReading) window.ToggleReading();
            await PumpLayout(window);
            check(mainMenu.IsVisible && brand.IsVisible && contextToolbar.IsVisible &&
                statusControls.IsVisible && footer.IsVisible,
                "布局：阅读态保留菜单、品牌、上下文工具条、文档状态与底栏");
            check(reading.Content?.ToString() == "返回编辑",
                "布局：普通文本阅读态显示返回编辑");

            var markdown = window.NewDocument("# 布局\n\n正文");
            ((ComboBox)window.FindName("KindCombo")).SelectedItem = TextKind.Markdown;
            await PumpLayout(window);
            check(reading.Content?.ToString() == "返回预览",
                "布局：Markdown 编辑态显示返回预览");
            window.ToggleReading();
            await PumpLayout(window);
            check(reading.Content?.ToString() == "编辑原文",
                "布局：Markdown 阅读态显示编辑原文");

            window.ToggleFocus();
            await PumpLayout(window);
            check(!mainMenu.IsVisible && brand.IsVisible && contextToolbar.IsVisible &&
                statusControls.IsVisible && footer.IsVisible,
                    "布局：专注态隐藏窗口内菜单栏，保留品牌、上下文工具条、文档状态与底栏");
            check(!tabRow.IsVisible && !sidebar.IsVisible && focus.Content?.ToString() == "退出专注",
                "布局：专注态隐藏标签行与侧栏并更新退出入口");

            foreach (double width in new[] { 820d, 1120d })
            {
                window.Width = width;
                await PumpLayout(window);
                var root = (FrameworkElement)window.Content;
                var sidebarBounds = BoundsIn(sidebar, root);
                var readingBounds = BoundsIn(contextToolbar, root);
                check(readingBounds.Left >= sidebarBounds.Right - 1,
                    $"布局：{width:0} 宽上下文工具条只占右侧文档区");
                CheckContained(root, Descendants(brand).OfType<Button>().Where(button => button.IsVisible),
                    $"布局：{width:0} 宽品牌按钮不溢出", check);
                CheckContained(root, Descendants(contextToolbar).OfType<Button>()
                        .Where(button => button.IsVisible),
                    $"布局：{width:0} 宽上下文按钮不溢出", check);
                CheckFooter(window, footer, [kindCombo, codecCombo, endingCombo], check);
            }

            window.ToggleFocus();
            window.ToggleReading();
            await PumpLayout(window);
            check(tabRow.IsVisible && sidebar.IsVisible && mainMenu.IsVisible && brand.IsVisible,
                "布局：退出专注和阅读后恢复菜单、标签、侧栏与品牌结构");
            var rootAfter = (FrameworkElement)window.Content;
            check(contextToolbar.IsVisible &&
                Named<FrameworkElement>(window, "CopyReadingButton").Visibility == Visibility.Collapsed &&
                Named<FrameworkElement>(window, "MarkdownEditOptionsMenu").Visibility == Visibility.Visible,
                "布局：退出阅读后复用上下文工具条并恢复 Markdown 编辑入口");

            foreach (double width in new[] { 820d, 1120d })
            {
                window.Width = width;
                await PumpLayout(window);
                var root = (FrameworkElement)window.Content;
                CheckContained(root, Descendants(brand).OfType<Button>().Where(button => button.IsVisible),
                    $"布局：{width:0} 宽编辑品牌按钮不溢出", check);
                check(BoundsIn(contextToolbar, root).Left >= BoundsIn(sidebar, root).Right - 1,
                    $"布局：{width:0} 宽统一工具条只占右侧文档区");
            }
        }
        finally
        {
            window?.CloseForShutdown();
        }
    }

    public static void CheckEntryPoints(MainWindow window, Action<bool, string> check)
    {
        check(window.FindName("FormatButton") is Button { IsVisible: true },
            "构建2：编辑区有明显格式化入口");
        check(window.FindName("StructureButton") is Button,
            "构建2：JSON有结构折叠入口");
        check(window.FindName("MarkdownButton") is Button,
            "构建2：Markdown有分屏预览入口");
        var dialogs = typeof(MainWindow).Assembly.GetType("Qingyue.Windows.Dialogs")!;
        var help = dialogs.GetMethod("CreateHelp", BindingFlags.Static | BindingFlags.Public);
        check(help is not null, "构建2：帮助使用主题化专用窗口");
        if (help?.Invoke(null, [window, false]) is System.Windows.Window dialog)
        {
            check(dialog.Owner == window && dialog.Resources.Contains("AccentBrush"),
                "构建2：帮助沿用当前窗口主题资源");
            dialog.Close();
        }
    }

    public static async Task CheckSearchEscape(MainWindow window, Action<bool, string> check)
    {
        var searchPanel = Named<FrameworkElement>(window, "SearchPanel");
        var searchQuery = Named<TextBox>(window, "SearchQuery");
        var cancel = Named<Button>(window, "SearchCancelButton");
        var tabRow = Named<FrameworkElement>(window, "TabRow");
        var sidebar = Named<Border>(window, "Sidebar");
        var menu = Named<Menu>(window, "MainMenu");

        ClickMenuItem(menu, "find");
        await PumpLayout(window);
        check(searchPanel.Visibility == Visibility.Visible,
            "Esc：查找菜单真实打开完整搜索面板");

        Named<Button>(window, "SettingsButton").Focus();
        PressEscape(window);
        await PumpLayout(window);
        check(searchPanel.Visibility == Visibility.Collapsed,
            "Esc：焦点不在搜索框时仍由窗口关闭完整搜索面板");

        ClickMenuItem(menu, "find");
        searchQuery.Text = "布局";
        await PumpLayout(window);
        check(cancel.Visibility == Visibility.Visible,
            "Esc：查找执行期间显示取消按钮");
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await PumpLayout(window);
        check(searchPanel.Visibility == Visibility.Visible,
            "Esc：取消查找只停止任务，不关闭搜索面板");

        PressEscape(window);
        await PumpLayout(window);
        window.ToggleFocus();
        await PumpLayout(window);
        check(!tabRow.IsVisible && !sidebar.IsVisible,
            "Esc：进入专注后标签行和侧栏隐藏");
        PressEscape(window);
        await PumpLayout(window);
        check(tabRow.IsVisible && sidebar.IsVisible,
            "Esc：搜索关闭后再次按 Esc 退出专注");
    }

    public static async Task CheckFormatting(MainWindow window, Action<bool, string> check)
    {
        var plain = window.NewDocument("第一段  \n\n\n第二段\t ");
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        ((ComboBox)window.FindName("KindCombo")).SelectedItem = TextKind.Plain;
        ((Button)window.FindName("FormatButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(plain.File.Text == "第一段\n\n第二段", "构建2：TXT按钮整理空白并保留分段");
        plain.Editor.Undo();
        check(plain.File.Text == "第一段  \n\n\n第二段\t ", "构建2：TXT格式化一次撤销：" + System.Text.Json.JsonSerializer.Serialize(plain.File.Text));

        var markdown = window.NewDocument("#   标题\n\n\n正文  \n下一行\n\n```txt\n a   \n\n\n b\n```\n");
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        string before = markdown.File.Text;
        ((ComboBox)window.FindName("KindCombo")).SelectedItem = TextKind.Markdown;
        ((Button)window.FindName("FormatButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(markdown.File.Text.Contains("正文  \n下一行") && markdown.File.Text.Contains(" a   \n\n\n b"),
            "构建2：MD格式化保留硬换行和围栏代码");
        if (markdown.File.Text != before) markdown.Editor.Undo();
        check(markdown.File.Text == before, "构建2：MD格式化可还原完整原文");
        await CheckMarkdownPreview(window, check);

        var json = window.NewDocument("{\"中文\":\"正常\",\"重复\":1,\"重复\":2,\"大数\":9007199254740993}");
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        ((Button)window.FindName("FormatButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(json.File.Text.Contains("中文") && json.File.Text.Contains("9007199254740993"),
            "构建2：JSON界面格式化中文直出且大数不变");
        ((Button)window.FindName("StructureButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var host = (ContentControl)window.FindName("DocumentHost");
        check(host.Content is JsonStructureView, "构建2：结构按钮进入可折叠JSON结构");
        var root = (TreeViewItem)((JsonStructureView)host.Content).Items[0];
        check(root.Items.Count == 4, "构建2：JSON结构不丢弃重复字段");
        root.IsExpanded = false;
        check(!root.IsExpanded && json.File.Text.Contains("中文"), "构建2：折叠结构不修改原文");
        ((Button)window.FindName("StructureButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(ReferenceEquals(host.Content, json.EditorSurface), "构建2：结构视图可返回原编辑器");
        ((Button)window.FindName("StructureButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((ComboBox)window.FindName("KindCombo")).SelectedItem = TextKind.Plain;
        check(ReferenceEquals(host.Content, json.EditorSurface), "构建2：结构视图切换语言后自动返回编辑");
        ((ComboBox)window.FindName("KindCombo")).SelectedItem = TextKind.Json;
        ((Button)window.FindName("StructureButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        typeof(MainWindow).GetMethod("ShowSearch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false]);
        check(ReferenceEquals(host.Content, json.EditorSurface), "构建2：JSON结构中查找会显示原文编辑区");
        ((FrameworkElement)window.FindName("SearchPanel")).Visibility = Visibility.Collapsed;
        window.Width = 1120;
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var artifacts = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
        System.IO.Directory.CreateDirectory(artifacts);
        Program.SaveScreenshot(window, System.IO.Path.Combine(artifacts, "windows-build2-json-dark.png"));
        var tokens = JsonSyntax.Tokens("\"key\": \"a\\\"b\", \"n\": 12, \"b\": true").ToArray();
        check(tokens.Count(x => x.Kind == JsonTokenColor.Key) == 3 &&
            tokens.Count(x => x.Kind == JsonTokenColor.String) == 1 &&
            tokens.Count(x => x.Kind == JsonTokenColor.Value) == 2,
            "构建2：JSON分色识别键、转义字符串及数值布尔值");
        window.ApplyTheme(false);
        var dialogs = typeof(MainWindow).Assembly.GetType("Qingyue.Windows.Dialogs")!;
        var help = (Window)dialogs.GetMethod("CreateHelp", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [window, false])!;
        check(ReferenceEquals(help.Resources["EditorBrush"], window.Resources["EditorBrush"]),
            "构建2：浅色主题的帮助不退回默认深色");
        help.Show();
        help.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Program.SaveScreenshot(help, System.IO.Path.Combine(artifacts, "windows-build2-help-light.png"));
        help.Close();
        window.ApplyTheme(true);
        var darkHelp = (Window)dialogs.GetMethod("CreateHelp", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [window, false])!;
        darkHelp.Show(); darkHelp.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Program.SaveScreenshot(darkHelp, System.IO.Path.Combine(artifacts, "windows-build2-help-dark.png"));
        darkHelp.Close();
    }

    private static async Task CheckMarkdownPreview(MainWindow window, Action<bool, string> check)
    {
        const string source = "# 轻阅 · Markdown\n\n## 一、清晰的内容层次\n\n正文支持 **重点**、*强调* 与 `行内代码`。\n\n> 引用有独立的边线和背景。\n\n### 安装与使用\n\n- 选择安装位置\n- 点击安装并查看进度\n\n```json\n{\"theme\": \"graphite\", \"version\": \"0.2.0\"}\n```\n\n| 功能 | 状态 |\n| --- | --- |\n| MD 格式化 | 支持 |\n| 分屏预览 | 支持 |\n";
        var tab = window.NewDocument(source);
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        ((ComboBox)window.FindName("KindCombo")).SelectedItem = TextKind.Markdown;
        var button = (Button)window.FindName("MarkdownButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var host = (ContentControl)window.FindName("DocumentHost");
        check(host.Content is Grid && !ReferenceEquals(host.Content, tab.EditorSurface), "构建2：MD分屏保留原文编辑器");
        var split = (Grid)host.Content;
        MarkdownPreview Preview() => (MarkdownPreview)((ContentControl)((Grid)split.Children[2]).Children[1]).Content;
        var preview = Preview();
        check(preview.IsReadOnly && preview.Document.Blocks.OfType<System.Windows.Documents.Paragraph>().First().FontSize > tab.Editor.FontSize * 1.6,
            "构建2：MD预览标题大小与正文形成层次");
        check(tab.File.Text == source, "构建2：预览不改写Markdown原文");
        tab.Editor.AppendText("\n新增正文，用于验证实时预览。\n");
        await Task.Delay(550);
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        check(new System.Windows.Documents.TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text.Contains("新增正文"),
            "构建2：MD编辑后防抖更新预览");
        window.Width = 1280;
        window.UpdateLayout();
        var artifacts = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
        Program.SaveScreenshot(window, System.IO.Path.Combine(artifacts, "windows-build2-markdown-dark.png"));
        window.ApplyTheme(false);
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Program.SaveScreenshot(window, System.IO.Path.Combine(artifacts, "windows-build2-markdown-light.png"));
        window.ApplyTheme(true);
        var other = window.NewDocument("另一个文档");
        window.SelectTab(tab);
        check(ReferenceEquals(host.Content, split), "构建2：MD分屏切换标签后仍可返回");
        window.ToggleReading();
        check(ReferenceEquals(host.Content, tab.Preview), "构建2：MD分屏可切换到完整阅读模式");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(!tab.IsReading && !tab.SplitPreview && ReferenceEquals(host.Content, tab.EditorSurface),
            "构建2：阅读中点击MD预览按macOS契约退出阅读并关闭分屏");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(!tab.IsReading && tab.SplitPreview && ReferenceEquals(host.Content, split),
            "构建2：退出阅读后可重新开启分屏");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(ReferenceEquals(host.Content, tab.EditorSurface), "构建2：关闭预览返回原文编辑");
        window.SelectTab(other);
        window.RenameTab(other, "测试语言.md");
        check(other.Kind == TextKind.Markdown, "构建2：未保存文件按新名称扩展名识别语言");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(new System.Windows.Documents.TextRange(Preview().Document.ContentStart, Preview().Document.ContentEnd).Text.Contains("另一个文档"),
            "构建2：新文档预览首帧显示自身内容");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.SelectTab(other);
    }

    private static T Named<T>(MainWindow window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new Exception($"布局控件不存在或类型错误：{name}");

    private static T Field<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().Name, name));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static Rect BoundsIn(FrameworkElement element, FrameworkElement root)
    {
        var origin = element.TranslatePoint(new Point(0, 0), root);
        return new Rect(origin, element.RenderSize);
    }

    private static bool Nearly(double actual, double expected) => Math.Abs(actual - expected) <= 1;

    private static bool ColorsEqual(Brush left, Brush right) =>
        left is SolidColorBrush first && right is SolidColorBrush second && first.Color == second.Color;

    private static void CheckContained(FrameworkElement root, IEnumerable<Button> buttons,
        string message, Action<bool, string> check)
    {
        var invalid = buttons.FirstOrDefault(button =>
        {
            var bounds = BoundsIn(button, root);
            return bounds.Left < -1 || bounds.Right > root.ActualWidth + 1;
        });
        check(invalid is null, message + (invalid is null ? "" : "：" + invalid.Content));
    }

    private static void CheckFooter(MainWindow window, FrameworkElement footer,
        IReadOnlyList<ComboBox> combos, Action<bool, string> check)
    {
        var root = (FrameworkElement)window.Content;
        var ordered = combos.OrderBy(combo => BoundsIn(combo, root).Left).ToArray();
        check(ordered.Length == 3 && combos.All(combo => combo.IsVisible),
            "布局：底栏始终保留语言、编码和换行三个下拉");
        check(ordered.Zip(new[] { 150d, 138d, 178d })
                .All(pair => Nearly(pair.First.ActualWidth, pair.Second)),
            "布局：底栏下拉宽度依次为 150 / 138 / 178");
        check(ordered.SequenceEqual([Named<ComboBox>(window, "KindCombo"),
                Named<ComboBox>(window, "CodecCombo"), Named<ComboBox>(window, "EndingCombo")]),
            "布局：底栏下拉顺序为文档语言、保存编码、文件换行方式");
        check(ordered.Zip(ordered.Skip(1))
                .All(pair => BoundsIn(pair.First, root).Right + 7 <= BoundsIn(pair.Second, root).Left),
            "布局：底栏下拉保持可见间距");
    }

    private static async Task PumpLayout(MainWindow window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static MenuItem? FindMenuItem(DependencyObject root, string tag)
    {
        if (root is MenuItem item && string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            return item;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (FindMenuItem(child, tag) is { } found) return found;
        return null;
    }

    private static void ClickMenuItem(Menu menu, string tag)
    {
        var item = FindMenuItem(menu, tag) ?? throw new Exception("菜单项不存在：" + tag);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static void PressEscape(Window window)
    {
        var source = PresentationSource.FromVisual(window)
            ?? throw new Exception("窗口尚未加载，无法模拟键盘事件");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        window.RaiseEvent(args);
    }
}
