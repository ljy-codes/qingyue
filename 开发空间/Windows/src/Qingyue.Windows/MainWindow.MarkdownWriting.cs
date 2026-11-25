using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Qingyue.Core;
using DocumentList = System.Windows.Documents.List;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private bool markdownLocating;
    private readonly DispatcherTimer markdownLocateTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private FlowDocument? markdownNavigationDocument;
    private Block[] markdownNavigationBlocks = [];
    private bool markdownLocateFromEditor;
    private bool markdownLocateEditorViewport;
    private long markdownSuppressScrollUntil;

    private void InitializeMarkdownWriting()
    {
        markdownLocateTimer.Tick += (_, _) =>
        {
            markdownLocateTimer.Stop();
            if (markdownLocateFromEditor)
            {
                int? source = null;
                if (markdownLocateEditorViewport && CurrentTab is { } tab)
                {
                    int row = tab.Editor.GetFirstVisibleLineIndex();
                    if (row >= 0) source = SourceOffset(tab, tab.Editor.GetCharacterIndexFromLineIndex(row));
                }
                LocateMarkdownPreviewAt(source);
            }
            else LocateMarkdownEditorViewport();
        };
        Closed += (_, _) => markdownLocateTimer.Stop();
    }

    private void MarkdownWritingClick(object sender, RoutedEventArgs e)
    {
        if (exitInProgress || modalDepth > 0) return;
        if (sender is FrameworkElement { Tag: string action } &&
            Enum.TryParse<MarkdownEditAction>(action, out var command))
            CurrentTab?.MarkdownWriter.Execute(command);
        RefreshMarkdownWriting();
        e.Handled = true;
    }

    private void MarkdownTableClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (exitInProgress || modalDepth > 0 || CurrentTab is not { } tab) return;
        try
        {
            bool changed = sender is FrameworkElement { Tag: string action } &&
                (action == "PasteTable" ? tab.MarkdownWriter.PasteTable(Clipboard.GetText())
                    : Enum.TryParse<MarkdownTableAction>(action, out var command) && tab.MarkdownWriter.ExecuteTable(command));
            if (!changed) StatusText.Text = "表格操作未执行：请将光标置于表格内；粘贴表格需包含制表符，最多 32 列、1024 单元格。";
            RefreshMarkdownWriting();
        }
        catch (Exception ex) when (SharingFailure(ex))
        { StatusText.Text = "表格操作未完成：" + ex.Message; }
    }

    private void MarkdownLocateClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: "preview" }) LocateMarkdownPreview();
        else LocateMarkdownEditor();
        e.Handled = true;
    }

    private void RefreshMarkdownWriting()
    {
        bool writing = CurrentTab is { Kind: TextKind.Markdown, IsReading: false };
        MarkdownEditOptionsMenu.Visibility = writing ? Visibility.Visible : Visibility.Collapsed;
        MarkdownWritingStateBadge.Visibility = writing ? Visibility.Visible : Visibility.Collapsed;
        MarkdownWritingHint.Visibility = writing ? Visibility.Visible : Visibility.Collapsed;
        if (!writing || CurrentTab is not { } tab) return;
        string text = tab.Editor.Text;
        int caret = Math.Clamp(tab.Editor.CaretIndex, 0, text.Length);
        int lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var marker = tab.MarkdownWriter.Markers.FirstOrDefault(m => m.Start == lineStart);
        if (marker is not null)
            MarkdownWritingState.Text = $"{(marker.Task ? "任务" : marker.Ordered ? "有序列表" : "无序列表")} · 第 {marker.Level + 1} 层";
        else
        {
            int end = text.IndexOfAny(['\r', '\n'], lineStart);
            string line = text[lineStart..(end < 0 ? text.Length : end)];
            var heading = MarkdownSyntax.HeadingPattern.Match(line);
            MarkdownWritingState.Text = heading.Success ? $"H{heading.Groups[1].Length} 标题"
                : MarkdownSyntax.QuotePattern.IsMatch(line) ? "引用" : "正文";
        }
        if (tab.Editor.SelectionLength > 0) MarkdownWritingState.Text += $" · 已选 {tab.Editor.SelectionLength} 字符";
    }

    private void QueueMarkdownLocate(EditorTab tab, bool fromEditor, bool viewport = false)
    {
        if (markdownLocating || markdownTab != tab || CurrentTab != tab || tab.IsReading) return;
        if (viewport && Environment.TickCount64 < markdownSuppressScrollUntil) return;
        markdownLocateFromEditor = fromEditor;
        markdownLocateEditorViewport = viewport;
        markdownLocateTimer.Stop();
        markdownLocateTimer.Start();
    }

    private bool CanLocateMarkdown(out EditorTab tab)
    {
        tab = CurrentTab!;
        return !markdownLocating && tab is { IsReading: false, Kind: TextKind.Markdown } &&
            markdownTab == tab && markdownPreview is not null && markdownText == tab.File.Text &&
            ReferenceEquals(DocumentHost.Content, markdownSplit);
    }

    private Block[] MarkdownNavigationBlocks()
    {
        if (ReferenceEquals(markdownNavigationDocument, markdownPreview!.Document)) return markdownNavigationBlocks;
        markdownNavigationDocument = markdownPreview.Document;
        static IEnumerable<Block> SourceBlocks(BlockCollection blocks)
        {
            foreach (var block in blocks)
            {
                if (block.Tag is ReadingBlock) yield return block;
                if (block is DocumentList list)
                    foreach (var item in list.ListItems)
                        foreach (var child in SourceBlocks(item.Blocks)) yield return child;
                else if (block is Section section)
                    foreach (var child in SourceBlocks(section.Blocks)) yield return child;
                else if (block is Table table)
                    foreach (var group in table.RowGroups)
                        foreach (var row in group.Rows)
                            foreach (var cell in row.Cells)
                                foreach (var child in SourceBlocks(cell.Blocks)) yield return child;
            }
        }
        markdownNavigationBlocks = SourceBlocks(markdownPreview.Document.Blocks).ToArray();
        return markdownNavigationBlocks;
    }

    // Locating a source block does not modify the user's source or preview selection.
    private bool LocateMarkdownPreview() => LocateMarkdownPreviewAt(null);

    private bool LocateMarkdownPreviewAt(int? sourceOffset)
    {
        if (!CanLocateMarkdown(out var tab)) return false;
        int source = sourceOffset ?? SourceOffset(tab, tab.Editor.CaretIndex);
        var blocks = MarkdownNavigationBlocks();
        var target = blocks.LastOrDefault(b => ((ReadingBlock)b.Tag).SourceStart <= source)
            ?? blocks.FirstOrDefault();
        if (target is null) return false;
        markdownLocating = true;
        markdownSuppressScrollUntil = Environment.TickCount64 + 250;
        try { target.BringIntoView(); }
        finally { markdownLocating = false; }
        return true;
    }

    private ReadingBlock? MarkdownBlockAt(TextPointer pointer)
    {
        for (var element = pointer.Parent as FrameworkContentElement; element is not null;
            element = element.Parent as FrameworkContentElement)
            if (element.Tag is ReadingBlock block) return block;
        return MarkdownNavigationBlocks().FirstOrDefault(b => pointer.CompareTo(b.ContentEnd) <= 0)?.Tag as ReadingBlock;
    }

    private bool LocateMarkdownEditor()
    {
        if (!CanLocateMarkdown(out var tab)) return false;
        var block = MarkdownBlockAt(markdownPreview!.Selection.Start);
        if (block is null) return false;
        markdownLocating = true;
        markdownSuppressScrollUntil = Environment.TickCount64 + 250;
        try
        {
            int offset = EditorOffset(tab, block.SourceStart);
            tab.Editor.Select(offset, 0);
            tab.Editor.ScrollToLine(Math.Max(0, tab.Editor.GetLineIndexFromCharacterIndex(offset)));
            tab.Editor.Focus();
        }
        finally { markdownLocating = false; }
        return true;
    }

    private void LocateMarkdownEditorViewport()
    {
        if (!CanLocateMarkdown(out var tab)) return;
        var pointer = markdownPreview!.GetPositionFromPoint(new Point(40, 8), true);
        var block = pointer is null ? null : MarkdownBlockAt(pointer);
        if (block is null) return;
        markdownLocating = true;
        markdownSuppressScrollUntil = Environment.TickCount64 + 250;
        try { tab.Editor.ScrollToLine(Math.Max(0, tab.Editor.GetLineIndexFromCharacterIndex(EditorOffset(tab, block.SourceStart)))); }
        finally { markdownLocating = false; }
    }

    private void AttachMarkdownPreviewNavigation(MarkdownPreview preview)
    {
        preview.PreviewMouseDoubleClick += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (LocateMarkdownEditor()) e.Handled = true;
        };
        preview.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.VerticalChange != 0 && preview.IsMouseOver && CurrentTab is { } tab)
                QueueMarkdownLocate(tab, false, viewport: true);
        }));
    }
}
