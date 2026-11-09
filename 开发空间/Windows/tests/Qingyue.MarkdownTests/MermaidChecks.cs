using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Qingyue.Core;
using Qingyue.Windows;

internal static class MermaidChecks
{
    internal const string Sequence = """
        sequenceDiagram
            participant W as 管理台
            participant B as 业务后端
            participant F as 普通前端
            participant U as 接入服务
            participant C as 流程服务
            participant M as 消息队列
            W->>C: POST /api/drafts/save 保存草稿
            W->>C: POST /api/drafts/publish 发布
            B->>C: POST /api/instances/preview 发起前预览
            B->>C: POST /api/instances/start 发起
            C-->>B: instanceId, instanceCode
            F->>U: POST /api/instances/list 查询列表
            U->>C: POST /api/instances/query 查询
            F->>U: POST /api/tasks/handle 提交同意或拒绝
            U->>C: POST /api/tasks/approve 审批
            C-->>M: node / end 消息（异步）
            M-->>B: 更新业务单据状态
        """;

    internal static readonly string Source =
        "# 流程说明\n\n| 能力 | 说明 |\n| --- | --- |\n| 预览 | 离线图形 |\n\n" +
        "```mermaid\n" + Sequence + "\n```\n\n## 下一节\n\n原文仍可编辑。\n";

    internal static void Preview()
    {
        if (TextServices.DetectKind(null, Source) != TextKind.Markdown)
            throw new Exception("粘贴到未命名文档的 Markdown 应自动识别");
        var preview = new MarkdownPreview(Source, 16, 680);
        if (preview.Document.Blocks.OfType<BlockUIContainer>().Count() != 1)
            throw new Exception("mermaid 围栏应显示一个真实图表控件，当前仍只显示源码");
        if (!preview.Document.Blocks.OfType<Table>().Any())
            throw new Exception("图表与 Markdown 表格应能共存");
        if (new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text.Contains("```"))
            throw new Exception("预览不应把 Markdown 围栏作为普通正文显示");
    }

    internal static void FencesAndFallback()
    {
        var model = ReadingModel.Build(Source, TextKind.Markdown);
        var block = model.Blocks.Single(b => b.Kind == ReadingBlockKind.Code);
        if (block.CodeLanguage != "mermaid" || block.Text != Sequence.ReplaceLineEndings("\n"))
            throw new Exception("围栏语言必须保留，图形不能更改原始代码");
        foreach (string fence in new[] { "```txt", "```", "~~~cs" })
        {
            string close = fence.StartsWith('~') ? "~~~" : "```";
            var plain = new MarkdownPreview(fence + "\n" + Sequence + "\n" + close, 16, 680);
            if (plain.Document.Blocks.OfType<BlockUIContainer>().Any())
                throw new Exception("普通代码块不能被误识别为 Mermaid");
        }
        string invalid = "sequenceDiagram\nparticipant A\nalt 尚未支持\nA->>B: 原文😀";
        var preview = new MarkdownPreview("~~~mermaid\n" + invalid + "\n~~~", 16, 680);
        var panel = preview.Document.Blocks.OfType<BlockUIContainer>().Single().Child;
        if (Descendants(panel).OfType<MermaidSequenceView>().Any())
            throw new Exception("不支持的指令不得静默丢失后继续绘图");
        var source = Descendants(panel).OfType<TextBox>().Single();
        if (source.Text != invalid || !source.IsReadOnly)
            throw new Exception("失败时应完整保留可复制的只读源码");
        if (!Descendants(panel).OfType<Expander>().Single().IsExpanded ||
            !Descendants(panel).OfType<TextBlock>().Any(t => t.Text.Contains("图表暂未显示")))
            throw new Exception("图表失败原因和源码应直接可见");
        preview.Update(Source);
        if (preview.Document.Blocks.OfType<BlockUIContainer>().Count() != 1 ||
            !Descendants(preview.Document.Blocks.OfType<BlockUIContainer>().Single().Child).OfType<MermaidSequenceView>().Any())
            throw new Exception("修正代码后应恢复图表，不能一直复用失败预览");
        preview.Update("# 新正文\n\n图表已删除。");
        if (preview.Document.Blocks.OfType<BlockUIContainer>().Any() || preview.IsDocumentEnabled)
            throw new Exception("删除图表后不能遗留图形或交互状态");
    }

