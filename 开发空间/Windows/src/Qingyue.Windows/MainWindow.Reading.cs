using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private readonly Dictionary<EditorTab, Task> restoringReadingTabs = [];

    public void ToggleReading()
    {
        if (CurrentTab is not { } tab) return;
        if (tab.IsReading)
        {
            CaptureReadingViewport(tab);
            tab.ViewportGeneration++;
            int offset = tab.ViewportAnchor ?? CurrentSourceAnchor(tab);
            tab.SourceAnchor = offset;
            tab.IsReading = false;
            tab.Editor.IsReadOnly = false;
            tab.ReleaseReadingResources();
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
            EnterReading(tab, anchor, snapToBlockStart: false);
            RestoreReadingViewport(tab, anchor, tab.ViewportDelta);
        }
        RefreshCurrent();
    }

    private Task EnsureRestoredReadingAsync(EditorTab? tab)
    {
        if (restoringWorkspace || tab is null || !tabs.Contains(tab) || !tab.IsReading
            || tab.Rendered is not null || approvedShutdown || exitInProgress)
            return Task.CompletedTask;
        if (restoringReadingTabs.TryGetValue(tab, out var existing)) return existing;
        var task = RestoreReadingModeAsync(tab);
        restoringReadingTabs[tab] = task;
        if (task.IsCompleted) restoringReadingTabs.Remove(tab);
        return task;
    }

    private void EnsureRestoredReading(EditorTab? tab) => _ = EnsureRestoredReadingAsync(tab);

    private async Task RestoreReadingModeAsync(EditorTab tab)
    {
        int sourceAnchor = Math.Clamp(tab.SourceAnchor, 0, tab.File.Text.Length);
        try
        {
            if (!await EnterReadingAsync(tab, sourceAnchor, focus: false, snapToBlockStart: false)
                || !tabs.Contains(tab) || !tab.IsReading)
                return;
            if (CurrentTab != tab)
            {
                tab.ReleaseReadingResources();
                return;
            }
            // EnterReadingAsync preserves the exact source anchor. The viewport
            // anchor remains the block-level position persisted by the session.
            RestoreReadingViewport(tab, tab.ViewportAnchor ?? sourceAnchor, tab.ViewportDelta);
            if (CurrentTab == tab) RefreshCurrent();
        }
        catch (Exception ex)
        {
            if (CurrentTab == tab) StatusText.Text = "恢复阅读视图失败：" + ex.Message;
        }
        finally
        {
            restoringReadingTabs.Remove(tab);
        }
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
                ? RenderedReading.Markdown(model, tab.File.Text, tab.File.Path,
                    CurrentReadingPresentation(), settings.ReadingWidth)
                : new RenderedReading(model, sourcePath: tab.File.Path,
                    presentation: CurrentReadingPresentation())
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
            var preview = tab.EnsurePreview(settings.FontSize + 1);
            tab.Editor.IsReadOnly = false;
            preview.Document = rendered.Document;
            ReadingClipboard.RefreshContextMenus(preview);
            UpdateReadingWidth(tab);
            var pointer = rendered.PointerAt(model.SourceToDisplay(tab.SourceAnchor));
            preview.Selection.Select(pointer, pointer);
            if (CurrentTab == tab)
            {
                DocumentHost.Content = preview;
                if (focus) FocusLoadedReading(tab);
                tab.DispatcherBringIntoView(pointer);
            }
        }
        else
        {
            tab.Rendered = null;
            tab.ReleasePreviewDocument();
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
            if (CurrentTab == tab && tab.IsReading && modalDepth == 0 && !SearchPanel.IsKeyboardFocusWithin
                && ReferenceEquals(DocumentHost.Content, tab.Preview)
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
        if (!TryGetOutlineLocation(OutlineList.SelectedItem, out int sourceStart, out int displayStart)
            || CurrentTab is not { } tab) return;
        RememberReadingPosition(tab);
        if (tab.IsReading && tab.Rendered is not null)
        {
            var pointer = tab.Rendered.PointerAt(displayStart);
            tab.Preview.Selection.Select(pointer, pointer);
            tab.DispatcherBringIntoView(pointer);
            tab.Preview.Focus();
        }
        else
        {
            ExitStructureForEditing(tab);
            tab.Editor.CaretIndex = EditorOffset(tab, sourceStart);
            ScrollToCaret(tab);
            tab.Editor.Focus();
        }
    }

    private static IReadOnlyList<object> BuildOutlineItems(EditorTab tab, ReadingModel model) =>
        tab.Kind == TextKind.Markdown
            ? MarkdownOutline.Build(tab.File.Text, model).Cast<object>().ToArray()
            : model.Headings.Cast<object>().ToArray();

    private static bool TryGetOutlineLocation(object? item, out int sourceStart, out int displayStart)
    {
        switch (item)
        {
            case MarkdownOutlineEntry markdown:
                sourceStart = markdown.SourceStart;
                displayStart = markdown.DisplayStart;
                return true;
            case ReadingHeading heading:
                sourceStart = heading.SourceStart;
                displayStart = heading.DisplayStart;
                return true;
            default:
                sourceStart = displayStart = 0;
                return false;
        }
    }

    private void UpdateReadingWidth(EditorTab tab)
    {
        if (tab.Rendered is null) return;
        tab.Rendered.UpdatePresentation(tab.Preview, CurrentReadingPresentation(), settings.ReadingWidth);
    }

    private ReadingPresentation CurrentReadingPresentation() =>
        settings.ToPresentation() with { FontSize = settings.FontSize + 1 };

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
        FocusButton.Content = focusMode ? "退出专注" : "专注";
        ApplySidebar();
        if (CurrentTab is { } tab) RefreshPresentation(tab);
    }

    public void ApplyTheme(bool dark) =>
        ApplyTheme(ReaderTheme.Parse(settings.Theme, settings.Dark), dark);

    public void ToggleTheme()
    {
        bool dark = !settings.EffectiveDark;
        settings.FollowSystem = false;
        ApplyTheme(ReaderTheme.Parse(settings.Theme, settings.Dark), dark);
    }

    public void ApplyTheme(ReaderThemeKind theme)
    {
        bool dark = theme switch
        {
            ReaderThemeKind.Dark => true,
            ReaderThemeKind.Light or ReaderThemeKind.WarmPaper => false,
            _ => settings.Dark
        };
        ApplyTheme(ReaderTheme.Parse(theme.ToString(), dark), dark);
    }

    private void ApplyTheme(ReaderThemeKind theme, bool dark)
    {
        settings.Theme = ReaderTheme.NormalizeName(theme.ToString(), dark);
        settings.Dark = dark;
        var palette = ReaderTheme.Apply(Resources, theme, dark);
        ApplyNativeTitleBarTheme(palette);
        foreach (var tab in tabs) RefreshSyntax(tab);
        SchedulePersist();
    }

    public void ApplyAppIconTheme(AppIconThemeKind theme)
    {
        settings.IconTheme = AppIconTheme.NormalizeName(theme.ToString());
        Icon = AppIconTheme.Load(theme);
    }

    private void ApplyAppIconTheme()
    {
        ApplyAppIconTheme(AppIconTheme.Parse(settings.IconTheme));
    }

    private void ShowReadingAppearance()
    {
        if (readingAppearanceWindow is { Window.IsLoaded: true } existing)
        {
            existing.SyncFromSettings(settings);
            existing.Window.Activate();
            return;
        }

        readingAppearanceWindow = ReadingAppearanceDialog.Create(this, settings, selection =>
        {
            selection.ApplyTo(settings);
            ApplyAppIconTheme();
            ApplyTheme(ReaderTheme.Parse(settings.Theme, settings.Dark));
            ApplyEditorSettings();
        });
        readingAppearanceWindow.Window.Closed += (_, _) => readingAppearanceWindow = null;
        readingAppearanceWindow.Window.Show();
        readingAppearanceWindow.Window.Activate();
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
