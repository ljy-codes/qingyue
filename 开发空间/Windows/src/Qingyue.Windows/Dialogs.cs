using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        var subtitle = new TextBlock { Text = "0.2.0 · Windows 构建 3", Margin = new Thickness(0, 7, 0, 0) };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        title.Children.Add(subtitle); brand.Children.Add(title); panel.Children.Add(brand);
        panel.Children.Add(Card("从一份新文件开始",
            "双击标签栏空白处新建，按住标签左右拖动排序。\n双击选取文本片段，三击选中整行。", "Ctrl+N  新建    Ctrl+O  打开"));
        panel.Children.Add(Card("让内容更好读",
            "上方“格式化”支持 JSON、Markdown；“整理 TXT”清理空白。\n“MD 预览”支持离线 Mermaid 时序图；“JSON 结构”可折叠层级。", "Ctrl+Alt+F  格式化    Ctrl+R  阅读"));
        panel.Children.Add(Card("每一次修改，都看得见",
            "行号旁橙色表示尚未保存，绿色表示本次会话已保存的修改。\n恢复草稿只保存在本机；修改原文件仍需点击保存。", "Ctrl+S  保存    Ctrl+Z  撤销"));
        panel.Children.Add(new TextBlock
        {
            Text = about ? "本机文本阅读与编辑 · .NET 10 / WPF\n项目作者：ljy-codes · github.com/ljy-codes/qingyue"
                : "Ctrl+F 查找  ·  Ctrl+H 替换  ·  Ctrl+G 跳转行\nCtrl+B 侧栏  ·  F2 重命名  ·  Ctrl+Tab 切换标签",
            Margin = new Thickness(0, 20, 0, 20), TextWrapping = TextWrapping.Wrap
        });
        var close = PrimaryButton("开始使用");
        close.IsCancel = true;
        close.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(close);
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return dialog;
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
        button.Foreground = Brushes.White;
        return button;
    }
}
