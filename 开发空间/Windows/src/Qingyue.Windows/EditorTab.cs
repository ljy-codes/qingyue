using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Qingyue.Core;
using TextSelection = Qingyue.Core.TextSelection;

namespace Qingyue.Windows;

public sealed class EditorTab : INotifyPropertyChanged
{
    public DocumentFile File { get; }
    public TextBox Editor { get; }
    public RichTextBox Preview { get; }
    public Grid EditorSurface { get; }
    public TextBlock LineNumbers { get; }
    public TextPositionIndex Positions { get; private set; }
    public IReadOnlyList<LineChange> LineChanges { get; private set; } = [];
    public ReadingModel? Reading { get; set; }
    public RenderedReading? Rendered { get; set; }
    public bool IsReading { get; set; }
    public bool RecoverDraft { get; set; } = true;
    public bool AutomaticKind { get; set; } = true;
    public TextKind Kind { get; set; }
    public int SourceAnchor { get; set; }
    public string DisplayTitle => File.Title + (File.IsDirty && !IsEmpty ? "  •" : "");
    public bool IsEmpty => File.Path is null && File.Text.Length == 0
        && (File.SuggestedName is null || automaticallyNamed && File.SuggestedName == automaticName);
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool updatingLines;
    private readonly Canvas changeMargin = new() { Width = 5, HorizontalAlignment = HorizontalAlignment.Right };
    private LineChangeTracker changeTracker;
    private readonly DispatcherTimer changeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private CancellationTokenSource? changeCancellation;
    private long changeGeneration;
    private bool automaticallyNamed;
    private string? automaticName;

