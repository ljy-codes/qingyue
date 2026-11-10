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

    private void AttachReadingNavigation(EditorTab tab)
    {
        tab.Preview.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (!tab.IsReading || e.VerticalChange == 0 || CurrentTab != tab) return;
            CaptureReadingViewport(tab);
            FollowReadingOutline(tab);
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

    private static int EditingViewportAnchor(EditorTab tab)
    {
        int line = tab.Editor.IsLoaded ? tab.Editor.GetFirstVisibleLineIndex() : -1;
        int index = line >= 0 ? tab.Editor.GetCharacterIndexFromLineIndex(line) : -1;
        return index >= 0 ? SourceOffset(tab, index) : CurrentSourceAnchor(tab);
    }

    private void NavigationTabChanging(EditorTab next)
    {
        if (ReferenceEquals(navigationTab, next)) return;
        if (navigationTab is { } previous)
        {
            CaptureReadingViewport(previous);
            previous.ViewportGeneration++;
        }
        navigationTab = next;
        searchOriginTab = null;
        CancelInactiveLoading();
        if (next.IsReading && next.ViewportAnchor is { } anchor)
            RestoreReadingViewport(next, anchor, next.ViewportDelta);
    }

    private void RememberReadingPosition(EditorTab tab)
    {
        CaptureReadingViewport(tab);
        tab.ReturnAnchor = tab.ViewportAnchor ?? CurrentSourceAnchor(tab);
        tab.ReturnDelta = tab.ViewportDelta;
        ReturnPositionButton.IsEnabled = true;
    }

    private void ReturnToReadingPosition()
    {
        if (CurrentTab is not { ReturnAnchor: { } anchor } tab) return;
        double delta = tab.ReturnDelta;
        CaptureReadingViewport(tab);
        tab.ReturnAnchor = tab.ViewportAnchor;
        tab.ReturnDelta = tab.ViewportDelta;
        if (tab.IsReading) RestoreReadingViewport(tab, anchor, delta);
        else
        {
            tab.Editor.CaretIndex = EditorOffset(tab, anchor);
            ScrollToCaret(tab);
        }
        FocusDocument();
    }

    private void FollowReadingOutline(EditorTab tab)
    {
        if (CurrentTab != tab || tab.Reading is null || tab.ViewportAnchor is not { } anchor) return;
        var heading = tab.Reading.Headings.LastOrDefault(h => h.SourceStart <= anchor);
        if (ReferenceEquals(OutlineList.SelectedItem, heading)) return;
        followingOutline = true;
        try { OutlineList.SelectedItem = heading; }
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
        SearchPanel.Visibility = Visibility.Collapsed;
        if (searchOriginTab is { } tab && CurrentTab == tab && tab.IsReading)
            RestoreReadingViewport(tab, searchOriginAnchor, searchOriginDelta);
        searchOriginTab = null;
        FocusDocument();
    }
}
