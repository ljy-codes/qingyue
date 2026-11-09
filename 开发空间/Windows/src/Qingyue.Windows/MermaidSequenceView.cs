using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Qingyue.Core;

namespace Qingyue.Windows;

/// <summary>A local, read-only WPF drawing. Create on the UI thread; no browser or external assets.</summary>
public sealed class MermaidSequenceView : UserControl
{
    private const double Gutter = 24;
    private static readonly IReadOnlyDictionary<string, DependencyProperty> ThemeProperties =
        new[] { "TextBrush", "MutedBrush", "AccentTextBrush", "SyntaxKeyBrush", "PanelBrush", "EditorBrush", "BorderBrush" }
        .ToDictionary(key => key, key => DependencyProperty.RegisterAttached("Resolved" + key,
            typeof(Brush), typeof(MermaidSequenceView), new PropertyMetadata(null)));
    private readonly Canvas canvas = new();
    private readonly Dictionary<string, int> participantIndices = new(StringComparer.Ordinal);
    private readonly SequenceParticipant[] participants;
    private readonly double participantWidth;
    private readonly double laneWidth;
    private double leftEdge;
    private double rightEdge;
    private double bottomEdge;

    /// <summary>Unscaled drawing bounds, including room for edge notes and self messages.</summary>
    public double DiagramWidth { get; private set; }
    public double DiagramHeight { get; private set; }

    public MermaidSequenceView(SequenceDiagram diagram, double fontSize = 14)
    {
        ArgumentNullException.ThrowIfNull(diagram);
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        FontSize = fontSize;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FlowDirection = FlowDirection.LeftToRight;
        Padding = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        participants = diagram.Participants.ToArray();
        participantWidth = Math.Max(144, fontSize * 10 + 20);
        laneWidth = participantWidth + Math.Max(40, fontSize * 3);
        for (int i = 0; i < participants.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(participants[i].Id) ||
                !participantIndices.TryAdd(participants[i].Id, i))
                throw new ArgumentException("时序图参与者标识必须非空且唯一。", nameof(diagram));
        }