    internal static void ReadingMapping()
    {
        var model = ReadingModel.Build(Source, TextKind.Markdown);
        var rendered = new RenderedReading(model);
        if (rendered.Document.Blocks.OfType<BlockUIContainer>().Count() != 1)
            throw new Exception("完整阅读模式也必须显示图形");
        var code = model.Blocks.Single(b => b.Kind == ReadingBlockKind.Code);
        var source = rendered.Document.Blocks.OfType<Paragraph>().Single(p =>
            p.Inlines.FirstInline is Run run && run.Text.StartsWith("sequenceDiagram"));
        if (new TextRange(source.ContentStart, source.ContentEnd).Text != code.Text)
            throw new Exception("阅读模式保留可搜索选择的原始代码");
        for (int offset = 0; offset <= model.VisibleText.Length; offset++)
        {
            if (offset > 0 && offset < model.VisibleText.Length &&
                char.IsHighSurrogate(model.VisibleText[offset - 1]) && char.IsLowSurrogate(model.VisibleText[offset]))
                continue;
            if (rendered.OffsetAt(rendered.PointerAt(offset)) != offset)
                throw new Exception($"插入图表后查找定位发生偏移：{offset}");
        }
    }

    internal static void Layout()
    {
        var preview = new MarkdownPreview(Source, 16, 1000);
        preview.Resources["TextBrush"] = Brushes.DarkSlateGray;
        preview.Resources["EditorBrush"] = Brushes.White;
        preview.Resources["PanelBrush"] = Brushes.AliceBlue;
        preview.Resources["BorderBrush"] = Brushes.LightSteelBlue;
        preview.Resources["MutedBrush"] = Brushes.SlateGray;
        preview.Resources["AccentTextBrush"] = Brushes.RoyalBlue;
        preview.Resources["SyntaxKeyBrush"] = Brushes.Teal;
        preview.Measure(new Size(1100, 1200));
        preview.Arrange(new Rect(0, 0, 1100, 1200));
        preview.UpdateLayout();
        var view = Descendants(preview.Document.Blocks.OfType<BlockUIContainer>().Single().Child)
            .OfType<MermaidSequenceView>().Single();
        if (!double.IsFinite(view.DiagramWidth) || !double.IsFinite(view.DiagramHeight) ||
            view.DiagramWidth < 500 || view.DiagramHeight < 300)
            throw new Exception("多参与者时序图必须具有真实且有限的绘制范围");
        if (!Descendants(view).OfType<TextBlock>().Any(t => t.Text.Contains("管理台")) ||
            !Descendants(view).OfType<TextBlock>().Any(t => t.Text.Contains("保存草稿")) ||
            !Descendants(view).OfType<System.Windows.Shapes.Shape>().Any())
            throw new Exception("图形必须实际显示中文参与者、消息文字和向量箭头");
        var scroll = Descendants(view).OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null || scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled)
            throw new Exception("较宽图形必须能横向滚动，不能截断");
    }

    internal static void DocumentBudget()
    {
        string source = string.Concat(Enumerable.Range(1, 40).Select(index =>
            $"```mermaid\nsequenceDiagram\nA->>B: 消息{index}\n```\n\n"));
        var preview = new MarkdownPreview(source, 16, 680);
        if (preview.Document.Blocks.OfType<BlockUIContainer>().Count() != 12)
            throw new Exception("大量图表应限制为每篇最多12个交互预览，避免阻塞界面");
        string visible = new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text;
        if (!visible.Contains("消息40") || !visible.Contains("本篇图表预览已达到"))
            throw new Exception("超出预算的图表必须保留源码并明确说明");
        var reading = new RenderedReading(ReadingModel.Build(source, TextKind.Markdown));
        if (reading.Document.Blocks.OfType<BlockUIContainer>().Count() != 12 ||
            !new TextRange(reading.Document.ContentStart, reading.Document.ContentEnd).Text.Contains("消息40"))
            throw new Exception("阅读模式也必须限制图形总量并保留完整源码");
        string manyEvents = string.Concat(Enumerable.Repeat("```mermaid\nsequenceDiagram\n" +
            string.Concat(Enumerable.Range(1, 150).Select(index => $"A->>B: 消息{index}\n")) + "```\n\n", 3));
        preview.Update(manyEvents);
        var controls = preview.Document.Blocks.OfType<BlockUIContainer>().ToArray();
        if (controls.SelectMany(block => Descendants(block.Child)).OfType<MermaidSequenceView>().Count() != 2 ||
            !controls.SelectMany(block => Descendants(block.Child)).OfType<TextBlock>()
                .Any(text => text.Text.Contains("事件总量超过 400")))
            throw new Exception("多图事件预算超过400时必须明确回退，不可继续创建几何控件");
        string largeLabels = "```mermaid\nsequenceDiagram\n" +
            string.Concat(Enumerable.Repeat("A->>B: " + new string('文', 1_700) + "\n", 15)) + "```\n\n";
        preview.Update(string.Concat(Enumerable.Repeat(largeLabels, 3)));
        if (preview.Document.Blocks.OfType<BlockUIContainer>().Count() != 2 ||
            !new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text.Contains("64,000"))
            throw new Exception("多图源码预算超过64000时必须保留文字并提示");
        preview.Update("```mermaid\nsequenceDiagram\nA->>B: 恢复图形\n```");
        if (preview.Document.Blocks.OfType<BlockUIContainer>().Count() != 1)
            throw new Exception("预览更新必须重置图表预算");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
