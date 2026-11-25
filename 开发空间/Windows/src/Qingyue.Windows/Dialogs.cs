using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;

namespace Qingyue.Windows;

internal static class Dialogs
{
    public static string? Prompt(Window owner, string title, string description, string initial)
    {
        var dialog = Shell(owner, title, 440);
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        var input = new TextBox { Text = initial };
        stack.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = "确定", IsDefault = true };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); stack.Children.Add(buttons);
        dialog.Content = stack;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    public static string CloseChoice(Window owner, string title, bool allowKeep)
    {
        var dialog = Shell(owner, "处理未保存的修改", 500);
        string result = "cancel";
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = $"“{title}”尚未保存", FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock
        {
            Text = allowKeep ? "可以保存原文件，或保留本机恢复草稿，下次继续。" : "保存后再关闭；放弃会清除这份文档的恢复草稿。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 20)
        });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, choice) in new[] { ("保存", "save"), ("保留草稿", "keep"), ("放弃", "discard"), ("取消", "cancel") })
        {
            if (choice == "keep" && !allowKeep) continue;
            var button = new Button { Content = label, Margin = new Thickness(5, 0, 0, 0), IsCancel = choice == "cancel", IsDefault = choice == "save" };
            button.Click += (_, _) => { result = choice; dialog.DialogResult = choice != "cancel"; };
            buttons.Children.Add(button);
        }
        stack.Children.Add(buttons); dialog.Content = stack; dialog.ShowDialog();
        return result;
    }

    public static Window Shell(Window owner, string title, double width)
    {
        var dialog = new Window
        {
            Owner = owner, Title = title, Width = width, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, MaxHeight = SystemParameters.WorkArea.Height - 40,
            Icon = owner.Icon
        };
        // Owned windows do not inherit the owner's local resources through the visual tree.
        foreach (var key in owner.Resources.Keys)
            dialog.Resources[key] = owner.Resources[key];
        return dialog;
    }

    public static bool? Message(Window owner, string text, string title)
    {
        var dialog = Shell(owner, title, 540);
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(Heading(title, 21));
        panel.Children.Add(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 24)
        });
        var close = PrimaryButton("知道了");
        close.IsCancel = true;
        close.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(close);
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return dialog.ShowDialog();
    }

    public static bool Confirm(Window owner, string title, string description,
        string cancelText, string confirmText)
    {
        var dialog = Shell(owner, title, 500);
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(Heading(title, 21));
        panel.Children.Add(new TextBlock
        {
            Text = description, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 18, 0, 24)
        });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button
        {
            Content = cancelText, IsCancel = true,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var confirm = PrimaryButton(confirmText);
        confirm.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        panel.Children.Add(actions);
        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }

    public static bool? UpdateAvailable(Window owner, UpdateCheckResult result, Action openReleasePage)
    {
        var dialog = Shell(owner, "发现轻阅更新", 540);
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(Heading("发现新版本 " + result.LatestVersion, 21));
        panel.Children.Add(new TextBlock
        {
            Text = $"当前版本：{result.CurrentVersion}\n\n更新通过 GitHub Release 提供，打开发布页后可下载最新安装包。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 24)
        });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var later = new Button { Content = "稍后", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var open = PrimaryButton("打开发布页");
        open.Click += (_, _) =>
        {
            openReleasePage();
            dialog.DialogResult = true;
        };
        actions.Children.Add(later);
        actions.Children.Add(open);
        panel.Children.Add(actions);
        dialog.Content = panel;
        return dialog.ShowDialog();
    }

    public static Window CreateHelp(Window owner, bool about)
    {
        var dialog = Shell(owner, about ? "关于轻阅" : "轻阅使用指南", 660);
        var panel = new StackPanel { Margin = new Thickness(30) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        brand.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Qingyue;component/Assets/Qingyue.png")),
            Width = 64, Height = 64, Margin = new Thickness(0, 0, 18, 0)
        });
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(Heading(about ? "轻阅 Qingyue" : "留一点空间，给文字。", 25));
        var subtitle = new TextBlock { Text = VersionCaption(), Margin = new Thickness(0, 7, 0, 0) };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        title.Children.Add(subtitle); brand.Children.Add(title); panel.Children.Add(brand);
        panel.Children.Add(Card("从一份新文件开始",
            "双击标签栏空白处新建，按住标签左右拖动排序。\n双击选取文本片段，三击选中整行。", "Ctrl+N  新建    Ctrl+O  打开"));
        panel.Children.Add(Card("让内容更好读",
            "Markdown 打开即预览；图片可点击放大，宽表格可展开阅读。\n滚动位置随会话保存；退出查找返回原阅读位置。", "Ctrl+R  预览 / 编辑    Alt+←  返回位置"));
        panel.Children.Add(Card("读完，方便带走",
            "“复制”提供富文本、纯文本和 Markdown；代码块和表格可单独复制。\n从文件菜单导出 HTML，可将已加载图片内嵌带走。打开与复制准备均可取消。", "Ctrl+C  复制选区    文件 → 导出 HTML"));
        panel.Children.Add(Card("把阅读留下来",
            "选中文字后点击“摘录”，可以保存引文、备注、标签和颜色；批注保存在本机并可从侧栏跳回原文。\n当前文档还能导出 Markdown 或 HTML 阅读简报，包含章节、进度和定位状态。", "编辑 → 批注与阅读成果  添加摘录    视图 → 阅读外观"));
        panel.Children.Add(Card("每一次修改，都看得见",
            "行号旁橙色表示尚未保存，绿色表示本次会话已保存的修改。\n恢复草稿只保存在本机；修改原文件仍需点击保存。", "Ctrl+S  保存    Ctrl+Z  撤销"));
        panel.Children.Add(new TextBlock
        {
            Text = about ? "本机文本阅读与编辑 · .NET 10 / WPF\n项目作者：ljy-codes · github.com/ljy-codes/qingyue"
                : "Ctrl+F 查找  ·  Ctrl+Shift+H 替换  ·  Ctrl+L 跳转行\nCtrl+B 侧栏  ·  Ctrl+Shift+] / [ 切换标签",
            Margin = new Thickness(0, 20, 0, 20), TextWrapping = TextWrapping.Wrap
        });
        var close = PrimaryButton("开始使用");
        close.IsCancel = true;
        close.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(close);
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return dialog;
    }

    private static string VersionCaption()
    {
        var assembly = typeof(Dialogs).Assembly;
        string informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "";
        string version = informational.Split('+', 2)[0];
        if (string.IsNullOrWhiteSpace(version))
        {
            var fallback = assembly.GetName().Version ?? new Version(0, 0, 0);
            version = $"{fallback.Major}.{fallback.Minor}.{fallback.Build}";
        }
        const string buildMarker = "windows-build.";
        int marker = informational.IndexOf(buildMarker, StringComparison.OrdinalIgnoreCase);
        string build = "1";
        if (marker >= 0)
        {
            int start = marker + buildMarker.Length;
            int end = start;
            while (end < informational.Length && char.IsAsciiDigit(informational[end])) end++;
            if (end > start) build = informational[start..end];
        }
        return $"{version} · Windows 构建 {build}";
    }

    public static bool? PreviewTextChanges(Window owner, string before, string after)
    {
        var dialog = Shell(owner, "预览整理空白", 860);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Heading("仅整理行尾空白", 21));
        panel.Children.Add(new TextBlock
        {
            Text = "当前语言没有完整格式化器。行尾空格可能属于字符串内容，请比较后决定是否应用。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 18)
        });
        var columns = new Grid { Height = 300 };
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (text, column, label) in new[] { (before, 0, "整理前"), (after, 1, "整理后") })
        {
            var stack = new DockPanel { Margin = new Thickness(column == 1 ? 8 : 0, 0, column == 0 ? 8 : 0, 0) };
            var caption = Heading(label, 14);
            DockPanel.SetDock(caption, Dock.Top);
            stack.Children.Add(caption);
            var editor = new TextBox
            {
                Text = text, IsReadOnly = true, AcceptsReturn = true,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 10, 0, 0)
            };
            stack.Children.Add(editor); Grid.SetColumn(stack, column); columns.Children.Add(stack);
        }
        panel.Children.Add(columns);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        actions.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) });
        var apply = PrimaryButton("应用整理");
        apply.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(apply); panel.Children.Add(actions);
        dialog.Content = panel;
        return dialog.ShowDialog();
    }

    private static TextBlock Heading(string text, double size)
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        return label;
    }

    private static Border Card(string title, string body, string shortcut)
    {
        var content = new StackPanel();
        var heading = Heading(title, 16);
        heading.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        content.Children.Add(heading);
        content.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 10), LineHeight = 23 });
        var keys = new TextBlock { Text = shortcut, FontSize = 12 };
        keys.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        content.Children.Add(keys);
        var border = new Border { Child = content, Padding = new Thickness(18, 15, 18, 15), CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 18, 0, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        return border;
    }

    private static Button PrimaryButton(string text)
    {
        var button = new Button { Content = text, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 110 };
        button.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
        button.SetResourceReference(Control.ForegroundProperty, "AccentForegroundBrush");
        return button;
    }
}
