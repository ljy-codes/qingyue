using System.Reflection;
using System.Windows.Controls;
using System.Windows;
using Qingyue.Core;
using Qingyue.Windows;

internal static class Build2Checks
{
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
        check(!tab.IsReading && ReferenceEquals(host.Content, split), "构建2：阅读中点击MD预览直接开启分屏");
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
}
