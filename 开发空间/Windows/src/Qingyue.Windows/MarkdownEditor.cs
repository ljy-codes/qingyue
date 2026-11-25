using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Qingyue.Core;

namespace Qingyue.Windows;

/// <summary>Owns structured commands, while the native TextBox owns typing, selection and IME.</summary>
internal sealed class MarkdownEditor
{
    private readonly EditorTab tab;
    private bool composing;
    private string? markerText;
    private IReadOnlyList<MarkdownListMarker> markers = [];
    private readonly Dictionary<int, int[]> guideColumns = [];
    internal IReadOnlyList<int> GuideColumns(MarkdownListMarker marker) =>
        guideColumns.TryGetValue(marker.Start, out var columns) ? columns : [];
    internal bool IsComposing => composing;
    internal bool CanEdit => !composing && tab.Kind == TextKind.Markdown && !tab.IsReading &&
        !tab.Editor.IsReadOnly && tab.Editor.Text.Length <= DocumentSyntaxCache.MaxDocumentCharacters;
    public IReadOnlyList<MarkdownListMarker> Markers
    {
        get
        {
            string text = tab.Editor.Text;
            if (!ReferenceEquals(text, markerText) && !string.Equals(text, markerText, StringComparison.Ordinal))
            {
                markerText = text;
                markers = text.Length <= DocumentSyntaxCache.MaxDocumentCharacters
                    ? MarkdownEditing.ListMarkers(text) : [];
                guideColumns.Clear();
                var parents = new List<MarkdownListMarker>();
                foreach (var marker in markers)
                {
                    while (parents.Count > 0 && parents[^1].Level >= marker.Level) parents.RemoveAt(parents.Count - 1);
                    if (marker.Level > 0)
                        guideColumns[marker.Start] = parents.Take(16).Select(p => p.ContentIndent).Distinct().ToArray();
                    parents.Add(marker);
                }
            }
            return markers;
        }
    }

    public MarkdownEditor(EditorTab tab)
    {
        this.tab = tab;
        tab.Editor.PreviewKeyDown += KeyDown;
        TextCompositionManager.AddPreviewTextInputStartHandler(tab.Editor, (_, _) => composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(tab.Editor, (_, _) => composing = false);
        tab.Editor.LostKeyboardFocus += (_, _) => composing = false;
        tab.Editor.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 1 || tab.Kind != TextKind.Markdown || composing) return;
            var point = e.GetPosition(tab.Editor);
            int offset = tab.Editor.GetCharacterIndexFromPoint(point, false);
            var marker = Markers.FirstOrDefault(m => m.Task && offset >= m.Start && offset < m.ContentStart);
            if (marker is null) return;
            var rect = tab.Editor.GetRectFromCharacterIndex(marker.Start);
            if (rect.IsEmpty || point.Y < rect.Top || point.Y > rect.Bottom) return;
            int start = tab.Editor.SelectionStart, length = tab.Editor.SelectionLength;
            var edit = MarkdownEditing.Apply(tab.Editor.Text, marker.ContentStart, 0, MarkdownEditAction.ToggleTask);
            if (edit is null) return;
            Apply(edit with { SelectionStart = start, SelectionLength = length });
            e.Handled = true;
        };
    }

    public bool Execute(MarkdownEditAction action)
    {
        if (!CanEdit) return false;
        var edit = MarkdownEditing.Apply(tab.Editor.Text, tab.Editor.SelectionStart, tab.Editor.SelectionLength, action);
        if (edit is null) return false;
        Apply(edit);
        return true;
    }

    public bool ExecuteTable(MarkdownTableAction action)
    {
        if (!CanEdit) return false;
        var edit = MarkdownTableEditing.Apply(tab.Editor.Text, tab.Editor.SelectionStart, tab.Editor.SelectionLength, action);
        if (edit is null) return false;
        Apply(edit);
        return true;
    }

    public bool PasteTable(string tsv)
    {
        if (!CanEdit) return false;
        var edit = MarkdownTableEditing.FromTsv(tab.Editor.Text, tab.Editor.SelectionStart, tab.Editor.SelectionLength, tsv);
        if (edit is null) return false;
        Apply(edit);
        return true;
    }

    internal bool InsertImage(string markdown)
    {
        if (!CanEdit) return false;
        int start = tab.Editor.SelectionStart;
        Apply(new MarkdownEdit(start, tab.Editor.SelectionLength, markdown, start + markdown.Length, 0));
        return true;
    }

    private void Apply(MarkdownEdit edit)
    {
        var editor = tab.Editor;
        if (edit.Length != 0 || edit.Text.Length != 0)
        {
            editor.BeginChange();
            try
            {
                editor.Select(edit.Start, edit.Length);
                editor.SelectedText = edit.Text;
            }
            finally { editor.EndChange(); }
        }
        int selection = Math.Clamp(edit.SelectionStart, 0, editor.Text.Length);
        editor.Select(selection, Math.Clamp(edit.SelectionLength, 0, editor.Text.Length - selection));
        editor.Focus();
    }

    private void KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || composing || tab.Kind != TextKind.Markdown) return;
        if (HandleKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>
    /// Applies only the source-preserving editing gestures that macOS also
    /// implements. Everything else remains a native TextBox input so app
    /// shortcuts such as Ctrl+B are never swallowed by a focused Markdown view.
    /// </summary>
    internal bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (composing || tab.Kind != TextKind.Markdown) return false;
        MarkdownTableAction? tableAction = (key, modifiers) switch
        {
            (Key.Tab, ModifierKeys.None) => MarkdownTableAction.NextCell,
            (Key.Tab, ModifierKeys.Shift) => MarkdownTableAction.PreviousCell,
            (Key.Enter, ModifierKeys.None) => MarkdownTableAction.InsertRow,
            _ => null
        };
        if (tableAction is { } tableCommand && ExecuteTable(tableCommand)) return true;
        MarkdownEditAction? action = (key, modifiers) switch
        {
            (Key.Enter, ModifierKeys.None) => MarkdownEditAction.Enter,
            (Key.Enter, ModifierKeys.Shift) => MarkdownEditAction.SoftBreak,
            (Key.Tab, ModifierKeys.None) => MarkdownEditAction.Indent,
            (Key.Tab, ModifierKeys.Shift) => MarkdownEditAction.Outdent,
            _ => null
        };
        return action is { } command && Execute(command);
    }
}
