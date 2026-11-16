using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Qingyue.Core;

namespace Qingyue.Windows;

/// <summary>Offline WPF rendering for the supported Mermaid flowchart subset.</summary>
public sealed class MermaidFlowchartView : UserControl
{
    private const double Gutter = 28;
    private const int MaximumSnapshotWidth = 4096;
    private const int MaximumSnapshotHeight = 8192;
    private const long MaximumSnapshotPixels = 16_777_216;
    private static readonly IReadOnlyDictionary<string, DependencyProperty> ThemeProperties =
        new[] { "TextBrush", "MutedBrush", "AccentTextBrush", "SyntaxKeyBrush", "PanelBrush", "EditorBrush", "BorderBrush" }
        .ToDictionary(key => key, key => DependencyProperty.RegisterAttached("Resolved" + key,
            typeof(Brush), typeof(MermaidFlowchartView), new PropertyMetadata(null)));

    private readonly Canvas canvas = new();
    private readonly Dictionary<string, Rect> nodeBounds = new(StringComparer.Ordinal);
    private readonly double nodeWidth;
    private readonly double nodeHeight;
    private readonly double gap;
    private readonly MermaidFlowchartDirection direction;
    private double leftEdge;
    private double rightEdge;
    private double topEdge;
    private double bottomEdge;

    public double DiagramWidth { get; private set; }
    public double DiagramHeight { get; private set; }

    public BitmapSource? CreateClipboardSnapshot()
    {
        Dispatcher.VerifyAccess();
        if (!double.IsFinite(DiagramWidth) || !double.IsFinite(DiagramHeight) ||
            DiagramWidth <= 0 || DiagramHeight <= 0)
            return null;
        int width = (int)Math.Ceiling(DiagramWidth);
        int height = (int)Math.Ceiling(DiagramHeight);
        if (width > MaximumSnapshotWidth || height > MaximumSnapshotHeight ||
            (long)width * height > MaximumSnapshotPixels)
            return null;
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        bitmap.Freeze();
        return bitmap;
    }

