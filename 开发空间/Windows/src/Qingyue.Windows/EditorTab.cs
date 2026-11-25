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
    internal MarkdownEditor MarkdownWriter { get; }
    private RichTextBox? preview;
    public RichTextBox Preview => EnsurePreview();
    public bool IsPreviewCreated => preview is not null;
    public Grid EditorSurface { get; }
    public TextBlock LineNumbers { get; }
    public TextPositionIndex Positions { get; private set; }
    public IReadOnlyList<LineChange> LineChanges { get; private set; } = [];
    public ReadingModel? Reading { get; set; }
    public RenderedReading? Rendered { get; set; }
    public bool IsReading { get; set; }
    public bool SplitPreview { get; set; }
    public bool RecoverDraft { get; set; } = true;
    public bool AutomaticKind { get; set; } = true;
    public TextKind Kind { get; set; }
    public int SourceAnchor { get; set; }
    internal int? ViewportAnchor { get; set; }
    internal double ViewportDelta { get; set; }
    internal int? EditorViewportAnchor { get; set; }
    internal double EditorHorizontalOffset { get; set; }
    internal long ViewportGeneration { get; set; }
    internal string? RenderedText { get; set; }
    internal string? RenderedPath { get; set; }
    internal TextKind RenderedKind { get; set; }
    internal List<(int Anchor, double Delta)> Navigation { get; } = [];
    internal int? ReturnAnchor => Navigation.Count == 0 ? null : Navigation[^1].Anchor;
    internal double ReturnDelta => Navigation.Count == 0 ? 0 : Navigation[^1].Delta;
    public string DisplayTitle => File.Title + (File.IsDirty && !IsEmpty ? "  •" : "");
    public bool IsEmpty => File.Path is null && File.Text.Length == 0
        && (File.SuggestedName is null || automaticallyNamed && File.SuggestedName == automaticName);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action<RichTextBox>? PreviewCreated;
    private bool updatingLines;
    private readonly Canvas changeMargin = new() { Width = 5, HorizontalAlignment = HorizontalAlignment.Right };
    private LineChangeTracker changeTracker;
    private readonly DispatcherTimer changeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private CancellationTokenSource? changeCancellation;
    private long changeGeneration;
    private bool automaticallyNamed;
    private string? automaticName;
    private bool lineNumberRefreshQueued;

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
            FontSize = 15, UndoLimit = 100, IsUndoEnabled = true,
            IsInactiveSelectionHighlightEnabled = true,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        Editor.IsUndoEnabled = false;
        Editor.IsUndoEnabled = true;
        MarkdownWriter = new MarkdownEditor(this);
        Editor.PreviewKeyDown += HandleTextIndentation;
        System.Windows.Automation.AutomationProperties.SetName(Editor, "文档编辑器");
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

    internal RichTextBox EnsurePreview(double fontSize = 16)
    {
        if (preview is not null)
        {
            preview.FontSize = fontSize;
            return preview;
        }

        preview = new RichTextBox
        {
            IsReadOnly = true, IsDocumentEnabled = true, BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsUndoEnabled = false, Padding = new Thickness(0),
            IsInactiveSelectionHighlightEnabled = true,
            FontSize = fontSize
        };
        System.Windows.Automation.AutomationProperties.SetName(preview, "只读阅读视图");
        preview.SetResourceReference(Control.BackgroundProperty, "EditorBrush");
        preview.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        preview.SetResourceReference(RichTextBox.SelectionBrushProperty, "AccentBrush");
        PreviewCreated?.Invoke(preview);
        return preview;
    }

    internal void SetPreviewFontSize(double fontSize)
    {
        if (preview is not null) preview.FontSize = fontSize;
    }

    internal void ReleaseReadingResources()
    {
        Reading = null;
        Rendered = null;
        RenderedText = null;
        RenderedPath = null;
        RenderedKind = Kind;
        ReleasePreviewDocument();
    }

    internal void ReleasePreviewDocument()
    {
        if (preview is not null) preview.Document = new FlowDocument();
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

    private void HandleTextIndentation(object sender, KeyEventArgs e)
    {
        if (e.Handled || MarkdownWriter.IsComposing || e.Key != Key.Tab || Editor.IsReadOnly) return;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers is not ModifierKeys.None and not ModifierKeys.Shift) return;
        var edit = TextEditing.Indent(Editor.Text, Editor.SelectionStart, Editor.SelectionLength,
            modifiers == ModifierKeys.Shift);
        if (edit is not null)
        {
            Editor.BeginChange();
            try
            {
                Editor.Select(edit.Start, edit.Length);
                Editor.SelectedText = edit.Replacement;
                Editor.Select(edit.SelectionStart, edit.SelectionLength);
            }
            finally { Editor.EndChange(); }
        }
        e.Handled = true;
        Editor.Focus();
    }

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
            int first = Editor.GetFirstVisibleLineIndex();
            int last = Editor.GetLastVisibleLineIndex();
            if (first < 0 || last < 0) return;
            first = Math.Clamp(first, 0, count - 1);
            last = Math.Clamp(last, first, count - 1);
            if (!TryGetCharacterIndexFromLineIndex(first, count, out int firstOffset)) return;
            if (firstOffset < 0) return;

            // Use logical source line numbers even when word wrapping creates visual rows.
            var source = Editor.Text;
            var output = new List<string>();
            changeMargin.Children.Clear();
            double top = double.NaN;
            for (int row = first; row <= Math.Min(count - 1, Math.Min(last + 1, first + 150)); row++)
            {
                // TextBox line indexes can become temporarily stale while a tab is
                // selected and its layout is being replaced. Do not trust the
                // LineCount snapshot alone; retry after the next layout pass.
                if (!TryGetCharacterIndexFromLineIndex(row, count, out int offset)) return;
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

    private bool TryGetCharacterIndexFromLineIndex(int row, int lineCount, out int offset)
    {
        offset = -1;
        if (!Editor.IsLoaded || row < 0 || row >= lineCount || Editor.LineCount != lineCount)
        {
            QueueLineNumberRefresh();
            return false;
        }

        try
        {
            offset = Editor.GetCharacterIndexFromLineIndex(row);
            return offset >= 0;
        }
        catch (ArgumentOutOfRangeException)
        {
            // WPF can still reject a row while selection/layout is changing.
            // Recompute after Loaded instead of leaving the gutter half updated.
            QueueLineNumberRefresh();
            return false;
        }
    }

    private void QueueLineNumberRefresh()
    {
        if (lineNumberRefreshQueued || !Editor.IsLoaded) return;
        lineNumberRefreshQueued = true;
        Editor.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            lineNumberRefreshQueued = false;
            if (Editor.IsLoaded) UpdateLineNumbers();
        });
    }
}

public sealed class ReaderAction(Func<bool> canExecute, Action execute) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter)
    {
        if (canExecute()) execute();
    }
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
