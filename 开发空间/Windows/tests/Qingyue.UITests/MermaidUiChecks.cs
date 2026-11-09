using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Qingyue.Core;
using Qingyue.Windows;

internal static class MermaidUiChecks
{
    public static async Task Run(MainWindow window, Action<bool, string> check)
    {
        const string source = """
            # 离线时序图

            ```mermaid
            sequenceDiagram
                participant W as 管理台
                participant B as 业务后端
                participant F as 前端
                participant U as 接入服务
                participant C as 流程服务
                participant M as 消息队列
                W->>C: POST /api/drafts/save 保存草稿
                B->>C: POST /api/instances/start 发起流程
                C-->>B: 返回实例编号
                F->>U: POST /api/tasks/handle 提交审批
                U->>C: POST /api/tasks/approve 审批
                C-->>M: 异步通知
                M-->>B: 更新业务状态
            ```

            ## 原文仍可编辑
            """;
        var previous = window.CurrentTab!;
        var tab = window.NewDocument();
        tab.Editor.Text = source;
        await Idle(window);
        check(tab.Kind == TextKind.Markdown, "构建3：新文件粘贴Markdown自动识别");
        var button = (Button)window.FindName("MarkdownButton");
        check(button.IsVisible, "构建3：粘贴后MD预览入口可见");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var host = (ContentControl)window.FindName("DocumentHost");
        var split = (Grid)host.Content;
        var preview = (MarkdownPreview)((ContentControl)((Grid)split.Children[2]).Children[1]).Content;
        MermaidSequenceView? Graph() => preview.Document.Blocks.OfType<BlockUIContainer>()
            .SelectMany(block => Descendants(block.Child)).OfType<MermaidSequenceView>().SingleOrDefault();
        check(Graph() is not null, "构建3：实际MD分屏显示时序图");
        tab.Editor.Text = source.Replace("更新业务状态", "回写业务状态");
        await Task.Delay(550);
        await Idle(window);
        check(Graph() is { } graph && Descendants(graph).OfType<TextBlock>().Any(t => t.Text == "回写业务状态"),
            "构建3：编辑消息后防抖刷新实际图形");
        window.Width = 1420;
        window.Height = 940;
        window.ApplyTheme(true);
        await Idle(window);
        var artifacts = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
        System.IO.Directory.CreateDirectory(artifacts);
        Program.SaveScreenshot(window, System.IO.Path.Combine(artifacts, "windows-build3-mermaid-dark.png"));
        window.ApplyTheme(false);
        await Idle(window);
        Program.SaveScreenshot(window, System.IO.Path.Combine(artifacts, "windows-build3-mermaid-light.png"));
        check(Graph() is { } light && Descendants(light).OfType<TextBlock>().All(t =>
            t.Foreground is SolidColorBrush { Color.A: > 0 }), "构建3：明暗主题切换保留图形与可见文字");
        var scroll = Descendants(Graph()!).OfType<ScrollViewer>().Single();
        check(scroll.ViewportWidth > 0 && scroll.ScrollableWidth > 500,
            "构建3：分屏实际文档内宽图有可用横向滚动范围");
        scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth);
        preview.ScrollToEnd();
        await Idle(window);
        check(scroll.HorizontalOffset > 500 &&
            new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text.Contains("原文仍可编辑"),
            "构建3：可滚动到末列并继续阅读图后正文");
        Program.SaveScreenshot(window, System.IO.Path.Combine(artifacts, "windows-build3-mermaid-scroll-end.png"));
        window.ApplyTheme(true);

        window.ToggleReading();
        await Idle(window);
        check(tab.IsReading && tab.Rendered!.Document.Blocks.OfType<BlockUIContainer>().Any(),
            "构建3：完整阅读包含时序图");
        void Click(string tag) => Program.ClickAction(window, tag);
        Click("find");
        ((TextBox)window.FindName("SearchQuery")).Text = "/api/tasks/approve";
        await Task.Delay(500);
        await Idle(window);
        check(((TextBlock)window.FindName("SearchCount")).Text == "1 处匹配",
            "构建3：时序图阅读仍能查找原始消息");
        Click("next");
        check(tab.Preview.Selection.Text == "/api/tasks/approve", "构建3：图形插入不破坏搜索位置");
        Click("hideFind");
        window.ToggleReading();
        check(tab.File.Text == source.Replace("更新业务状态", "回写业务状态") &&
            tab.Editor.Text == tab.File.Text, "构建3：返回编辑保留完整围栏与消息");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.SelectTab(previous);
    }

    private static async Task Idle(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
