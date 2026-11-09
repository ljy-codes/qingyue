using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    public void ToggleReading()
    {
        if (CurrentTab is not { } tab) return;
        if (tab.IsReading)
        {
            int offset = CurrentSourceAnchor(tab);
            tab.SourceAnchor = offset;
            tab.IsReading = false;
            tab.Editor.IsReadOnly = false;
            DocumentHost.Content = tab.EditorSurface;
            tab.Editor.CaretIndex = EditorOffset(tab, offset);
            tab.Editor.Focus();
            ScrollToCaret(tab);
        }
        else EnterReading(tab);
        RefreshCurrent();
    }

    private static int CurrentSourceAnchor(EditorTab tab) =>
        tab.IsReading && tab.Rendered is not null && tab.Reading is not null
            ? tab.Reading.DisplayToSource(tab.Rendered.OffsetAt(tab.Preview.Selection.Start))
            : SourceOffset(tab, tab.Editor.CaretIndex);

    private void EnterReading(EditorTab tab, int? sourceAnchor = null)
    {
        tab.SourceAnchor = Math.Clamp(sourceAnchor ?? CurrentSourceAnchor(tab), 0, tab.File.Text.Length);
        tab.Reading = ReadingModel.Build(tab.File.Text, tab.Kind);
        tab.IsReading = true;
        if (tab.File.Text.Length <= 200_000)
        {
            tab.Rendered = new RenderedReading(tab.Reading);
            tab.Preview.Document = tab.Rendered.Document;
            UpdateReadingWidth(tab);
            var pointer = tab.Rendered.PointerAt(tab.Reading.SourceToDisplay(tab.SourceAnchor));
            tab.Preview.Selection.Select(pointer, pointer);
            if (CurrentTab == tab)
            {
                DocumentHost.Content = tab.Preview;
                tab.Preview.Focus();
                tab.DispatcherBringIntoView(pointer);
            }
        }
        else
        {
            tab.Rendered = null;
            tab.Editor.IsReadOnly = true;
            tab.Editor.CaretIndex = EditorOffset(tab, tab.SourceAnchor);
            ScrollToCaret(tab);
        }
    }

    private static int SourceOffset(EditorTab tab, int editorOffset) => tab.Positions.ToSource(editorOffset);

    private static int EditorOffset(EditorTab tab, int sourceOffset) => tab.Positions.ToEditor(sourceOffset);

    private static void ScrollToCaret(EditorTab tab)
    {
        int line = tab.Editor.GetLineIndexFromCharacterIndex(tab.Editor.CaretIndex);
        if (line >= 0) tab.Editor.ScrollToLine(line);
    }

    private void OutlineSelected(object sender, SelectionChangedEventArgs e)
    {
        if (OutlineList.SelectedItem is not ReadingHeading heading || CurrentTab is not { } tab) return;
        if (tab.IsReading && tab.Rendered is not null)
        {
            var pointer = tab.Rendered.PointerAt(heading.DisplayStart);
            tab.Preview.Selection.Select(pointer, pointer);
            tab.DispatcherBringIntoView(pointer);
            tab.Preview.Focus();
        }
        else
        {
            ExitStructureForEditing(tab);
            tab.Editor.CaretIndex = EditorOffset(tab, heading.SourceStart);
            ScrollToCaret(tab);
            tab.Editor.Focus();
        }
    }

    private void UpdateReadingWidth(EditorTab tab)
    {
        if (tab.Rendered is null) return;
        tab.Rendered.Document.FontSize = settings.FontSize + 1;
        tab.Rendered.Document.LineHeight = (settings.FontSize + 1) * 1.75;
        double available = Math.Max(300, DocumentHost.ActualWidth - 24);
        double margin = Math.Max(24, (available - settings.ReadingWidth) / 2);
        // RichTextBox transfers its Padding to the document when first loaded.
        tab.Preview.Padding = new Thickness(margin, 28, margin, 60);
        tab.Rendered.Document.PagePadding = tab.Preview.Padding;
    }

    private void UpdateReadingWidths()
    {
        foreach (var tab in tabs) UpdateReadingWidth(tab);
        if (CurrentTab is { } current) RefreshMarkdownPresentation(current);
    }

    public void ToggleSidebar()
    {
        settings.Sidebar = !settings.Sidebar;
        ApplySidebar();
        SchedulePersist();
    }

    private void ApplySidebar()
    {
        bool visible = settings.Sidebar && !focusMode;
        Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(visible ? 218 : 0);
        UpdateReadingWidths();
    }

    public void ToggleFocus()
    {
        focusMode = !focusMode;
        MainMenu.Visibility = focusMode ? Visibility.Collapsed : Visibility.Visible;
        TabRow.Visibility = focusMode ? Visibility.Collapsed : Visibility.Visible;
        FocusButton.Content = focusMode ? "退出专注" : "专注";
        ApplySidebar();
    }

    public void ApplyTheme(bool dark)
    {
        settings.Dark = dark;
        var names = new[] { "WindowBrush", "PanelBrush", "EditorBrush", "TextBrush", "MutedBrush", "BorderBrush", "AccentBrush", "SelectionBrush", "HoverBrush" };
        var colors = dark
            ? new[] { "#2B2B2B", "#333333", "#3F3F3F", "#DCDCCC", "#BDBDB5", "#585858", "#4782FF", "#365579", "#4A4A4A" }
            : new[] { "#F3F5F8", "#E9EDF3", "#FFFFFF", "#17253B", "#53647A", "#CFD7E3", "#2464DB", "#CFDFFF", "#DFE7F4" };
        for (int i = 0; i < names.Length; i++)
            Resources[names[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        Resources[SystemColors.MenuBrushKey] = Resources["PanelBrush"];
        Resources[SystemColors.MenuTextBrushKey] = Resources["TextBrush"];
        Resources[SystemColors.WindowBrushKey] = Resources["PanelBrush"];
        Resources[SystemColors.WindowTextBrushKey] = Resources["TextBrush"];
        var accents = new[] { "AccentTextBrush", "SyntaxKeyBrush", "SyntaxStringBrush", "SyntaxValueBrush", "SyntaxCommentBrush", "UnsavedBrush", "SavedBrush" };
        var accentColors = dark
            ? new[] { "#88C8FF", "#88C8FF", "#FFD096", "#74DEC7", "#BAC5BA", "#FFBE72", "#74D6AD" }
            : new[] { "#1F60BD", "#175BB5", "#A64A0A", "#087563", "#5C6D59", "#A95A00", "#19764E" };
        for (int i = 0; i < accents.Length; i++)
            Resources[accents[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accentColors[i]));
        foreach (var tab in tabs) RefreshSyntax(tab);
        SchedulePersist();
    }
}

internal static class ReadingViewExtensions
{
    public static void DispatcherBringIntoView(this EditorTab tab, System.Windows.Documents.TextPointer pointer) =>
        tab.Preview.Dispatcher.BeginInvoke(() =>
        {
            pointer.Paragraph?.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
}
