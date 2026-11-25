using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Qingyue.Core;

namespace Qingyue.Windows;

public sealed class AnnotationHighlightAdorner : Adorner
{
    public const int MaximumRenderedAnnotations = 5000;

    private readonly FrameworkElement element;
    private readonly Func<DocumentAnnotation, IReadOnlyList<Rect>> rectangles;
    private IReadOnlyList<DocumentAnnotation> annotations = [];

    public AnnotationHighlightAdorner(
        FrameworkElement adornedElement,
        Func<DocumentAnnotation, IReadOnlyList<Rect>> rectangles)
        : base(adornedElement)
    {
        element = adornedElement ?? throw new ArgumentNullException(nameof(adornedElement));
        this.rectangles = rectangles ?? throw new ArgumentNullException(nameof(rectangles));
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        adornedElement.SizeChanged += AdornedElementChanged;
        adornedElement.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(AdornedElementScrolled));
    }

    public void Update(IReadOnlyList<DocumentAnnotation> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        annotations = value
            .Where(item => item.AnchorState is AnnotationAnchorState.Exact or AnnotationAnchorState.Shifted)
            .Take(MaximumRenderedAnnotations)
            .ToArray();
        InvalidateVisual();
    }

    public void Detach()
    {
        element.SizeChanged -= AdornedElementChanged;
        element.RemoveHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(AdornedElementScrolled));
    }

    public static bool TryGetReadingRange(
        ReadingModel model,
        DocumentAnnotation annotation,
        out int displayStart,
        out int displayLength)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(annotation);
        displayStart = displayLength = 0;
        if (annotation.AnchorState is not (AnnotationAnchorState.Exact or AnnotationAnchorState.Shifted)
            || string.IsNullOrEmpty(annotation.Quote))
            return false;

        var block = model.DisplayRangeForSource(annotation.SourceStart);
        if (block.Length <= 0) return false;

        string visible = model.VisibleText;
        int blockStart = Math.Clamp(block.Start, 0, visible.Length);
        int blockLength = Math.Clamp(block.Length, 0, visible.Length - blockStart);
        int found = visible.IndexOf(annotation.Quote, blockStart, blockLength, StringComparison.Ordinal);
        if (found >= 0)
        {
            displayStart = found;
            displayLength = annotation.Quote.Length;
            return true;
        }

        // A reading annotation stores the Markdown source quote, so emphasis,
        // link destinations and other hidden syntax can prevent an exact text
        // match. Match macOS by highlighting the whole rendered source block.
        displayStart = blockStart;
        displayLength = blockLength;
        return true;
    }

    internal static IReadOnlyList<Rect> RichTextBoxRects(
        RichTextBox preview,
        ReadingModel model,
        RenderedReading rendered,
        DocumentAnnotation annotation)
    {
        if (!TryGetReadingRange(model, annotation, out int start, out int length)) return [];
        return SearchHighlightAdorner.RichTextBoxRects(
            preview, rendered.PointerAt, start, length);
    }

    internal static IReadOnlyList<Rect> TextBoxRects(
        TextBox editor,
        TextPositionIndex positions,
        DocumentAnnotation annotation)
    {
        if (annotation.AnchorState is not (AnnotationAnchorState.Exact or AnnotationAnchorState.Shifted))
            return [];
        int start = positions.ToEditor(annotation.SourceStart);
        int end = positions.ToEditor(annotation.SourceEnd);
        return SearchHighlightAdorner.TextBoxRects(editor, start, Math.Max(0, end - start));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (annotations.Count == 0) return;

        var clip = new Rect(new Point(), element.RenderSize);
        drawingContext.PushClip(new RectangleGeometry(clip));
        foreach (var annotation in annotations)
        {
            Brush baseBrush = element.TryFindResource(ColorKey(annotation.Color)) as Brush
                ?? element.TryFindResource("AnnotationAmberBrush") as Brush
                ?? Brushes.Goldenrod;
            Color color = baseBrush is SolidColorBrush solid ? solid.Color : Colors.Goldenrod;
            var fill = new SolidColorBrush(Color.FromArgb(76, color.R, color.G, color.B));
            var border = new SolidColorBrush(Color.FromArgb(170, color.R, color.G, color.B));
            if (fill.CanFreeze) fill.Freeze();
            if (border.CanFreeze) border.Freeze();
            var pen = new Pen(border, 1);
            if (pen.CanFreeze) pen.Freeze();
            foreach (var rect in rectangles(annotation))
            {
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
                var visible = rect;
                visible.Intersect(clip);
                if (!visible.IsEmpty)
                    drawingContext.DrawRoundedRectangle(fill, pen, visible, 2, 2);
            }
        }
        drawingContext.Pop();
    }

    private static string ColorKey(AnnotationColor color) => color switch
    {
        AnnotationColor.Blue => "AnnotationBlueBrush",
        AnnotationColor.Green => "AnnotationGreenBrush",
        AnnotationColor.Rose => "AnnotationRoseBrush",
        AnnotationColor.Violet => "AnnotationVioletBrush",
        _ => "AnnotationAmberBrush"
    };

    private void AdornedElementChanged(object sender, SizeChangedEventArgs e) => InvalidateVisual();
    private void AdornedElementScrolled(object sender, ScrollChangedEventArgs e) => InvalidateVisual();
}