    public MermaidFlowchartView(MermaidFlowchart diagram, double fontSize = 14)
    {
        ArgumentNullException.ThrowIfNull(diagram);
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (diagram.Nodes.Count == 0 || diagram.Nodes.Count > 100)
            throw new ArgumentException("流程图节点数量必须在 1 到 100 之间。", nameof(diagram));

        FontSize = fontSize;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        Padding = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        direction = diagram.Direction;
        nodeWidth = Math.Max(148, fontSize * 10 + 32);
        nodeHeight = Math.Max(64, fontSize * 2.8 + 24);
        gap = Math.Max(54, fontSize * 3.4);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in diagram.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id))
                throw new ArgumentException("流程图节点标识必须非空且唯一。", nameof(diagram));
        }

        Theme(this, (ForegroundProperty, "TextBrush"), (BackgroundProperty, "EditorBrush"));
        Theme(canvas, (Panel.BackgroundProperty, "EditorBrush"));
        AutomationProperties.SetName(this, $"Mermaid 流程图，{diagram.Nodes.Count} 个节点，{diagram.Edges.Count} 条连线");
        Draw(diagram);
        canvas.Width = DiagramWidth;
        canvas.Height = DiagramHeight;

        var scroll = new ScrollViewer
        {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            CanContentScroll = false,
            Padding = new Thickness(0),
            Height = Math.Min(DiagramHeight + SystemParameters.HorizontalScrollBarHeight + 2, 1200)
        };
        AutomationProperties.SetName(scroll, "Mermaid 流程图滚动区域");
        AutomationProperties.SetHelpText(scroll, "使用水平或垂直滚动条查看完整流程图。");
        Content = Theme(new Border
        {
            Child = scroll,
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        }, (Border.BackgroundProperty, "EditorBrush"), (Border.BorderBrushProperty, "BorderBrush"));
    }

    protected override Size MeasureOverride(Size constraint)
    {
        double width = double.IsFinite(constraint.Width)
            ? constraint.Width : Math.Min(960, DiagramWidth + 18 + Padding.Left + Padding.Right);
        return base.MeasureOverride(new Size(width, double.PositiveInfinity));
    }

    private void Draw(MermaidFlowchart diagram)
    {
        var nodes = diagram.Nodes.ToArray();
        bool horizontal = direction is MermaidFlowchartDirection.LeftRight or MermaidFlowchartDirection.RightLeft;
        double contentWidth = horizontal
            ? Gutter * 2 + nodes.Length * nodeWidth + Math.Max(0, nodes.Length - 1) * gap
            : Gutter * 2 + nodeWidth + 180;
        double contentHeight = horizontal
            ? Gutter * 2 + nodeHeight + 150
            : Gutter * 2 + nodes.Length * nodeHeight + Math.Max(0, nodes.Length - 1) * gap;
        double startX = horizontal ? Gutter : 120;
        double startY = horizontal ? 76 : Gutter;

        for (int i = 0; i < nodes.Length; i++)
        {
            int position = horizontal
                ? direction == MermaidFlowchartDirection.RightLeft ? nodes.Length - 1 - i : i
                : direction == MermaidFlowchartDirection.BottomUp ? nodes.Length - 1 - i : i;
            double x = horizontal ? startX + position * (nodeWidth + gap) : startX;
            double y = horizontal ? startY : startY + position * (nodeHeight + gap);
            nodeBounds[nodes[i].Id] = new Rect(x, y, nodeWidth, nodeHeight);
        }

        foreach (var edge in diagram.Edges)
        {
            if (!nodeBounds.TryGetValue(edge.From, out var from) ||
                !nodeBounds.TryGetValue(edge.To, out var to))
                continue;
            DrawEdge(edge, Connect(from, to), horizontal);
        }
        foreach (var node in nodes)
        {
            Rect bounds = nodeBounds[node.Id];
            var element = Node(node, bounds);
            Add(element, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        leftEdge = Math.Min(leftEdge, 0);
        topEdge = Math.Min(topEdge, 0);
        DiagramWidth = Math.Ceiling(Math.Max(contentWidth, rightEdge - leftEdge + Gutter));
        DiagramHeight = Math.Ceiling(Math.Max(contentHeight, bottomEdge - topEdge + Gutter));
        double shiftX = Gutter - leftEdge;
        double shiftY = Gutter - topEdge;
        foreach (FrameworkElement child in canvas.Children)
        {
            Canvas.SetLeft(child, Canvas.GetLeft(child) + shiftX);
            Canvas.SetTop(child, Canvas.GetTop(child) + shiftY);
        }
    }

    private void DrawEdge(MermaidFlowchartEdge edge, (Point Start, Point End) points, bool horizontal)
    {
        double x = Math.Min(points.Start.X, points.End.X);
        double y = Math.Min(points.Start.Y, points.End.Y);
        double width = Math.Max(1, Math.Abs(points.End.X - points.Start.X));
        double height = Math.Max(1, Math.Abs(points.End.Y - points.Start.Y));
        var line = Theme(new Line
        {
            X1 = points.Start.X - x, X2 = points.End.X - x,
            Y1 = points.Start.Y - y, Y2 = points.End.Y - y,
            StrokeThickness = edge.Thick ? 2.8 : 1.8,
            IsHitTestVisible = false
        }, (Shape.StrokeProperty, "AccentTextBrush"));
        if (edge.Dashed) line.StrokeDashArray = new DoubleCollection { 5, 3 };
        Add(line, x, y, width, height);
        if (edge.Label is { Length: > 0 } label)
        {
            double labelWidth = horizontal ? Math.Min(160, Math.Max(72, nodeWidth - 8)) : 170;
            var text = Text(label, labelWidth, "SyntaxKeyBrush");
            text.Measure(new Size(labelWidth, double.PositiveInfinity));
            double labelHeight = Math.Max(text.LineHeight, Math.Ceiling(text.DesiredSize.Height));
            double labelX = (points.Start.X + points.End.X - labelWidth) / 2;
            double labelY = (points.Start.Y + points.End.Y - labelHeight) / 2 - (horizontal ? 18 : 0);
            Add(Theme(new Border
            {
                Child = text, Padding = new Thickness(4, 1, 4, 1),
                Background = Fallback("EditorBrush"),
                BorderThickness = new Thickness(0)
            }), labelX, labelY, labelWidth, labelHeight + 2);
        }
        Arrow(points.Start, points.End, edge.Directed);
    }

    private void Arrow(Point start, Point end, bool hasArrow)
    {
        if (!hasArrow) return;
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;
        double ux = dx / length, uy = dy / length;
        double px = -uy, py = ux;
        Point tip = end;
        Point left = new(end.X - ux * 11 + px * 5, end.Y - uy * 11 + py * 5);
        Point right = new(end.X - ux * 11 - px * 5, end.Y - uy * 11 - py * 5);
        double x = Math.Min(tip.X, Math.Min(left.X, right.X));
        double y = Math.Min(tip.Y, Math.Min(left.Y, right.Y));
        var arrow = Theme(new Polygon
        {
            Points = new PointCollection
            {
                new(tip.X - x, tip.Y - y), new(left.X - x, left.Y - y),
                new(right.X - x, right.Y - y)
            },
            StrokeThickness = 1
        }, (Shape.StrokeProperty, "AccentTextBrush"), (Shape.FillProperty, "AccentTextBrush"));
        Add(arrow, x, y, Math.Max(1, Math.Max(tip.X, Math.Max(left.X, right.X)) - x),
            Math.Max(1, Math.Max(tip.Y, Math.Max(left.Y, right.Y)) - y));
    }

    private FrameworkElement Node(MermaidFlowchartNode node, Rect bounds)
    {
        var label = Text(node.Label, bounds.Width - 22);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.Margin = new Thickness(10, 4, 10, 4);
        AutomationProperties.SetName(label, $"节点：{node.Label}");
        var grid = new Grid { Width = bounds.Width, Height = bounds.Height };
        FrameworkElement shape;
        switch (node.Shape)
        {
            case MermaidFlowchartNodeShape.Rounded:
                shape = Theme(new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) },
                    (Border.BackgroundProperty, "PanelBrush"), (Border.BorderBrushProperty, "BorderBrush"));
                break;
            case MermaidFlowchartNodeShape.Diamond:
                shape = Theme(new Polygon
                {
                    Points = new PointCollection
                    {
                        new(bounds.Width / 2, 0), new(bounds.Width, bounds.Height / 2),
                        new(bounds.Width / 2, bounds.Height), new(0, bounds.Height / 2)
                    },
                    StrokeThickness = 1
                }, (Shape.FillProperty, "PanelBrush"),
                    (Shape.StrokeProperty, "BorderBrush"));
                break;
            case MermaidFlowchartNodeShape.Circle:
                shape = Theme(new Ellipse { StrokeThickness = 1 },
                    (Shape.FillProperty, "PanelBrush"), (Shape.StrokeProperty, "BorderBrush"));
                break;
            case MermaidFlowchartNodeShape.Subroutine:
                var outer = Theme(new Border { BorderThickness = new Thickness(1) },
                    (Border.BackgroundProperty, "PanelBrush"), (Border.BorderBrushProperty, "BorderBrush"));
                var inner = Theme(new Border { BorderThickness = new Thickness(1), Margin = new Thickness(6) },
                    (Border.BorderBrushProperty, "BorderBrush"));
                grid.Children.Add(outer);
                grid.Children.Add(inner);
                grid.Children.Add(label);
                AutomationProperties.SetName(grid, $"节点：{node.Label}");
                return grid;
            default:
                shape = Theme(new Border { BorderThickness = new Thickness(1) },
                    (Border.BackgroundProperty, "PanelBrush"), (Border.BorderBrushProperty, "BorderBrush"));
                break;
        }
        grid.Children.Add(shape);
        grid.Children.Add(label);
        AutomationProperties.SetName(grid, $"节点：{node.Label}");
        return grid;
    }

    private TextBlock Text(string value, double width, string color = "TextBrush")
    {
        var label = Theme(new TextBlock
        {
            Text = value, Width = width, FontFamily = FontFamily, FontSize = FontSize,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            LineHeight = Math.Ceiling(FontSize * 1.5),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        }, (TextBlock.ForegroundProperty, color));
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
        return label;
    }

    private static (Point Start, Point End) Connect(Rect from, Rect to)
    {
        double fromX = from.X + from.Width / 2;
        double fromY = from.Y + from.Height / 2;
        double toX = to.X + to.Width / 2;
        double toY = to.Y + to.Height / 2;
        double dx = toX - fromX;
        double dy = toY - fromY;
        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            double sign = Math.Sign(dx);
            return (new Point(fromX + sign * from.Width / 2, fromY),
                new Point(toX - sign * to.Width / 2, toY));
        }
        double verticalSign = Math.Sign(dy);
        return (new Point(fromX, fromY + verticalSign * from.Height / 2),
            new Point(toX, toY - verticalSign * to.Height / 2));
    }

    private void Add(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        canvas.Children.Add(element);
        leftEdge = Math.Min(leftEdge, x);
        rightEdge = Math.Max(rightEdge, x + width);
        topEdge = Math.Min(topEdge, y);
        bottomEdge = Math.Max(bottomEdge, y + height);
    }

    private static T Theme<T>(T element, params (DependencyProperty Property, string Key)[] properties)
        where T : FrameworkElement
    {
        foreach (var (property, key) in properties)
        {
            var token = ThemeProperties[key];
            element.SetResourceReference(token, key);
            var fallback = Fallback(key);
            BindingOperations.SetBinding(element, property, new Binding
            {
                Source = element, Path = new PropertyPath("(0)", token),
                Mode = BindingMode.OneWay, TargetNullValue = fallback, FallbackValue = fallback
            });
        }
        return element;
    }

    private static Brush Fallback(string key) => new SolidColorBrush(key switch
    {
        "TextBrush" => Color.FromRgb(220, 220, 204),
        "MutedBrush" => Color.FromRgb(189, 189, 181),
        "AccentTextBrush" or "SyntaxKeyBrush" => Color.FromRgb(136, 200, 255),
        "PanelBrush" => Color.FromRgb(51, 51, 51),
        "BorderBrush" => Color.FromRgb(88, 88, 88),
        _ => Color.FromRgb(63, 63, 63)
    });
}
