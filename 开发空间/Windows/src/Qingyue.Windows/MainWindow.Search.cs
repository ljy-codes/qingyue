using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private long searchGeneration;
    private IReadOnlyList<SearchMatch> matches = [];
    private string matchedText = "";
    private int matchIndex = -1;
    private long completedGeneration = -1;
    private SearchHighlightAdorner? searchHighlightAdorner;
    private FrameworkElement? searchHighlightTarget;

    private void TrackTextCommandTarget(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not TextBoxBase input || EditMenu is null) return;
        foreach (var item in EditMenu.Items.OfType<MenuItem>())
            if (item.Command is not null) item.CommandTarget = input;
    }

    private void SearchCommandCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        // Enabled text commands are handled by the input before reaching this
        // boundary. Disabled ones must not fall back to the document focus scope.
        e.CanExecute = false;
        e.Handled = true;
    }

    private void ShowSearch(bool replace)
    {
        ClearSearchHighlights();
        RememberSearchOrigin();
        if (CurrentTab is { } current) ExitStructureForEditing(current);
        if (replace && CurrentTab?.IsReading == true) ToggleReading();
        SearchPanel.Visibility = Visibility.Visible;
        ReplacePanel.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
        if (CurrentTab is { IsReading: false } tab && tab.Editor.SelectionLength is > 0 and <= 4096)
            SearchQuery.Text = tab.Editor.SelectedText;
        SearchQuery.Focus(); SearchQuery.SelectAll(); QueueSearch();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) => QueueSearch();
    private void SearchOptionChanged(object sender, RoutedEventArgs e) => QueueSearch();
    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { MoveSearch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; }
        if (e.Key == Key.Escape) { HideSearch(); e.Handled = true; }
    }

    private async void QueueSearch()
    {
        long generation = ++searchGeneration;
        matches = []; matchIndex = -1; completedGeneration = -1;
        ClearSearchHighlights();
        if (SearchPanel is null || SearchPanel.Visibility != Visibility.Visible || CurrentTab is not { } tab) return;
        if (SearchQuery.Text.Length == 0) { SearchCount.Text = "输入查找内容"; return; }
        string query = SearchQuery.Text;
        string replacement = ReplacePanel.Visibility == Visibility.Visible ? ReplacementText.Text : "";
        bool reading = tab.IsReading && tab.Rendered is not null;
        if (reading && tab.Reading is null)
        {
            SearchCount.Text = "正在准备阅读…";
            return;
        }
        string text = reading ? tab.Reading!.VisibleText : tab.File.Text;
        var options = new SearchOptions(CaseOption.IsChecked == true, RegexOption.IsChecked == true, WordOption.IsChecked == true);
        SearchCount.Text = "正在查找…";
        try
        {
            await Task.Delay(140);
            if (generation != searchGeneration) return;
            var result = await Task.Run(() => SearchEngine.Find(text, query, replacement, options));
            if (generation != searchGeneration || CurrentTab != tab || reading != (tab.IsReading && tab.Rendered is not null)) return;
            matches = result; matchedText = text; completedGeneration = generation;
            SearchCount.Text = result.Count == 0 ? "没有匹配" : $"1 / {result.Count:N0}";
            if (result.Count > 0)
            {
                int caret = reading ? tab.Rendered!.OffsetAt(tab.Preview.Selection.Start)
                    : SourceOffset(tab, tab.Editor.CaretIndex);
                matchIndex = Enumerable.Range(0, result.Count).FirstOrDefault(i => result[i].Start >= caret, 0);
                SelectMatch(tab, result[matchIndex], focus: false);
            }
            UpdateSearchHighlights(tab, result, reading);
        }
        catch (Exception ex)
        {
            if (generation == searchGeneration) SearchCount.Text = "查找失败：" + ex.Message;
        }
    }

    private void MoveSearch(int delta)
    {
        if (SearchPanel.Visibility != Visibility.Visible) { ShowSearch(false); return; }
        if (completedGeneration != searchGeneration || matches.Count == 0 || CurrentTab is not { } tab) return;
        if (matchIndex < 0)
        {
            int caret = tab.IsReading && tab.Rendered is not null
                ? tab.Rendered.OffsetAt(tab.Preview.Selection.Start) : SourceOffset(tab, tab.Editor.CaretIndex);
            matchIndex = delta > 0
                ? Enumerable.Range(0, matches.Count).FirstOrDefault(i => matches[i].Start >= caret, 0)
                : Enumerable.Range(0, matches.Count).LastOrDefault(i => matches[i].Start < caret, matches.Count - 1);
        }
        else matchIndex = (matchIndex + delta + matches.Count) % matches.Count;
        SelectMatch(tab, matches[matchIndex]);
        UpdateSearchHighlights(tab, matches, tab.IsReading && tab.Rendered is not null);
        SearchCount.Text = $"{matchIndex + 1:N0} / {matches.Count:N0}";
    }

    private static void SelectMatch(EditorTab tab, SearchMatch match, bool focus = true)
    {
        if (tab.IsReading && tab.Rendered is not null)
        {
            var start = tab.Rendered.PointerAt(match.Start);
            tab.Preview.Selection.Select(start, tab.Rendered.PointerAt(match.Start + match.Length));
            tab.DispatcherBringIntoView(start);
            if (focus) tab.Preview.Focus();
        }
        else
        {
            int start = EditorOffset(tab, match.Start), end = EditorOffset(tab, match.Start + match.Length);
            tab.Editor.Select(start, end - start); ScrollToCaret(tab);
            if (focus) tab.Editor.Focus();
        }
    }

    private bool CanReplace(out EditorTab tab)
    {
        tab = CurrentTab!;
        return tab is { IsReading: false } && completedGeneration == searchGeneration &&
            matches.Count > 0 && matchedText == tab.File.Text;
    }

    private void ReplaceOne()
    {
        if (!CanReplace(out var tab)) return;
        if (matchIndex < 0) { MoveSearch(1); return; }
        var match = matches[matchIndex];
        tab.Editor.BeginChange();
        try
        {
            SelectMatch(tab, match);
            tab.Editor.SelectedText = match.Replacement;
        }
        finally { tab.Editor.EndChange(); }
    }

    private void ReplaceAll()
    {
        if (!CanReplace(out var tab)) return;
        string replacement = SearchEngine.ReplaceAll(matchedText, matches);
        tab.Editor.BeginChange();
        try { tab.Editor.SelectAll(); tab.Editor.SelectedText = replacement; }
        finally { tab.Editor.EndChange(); }
    }

    private void UpdateSearchHighlights(EditorTab tab, IReadOnlyList<SearchMatch> result, bool reading)
    {
        if (result.Count == 0 || CurrentTab != tab)
        {
            ClearSearchHighlights();
            return;
        }
        FrameworkElement target = reading && tab.Rendered is not null ? tab.Preview : tab.Editor;
        if (!target.IsLoaded)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (CurrentTab == tab && completedGeneration == searchGeneration)
                    UpdateSearchHighlights(tab, result, reading);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        if (!ReferenceEquals(searchHighlightTarget, target))
        {
            ClearSearchHighlights();
            var layer = AdornerLayer.GetAdornerLayer(target);
            if (layer is null) return;
            searchHighlightTarget = target;
            searchHighlightAdorner = new SearchHighlightAdorner(target, match =>
                reading && tab.Rendered is not null
                    ? SearchHighlightAdorner.RichTextBoxRects(tab.Preview,
                        offset => tab.Rendered.PointerAt(offset), match.Start, match.Length)
                    : SearchHighlightAdorner.TextBoxRects(tab.Editor,
                        EditorOffset(tab, match.Start),
                        EditorOffset(tab, match.Start + match.Length) - EditorOffset(tab, match.Start)));
            layer.Add(searchHighlightAdorner);
        }
        searchHighlightAdorner?.Update(result);
    }

    private void ClearSearchHighlights()
    {
        if (searchHighlightAdorner is not null && searchHighlightTarget is not null)
        {
            var layer = AdornerLayer.GetAdornerLayer(searchHighlightTarget);
            layer?.Remove(searchHighlightAdorner);
            searchHighlightAdorner.Detach();
        }
        searchHighlightAdorner = null;
        searchHighlightTarget = null;
    }
}
