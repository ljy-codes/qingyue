using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private EditorTab? navigationTab, searchOriginTab;
    private int searchOriginAnchor;
    private double searchOriginDelta;
    private bool followingOutline;

    public string ReadingProgressText(EditorTab tab)
    {
        int percent = ProgressPercent(tab);
        return $"进度 {percent}% · {tab.File.Text.Length:N0} 字符 · {(tab.File.IsDirty ? "未保存" : "已保存")}";
    }

    public IReadOnlyList<DocumentBookmark> CurrentBookmarks(EditorTab tab) => bookmarks.For(tab.File);

    public void ToggleBookmark()
    {
        if (CurrentTab is not { } tab) return;
        if (tab.IsReading) CaptureReadingViewport(tab);
        int anchor = tab.IsReading
            ? tab.ViewportAnchor ?? CurrentSourceAnchor(tab)
            : EditingViewportAnchor(tab);
        anchor = Math.Clamp(anchor, 0, tab.File.Text.Length);
        var (lineStart, lineEnd) = SourceLineRange(tab.File.Text, anchor);
        var existing = bookmarks.For(tab.File).FirstOrDefault(item =>
            item.SourceAnchor >= lineStart && item.SourceAnchor <= lineEnd);
        if (existing is not null) bookmarks.Remove(existing.Id);
        else bookmarks.Add(tab.File, anchor);
        bookmarksDirty = true;
        ScheduleBookmarkSave();
        RefreshBookmarks();
        RefreshReadingProgress(tab);
    }

    private static (int Start, int End) SourceLineRange(string text, int sourceOffset)
    {
        int offset = Math.Clamp(sourceOffset, 0, text.Length);
        int start = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        int newline = text.IndexOf('\n', offset);
        int end = newline < 0 ? text.Length : newline + 1;
        return (start, end);
    }

    public async void NavigateToBookmark(DocumentBookmark bookmark)
    {
        var tab = tabs.FirstOrDefault(item => bookmarks.For(item.File).Any(saved => saved.Id == bookmark.Id));
        if (tab is null && bookmark.Path is { } path && File.Exists(path))
        {
            await OpenPathsAsync([path]);
            if (CurrentTab is { } opened) NavigateToBookmark(bookmark);
            return;
        }
        if (tab is null) return;
        SelectTab(tab);
        RememberReadingPosition(tab);
        int anchor = Math.Clamp(bookmark.SourceAnchor, 0, tab.File.Text.Length);
        if (tab.Kind == TextKind.Markdown)
        {
            if (!tab.IsReading)
            {
                await EnterReadingAsync(tab, anchor, focus: true);
                if (!tab.IsReading) return;
            }
            else RestoreReadingViewport(tab, anchor, tab.ViewportDelta);
        }
        else if (tab.IsReading)
        {
            ToggleReading();
            tab.Editor.CaretIndex = EditorOffset(tab, anchor);
            ScrollToCaret(tab);
            tab.Editor.Focus();
        }
        else
        {
            tab.Editor.CaretIndex = EditorOffset(tab, anchor);
            ScrollToCaret(tab);
            tab.Editor.Focus();
        }
        SchedulePersist();
    }

    private WorkspaceEntry CaptureWorkspaceEntry(EditorTab tab)
    {
        CaptureReadingViewport(tab);
        return new WorkspaceEntry(tab.File.Id, tab.File.Path, tab.IsReading,
            CurrentSourceAnchor(tab), tab.Editor.SelectionStart, tab.Editor.SelectionLength,
            tab.RecoverDraft, tab.AutomaticKind, tab.Kind)
        {
            ViewportAnchor = tab.ViewportAnchor,
            ViewportDelta = tab.ViewportDelta
        };
    }

    private void RememberRecentPosition(EditorTab tab)
    {
        if (tab.File.Path is not { } path || !tabs.Contains(tab)) return;
        if (tab.IsReading) CaptureReadingViewport(tab);
        int selectionStart = SourceOffset(tab, tab.Editor.SelectionStart);
        int selectionEnd = SourceOffset(tab, tab.Editor.SelectionStart + tab.Editor.SelectionLength);
        int sourceAnchor = CurrentSourceAnchor(tab);
        int editorViewport = tab.IsReading
            ? tab.EditorViewportAnchor ?? SourceOffset(tab, tab.Editor.SelectionStart)
            : EditingViewportAnchor(tab);
        double horizontalOffset = tab.IsReading
            ? tab.EditorHorizontalOffset
            : EditorHorizontalOffset(tab);
        if (!tab.IsReading)
        {
            tab.EditorViewportAnchor = editorViewport;
            tab.EditorHorizontalOffset = horizontalOffset;
        }
        int readingViewport = tab.IsReading
            ? tab.ViewportAnchor ?? sourceAnchor
            : tab.ViewportAnchor ?? tab.SourceAnchor;
        double readingDelta = tab.IsReading ? tab.ViewportDelta : 0;
        settings.RememberRecentPosition(path, new RecentPosition(
            Math.Max(0, selectionStart),
            Math.Max(0, selectionEnd - selectionStart),
            Math.Max(0, sourceAnchor),
            Math.Max(0, editorViewport),
            Math.Max(0, horizontalOffset),
            Math.Max(0, readingViewport),
            readingDelta,
            tab.IsReading));
    }

    private bool RestoreRecentPosition(EditorTab tab)
    {
        if (tab.File.Path is not { } path ||
            !settings.RecentPositions.TryGetValue(path, out var position))
        {
            tab.IsReading = tab.Kind == TextKind.Markdown;
            return false;
        }
        int selectionStart = EditorOffset(tab, Math.Clamp(position.SelectionStart, 0, tab.File.Text.Length));
        int selectionEnd = EditorOffset(tab, Math.Clamp(
            position.SelectionStart + position.SelectionLength, 0, tab.File.Text.Length));
        tab.Editor.Select(selectionStart, Math.Max(0, selectionEnd - selectionStart));
        tab.SourceAnchor = Math.Clamp(position.SourceAnchor, 0, tab.File.Text.Length);
        tab.ViewportAnchor = Math.Clamp(position.ReadingViewportAnchor, 0, tab.File.Text.Length);
        tab.ViewportDelta = Math.Clamp(position.ReadingViewportDelta, -2_000, 2_000);
        tab.EditorViewportAnchor = Math.Clamp(position.EditorViewportAnchor, 0, tab.File.Text.Length);
        tab.EditorHorizontalOffset = Math.Clamp(position.EditorHorizontalOffset, 0, 100_000_000);
        tab.IsReading = position.IsReading;
        return true;
    }

    private void RestoreEditorViewportWhenReady(EditorTab tab)
    {
        if (tab.IsReading || tab.EditorViewportAnchor is not { } anchor) return;
        double horizontalOffset = tab.EditorHorizontalOffset;
        Dispatcher.BeginInvoke(() =>
        {
            if (!tabs.Contains(tab) || CurrentTab != tab || tab.IsReading) return;
            int index = EditorOffset(tab, Math.Clamp(anchor, 0, tab.File.Text.Length));
            int line = tab.Editor.GetLineIndexFromCharacterIndex(index);
            if (line >= 0) tab.Editor.ScrollToLine(line);
            if (tab.Editor.Template?.FindName("PART_ContentHost", tab.Editor) is ScrollViewer scroller)
                scroller.ScrollToHorizontalOffset(Math.Max(0, horizontalOffset));
        }, DispatcherPriority.Loaded);
    }

    private static double EditorHorizontalOffset(EditorTab tab) =>
        tab.Editor.Template?.FindName("PART_ContentHost", tab.Editor) is ScrollViewer scroller
            ? Math.Max(0, scroller.HorizontalOffset)
            : Math.Max(0, tab.EditorHorizontalOffset);

    private void AttachReadingNavigation(EditorTab tab, RichTextBox preview)
    {
        preview.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (!tab.IsReading || e.VerticalChange == 0 || CurrentTab != tab) return;
            CaptureReadingViewport(tab);
            FollowReadingOutline(tab);
            RefreshReadingProgress(tab);
            SchedulePersist();
        }));
    }

    private void CaptureReadingViewport(EditorTab tab)
    {
        if (!tab.IsReading || tab.Rendered is null || tab.Reading is null || !tab.Preview.IsLoaded
            || !ReferenceEquals(DocumentHost.Content, tab.Preview)) return;
        try
        {
            double x = Math.Clamp(tab.Preview.Document.PagePadding.Left + 4, 4, Math.Max(4, tab.Preview.ActualWidth - 4));
            var pointer = tab.Preview.GetPositionFromPoint(new Point(x, 6), true);
            if (pointer is null) return;
            var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
            tab.ViewportAnchor = tab.Reading.DisplayToSource(tab.Rendered.OffsetAt(pointer));
            tab.ViewportDelta = !rect.IsEmpty && double.IsFinite(rect.Top) ? Math.Clamp(rect.Top, -2000, 2000) : 0;
            UpdateReadingListProgress(tab);
            RefreshReadingProgress(tab);
        }
        catch (InvalidOperationException) { /* A layout replacement will provide the next valid viewport. */ }
    }

    private void RestoreReadingViewport(EditorTab tab, WorkspaceEntry entry)
    {
        tab.ViewportAnchor = Math.Clamp(entry.ViewportAnchor ?? entry.SourceAnchor, 0, tab.File.Text.Length);
        tab.ViewportDelta = double.IsFinite(entry.ViewportDelta) ? Math.Clamp(entry.ViewportDelta, -2000, 2000) : 0;
        RestoreReadingViewport(tab, tab.ViewportAnchor.Value, tab.ViewportDelta);
    }

    private void RestoreReadingViewport(EditorTab tab, int anchor, double delta)
    {
        tab.ViewportAnchor = Math.Clamp(anchor, 0, tab.File.Text.Length);
        tab.ViewportDelta = double.IsFinite(delta) ? Math.Clamp(delta, -2000, 2000) : 0;
        if (tab.Rendered is not { } rendered || tab.Reading is not { } model) return;
        long generation = ++tab.ViewportGeneration;
        var pointer = rendered.PointerAt(model.SourceToDisplay(tab.ViewportAnchor.Value));
        bool Current() => CurrentTab == tab && tab.IsReading && tabs.Contains(tab)
            && ReferenceEquals(tab.Rendered, rendered) && generation == tab.ViewportGeneration
            && !approvedShutdown && !exitInProgress;
        Dispatcher.BeginInvoke(() =>
        {
            if (!Current()) return;
            pointer.Paragraph?.BringIntoView();
            Dispatcher.BeginInvoke(() =>
            {
                if (!Current()) return;
                var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
                if (!rect.IsEmpty)
                    tab.Preview.ScrollToVerticalOffset(Math.Max(0, tab.Preview.VerticalOffset + rect.Top - delta));
                CaptureReadingViewport(tab);
                FollowReadingOutline(tab);
            }, DispatcherPriority.ContextIdle);
        }, DispatcherPriority.Loaded);
    }

    private static int ProgressPercent(EditorTab tab)
    {
        if (tab.File.Text.Length == 0) return 0;
        int anchor = Math.Clamp(tab.ViewportAnchor ?? tab.SourceAnchor, 0, tab.File.Text.Length);
        return anchor >= tab.File.Text.Length - 1
            ? 100
            : Math.Clamp((int)Math.Round(anchor * 100d / tab.File.Text.Length), 0, 99);
    }

    private void RefreshReadingProgress(EditorTab tab)
    {
        if (CurrentTab != tab) return;
        RefreshLocation();
    }

    private static int EditingViewportAnchor(EditorTab tab)
    {
        int line = tab.Editor.IsLoaded ? tab.Editor.GetFirstVisibleLineIndex() : -1;
        int index = -1;
        if (line >= 0 && line < tab.Editor.LineCount)
        {
            try { index = tab.Editor.GetCharacterIndexFromLineIndex(line); }
            catch (ArgumentOutOfRangeException) { index = -1; }
        }
        return index >= 0 ? SourceOffset(tab, index) : CurrentSourceAnchor(tab);
    }

    private void NavigationTabChanging(EditorTab next)
    {
        if (ReferenceEquals(navigationTab, next)) return;
        if (navigationTab is { } previous)
        {
            CaptureReadingViewport(previous);
            RememberRecentPosition(previous);
            previous.ViewportGeneration++;
        }
        navigationTab = next;
        searchOriginTab = null;
        CancelInactiveLoading();
        if (next.IsReading && next.ViewportAnchor is { } anchor)
            RestoreReadingViewport(next, anchor, next.ViewportDelta);
        else if (!next.IsReading)
            RestoreEditorViewportWhenReady(next);
    }

    private void RememberReadingPosition(EditorTab tab)
    {
        if (!tabs.Contains(tab)) return;
        CaptureReadingViewport(tab);
        int anchor = Math.Clamp(
            tab.ViewportAnchor ?? CurrentSourceAnchor(tab), 0, tab.File.Text.Length);
        double delta = double.IsFinite(tab.ViewportDelta)
            ? Math.Clamp(tab.ViewportDelta, -2000, 2000)
            : 0;
        tab.Navigation.Add((anchor, delta));
        if (tab.Navigation.Count > 64) tab.Navigation.RemoveAt(0);
        if (CurrentTab == tab) ReturnPositionButton.IsEnabled = true;
    }

    private void ReturnToReadingPosition()
    {
        if (CurrentTab is not { } tab || tab.Navigation.Count == 0) return;
        var target = tab.Navigation[^1];
        tab.Navigation.RemoveAt(tab.Navigation.Count - 1);
        int anchor = target.Anchor;
        double delta = target.Delta;
        if (tab.IsReading) RestoreReadingViewport(tab, anchor, delta);
        else
        {
            tab.Editor.CaretIndex = EditorOffset(tab, anchor);
            ScrollToCaret(tab);
        }
        ReturnPositionButton.IsEnabled = tab.Navigation.Count > 0;
        FocusDocument();
    }

    private void FollowReadingOutline(EditorTab tab)
    {
        if (CurrentTab != tab || tab.Reading is null || tab.ViewportAnchor is not { } anchor) return;
        var heading = tab.Reading.Headings.LastOrDefault(h => h.SourceStart <= anchor);
        var item = OutlineList.Items.Cast<object>().FirstOrDefault(candidate =>
            TryGetOutlineLocation(candidate, out int sourceStart, out _) &&
            heading is not null && sourceStart == heading.SourceStart);
        if (ReferenceEquals(OutlineList.SelectedItem, item)) return;
        followingOutline = true;
        try { OutlineList.SelectedItem = item; }
        finally { followingOutline = false; }
    }

    private void RememberSearchOrigin()
    {
        if (SearchPanel.Visibility == Visibility.Visible || CurrentTab is not { IsReading: true } tab) return;
        CaptureReadingViewport(tab);
        searchOriginTab = tab;
        searchOriginAnchor = tab.ViewportAnchor ?? CurrentSourceAnchor(tab);
        searchOriginDelta = tab.ViewportDelta;
    }

    private void HideSearch()
    {
        searchGeneration++;
        searchCancellation?.Cancel();
        searchCancellation = null;
        SearchProgressBar.Visibility = Visibility.Collapsed;
        SearchCancelButton.Visibility = Visibility.Collapsed;
        ClearSearchHighlights();
        SearchPanel.Visibility = Visibility.Collapsed;
        if (searchOriginTab is { } tab && CurrentTab == tab && tab.IsReading)
            RestoreReadingViewport(tab, searchOriginAnchor, searchOriginDelta);
        searchOriginTab = null;
        FocusDocument();
    }
}