        Theme(this, (ForegroundProperty, "TextBrush"), (BackgroundProperty, "EditorBrush"));
        Theme(canvas, (Panel.BackgroundProperty, "EditorBrush"));
        AutomationProperties.SetName(this, $"Mermaid 时序图，{participants.Length} 个参与者，{diagram.Events.Count} 个事件");
        Draw(diagram);
        canvas.Width = DiagramWidth;
        canvas.Height = DiagramHeight;
        var scroll = new ScrollViewer
        {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            CanContentScroll = false,
            Padding = new Thickness(0),
            // Reserve the horizontal bar as well as the entire drawing. A FlowDocument
            // must measure the full height before it paginates the BlockUIContainer.
            Height = DiagramHeight + SystemParameters.HorizontalScrollBarHeight + 2
        };
        AutomationProperties.SetName(scroll, "Mermaid 时序图横向滚动区域");
        AutomationProperties.SetHelpText(scroll, "使用水平滚动条或左右方向键查看全部参与者。");
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
        // A document/StackPanel may offer infinity horizontally. Keep the viewport finite
        // while the Canvas retains its full extent for horizontal scrolling.
        double width = double.IsFinite(constraint.Width)
            ? constraint.Width : Math.Min(960, DiagramWidth + 18 + Padding.Left + Padding.Right);
        return base.MeasureOverride(new Size(width, double.PositiveInfinity));
    }

    private void Draw(SequenceDiagram diagram)
    {
        if (participants.Length == 0)
        {
            var empty = Text("暂无参与者", 200, "MutedBrush");
            Add(empty, Gutter, Gutter, 200, TextHeight(empty));
            Finish(Gutter * 2 + TextHeight(empty));
            return;
        }

        var headers = participants.Select(Header).ToArray();
        double headerHeight = headers.Max(header =>
        {
            header.Measure(new Size(participantWidth, double.PositiveInfinity));
            return Math.Ceiling(header.DesiredSize.Height);
        });
        for (int i = 0; i < headers.Length; i++)
            Add(headers[i], Center(i) - participantWidth / 2, Gutter, participantWidth, headerHeight);

        double y = Gutter + headerHeight + 24;
        int number = 0;
        foreach (var item in diagram.Events)
        {
            double from = Center(Index(item.From));
            double to = string.IsNullOrEmpty(item.To) && item.Kind == SequenceEventKind.Note
                ? from : Center(Index(item.To));
            if (item.Kind == SequenceEventKind.Note)
            {
                y = Note(item, from, to, y);
                continue;
            }

            number++;
            bool self = from == to;
            double textWidth = self ? laneWidth - 32 : Math.Min(420, Math.Abs(to - from) - 24);
            var label = Text((diagram.AutoNumber ? $"{number}. " : "") + item.Text, textWidth);
            AutomationProperties.SetName(label,
                $"消息 {number}：{participants[Index(item.From)].Label} → {participants[Index(item.To)].Label}：{item.Text}");
            double labelHeight = TextHeight(label);
            var backdrop = Theme(new Border { Child = label, Padding = new Thickness(4, 2, 4, 2) },
                (Border.BackgroundProperty, "EditorBrush"));
            Add(backdrop, self ? from + 12 : (from + to - textWidth) / 2 - 4,
                y, textWidth + 8, labelHeight + 4);
            double arrowY = y + labelHeight + 12;
            if (self)
            {
                double loop = Math.Max(48, laneWidth / 3);
                var path = Theme(new Polyline
                {
                    Points = new PointCollection
                    {
                        new(0, 0), new(loop, 0), new(loop, 28), new(0, 28)
                    },
                    StrokeThickness = 1.6
                }, (Shape.StrokeProperty, "AccentTextBrush"));
                if (item.Dashed) path.StrokeDashArray = new DoubleCollection { 5, 3 };
                Add(path, from, arrowY, loop, 28);
                ArrowHead(from, arrowY + 28, -1, item.Arrow);
                y = arrowY + 54;
            }
            else
            {
                Segment(from, arrowY, to, arrowY, "AccentTextBrush", item.Dashed);
                ArrowHead(to, arrowY, Math.Sign(to - from), item.Arrow);
                y = arrowY + 26;
            }
        }

        double bottom = y + 8;
        for (int i = 0; i < participants.Length; i++)
        {
            var life = Segment(Center(i), Gutter + headerHeight + 8, Center(i), bottom,
                "MutedBrush", dashed: true, thickness: 1);
            life.Opacity = .6;
            Panel.SetZIndex(life, -1);
        }
        Finish(bottom + Gutter);
    }

    private int Index(string id) => participantIndices.TryGetValue(id, out int index)
        ? index : throw new ArgumentException($"时序图引用了未知参与者：{id}");

    private double Center(int index) => Gutter + participantWidth / 2 + index * laneWidth;

    private FrameworkElement Header(SequenceParticipant participant)
    {
        var label = Text(participant.Label, participantWidth - 24, "AccentTextBrush");
        label.FontWeight = FontWeights.SemiBold;
        var body = new StackPanel();
        if (participant.IsActor)
        {
            var actor = new Canvas { Width = 44, Height = 48, HorizontalAlignment = HorizontalAlignment.Center };
            var head = Theme(new Ellipse
            {
                Width = 14, Height = 14, StrokeThickness = 1.6
            }, (Shape.StrokeProperty, "AccentTextBrush"), (Shape.FillProperty, "EditorBrush"));
            Canvas.SetLeft(head, 15);
            actor.Children.Add(head);
            foreach (var points in new[]
            {
                new PointCollection { new(22, 14), new(22, 31) },
                new PointCollection { new(8, 23), new(36, 23) },
                new PointCollection { new(10, 44), new(22, 31), new(34, 44) }
            })
                actor.Children.Add(Theme(new Polyline { Points = points, StrokeThickness = 1.6 },
                    (Shape.StrokeProperty, "AccentTextBrush")));
            body.Children.Add(actor);
        }
        body.Children.Add(label);
        var box = Theme(new Border
        {
            Child = body, Width = participantWidth, Padding = new Thickness(11, 10, 11, 10),
            BorderThickness = new Thickness(participant.IsActor ? 0 : 1),
            CornerRadius = new CornerRadius(6)
        }, (Border.BackgroundProperty, participant.IsActor ? "EditorBrush" : "PanelBrush"),
            (Border.BorderBrushProperty, "BorderBrush"));
        AutomationProperties.SetName(box, (participant.IsActor ? "角色：" : "参与者：") + participant.Label);
        return box;
    }

    private double Note(SequenceEvent item, double from, double to, double y)
    {
        double width = Math.Min(320, laneWidth - 24);
        double x;
        switch (item.Placement.ToLowerInvariant())
        {
            case "left":
                x = from - width - 18;
                break;
            case "right":
                x = from + 18;
                break;
            default:
                width = Math.Max(participantWidth, Math.Abs(to - from) + participantWidth * .8);
                x = (from + to - width) / 2;
                break;
        }
        var label = Text(item.Text, width - 22);
        label.TextAlignment = TextAlignment.Left;
        AutomationProperties.SetName(label, $"注释（{item.Placement}）：{item.Text}");
        var box = Theme(new Border
        {
            Child = label, Width = width, Padding = new Thickness(10),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4)
        }, (Border.BackgroundProperty, "PanelBrush"), (Border.BorderBrushProperty, "SyntaxKeyBrush"));
        box.Measure(new Size(width, double.PositiveInfinity));
        double height = Math.Ceiling(box.DesiredSize.Height);
        Add(box, x, y, width, height);
        return y + height + 22;
    }

    private TextBlock Text(string value, double width, string color = "TextBrush")
    {
        var label = Theme(new TextBlock
        {
            Text = value.Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase),
            Width = width, FontFamily = FontFamily, FontSize = FontSize,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            LineHeight = Math.Ceiling(FontSize * 1.5),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        }, (TextBlock.ForegroundProperty, color));
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
        return label;
    }

    private static double TextHeight(TextBlock label)
    {
        label.Measure(new Size(label.Width, double.PositiveInfinity));
        return Math.Max(label.LineHeight, Math.Ceiling(label.DesiredSize.Height));
    }

    private Line Segment(double x1, double y1, double x2, double y2, string color,
        bool dashed = false, double thickness = 1.6)
    {
        double x = Math.Min(x1, x2), y = Math.Min(y1, y2);
        var line = Theme(new Line
        {
            X1 = x1 - x, X2 = x2 - x, Y1 = y1 - y, Y2 = y2 - y,
            StrokeThickness = thickness, IsHitTestVisible = false
        }, (Shape.StrokeProperty, color));
        if (dashed) line.StrokeDashArray = new DoubleCollection { 5, 3 };
        Add(line, x, y, Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        return line;
    }

    private void ArrowHead(double x, double y, int direction, string arrow)
    {
        // Mermaid -> / --> retain the message line but have no arrowhead.
        if (arrow == ">") return;
        if (arrow == "x")
        {
            Segment(x - 4, y - 4, x + 4, y + 4, "AccentTextBrush");
            Segment(x - 4, y + 4, x + 4, y - 4, "AccentTextBrush");
            return;
        }
        var points = new PointCollection
        {
            new(x - direction * 9, y - 4.5), new(x, y), new(x - direction * 9, y + 4.5)
        };
        Shape head = arrow == ")"
            ? new Polyline { Points = points, StrokeThickness = 1.6 }
            : new Polygon { Points = points, StrokeThickness = 1 };
        if (head is Polygon)
            Theme(head, (Shape.StrokeProperty, "AccentTextBrush"), (Shape.FillProperty, "AccentTextBrush"));
        else
            Theme(head, (Shape.StrokeProperty, "AccentTextBrush"));
        Add(head, 0, 0, Math.Max(x, x - direction * 9), y + 5);
    }

    private void Add(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        canvas.Children.Add(element);
        leftEdge = Math.Min(leftEdge, x);
        rightEdge = Math.Max(rightEdge, x + width);
        bottomEdge = Math.Max(bottomEdge, y + height);
    }

    private void Finish(double height)
    {
        // A note left of the first participant can have negative coordinates. Translate
        // every primitive together, preserving all arrows and the full scrollable extent.
        double shift = Gutter - leftEdge;
        foreach (FrameworkElement child in canvas.Children)
            Canvas.SetLeft(child, Canvas.GetLeft(child) + shift);
        DiagramWidth = Math.Ceiling(rightEdge - leftEdge + Gutter * 2);
        DiagramHeight = Math.Ceiling(Math.Max(height, bottomEdge + Gutter));
    }

    private static T Theme<T>(T element, params (DependencyProperty Property, string Key)[] properties)
        where T : FrameworkElement
    {
        // Resolve each token dynamically on the element. A binding supplies the missing
        // brush fallback without local Resources that would shadow ancestor themes.
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
        _ => Color.FromRgb(63, 63, 63) // EditorBrush
    });
}
