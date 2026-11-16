using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private bool annotationsDirty;
    private Task<AnnotationSaveResult>? annotationSaveTask;
    private AnnotationHighlightAdorner? annotationHighlightAdorner;
    private FrameworkElement? annotationHighlightTarget;

    public IReadOnlyList<DocumentAnnotation> CurrentAnnotations(EditorTab tab) =>
        annotations.For(tab.File);

    public void CaptureAnnotation()
    {
        if (CurrentTab is not { } tab || !TryGetSelectionDraft(tab, out var draft))
        {
            StatusText.Text = "请先选中要摘录的正文。";
            return;
        }

        var values = AnnotationEditorDialog.Show(this, "新增批注");
        if (values is null) return;
        try
        {
            var item = annotations.Add(tab.File, draft);
            if (!annotations.Update(item.Id, values.Note, values.Tags, values.Color))
                throw new InvalidOperationException("批注创建后无法更新编辑内容。");
            annotationsDirty = true;
            RefreshAnnotations();
            RefreshAnnotationHighlights();
            SchedulePersist();
            StatusText.Text = "批注已保存到本机。";
        }
        catch (Exception ex) { Notify("批注保存失败：\n" + ex.Message); }
    }

    private async Task ExportReadingBriefAsync(bool html)
    {
        if (CurrentTab is not { } tab)
        {
            Notify("当前没有可导出的文档。");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = html ? "导出 HTML 阅读简报" : "导出 Markdown 阅读简报",
            FileName = tab.File.Title + (html ? "-阅读简报.html" : "-阅读简报.md"),
            DefaultExt = html ? ".html" : ".md",
            Filter = html ? "HTML 阅读简报|*.html;*.htm|所有文件|*.*"
                : "Markdown 阅读简报|*.md|所有文件|*.*",
            OverwritePrompt = true
        };
        if (tab.File.Path is { } sourcePath)
            dialog.InitialDirectory = Path.GetDirectoryName(sourcePath);
        if (Modal(() => dialog.ShowDialog(this)) != true) return;

        try
        {
            var input = ReadingBriefExportService.CreateInput(
                tab.File.Title,
                tab.File.Path,
                tab.Kind,
                tab.File.Text,
                ProgressPercent(tab),
                DateTimeOffset.Now,
                annotations.For(tab.File));
            await ReadingBriefExportService.ExportAsync(
                input,
                dialog.FileName,
                html,
                tab.File.Path is { } path ? [path] : [],
                overwrite: File.Exists(dialog.FileName));
            StatusText.Text = (html ? "HTML" : "Markdown") + " 阅读简报已导出：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            Notify("阅读简报导出失败，原文没有变化：\n" + ex.Message);
        }
    }

    private bool TryGetSelectionDraft(EditorTab tab, out AnnotationDraft draft)
    {
        if (tab.IsReading && tab.Reading is not null && tab.Rendered is not null)
        {
            int start = tab.Rendered.OffsetAt(tab.Preview.Selection.Start);
            int end = tab.Rendered.OffsetAt(tab.Preview.Selection.End);
            if (end > start)
            {
                draft = AnnotationSelection.FromReading(
                    tab.File.Id, tab.File.Path, tab.Reading, start, end - start);
                return true;
            }
        }
        else if (tab.Editor.SelectionLength > 0)
        {
            draft = AnnotationSelection.FromEditor(
                tab.File.Id, tab.File.Path, tab.File.Text, tab.Positions,
                tab.Editor.SelectionStart, tab.Editor.SelectionLength);
            return draft.Quote.Length > 0;
        }

        draft = null!;
        return false;
    }

    private void EditAnnotation(DocumentAnnotation annotation)
    {
        var values = AnnotationEditorDialog.Show(this, "编辑批注",
            annotation.Note, annotation.Tags, annotation.Color);
        if (values is null) return;
        try
        {
            if (!annotations.Update(annotation.Id, values.Note, values.Tags, values.Color))
                throw new InvalidOperationException("批注已不存在。");
            annotationsDirty = true;
            RefreshAnnotations();
            RefreshAnnotationHighlights();
            SchedulePersist();
            StatusText.Text = "批注已更新。";
        }
        catch (Exception ex) { Notify("批注更新失败：\n" + ex.Message); }
    }

    private void RemoveAnnotation(DocumentAnnotation annotation)
    {
        if (!annotations.Remove(annotation.Id)) return;
        annotationsDirty = true;
        RefreshAnnotations();
        RefreshAnnotationHighlights();
        SchedulePersist();
        StatusText.Text = "批注已删除，原文没有变化。";
    }

    public sealed record AnnotationListItem(
        DocumentAnnotation Annotation,
        string QuoteText,
        string NoteText,
        string TagsText,
        string Location,
        string StateText);

    public void RefreshAnnotations()
    {
        if (AnnotationList is null) return;
        var tab = CurrentTab;
        var values = tab is null ? [] : annotations.For(tab.File)
            .Select(item => new AnnotationListItem(
                item,
                item.Quote.ReplaceLineEndings(" "),
                string.IsNullOrWhiteSpace(item.Note) ? "" : "备注：" + item.Note.ReplaceLineEndings(" "),
                item.Tags.Count == 0 ? "" : "标签：" + string.Join(" · ", item.Tags),
                $"源位置 {item.SourceStart:N0} · 第 {LineNumber(tab.File.Text, item.SourceStart)} 行",
                ReadingBriefExporter.AnchorStateText(item.AnchorState)))
            .ToArray();
        AnnotationList.ItemsSource = values;
        AnnotationTab.Header = values.Length == 0 ? "批注" : $"批注 ({values.Length})";
    }

    private void AnnotationDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AnnotationList.SelectedItem is AnnotationListItem item)
        {
            NavigateToAnnotation(item.Annotation);
            e.Handled = true;
        }
    }

    private void AnnotationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AnnotationList.SelectedItem is AnnotationListItem item)
        {
            NavigateToAnnotation(item.Annotation);
            e.Handled = true;
        }
    }

    private void EditAnnotationClick(object sender, RoutedEventArgs e)
    {
        if (ContextAnnotation(sender) is { } item) EditAnnotation(item.Annotation);
        e.Handled = true;
    }

    private void RemoveAnnotationClick(object sender, RoutedEventArgs e)
    {
        if (ContextAnnotation(sender) is { } item) RemoveAnnotation(item.Annotation);
        e.Handled = true;
    }

    private static AnnotationListItem? ContextAnnotation(object sender) =>
        sender is MenuItem { Parent: ContextMenu menu }
        && menu.PlacementTarget is FrameworkElement target
        && target.DataContext is AnnotationListItem item ? item : null;

    private async void NavigateToAnnotation(DocumentAnnotation annotation)
    {
        var tab = tabs.FirstOrDefault(candidate =>
            annotations.For(candidate.File).Any(item => item.Id == annotation.Id));
        if (tab is null && annotation.Path is { } path && File.Exists(path))
        {
            await OpenPathsAsync([path]);
            tab = tabs.FirstOrDefault(candidate =>
                annotations.For(candidate.File).Any(item => item.Id == annotation.Id));
        }
        if (tab is null) return;

        if (CurrentTab is { } current && current != tab) RememberReadingPosition(current);
        SelectTab(tab);
        int sourceStart = Math.Clamp(annotation.SourceStart, 0, tab.File.Text.Length);
        int sourceEnd = Math.Clamp(annotation.SourceEnd, sourceStart, tab.File.Text.Length);
        if (tab.Kind == TextKind.Markdown && !tab.IsReading)
        {
            await EnterReadingAsync(tab, sourceStart, focus: false, snapToBlockStart: false);
            RefreshCurrent();
        }

        if (tab.IsReading && tab.Reading is not null && tab.Rendered is not null)
        {
            int displayStart;
            int displayLength;
            if (!AnnotationHighlightAdorner.TryGetReadingRange(tab.Reading, annotation,
                    out displayStart, out displayLength))
            {
                displayStart = tab.Reading.SourceToDisplay(sourceStart);
                displayLength = Math.Max(0, tab.Reading.SourceToDisplay(sourceEnd) - displayStart);
            }
            var first = tab.Rendered.PointerAt(displayStart);
            var last = tab.Rendered.PointerAt(displayStart + displayLength);
            tab.Preview.Selection.Select(first, last);
            tab.DispatcherBringIntoView(first);
            tab.Preview.Focus();
        }
        else
        {
            int start = EditorOffset(tab, sourceStart);
            int end = EditorOffset(tab, sourceEnd);
            tab.Editor.Select(start, Math.Max(0, end - start));
            ScrollToCaret(tab);
            tab.Editor.Focus();
        }
        RefreshAnnotationHighlights();
        SchedulePersist();
    }

    private void RefreshAnnotationHighlights()
    {
        if (CurrentTab is not { } tab)
        {
            ClearAnnotationHighlights();
            return;
        }
        FrameworkElement target = tab.IsReading && tab.Rendered is not null ? tab.Preview : tab.Editor;
        if (!target.IsLoaded)
        {
            Dispatcher.BeginInvoke(RefreshAnnotationHighlights,
                System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer is null) return;
        if (!ReferenceEquals(annotationHighlightTarget, target))
        {
            ClearAnnotationHighlights();
            annotationHighlightTarget = target;
            annotationHighlightAdorner = new AnnotationHighlightAdorner(target,
                item => tab.IsReading && tab.Reading is not null && tab.Rendered is not null
                    ? AnnotationHighlightAdorner.RichTextBoxRects(
                        tab.Preview, tab.Reading, tab.Rendered, item)
                    : AnnotationHighlightAdorner.TextBoxRects(tab.Editor, tab.Positions, item));
            layer.Add(annotationHighlightAdorner);
        }
        annotationHighlightAdorner?.Update(annotations.For(tab.File));
    }

    private void ClearAnnotationHighlights()
    {
        if (annotationHighlightAdorner is not null && annotationHighlightTarget is not null)
        {
            AdornerLayer.GetAdornerLayer(annotationHighlightTarget)?.Remove(annotationHighlightAdorner);
            annotationHighlightAdorner.Detach();
        }
        annotationHighlightAdorner = null;
        annotationHighlightTarget = null;
    }

    private void RepairAnnotationsAfterTextChange(
        EditorTab tab, string oldText, string newText, bool refresh = true)
    {
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) return;
        var current = annotations.For(tab.File);
        if (current.Count > 0)
        {
            var repaired = AnnotationAnchorResolver.Repair(
                oldText, newText, tab.Kind, current, DateTimeOffset.UtcNow);
            var repairs = repaired.Where((item, index) =>
            {
                var previous = current[index];
                return item.SourceStart != previous.SourceStart
                    || item.SourceEnd != previous.SourceEnd
                    || item.AnchorState != previous.AnchorState;
            }).Select(item => new AnnotationAnchorRepair(
                item.Id, item.SourceStart, item.SourceEnd, item.AnchorState)).ToArray();
            if (repairs.Length > 0)
            {
                annotations.ApplyAnchorRepairs(repairs, DateTimeOffset.UtcNow);
                annotationsDirty = true;
            }
        }
        if (refresh && CurrentTab == tab)
        {
            RefreshAnnotations();
            RefreshAnnotationHighlights();
            SchedulePersist();
        }
    }

    private async Task<AnnotationSaveResult> SaveAnnotationsAsync()
    {
        if (!annotationsDirty)
            return new AnnotationSaveResult(DateTimeOffset.UtcNow, []);
        if (annotationSaveTask is not null)
        {
            await annotationSaveTask;
            return new AnnotationSaveResult(DateTimeOffset.UtcNow, []);
        }
        var save = annotations.SaveAsync();
        annotationSaveTask = save;
        try
        {
            var result = await save;
            if (result.Errors.Count == 0) annotationsDirty = false;
            return result;
        }
        finally { annotationSaveTask = null; }
    }

    private static int LineNumber(string text, int sourceOffset)
    {
        int line = 1;
        for (int i = 0; i < Math.Clamp(sourceOffset, 0, text.Length); i++)
            if (text[i] == '\n') line++;
        return line;
    }
}