    public EditorTab(DocumentFile file)
    {
        File = file;
        changeTracker = new LineChangeTracker(file.CaptureSnapshot().BaselineText);
        Positions = new TextPositionIndex(file.Text);
        Kind = TextServices.DetectKind(file.Path ?? file.SuggestedName, file.Text);
        Editor = new TextBox
        {
            Text = file.Text, AcceptsReturn = true, AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            Padding = new Thickness(18, 20, 24, 24),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 15, UndoLimit = 200, IsUndoEnabled = true,
            IsInactiveSelectionHighlightEnabled = true,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        Editor.IsUndoEnabled = false;
        Editor.IsUndoEnabled = true;
        System.Windows.Automation.AutomationProperties.SetName(Editor, "文档编辑器");
        Preview = new RichTextBox
        {
            IsReadOnly = true, IsDocumentEnabled = true, BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsUndoEnabled = false, Padding = new Thickness(0),
            IsInactiveSelectionHighlightEnabled = true
        };
        System.Windows.Automation.AutomationProperties.SetName(Preview, "只读阅读视图");
        Preview.SetResourceReference(Control.BackgroundProperty, "EditorBrush");
        Preview.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        Preview.SetResourceReference(RichTextBox.SelectionBrushProperty, "AccentBrush");
        Editor.SetResourceReference(TextBox.SelectionBrushProperty, "AccentBrush");
        EditorSurface = new Grid();
        EditorSurface.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        EditorSurface.ColumnDefinitions.Add(new ColumnDefinition());
        var gutter = new Border { MinWidth = 52, ClipToBounds = true };
        gutter.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        LineNumbers = new TextBlock
        {
            Text = "1", FontFamily = Editor.FontFamily, FontSize = 15,
            TextAlignment = TextAlignment.Right, Margin = new Thickness(8, 20, 10, 0)
        };
        LineNumbers.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var gutterContent = new Grid();
        gutterContent.Children.Add(LineNumbers);
        gutterContent.Children.Add(changeMargin);
        gutter.Child = gutterContent;
        EditorSurface.Children.Add(gutter);
        Grid.SetColumn(Editor, 1);
        EditorSurface.Children.Add(Editor);
        Editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => UpdateLineNumbers()));
        Editor.SizeChanged += (_, _) => UpdateLineNumbers();
        Editor.Loaded += (_, _) => UpdateLineNumbers();
        Editor.Loaded += (_, _) => QueueLineChanges();
        Editor.Unloaded += (_, _) =>
        {
            changeTimer.Stop();
            changeCancellation?.Cancel();
            changeGeneration++;
        };
        Editor.TextChanged += (_, _) =>
        {
            RebuildPositions();
            LineChanges = [];
            QueueLineChanges();
            UpdateLineNumbers();
        };
        Editor.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount < 2 || e.ChangedButton != MouseButton.Left) return;
            if (SelectTextAt(e.GetPosition(Editor), e.ClickCount)) e.Handled = true;
        };
        changeTimer.Tick += async (_, _) =>
        {
            changeTimer.Stop();
            long generation = changeGeneration;
            var tracker = changeTracker;
            string text = Editor.Text;
            using var cancellation = new CancellationTokenSource();
            changeCancellation = cancellation;
            try
            {
                var changes = await Task.Run(() => tracker.Compare(text, cancellation.Token), cancellation.Token);
                if (generation != changeGeneration || cancellation.IsCancellationRequested) return;
                LineChanges = changes;
                UpdateLineNumbers();
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(changeCancellation, cancellation)) changeCancellation = null;
            }
        };
    }

    public void NotifyChanged()
    {
        // A manual rename permanently turns a placeholder into a recoverable named document.
        if (automaticallyNamed && File.SuggestedName != automaticName) automaticallyNamed = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayTitle)));
    }

    internal void MarkAutomaticallyNamed()
    {
        automaticallyNamed = true;
        automaticName = File.SuggestedName;
    }
    public void RebuildPositions() => Positions = new TextPositionIndex(Editor.Text);

    /// <summary>Call only after DocumentFile.Save succeeds, never after a draft/session save.</summary>
    public void TrackSavedBaseline()
    {
        changeTracker = changeTracker.WithSavedBaseline(File.CaptureSnapshot().BaselineText);
        LineChanges = [];
        changeMargin.Children.Clear();
        QueueLineChanges();
    }

    internal bool SelectTextAt(Point position, int clickCount)
    {
        if (clickCount < 2 || Editor.IsReadOnly) return false;
        // Scrollbar arrows/thumbs keep their own double-click behavior.
        // Disabled, visible bars are omitted by InputHitTest, so also check their bounds.
        if (Editor.Template?.FindName("PART_ContentHost", Editor) is ScrollViewer scroller)
            foreach (string name in new[] { "PART_VerticalScrollBar", "PART_HorizontalScrollBar" })
                if (scroller.Template?.FindName(name, scroller) is ScrollBar { IsVisible: true } bar
                    && new Rect(bar.TranslatePoint(new Point(), Editor), bar.RenderSize).Contains(position))
                    return false;
        for (var hit = Editor.InputHitTest(position) as DependencyObject; hit is Visual; hit = VisualTreeHelper.GetParent(hit))
            if (hit is ScrollBar) return false;
        int offset = Editor.GetCharacterIndexFromPoint(position, true);
        if (offset < 0) return false;
        var range = clickCount >= 3 ? TextSelection.LogicalLineAt(Editor.Text, offset)
            : TextSelection.SegmentAt(Editor.Text, offset);
        Editor.Focus();
        Editor.Select(range.Start, range.Length);
        return true;
    }

    private void QueueLineChanges()
    {
        changeGeneration++;
        changeCancellation?.Cancel();
        changeTimer.Stop();
        if (Editor.IsLoaded) changeTimer.Start();
    }

    private bool TryGetLineChange(int line, out LineChange change)
    {
        int low = 0, high = LineChanges.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            var candidate = LineChanges[middle];
            if (candidate.Line == line) { change = candidate; return true; }
            if (candidate.Line < line) low = middle + 1; else high = middle - 1;
        }
        change = default;
        return false;
    }

    public void UpdateLineNumbers()
    {
        if (!Editor.IsLoaded || updatingLines) return;
        updatingLines = true;
        try
        {
        int count = Editor.LineCount;
        if (count == 0) return;
        int first = Editor.GetFirstVisibleLineIndex(), last = Editor.GetLastVisibleLineIndex();
        if (first < 0 || last < 0) return;
        // Use logical source line numbers even when word wrapping creates visual rows.
        var source = Editor.Text;
        var output = new List<string>();
        changeMargin.Children.Clear();
        double top = double.NaN;
        for (int row = first; row <= Math.Min(count - 1, Math.Min(last + 1, first + 150)); row++)
        {
            int offset = Editor.GetCharacterIndexFromLineIndex(row);
            if (offset < 0) break;
            var rect = Editor.GetRectFromCharacterIndex(offset);
            if (rect.IsEmpty) continue;
            if (double.IsNaN(top)) top = rect.Top;
            bool continuation = offset > 0 && source[offset - 1] is not '\n' and not '\r';
            int line = Positions.LogicalLineAt(offset);
            output.Add(continuation ? "" : line.ToString());
            if (!TryGetLineChange(line, out var change) || change.IsDeletion && continuation) continue;
            var marker = new Rectangle
            {
                Width = 4,
                Height = change.IsDeletion ? 3 : Math.Max(3, rect.Height),
                ToolTip = (change.Kind == LineChangeKind.Unsaved ? "未保存修改" : "本次会话已保存修改")
                    + (change.IsDeletion ? "（此处删除了行）" : "（修改或新增行）")
            };
            marker.SetResourceReference(Shape.FillProperty, change.Kind == LineChangeKind.Unsaved ? "UnsavedBrush" : "SavedBrush");
            Canvas.SetTop(marker, rect.Top);
            changeMargin.Children.Add(marker);
        }
        LineNumbers.Text = string.Join("\n", output);
        LineNumbers.LineHeight = Editor.FontSize * Editor.FontFamily.LineSpacing;
        if (!double.IsNaN(top)) LineNumbers.Margin = new Thickness(8, top, 10, 0);
        }
        finally { updatingLines = false; }
    }
}

public sealed class ReaderAction(Action execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
