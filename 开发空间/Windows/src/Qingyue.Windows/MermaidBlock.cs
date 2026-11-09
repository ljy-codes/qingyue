using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Qingyue.Core;

namespace Qingyue.Windows;

internal static class MermaidBlock
{
    internal static bool IsMermaid(ReadingBlock block) =>
        block.Kind == ReadingBlockKind.Code &&
        string.Equals(block.CodeLanguage, "mermaid", StringComparison.OrdinalIgnoreCase);

    internal static FrameworkElement Create(ReadingBlock block, double fontSize, bool sourceBelow = false,
        Budget? budget = null)
    {
        bool parsed = MermaidSequence.TryParse(block.Text, out var diagram, out var error);
        if (parsed && budget is not null && !budget.TakeEvents(diagram!.Events.Count))
        {
            parsed = false;
            error = "本篇图表事件总量超过 400，保留源码以保证阅读响应。";
        }
        var content = new StackPanel();
        var title = new TextBlock
        {
            Text = parsed ? "MERMAID  /  时序图" : "MERMAID  /  源码",
            FontSize = Math.Max(11, fontSize * .75), FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        content.Children.Add(title);
        if (parsed)
        {
            var hint = new TextBlock
            {
                Text = "宽图可使用底部横向滚动条查看全部角色。",
                FontSize = Math.Max(11, fontSize * .75), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            content.Children.Add(hint);
            content.Children.Add(new MermaidSequenceView(diagram!, Math.Clamp(fontSize * .85, 11, 22)));
        }
        else
        {
            var message = new TextBlock
            {
                Text = "图表暂未显示：" + error + (sourceBelow ? " 原始代码保留在下方。" : " 可在下方查看原始代码。"),
                TextWrapping = TextWrapping.Wrap, FontSize = Math.Max(12, fontSize * .85),
                Margin = new Thickness(0, 0, 0, 8)
            };
            message.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            content.Children.Add(message);
        }

        // The mapped reader keeps its original Run below the graph, so search and
        // source/display pointers still refer to real, selectable source characters.
        if (!sourceBelow)
        {
            var source = new TextBox
            {
                Text = block.Text, IsReadOnly = true, IsUndoEnabled = false,
                AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
                FontSize = Math.Max(11, fontSize * .8), BorderThickness = new Thickness(0),
                Padding = new Thickness(10), MaxHeight = 280,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            source.SetResourceReference(Control.BackgroundProperty, "EditorBrush");
            source.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            AutomationProperties.SetName(source, "Mermaid 只读源码");
            var expander = new Expander
            {
                Header = "查看 / 复制 Mermaid 源码", Content = source, IsExpanded = !parsed,
                Margin = new Thickness(0, 12, 0, 0), FontSize = Math.Max(12, fontSize * .8)
            };
            expander.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
            content.Children.Add(expander);
        }
        var panel = new Border
        {
            Child = content, Padding = new Thickness(16), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        panel.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        AutomationProperties.SetName(panel, parsed ? "Mermaid 时序图" : "Mermaid 图表提示");
        return panel;
    }

    // Bound all interactive controls as well as total vector geometry. A fresh budget
    // belongs to each document build, so fixing/removing code always restores capacity.
    internal sealed class Budget
    {
        private int blocks, characters, events;
        private bool notified;

        internal bool TakeBlock(ReadingBlock block) =>
            ++blocks <= 12 && (characters += block.Text.Length) <= 64_000;

        internal bool TakeEvents(int count) => (events += count) <= 400;

        internal Paragraph? LimitNotice()
        {
            if (notified) return null;
            notified = true;
            var notice = new Paragraph(new Run(
                "本篇图表预览已达到 12 个代码块或 64,000 字符的上限，后续图表保留源码。"))
            {
                FontSize = 12, Margin = new Thickness(0, 8, 0, 12)
            };
            notice.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
            return notice;
        }
    }
}
