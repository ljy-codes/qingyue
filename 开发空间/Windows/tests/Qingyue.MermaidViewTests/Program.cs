using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Qingyue.Core;
using Qingyue.Windows;

internal static class Program
{
    private static int passed;
    private static int failed;

    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Test("六角色长消息、末列self和左右Note均保留完整绘图边界", () =>
        {
            var view = new MermaidSequenceView(Sample());
            Layout(view, 640);
            Check(double.IsFinite(view.DiagramWidth) && view.DiagramWidth > 1200, "宽度未覆盖六个角色");
            Check(double.IsFinite(view.DiagramHeight) && view.DiagramHeight > 500, "长文本未计入高度");
            var canvas = Drawing(view);
            var bounds = VisualTreeHelper.GetDescendantBounds(canvas);
            Check(bounds.Left >= -1 && bounds.Right <= view.DiagramWidth + 1, $"横向截断：{bounds}");
            Check(bounds.Top >= -1 && bounds.Bottom <= view.DiagramHeight + 1, $"纵向截断：{bounds}");
            Check(Descendants<Ellipse>(view).Any(), "缺少actor头部");
            Check(Descendants<Polyline>(view).Any(p => p.Points.Count == 4), "缺少self折返箭头");
            var lines = Descendants<Line>(view).ToArray();
            Check(lines.Count(line => line.Y1 != line.Y2 && line.StrokeDashArray.Count > 0) == 6, "生命线不完整");
            Check(lines.Any(line => line.Y1 == line.Y2 && line.StrokeDashArray.Count > 0), "缺少虚线响应");
            Check(lines.Any(line => line.Y1 == line.Y2 && line.StrokeDashArray.Count == 0), "缺少实线请求");
            Check(Descendants<Polygon>(view).Count() >= 4, "缺少实心箭头");
            Check(Descendants<TextBlock>(view).Any(t => t.Text.StartsWith("1. POST /api/drafts/save")), "编号或原始消息丢失");
            Check(!Descendants<TextBlock>(view).Any(t => t.Text.StartsWith("4. ") && t.Text.Contains("注释")), "Note错误占用消息编号");
        });
        Test("有限视口和无限宽父容器都可横向滚动", () =>
        {
            var view = new MermaidSequenceView(Sample());
            Layout(view, 400);
            var scroll = Descendants<ScrollViewer>(view).Single();
            Check(scroll.ViewportWidth <= 400 && scroll.ScrollableWidth > 800, "未建立横向滚动范围");
            scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth);
            view.UpdateLayout();
            Check(scroll.HorizontalOffset > 0, "无法滚动到最右侧");
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Check(double.IsFinite(view.DesiredSize.Width) && view.DesiredSize.Width <= 960, "无限宽布局未收敛");
            Check(view.DesiredSize.Height >= view.DiagramHeight + SystemParameters.HorizontalScrollBarHeight,
                "控件期望高度裁掉绘图或水平滚动条");
            view.Padding = new Thickness(12);
            Layout(view, 400);
            Check(view.DesiredSize.Height >= view.DiagramHeight + 24, "Control.Padding未计入布局");
        });
        Test("中文、连续长URL换行增高，消息之间没有文字重叠", () =>
        {
            var shortView = new MermaidSequenceView(new SequenceDiagram(
                [new("A", "前端"), new("B", "后端")],
                [new("A", "B", "保存", SequenceEventKind.Message)]));
            var longView = new MermaidSequenceView(new SequenceDiagram(
                [new("A", "前端"), new("B", "后端")],
                [new("A", "B", "POST /api/drafts/save/" + new string('a', 250) +
                    "中文消息自动换行与完整显示", SequenceEventKind.Message)]));
            Layout(shortView, 450);
            Layout(longView, 450);
            Check(longView.DiagramHeight > shortView.DiagramHeight + 100, "长消息未改变实际高度");
            var label = Descendants<TextBlock>(longView).Single(t => t.Text.StartsWith("POST"));
            Check(label.ActualHeight > label.LineHeight * 4, "长URL未强制换行");
            var bounds = label.TransformToAncestor(Drawing(longView)).TransformBounds(new Rect(label.RenderSize));
            Check(bounds.Bottom < longView.DiagramHeight - 24, "长消息超出底部边界");
        });
        Test("缺省颜色可见，祖先资源替换时切换深浅主题并恢复默认", () =>
        {
            var view = new MermaidSequenceView(Sample());
            var host = new Border { Child = view };
            Layout(host, 720);
            var message = Descendants<TextBlock>(view).First(t => t.Text.Contains("POST /api/drafts/save"));
            var arrow = Descendants<Polygon>(view).First();
            var note = Descendants<Border>(view).First(b => b.Child is TextBlock t && t.Text.StartsWith("首列左侧"));
            Check(ColorOf(message.Foreground).A > 0, "无主题时文字不可见");
            Check(ColorOf(arrow.Fill).A > 0, "无主题时箭头不可见");
            Color fallback = ColorOf(message.Foreground);
            host.Resources = Palette(light: false);
            Layout(host, 720);
            Check(ColorOf(message.Foreground) == Colors.Beige, "祖先深色文字资源未生效");
            SaveDrawing(view, "sequence-dark.png");
            host.Resources = Palette(light: true);
            Layout(host, 720);
            Check(ColorOf(message.Foreground) == Colors.Black, "主题替换后文字仍为旧颜色");
            Check(ColorOf(arrow.Fill) == Colors.DarkBlue, "主题替换后箭头仍为旧颜色");
            Check(ColorOf(note.Background) == Colors.WhiteSmoke, "主题替换后注释仍为旧颜色");
            SaveDrawing(view, "sequence-light.png");
            host.Resources.Clear();
            Layout(host, 720);
            Check(ColorOf(message.Foreground) == fallback, "移除主题后未恢复可见默认颜色");
        });
        Test("屏幕阅读器能获得参与者、消息和滚动区域名字", () =>
        {
            var view = new MermaidSequenceView(Sample());
            Layout(view, 600);
            Check(AutomationProperties.GetName(view).Contains("6 个参与者"), "缺少图表名字");
            Check(Descendants<ScrollViewer>(view).All(s => AutomationProperties.GetName(s).Contains("滚动")), "滚动区域没有名字");
            Check(Descendants<Border>(view).Any(b => AutomationProperties.GetName(b).Contains("网页用户")), "参与者没有名字");
            Check(Descendants<TextBlock>(view).Any(t => AutomationProperties.GetName(t).Contains("消息 1：网页用户 → 草稿接口")), "消息缺少方向说明");
        });
        Test("空图及非法字号处理明确", () =>
        {
            var view = new MermaidSequenceView(new SequenceDiagram([], []));
            Layout(view, 320);
            Check(view.DiagramWidth > 0 && view.DiagramHeight > 0, "空图没有有限尺寸");
            foreach (double size in new[] { 0d, -1, double.NaN, double.PositiveInfinity })
            {
                try
                {
                    _ = new MermaidSequenceView(Sample(), size);
                    throw new Exception("非法字号未拒绝");
                }
                catch (ArgumentOutOfRangeException) { }
            }
        });
        foreach (string token in new[] { "->", "-->", "-)", "--)" })
        foreach (bool self in new[] { false, true })
            Test($"{token} {(self ? "自调用" : "普通消息")}：真实Canvas线身及{(token.EndsWith(')') ? "开放箭头" : "无箭头")}几何",
                () => CheckMessageGeometry(token, self));
        Console.WriteLine($"{passed} passed, {failed} failed. 无窗口、无网络、未访问剪贴板。");
        return failed == 0 ? 0 : 1;
    }

    private static void CheckMessageGeometry(string token, bool self)
    {
        // Exercise public parsing and the real WPF visual tree, not ArrowHead by reflection.
        string source = "sequenceDiagram\nparticipant A as 合成前端\nparticipant B as 合成服务\n" +
            $"A{token}{(self ? "A" : "B")}: 合成消息";
        Check(MermaidSequence.TryParse(source, out var diagram, out var error), $"样例解析失败：{error}");
        var view = new MermaidSequenceView(diagram!);
        Layout(view, 600);
        var canvas = Drawing(view);
        var shapes = canvas.Children.OfType<Shape>().ToArray();
        var lifelines = shapes.OfType<Line>().Where(line => line.X1 == line.X2 && line.Y1 != line.Y2).ToArray();
        Check(lifelines.Length == 2, "参与者生命线应保持完整");
        Shape body;
        Point start, end;
        if (self)
        {
            var loop = shapes.OfType<Polyline>().Single(line => line.Points.Count == 4);
            var points = loop.Points.Select(point => loop.TransformToAncestor(canvas).Transform(point)).ToArray();
            Check(points[1].X > points[0].X && points[1].Y == points[0].Y &&
                points[2].X == points[1].X && points[2].Y > points[1].Y &&
                points[3].X == points[0].X && points[3].Y == points[2].Y, "自调用折返路径被破坏");
            body = loop;
            start = points[0];
            end = points[3];
        }
        else
        {
            var line = shapes.OfType<Line>().Single(line => line.Y1 == line.Y2 && line.X1 != line.X2);
            body = line;
            start = line.TransformToAncestor(canvas).Transform(new Point(line.X1, line.Y1));
            end = line.TransformToAncestor(canvas).Transform(new Point(line.X2, line.Y2));
            Check(end.X > start.X && end.Y == start.Y, "普通消息线身方向错误");
        }
        double fromX = lifelines[0].TransformToAncestor(canvas).Transform(new Point(lifelines[0].X1, 0)).X;
        var target = lifelines[self ? 0 : 1];
        double toX = target.TransformToAncestor(canvas).Transform(new Point(target.X1, 0)).X;
        Check(Math.Abs(start.X - fromX) < .01 && Math.Abs(end.X - toX) < .01, "消息端点未连接对应生命线");
        Check(body.Stroke is not null && body.StrokeThickness > 0, "线身不可见");
        Check((body.StrokeDashArray.Count > 0) == token.StartsWith("--"), "线身虚实线与语法不符");
        var heads = shapes.Where(shape => shape != body && !lifelines.Contains(shape)).ToArray();
        Console.WriteLine($"  geometry: {token}, {(self ? "self" : "normal")}, body={body.GetType().Name}, " +
            $"dashed={body.StrokeDashArray.Count > 0}, heads=[{string.Join(", ", heads.Select(shape => shape.GetType().Name))}]");
        if (!token.EndsWith(')'))
        {
            Check(heads.Length == 0, $"{token} 应为无箭头线，但Canvas中仍有 {heads.Length} 个箭头图元");
            return;
        }
        Check(heads.Length == 1 && heads[0] is Polyline, $"{token} 应仅有一个开放箭头Polyline");
        var head = (Polyline)heads[0];
        Check(head.Points.Count == 3 && head.Fill is null && head.Stroke is not null && head.StrokeThickness > 0,
            "开放箭头必须为可见、无填充的三点V形");
        var headPoints = head.Points.Select(point => head.TransformToAncestor(canvas).Transform(point)).ToArray();
        int direction = self ? -1 : 1;
        Check((headPoints[1] - end).Length < .01 && headPoints[0].Y < end.Y && headPoints[2].Y > end.Y &&
            Math.Abs(headPoints[0].X - headPoints[2].X) < .01 &&
            (end.X - headPoints[0].X) * direction > 0, "开放箭头没有朝向并连接线身终点");
    }

    private static SequenceDiagram Sample() => new(
        [new("U", "网页用户", true), new("A", "草稿接口"), new("S", "工作流草稿服务"),
         new("D", "业务数据库"), new("M", "异步事件发布"), new("C", "审批状态缓存")],
        [new("U", "A", "POST /api/drafts/save\n中文别名与流程名称、节点信息和审批条件一并提交保存", SequenceEventKind.Message),
         new("A", "S", "保存草稿并校验版本：" + new string('x', 80), SequenceEventKind.Message),
         new("S", "D", "INSERT workflow_drafts（业务主键、审批节点与完整流程配置）", SequenceEventKind.Message),
         new("D", "A", "200 OK：保存成功，返回草稿标识与最新版本", SequenceEventKind.Message, Dashed: true),
         new("U", "U", "首列左侧注释，说明调用开始前的业务前置条件", SequenceEventKind.Note, Placement: "left"),
         new("S", "M", "跨角色注释：保存与通知在同一事务边界内完成", SequenceEventKind.Note),
         new("C", "C", "重新读取并校验当前审批状态，长消息需要在末列正常换行", SequenceEventKind.Message, Dashed: true),
         new("C", "C", "末列右侧注释，仍可通过横向滚动看到全部文字", SequenceEventKind.Note, Placement: "right")],
        AutoNumber: true);

    private static ResourceDictionary Palette(bool light) => new()
    {
        ["TextBrush"] = light ? Brushes.Black : Brushes.Beige,
        ["MutedBrush"] = light ? Brushes.DimGray : Brushes.LightGray,
        ["AccentTextBrush"] = light ? Brushes.DarkBlue : Brushes.LightSkyBlue,
        ["SyntaxKeyBrush"] = light ? Brushes.Teal : Brushes.Cyan,
        ["PanelBrush"] = light ? Brushes.WhiteSmoke : Brushes.DarkSlateGray,
        ["EditorBrush"] = light ? Brushes.White : Brushes.Black,
        ["BorderBrush"] = light ? Brushes.Gray : Brushes.SlateGray
    };

    private static void Layout(FrameworkElement element, double width)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
        element.UpdateLayout();
    }

    private static Canvas Drawing(MermaidSequenceView view) => Descendants<Canvas>(view).First();

    private static void SaveDrawing(MermaidSequenceView view, string name)
    {
        var canvas = Drawing(view);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.DiagramWidth),
            (int)Math.Ceiling(view.DiagramHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Color background = ColorOf(canvas.Background);
        int visible = Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] > 0 &&
            (pixels[i * 4] != background.B || pixels[i * 4 + 1] != background.G || pixels[i * 4 + 2] != background.R));
        Check(visible > 12000, "离屏绘图没有足够可见像素");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "artifacts", name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var output = File.Create(path);
        encoder.Save(output);
        Console.WriteLine("  rendered: " + path);
    }

    private static Color ColorOf(Brush brush) => brush is SolidColorBrush solid
        ? solid.Color : throw new Exception("缺少可见的纯色资源");

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Test(string name, Action body)
    {
        try
        {
            body();
            Console.WriteLine("PASS: " + name);
            passed++;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + name + "\n" + error);
            failed++;
        }
    }
}
