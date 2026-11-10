using System.Windows.Documents;
using System.Windows.Threading;
using Qingyue.Core;

namespace Qingyue.Windows;

/// <summary>Maps decorated Markdown Runs without splitting them or altering document text.</summary>
internal sealed class RichDocumentMap
{
    private sealed record Segment(int Start, int Length, TextPointer Pointer);
    private readonly List<Segment> segments = [];
    private readonly FlowDocument document;
    private readonly int length;

    public RichDocumentMap(FlowDocument document, ReadingModel model)
        : this(document, model.VisibleText.Length)
    {
        foreach (var _ in Initialize(model, CancellationToken.None)) { }
    }

    private RichDocumentMap(FlowDocument document, int length)
    {
        this.document = document;
        this.length = length;
    }

    internal static async Task<RichDocumentMap> CreateAsync(FlowDocument document, ReadingModel model,
        CancellationToken token)
    {
        document.VerifyAccess();
        var map = new RichDocumentMap(document, model.VisibleText.Length);
        foreach (var _ in map.Initialize(model, token))
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            token.ThrowIfCancellationRequested();
        }
        return map;
    }

    private IEnumerable<int> Initialize(ReadingModel model, CancellationToken token)
    {
        // ReadingBlock is a record containing the entire block Text. Its value hash
        // rescans that text for every Run; cursors belong to the tagged instance.
        var cursors = new Dictionary<ReadingBlock, int>(ReferenceEqualityComparer.Instance);
        int fallbackCursor = 0;
        int batch = 0;
        var elements = Elements(document.Blocks).ToArray();
        foreach (var paragraph in elements.Select(e => e.Element.ContentStart.Paragraph).OfType<Paragraph>().Distinct().ToArray())
        {
            token.ThrowIfCancellationRequested();
            // WPF TextRange.Text walks backwards over opening elements and includes
            // the list marker at the first Run. An empty inline edge stops that walk
            // without adding characters or changing the visible marker. Prepare all
            // edges before mapping, outside the document's live tree enumerators.
            if (paragraph.Parent is ListItem listItem && listItem.Blocks.FirstBlock == paragraph &&
                paragraph.Inlines.FirstInline is { } first && first is not Span { Inlines.Count: 0 })
                paragraph.Inlines.InsertBefore(first, new Span());
            if (++batch % 128 == 0) yield return batch;
        }
        foreach (var (element, block) in elements)
        {
            token.ThrowIfCancellationRequested();
            if (++batch % 128 == 0) yield return batch;
            if (element is not Run run)
            {
                if (block is not null) segments.Add(new(block.DisplayStart, 0, element.ContentStart));
                continue;
            }
            if (run.Text.Length == 0) continue;
            string text = block?.Text ?? model.VisibleText;
            int cursor = block is null ? fallbackCursor : cursors.GetValueOrDefault(block);
            int index = text.IndexOf(run.Text, cursor, StringComparison.Ordinal);
            if (index < 0) continue;
            segments.Add(new((block?.DisplayStart ?? 0) + index, run.Text.Length, run.ContentStart));
            if (block is null) fallbackCursor = index + run.Text.Length;
            else cursors[block] = index + run.Text.Length;
        }
    }

    public TextPointer PointerAt(int offset)
    {
        offset = Math.Clamp(offset, 0, length);
        Segment? previous = null;
        foreach (var segment in segments)
        {
            if (segment.Start > offset)
                return previous is null ? segment.Pointer : End(previous);
            // Prefer the next Run at a shared boundary. An inline image can sit
            // between Runs without occupying a character in model.VisibleText.
            // Returning the previous Run's end would include that UI's space
            // when selecting the following image caption.
            if (offset < segment.Start + segment.Length && segment.Length > 0)
                return segment.Pointer.GetPositionAtOffset(offset - segment.Start) ?? End(segment);
            previous = segment;
        }
        return previous is null ? document.ContentStart : End(previous);
    }

    public int OffsetAt(TextPointer pointer)
    {
        int previous = 0;
        foreach (var segment in segments)
        {
            if (pointer.CompareTo(segment.Pointer) < 0) return Math.Clamp(previous, 0, length);
            var end = End(segment);
            if (pointer.CompareTo(end) <= 0)
                return segment.Start + Math.Clamp(segment.Pointer.GetOffsetToPosition(pointer), 0, segment.Length);
            previous = segment.Start + segment.Length;
        }
        return Math.Clamp(previous, 0, length);
    }

    private static TextPointer End(Segment segment) =>
        segment.Pointer.GetPositionAtOffset(segment.Length) ?? segment.Pointer;

    private static IEnumerable<(TextElement Element, ReadingBlock? Block)> Elements(BlockCollection blocks, ReadingBlock? inherited = null)
    {
        foreach (Block block in blocks)
        {
            var source = block.Tag as ReadingBlock ?? inherited;
            switch (block)
            {
                case Paragraph paragraph:
                    foreach (var run in Inlines(paragraph.Inlines)) yield return (run, source);
                    break;
                case System.Windows.Documents.List list:
                    foreach (var item in list.ListItems)
                        foreach (var pair in Elements(item.Blocks, source)) yield return pair;
                    break;
                case Table table:
                    foreach (var group in table.RowGroups)
                        foreach (var row in group.Rows)
                            foreach (var cell in row.Cells)
                                foreach (var pair in Elements(cell.Blocks, source)) yield return pair;
                    break;
                case Section section:
                    foreach (var pair in Elements(section.Blocks, source)) yield return pair;
                    break;
                case BlockUIContainer:
                    yield return (block, source);
                    break;
            }
        }
    }

    private static IEnumerable<Run> Inlines(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            if (inline is Run run) yield return run;
            else if (inline is Span span)
                foreach (var child in Inlines(span.Inlines)) yield return child;
        }
    }
}
