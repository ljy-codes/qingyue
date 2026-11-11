using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Qingyue.Core;
using Qingyue.Windows;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "qingyue-reading-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Qingyue;component/Theme.xaml")
        });
        int result = 1;
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                string body = "# 大文档\n\n" + new string('文', 490_000);
                Check(ReadingModel.Build(body, TextKind.Markdown).Headings.Count == 1, "49万正文保留Markdown解析");
                Check(PreviewLimits.CanRender(new string('x', 500_000), TextKind.Markdown), "50万字符边界可预览");
                Check(!PreviewLimits.CanRender(new string('x', 500_001), TextKind.Markdown), "超过正文上限降级");
                string image = "![图片](data:image/png;base64," + new string('A', 600_000) + ")";
                Check(PreviewLimits.CanRender("# 内嵌图\n\n" + image, TextKind.Markdown), "内嵌图片不占正文50万额度");
                Check(!PreviewLimits.CanRender("```md\n" + image + "\n```", TextKind.Markdown), "代码中的data文本仍计正文");
                Check(!PreviewLimits.CanRender("示例：`" + image + "`", TextKind.Markdown), "行内代码中的data文本仍计正文");
                Check(!PreviewLimits.CanRender("示例：``" + image + "``", TextKind.Markdown), "多反引号代码中的data文本仍计正文");
                Check(!PreviewLimits.CanRender("示例：\\" + image, TextKind.Markdown), "转义图片语法仍计正文");
                Check(PreviewLimits.CanRender("`说明` " + image, TextKind.Markdown), "代码后的真实内嵌图片仍可预览");
                Check(!PreviewLimits.CanRender("![a] junk ](data:image/png;base64," + new string('A', 600_000) + ")",
                    TextKind.Markdown), "畸形图片引用中的data仍计正文");
                Check(!PreviewLimits.CanRender("![跨行\r标题](data:image/png;base64," + new string('A', 600_000) + ")",
                    TextKind.Markdown), "单CR换行的畸形图片仍计正文");
                string unfinished = string.Concat(Enumerable.Repeat("![", 100_000)) + new string('x', 500_001);
                var scanTime = Stopwatch.StartNew();
                Check(!PreviewLimits.CanRender(unfinished, TextKind.Markdown), "大量未闭合图片仍按正文降级");
                Check(scanTime.Elapsed < TimeSpan.FromSeconds(2), "未闭合图片扫描保持线性响应");
                string path = Path.Combine(root, "默认阅读 中文.md");
                File.WriteAllText(path, "# 阅读标题\n\n正文包含 **中文** 与第二处中文。\n\n| 参数 | 说明 |\n| --- | --- |\n| key | 值 |\n\n- 列表\n");
                window = new MainWindow(Path.Combine(root, "profile"), restoreSession: false);
                window.Show();
                await window.InitializeAsync();
                await window.OpenPathsAsync([path]);
                var markdown = window.CurrentTab!;
                await Idle(window);
                Check(markdown.IsReading, "磁盘Markdown打开即预览");
                Check(markdown.Preview.IsKeyboardFocusWithin, "首次预览加载后具有真实键盘焦点");
                Check(ReferenceEquals(((ContentControl)window.FindName("DocumentHost")).Content, markdown.Preview), "默认全宽预览没有分屏");
                Check(markdown.Preview.Document.Blocks.OfType<Table>().Any(), "全宽预览保留真实表格");
                Check(markdown.Preview.Document.Blocks.OfType<System.Windows.Documents.List>().Any(), "全宽预览保留列表层次");
                for (int round = 1; round <= 3; round++)
                {
                    ((Button)window.FindName("ReadingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Idle(window);
                    Check(!markdown.IsReading &&
                        ReferenceEquals(((ContentControl)window.FindName("DocumentHost")).Content, markdown.EditorSurface),
                        $"按钮第{round}轮返回编辑");
                    ((Button)window.FindName("ReadingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Idle(window);
                    Check(markdown.IsReading &&
                        ReferenceEquals(((ContentControl)window.FindName("DocumentHost")).Content, markdown.Preview),
                        $"按钮第{round}轮恢复全宽预览");
                }
                Action(window, "markdownPreview");
                await Idle(window);
                Check(!markdown.IsReading &&
                    !ReferenceEquals(((ContentControl)window.FindName("DocumentHost")).Content, markdown.EditorSurface),
                    "可选分屏进入编辑");
                markdown.Editor.AppendText("\n即时复制的新增内容");
                // Invoke only the production data factory; never read or write the system clipboard.
                var copied = (ReadingClipboardResult?)typeof(MainWindow)
                    .GetMethod("CreateReadingDocumentData", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, null);
                Check(copied is not null &&
                    (copied.Data.GetData(DataFormats.UnicodeText) as string)?.Contains("即时复制的新增内容") == true,
                    "分屏输入后立即复制全文包含最新正文");
                markdown.Editor.Undo();
                ((Button)window.FindName("ReadingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Idle(window);
                Check(markdown.IsReading &&
                    ReferenceEquals(((ContentControl)window.FindName("DocumentHost")).Content, markdown.Preview),
                    "从分屏编辑可恢复全宽预览");
                ((Button)window.FindName("ReadingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Idle(window);
                Check(!markdown.IsReading &&
                    ReferenceEquals(((ContentControl)window.FindName("DocumentHost")).Content, markdown.EditorSurface),
                    "分屏返回预览后仍可继续编辑");
                markdown.Editor.AppendText("\n\n编辑往返验收");
                ((Button)window.FindName("ReadingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Idle(window);
                Check(markdown.IsReading && markdown.Reading!.VisibleText.Contains("编辑往返验收"),
                    "实际按钮从编辑恢复最新预览");
                window.ToggleReading();
                markdown.Editor.Undo();
                window.ToggleReading();
                Check(!markdown.File.IsDirty, "往返预览保留编辑撤销与保存基线");
                foreach (string token in new[] { "阅读标题", "中文", "参数", "key", "值", "列表" })
                {
                    int offset = markdown.Reading!.VisibleText.IndexOf(token, StringComparison.Ordinal);
                    var start = markdown.Rendered!.PointerAt(offset);
                    var end = markdown.Rendered.PointerAt(offset + token.Length);
                    Check(new TextRange(start, end).Text == token && markdown.Rendered.OffsetAt(start) == offset,
                        "富文本导航准确映射：" + token);
                }
                int cellOffset = markdown.Reading!.VisibleText.IndexOf("值", StringComparison.Ordinal);
                markdown.Preview.Selection.Select(markdown.Rendered!.PointerAt(cellOffset),
                    markdown.Rendered.PointerAt(cellOffset + 1));
                var previewCopy = CopyData(markdown.Preview, "值");
                string previewHtml = (string)previewCopy.GetData(DataFormats.Html)!;
                Check(previewHtml.Contains("<table") && previewHtml.Contains("值") &&
                    !previewHtml.Contains("阅读标题"), "真实阅读控件复制钩子仅输出选中表格内容");
                string source = markdown.File.Text;
                Action(window, "find");
                var query = (TextBox)window.FindName("SearchQuery");
                query.Text = "中文";
                await Until(() => ((TextBlock)window.FindName("SearchCount")).Text.Contains(" / "));
                Check(markdown.Preview.Selection.Text == "中文", "输入搜索后自动高亮匹配");
                Check(query.IsKeyboardFocusWithin, "自动匹配不抢搜索输入焦点");
                Check(!CopyData(query, query.Text).GetDataPresent(DataFormats.Html, false),
                    "搜索输入框复制不进入Markdown富文本转换");
                await Idle(window);
                Check(HasSelectionDrawing(window), "首次自动查找实际绘制选区高亮");
                query.Text = "第二";
                await Until(() => markdown.Preview.Selection.Text == "第二");
                await Idle(window);
                Check(query.IsKeyboardFocusWithin && HasSelectionDrawing(window), "连续查询仍实际高亮并保留搜索输入焦点");
                query.Text = "中文";
                await Until(() => markdown.Preview.Selection.Text == "中文");
                var readingDocument = markdown.Preview.Document;
                foreach (double width in new[] { 840d, 1320d, 1120d })
                {
                    window.Width = width;
                    await Idle(window);
                    Check(ReferenceEquals(readingDocument, markdown.Preview.Document) &&
                        markdown.Preview.Selection.Text == "中文" && query.IsKeyboardFocusWithin &&
                        HasSelectionDrawing(window), $"真实窗口调整至{width}保留阅读文档及搜索高亮");
                }
                window.ToggleSidebar();
                await Idle(window);
                Check(ReferenceEquals(readingDocument, markdown.Preview.Document) &&
                    markdown.Preview.Selection.Text == "中文" && query.IsKeyboardFocusWithin,
                    "切换侧栏保留当前搜索与阅读文档");
                window.ToggleSidebar();
                var headingBlock = markdown.Preview.Document.Blocks.OfType<Paragraph>().First();
                var tableBlock = markdown.Preview.Document.Blocks.OfType<Table>().Single();
                double headingSize = headingBlock.FontSize, tableSize = tableBlock.FontSize;
                Action(window, "fontUp");
                await Idle(window);
                Check(headingBlock.FontSize > headingSize && tableBlock.FontSize > tableSize &&
                    markdown.Preview.Selection.Text == "中文" && ReferenceEquals(readingDocument, markdown.Preview.Document),
                    "真实字号命令同步标题表格并保留文档与选区");
                Action(window, "fontDown");
                await Idle(window);
                Check(markdown.File.Text == source && !markdown.File.IsDirty, "搜索不修改Markdown原文");
                Action(window, "next");
                Check(markdown.Preview.Selection.Text == "中文", "下一处匹配仍映射到可见文字");
                Action(window, "hideFind");
                window.ToggleReading();
                Check(!markdown.IsReading && markdown.Editor.Text.Contains("**中文**"), "进入编辑才展示原始Markdown代码");
                Check(!CopyData(markdown.Editor, "**中文**").GetDataPresent(DataFormats.Html, false),
                    "源码编辑复制不进入Markdown富文本转换");
                markdown.Editor.AppendText("\n新增 **内容**");
                window.ToggleReading();
                Check(markdown.IsReading && markdown.Reading!.VisibleText.Contains("新增 内容"), "再次预览反映未保存编辑");
                Check(markdown.File.IsDirty, "预览切换不会保存或丢失修改");
                var txt = window.NewDocument("第一处 匹配\n第二处 匹配");
                Action(window, "find");
                query.Text = "匹配";
                await Until(() => ((TextBlock)window.FindName("SearchCount")).Text.Contains(" / "));
                Check(txt.Editor.SelectedText == "匹配" && query.IsKeyboardFocusWithin, "普通文本搜索自动选中且可连续输入");
                query.Text = "第二";
                await Until(() => txt.Editor.SelectedText == "第二");
                Check(query.IsKeyboardFocusWithin, "修改查询后继续自动匹配");
                Action(window, "hideFind");
                txt.Editor.AppendText("!");
                txt.Editor.Undo();
                Check(txt.File.Text == "第一处 匹配\n第二处 匹配", "搜索不污染编辑撤销栈");
                txt.Editor.AppendText("正文修改");
                Action(window, "find");
                query.Text = "查询文字";
                query.IsUndoEnabled = false;
                query.IsUndoEnabled = true;
                string beforeQueryUndo = txt.File.Text;
                bool queryCanUndo = ApplicationCommands.Undo.CanExecute(null, query);
                if (queryCanUndo) ApplicationCommands.Undo.Execute(null, query);
                Check(!queryCanUndo && txt.File.Text == beforeQueryUndo, "搜索框撤销耗尽不会回退撤销正文");
                var selectAll = ((MenuItem)window.FindName("EditMenu")).Items.OfType<MenuItem>()
                    .Single(item => item.Command == ApplicationCommands.SelectAll);
                Check(ReferenceEquals(selectAll.CommandTarget, query), "编辑菜单显式指向当前搜索输入框");
                ApplicationCommands.SelectAll.Execute(null, selectAll.CommandTarget);
                Check(query.SelectedText == query.Text && txt.File.Text == beforeQueryUndo,
                    "搜索中菜单全选只作用于查询");
                Action(window, "hideFind");
                Check(ReferenceEquals(selectAll.CommandTarget, txt.Editor), "关闭查找后菜单恢复正文编辑目标");
                txt.Editor.Undo();
                string txtPath = Path.Combine(root, "外部刷新.txt");
                window.SaveTabToPath(txt, txtPath);
                var id = txt.File.Id;
                File.WriteAllText(txtPath, "外部写入的最新内容");
                window.SelectTab(markdown);
                window.SelectTab(txt);
                await Until(() => txt.File.Text == "外部写入的最新内容");
                Check(txt.File.Id == id && !txt.File.IsDirty, "切换标签实际钩子自动刷新且保留ID和保存基线");
                txt.Editor.AppendText("\n未保存编辑");
                File.WriteAllText(txtPath, "磁盘再次更新");
                window.SelectTab(markdown);
                window.SelectTab(txt);
                await Until(() => ((TextBlock)window.FindName("StatusText")).Text.Contains("已保留当前编辑"));
                Check(txt.File.Text.Contains("未保存编辑") && txt.File.IsDirty, "外部更新保护未保存内容并提示冲突");
                result = 0;
                Console.WriteLine($"Reading checks: {checks} passed");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                window?.CloseForShutdown();
                app.Shutdown();
                Directory.Delete(root, recursive: true);
            }
        };
        app.Run();
        return result;
    }

    private static Task Idle(MainWindow window) =>
        window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle).Task;

    private static DataObject CopyData(UIElement target, string text)
    {
        var data = new DataObject(DataFormats.UnicodeText, text);
        target.RaiseEvent(new DataObjectCopyingEventArgs(data, false));
        return data;
    }

    private static bool HasSelectionDrawing(Visual root)
    {
        if (root.GetType().Name == "CaretElement" &&
            VisualTreeHelper.GetDrawing(root) is { Bounds.Width: > 3, Bounds.Height: > 0 })
            return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (VisualTreeHelper.GetChild(root, i) is Visual child && HasSelectionDrawing(child))
                return true;
        return false;
    }

    private static async Task Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("等待预期状态超时");
            await Task.Delay(25);
        }
    }

    private static void Action(MainWindow window, string action)
    {
        MenuItem? MenuAction(ItemsControl parent)
        {
            foreach (var item in parent.Items.OfType<MenuItem>())
            {
                if (Equals(item.Tag, action)) return item;
                if (MenuAction(item) is { } nested) return nested;
            }
            return null;
        }
        if (MenuAction((Menu)window.FindName("MainMenu")) is { } menu)
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        else ((Button)Find(window, action)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static object? Find(DependencyObject parent, string action)
    {
        foreach (var item in LogicalTreeHelper.GetChildren(parent))
        {
            if (item is Button { Tag: string tag } button && tag == action) return button;
            if (item is DependencyObject child && Find(child, action) is { } found) return found;
        }
        return null;
    }
}
