using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private readonly DocumentNames documentNames = new();
    private bool editingInteractionsInitialized, tabDragActive;
    private EditorTab? tabDragCandidate;
    private Point tabDragOrigin;
    private string? pendingAutomaticDocumentName;

    // Call once immediately after InitializeComponent. No additional XAML event wiring is needed.
    private void InitializeEditingInteractions()
    {
        if (editingInteractionsInitialized) return;
        editingInteractionsInitialized = true;
        tabs.CollectionChanged += (_, e) =>
        {
            if (pendingAutomaticDocumentName is null || e.NewItems is null) return;
            foreach (EditorTab tab in e.NewItems)
            {
                if (tab.File.Path is not null || tab.File.SuggestedName != pendingAutomaticDocumentName) continue;
                tab.MarkAutomaticallyNamed();
                pendingAutomaticDocumentName = null;
                break;
            }
        };
        TabRow.PreviewMouseLeftButtonDown += TabRowEditingMouseDown;
        TabRow.PreviewMouseMove += TabRowEditingMouseMove;
        TabRow.PreviewMouseLeftButtonUp += TabRowEditingMouseUp;
        TabRow.LostMouseCapture += (_, _) => ResetTabDrag();
        Deactivated += (_, _) => ResetTabDrag();
    }

    private string NextDocumentName()
    {
        pendingAutomaticDocumentName = documentNames.Next(tabs.Select(t => t.File.Title));
        return pendingAutomaticDocumentName;
    }

    private static void TrackSavedBaseline(EditorTab tab) => tab.TrackSavedBaseline();

    private void TabRowEditingMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.Handled) return;
        e.Handled = HandleTabEditingPress(e.OriginalSource as DependencyObject, e.ClickCount, e.GetPosition(TabRow));
    }

    internal bool HandleTabEditingPress(DependencyObject? source, int clickCount, Point position)
    {
        ResetTabDrag();
        if (exitInProgress || modalDepth > 0 || IsTabInteractiveControl(source)) return false;
        var item = Ancestor<TabItem>(source);
        if (item?.DataContext is EditorTab tab)
        {
            // Keep the existing header's double-click rename route intact.
            if (clickCount == 1) { tabDragCandidate = tab; tabDragOrigin = position; }
            return false;
        }
        if (clickCount != 2 || tabs.Count >= 200) return false;
        NewDocument().Editor.Focus();
        return true;
    }

    private void TabRowEditingMouseMove(object sender, MouseEventArgs e)
    {
        if (tabDragCandidate is not { } tab) return;
        if (e.LeftButton != MouseButtonState.Pressed || exitInProgress || modalDepth > 0)
        { ResetTabDrag(); return; }
        Point position = e.GetPosition(TabRow);
        if (!tabDragActive)
        {
            if (Math.Abs(position.X - tabDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            if (!TabRow.CaptureMouse()) { ResetTabDrag(); return; }
            tabDragActive = true;
            SelectTab(tab);
        }
        TryReorderEditingTab(tab, e.GetPosition(TabStrip));
        // TabStrip is inside a horizontal ScrollViewer; keep edge dragging usable.
        var scroll = Ancestor<ScrollViewer>(TabStrip);
        if (scroll is not null)
        {
            double x = e.GetPosition(scroll).X;
            if (x < 24) scroll.LineLeft();
            else if (x > scroll.ActualWidth - 24) scroll.LineRight();
        }
        e.Handled = true;
    }

    internal bool TryReorderEditingTab(EditorTab tab, Point position)
    {
        int from = tabs.IndexOf(tab);
        if (from < 0 || tabs.Count < 2) return false;
        int target = from;
        // Midpoints avoid oscillation when differently sized headers trade places.
        for (int i = 0; i < tabs.Count; i++)
        {
            if (i == from || TabStrip.ItemContainerGenerator.ContainerFromIndex(i) is not TabItem item) continue;
            var origin = item.TranslatePoint(new Point(), TabStrip);
            double midpoint = origin.X + item.ActualWidth / 2;
            if (i < from && position.X < midpoint) { target = i; break; }
            if (i > from && position.X > midpoint) target = i;
        }
        if (target == from) return false;
        tabs.Move(from, target);
        SelectTab(tab);
        SchedulePersist();
        return true;
    }

    private void TabRowEditingMouseUp(object sender, MouseButtonEventArgs e)
    {
        bool dragged = tabDragActive;
        ResetTabDrag();
        if (dragged) e.Handled = true;
    }

    private void ResetTabDrag()
    {
        tabDragCandidate = null;
        tabDragActive = false;
        if (ReferenceEquals(Mouse.Captured, TabRow)) TabRow.ReleaseMouseCapture();
    }

    private static bool IsTabInteractiveControl(DependencyObject? source) =>
        Ancestor<ButtonBase>(source) is not null || Ancestor<ScrollBar>(source) is not null || Ancestor<Thumb>(source) is not null;

    private static T? Ancestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source)
                : source is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }
}
