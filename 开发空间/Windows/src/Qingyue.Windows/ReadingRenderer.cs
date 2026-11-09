using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Qingyue.Core;

namespace Qingyue.Windows;

public sealed class RenderedReading
{
    private readonly List<(int Offset, Run Run)> runs = [];
    public FlowDocument Document { get; } = new()
    {
        PagePadding = new Thickness(36, 28, 36, 60), ColumnWidth = double.PositiveInfinity,
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"), FontSize = 16,
        LineHeight = 27, TextAlignment = TextAlignment.Left
    };

    public RenderedReading(ReadingModel model, bool renderMermaid = true)
    {
        Document.SetResourceReference(FlowDocument.ForegroundProperty, "TextBrush");
        var diagramBudget = new MermaidBlock.Budget();
        foreach (var block in model.Blocks)
        {
            var run = new Run(block.Text);
            runs.Add((block.DisplayStart, run));
            var paragraph = new Paragraph(run) { Margin = new Thickness(0, 0, 0, 13) };
            StyleBlock(paragraph, block, Document.FontSize);
            if (renderMermaid && MermaidBlock.IsMermaid(block))
            {
                if (diagramBudget.TakeBlock(block))
                    Document.Blocks.Add(new BlockUIContainer(MermaidBlock.Create(block, Document.FontSize,
                        sourceBelow: true, budget: diagramBudget))
                    {
                        Margin = new Thickness(0, 8, 0, 4)
                    });
                else if (diagramBudget.LimitNotice() is { } notice) Document.Blocks.Add(notice);
            }
            Document.Blocks.Add(paragraph);
        }
        if (runs.Count == 0)
        {
            var run = new Run("");
            runs.Add((0, run));
            Document.Blocks.Add(new Paragraph(run));
        }
    }

    // Style only: the single Run and its text must stay intact for PointerAt/OffsetAt.
    internal static void StyleBlock(Paragraph paragraph, ReadingBlock block, double fontSize)
    {
        switch (block.Kind)
        {
            case ReadingBlockKind.Heading:
                int level = Math.Clamp(block.Level, 1, 6);
                paragraph.FontSize = fontSize * (level switch { 1 => 2, 2 => 1.6, 3 => 1.35, 4 => 1.2, 5 => 1.1, _ => 1 });
                paragraph.FontWeight = level == 1 ? FontWeights.Bold : level == 2 ? FontWeights.SemiBold : FontWeights.Medium;
                paragraph.SetResourceReference(Paragraph.ForegroundProperty,
                    level == 1 ? "AccentTextBrush" : level == 2 ? "SyntaxKeyBrush" : "TextBrush");
                paragraph.Margin = new Thickness(0, fontSize * (level switch { 1 => 1.75, 2 => 1.5, _ => 1.25 }), 0, fontSize * .75);
                paragraph.LineHeight = paragraph.FontSize * 1.4;
                paragraph.KeepWithNext = true;
                break;
            case ReadingBlockKind.Code:
                paragraph.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
                paragraph.FontSize = fontSize * .9;
                paragraph.LineHeight = fontSize * 1.6;
                paragraph.Padding = new Thickness(16, 12, 16, 12);
                paragraph.Margin = new Thickness(0, 8, 0, 18);
                paragraph.BorderThickness = new Thickness(1);
                paragraph.SetResourceReference(Paragraph.BorderBrushProperty, "BorderBrush");
                paragraph.SetResourceReference(Paragraph.BackgroundProperty, "PanelBrush");
                break;
            case ReadingBlockKind.Quote:
                paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                paragraph.SetResourceReference(Paragraph.BorderBrushProperty, "AccentTextBrush");
                paragraph.SetResourceReference(Paragraph.ForegroundProperty, "MutedBrush");
                paragraph.SetResourceReference(Paragraph.BackgroundProperty, "PanelBrush");
                paragraph.Padding = new Thickness(16, 8, 12, 8);
                paragraph.Margin = new Thickness(0, 8, 0, 16);
                break;
            case ReadingBlockKind.ListItem:
                paragraph.Margin = new Thickness(18 + block.Level * 12, 0, 0, 8);
                break;
            case ReadingBlockKind.Table:
                paragraph.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
                paragraph.FontSize = fontSize * .9;
                paragraph.SetResourceReference(Paragraph.BackgroundProperty, "PanelBrush");
                paragraph.SetResourceReference(Paragraph.BorderBrushProperty, "BorderBrush");
                paragraph.BorderThickness = new Thickness(1);
                paragraph.Padding = new Thickness(12);
                paragraph.Margin = new Thickness(0, 8, 0, 18);
                break;
            case ReadingBlockKind.Separator:
                paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
                paragraph.SetResourceReference(Paragraph.BorderBrushProperty, "BorderBrush");
                paragraph.SetResourceReference(Paragraph.ForegroundProperty, "MutedBrush");
                paragraph.Margin = new Thickness(0, 16, 0, 20);
                break;
        }
    }

    public TextPointer PointerAt(int offset)
    {
        var entry = runs[0];
        foreach (var candidate in runs)
        {
            if (candidate.Offset > offset) break;
            entry = candidate;
        }
        return entry.Run.ContentStart.GetPositionAtOffset(Math.Clamp(offset - entry.Offset, 0, entry.Run.Text.Length))
            ?? entry.Run.ContentEnd;
    }

    public int OffsetAt(TextPointer pointer)
    {
        foreach (var entry in runs)
        {
            if (pointer.CompareTo(entry.Run.ContentStart) < 0) return entry.Offset;
            if (pointer.CompareTo(entry.Run.ContentEnd) <= 0)
                return entry.Offset + new TextRange(entry.Run.ContentStart, pointer).Text.Length;
        }
        var last = runs[^1];
        return last.Offset + last.Run.Text.Length;
    }
}
