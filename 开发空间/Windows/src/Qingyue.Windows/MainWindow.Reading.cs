using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    public void ToggleReading()
    {
        if (CurrentTab is not { } tab) return;
        if (tab.IsReading)
        {
            if (markdownTab == tab) markdownTab = null;
            CaptureReadingViewport(tab);
            tab.ViewportGeneration++;
            int offset = tab.ViewportAnchor ?? CurrentSourceAnchor(tab);
            tab.SourceAnchor = offset;
            tab.IsReading = false;
            tab.Editor.IsReadOnly = false;
            DocumentHost.Content = tab.EditorSurface;
            tab.Editor.CaretIndex = EditorOffset(tab, offset);
            tab.Editor.Focus();
            ScrollToCaret(tab);
        }
        else
        {
            int anchor = EditingViewportAnchor(tab);
            tab.ViewportAnchor = anchor;
            tab.ViewportDelta = 0;
            EnterReading(tab, anchor);
            RestoreReadingViewport(tab, anchor, tab.ViewportDelta);
        }
        RefreshCurrent();
    }

    private static int CurrentSourceAnchor(EditorTab tab) =>
        tab.IsReading && tab.Rendered is not null && tab.Reading is not null
            ? tab.Reading.DisplayToSource(tab.Rendered.OffsetAt(tab.Preview.Selection.Start))
            : SourceOffset(tab, tab.Editor.CaretIndex);

    private void EnterReading(EditorTab tab, int? sourceAnchor = null, bool focus = true, bool snapToBlockStart = true)
    {
        tab.SourceAnchor = Math.Clamp(sourceAnchor ?? CurrentSourceAnchor(tab), 0, tab.File.Text.Length);
        if (CanReuseReading(tab))
        {
            if (snapToBlockStart) tab.SourceAnchor = SnapToReadingBlockStart(tab.Reading!, tab.SourceAnchor);
            PublishReading(tab, tab.Reading!, tab.Rendered, tab.File.Text, tab.File.Path, tab.Kind, tab.SourceAnchor, focus);
            return;
        }
        var model = ReadingModel.Build(tab.File.Text, tab.Kind);
        if (snapToBlockStart) tab.SourceAnchor = SnapToReadingBlockStart(model, tab.SourceAnchor);
        var rendered = PreviewLimits.CanRender(tab.File.Text, tab.Kind)
            ? tab.Kind == TextKind.Markdown
                ? RenderedReading.Markdown(model, tab.File.Text, tab.File.Path, settings.FontSize + 1, settings.ReadingWidth)
                : new RenderedReading(model, sourcePath: tab.File.Path)
            : null;
        PublishReading(tab, model, rendered, tab.File.Text, tab.File.Path, tab.Kind, tab.SourceAnchor, focus);
    }

    private static int SnapToReadingBlockStart(ReadingModel model, int sourceAnchor)
    {
        int anchor = Math.Clamp(sourceAnchor, 0, int.MaxValue);
        if (model.Blocks.Count == 0) return anchor;
        var containing = model.Blocks.FirstOrDefault(block =>
            anchor >= block.SourceStart && anchor <= block.SourceStart + block.SourceLength);
        if (containing is not null) return containing.SourceStart;
        var next = model.Blocks.FirstOrDefault(block => block.SourceStart > anchor);
        return next?.SourceStart ?? model.Blocks[^1].SourceStart;
    }

    private static bool CanReuseReading(EditorTab tab) =>
        tab.Reading is not null && tab.RenderedText == tab.File.Text && tab.RenderedKind == tab.Kind
        && tab.RenderedPath == tab.File.Path && (tab.Rendered is not null || !PreviewLimits.CanRender(tab.File.Text, tab.Kind));

    private void PublishReading(EditorTab tab, ReadingModel model, RenderedReading? rendered, string text,
        string? path, TextKind kind, int anchor, bool focus)
    {
        tab.SourceAnchor = anchor;
        tab.Reading = model;
        tab.Rendered = rendered;
        tab.RenderedText = text;
        tab.RenderedPath = path;
        tab.RenderedKind = kind;
        tab.IsReading = true;
        if (rendered is not null)
        {
            tab.Editor.IsReadOnly = false;
            tab.Preview.Document = rendered.Document;
            UpdateReadingWidth(tab);
            var pointer = rendered.PointerAt(model.SourceToDisplay(tab.SourceAnchor));
            tab.Preview.Selection.Select(pointer, pointer);
            if (CurrentTab == tab)
            {
                DocumentHost.Content = tab.Preview;
                if (focus) FocusLoadedReading(tab);
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

    private void FocusLoadedReading(EditorTab tab)
    {
        tab.Preview.Focus();
        if (tab.Preview.IsLoaded) return;
        // A newly attached RichTextBox cannot take keyboard focus until its
        // template is loaded. Its selection layer also needs that first focus.
        var expectedFocus = Keyboard.FocusedElement;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (CurrentTab == tab && tab.IsReading && ReferenceEquals(DocumentHost.Content, tab.Preview)
                && (Keyboard.FocusedElement is null || ReferenceEquals(Keyboard.FocusedElement, expectedFocus)))
                tab.Preview.Focus();
        });
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
        if (followingOutline) return;
        if (OutlineList.SelectedItem is not ReadingHeading heading || CurrentTab is not { } tab) return;
        RememberReadingPosition(tab);
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
        tab.Rendered.UpdatePresentation(tab.Preview, settings.FontSize + 1, settings.ReadingWidth);
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
        var names = new[] { "WindowBrush", "PanelBrush", "EditorBrush", "ReadingBrush", "ReadingPanelBrush",
            "TextBrush", "MutedBrush", "BorderBrush", "AccentBrush", "SelectionBrush", "HoverBrush" };
        var colors = dark
            ? new[] { "#191B1F", "#23262B", "#2A2825", "#2A2825", "#23262B", "#E8E2D8", "#BEB8AE", "#4B4D52", "#2F6FDB", "#355A91", "#34383F" }
            : new[] { "#F6F4EF", "#EFEAE2", "#FFFDF8", "#FFFDF8", "#EFEAE2", "#262A2F", "#6B6A66", "#D8D1C5", "#2F6FDB", "#D7E5FA", "#F3EDE4" };
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
    public static void DispatcherBringIntoView(this EditorTab tab, System.Windows.Documents.TextPointer pointer)
    {
        var expected = tab.Rendered;
        long generation = ++tab.ViewportGeneration;
        tab.Preview.Dispatcher.BeginInvoke(() =>
        {
            if (tab.IsReading && ReferenceEquals(tab.Rendered, expected) && tab.ViewportGeneration == generation
                && tab.Preview.IsLoaded) pointer.Paragraph?.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
