using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Qingyue.Core;

namespace Qingyue.Windows;

internal sealed class SearchHighlightAdorner : Adorner
{
    public const int MaximumRenderedMatches = 5000;
    private readonly FrameworkElement element;
    private readonly Func<SearchMatch, IReadOnlyList<Rect>> rectangles;
    private IReadOnlyList<SearchMatch> matches = [];

    public SearchHighlightAdorner(FrameworkElement adornedElement,
        Func<SearchMatch, IReadOnlyList<Rect>> rectangles)
        : base(adornedElement)
    {
        element = adornedElement;
        this.rectangles = rectangles ?? throw new ArgumentNullException(nameof(rectangles));
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        adornedElement.SizeChanged += AdornedElementSizeChanged;
        adornedElement.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(AdornedElementScrollChanged));
    }

    public void Update(IReadOnlyList<SearchMatch> value)
    {
        matches = value.Count <= MaximumRenderedMatches
            ? value
            : value.Take(MaximumRenderedMatches).ToArray();
        InvalidateVisual();
    }

    public void Detach()
    {
        element.SizeChanged -= AdornedElementSizeChanged;
        element.RemoveHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(AdornedElementScrollChanged));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (matches.Count == 0) return;

        var fill = element.TryFindResource("SearchHighlightBrush") as Brush
            ?? new SolidColorBrush(Color.FromArgb(110, 246, 194, 69));
        var border = element.TryFindResource("SearchHighlightBorderBrush") as Brush ?? fill;
        var pen = new Pen(border, 1);
        if (pen.CanFreeze) pen.Freeze();
        var clip = new Rect(new Point(), element.RenderSize);
        drawingContext.PushClip(new RectangleGeometry(clip));
        foreach (var match in matches)
            foreach (var rect in rectangles(match))
            {
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
                var visible = rect;
                visible.Intersect(clip);
                if (visible.IsEmpty) continue;
                drawingContext.DrawRoundedRectangle(fill, pen, visible, 2, 2);
            }
        drawingContext.Pop();
    }

    internal static IReadOnlyList<Rect> TextBoxRects(TextBox editor, int start, int length)
    {
        int end = Math.Clamp(start + Math.Max(0, length), start, editor.Text.Length);
        var result = new List<Rect>();
        Rect current = Rect.Empty;
        for (int offset = Math.Clamp(start, 0, editor.Text.Length); offset < end; offset++)
        {
            var rect = editor.GetRectFromCharacterIndex(offset, false);
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
            if (!current.IsEmpty && Math.Abs(current.Top - rect.Top) < 1 &&
                Math.Abs(current.Bottom - rect.Bottom) < 1)
            {
                current.Union(rect);
            }
            else
            {
                if (!current.IsEmpty) result.Add(current);
                current = rect;
            }
        }
        if (!current.IsEmpty) result.Add(current);
        return result;
    }

    internal static IReadOnlyList<Rect> RichTextBoxRects(RichTextBox preview,
        Func<int, TextPointer> pointerAt, int start, int length)
    {
        int end = Math.Max(start, start + Math.Max(0, length));
        var result = new List<Rect>();
        Rect current = Rect.Empty;
        for (int offset = Math.Max(0, start); offset < end; offset++)
        {
            Rect rect;
            try { rect = pointerAt(offset).GetCharacterRect(LogicalDirection.Forward); }
            catch (InvalidOperationException) { continue; }
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
            if (!current.IsEmpty && Math.Abs(current.Top - rect.Top) < 1 &&
                Math.Abs(current.Bottom - rect.Bottom) < 1)
            {
                current.Union(rect);
            }
            else
            {
                if (!current.IsEmpty) result.Add(current);
                current = rect;
            }
        }
        if (!current.IsEmpty) result.Add(current);
        return result;
    }

    private void AdornedElementSizeChanged(object sender, SizeChangedEventArgs e) => InvalidateVisual();
    private void AdornedElementScrollChanged(object sender, ScrollChangedEventArgs e) => InvalidateVisual();
}