public static class AnnotationSelection
{
    public static AnnotationDraft FromReading(
        Guid documentId,
        string? sourcePath,
        ReadingModel model,
        int displayStart,
        int displayLength)
    {
        ArgumentNullException.ThrowIfNull(model);
        int start = Math.Clamp(displayStart, 0, model.VisibleText.Length);
        int end = Math.Clamp(start + Math.Max(0, displayLength), start, model.VisibleText.Length);
        string quote = model.VisibleText[start..end];
        int beforeStart = Math.Max(0, start - 160);
        int afterEnd = Math.Min(model.VisibleText.Length, end + 160);
        int sourceStart = model.DisplayToSource(start);
        int sourceEnd = Math.Max(sourceStart, model.DisplayToSource(end));
        return new AnnotationDraft(
            documentId, sourcePath, sourceStart, sourceEnd, quote,
            model.VisibleText[beforeStart..start],
            model.VisibleText[end..afterEnd]);
    }

    public static AnnotationDraft FromEditor(
        Guid documentId,
        string? sourcePath,
        string sourceText,
        TextPositionIndex positions,
        int editorStart,
        int editorLength)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(positions);
        int start = positions.ToSource(editorStart);
        int end = Math.Clamp(positions.ToSource(editorStart + Math.Max(0, editorLength)),
            start, sourceText.Length);
        string quote = sourceText[start..end];
        int beforeStart = Math.Max(0, start - 160);
        int afterEnd = Math.Min(sourceText.Length, end + 160);
        return new AnnotationDraft(
            documentId, sourcePath, start, end, quote,
            sourceText[beforeStart..start], sourceText[end..afterEnd]);
    }
}
