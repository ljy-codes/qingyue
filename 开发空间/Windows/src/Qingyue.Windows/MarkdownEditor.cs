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
        if (composing || tab.Kind != TextKind.Markdown || tab.IsReading || tab.Editor.IsReadOnly) return false;
        if (tab.Editor.Text.Length > DocumentSyntaxCache.MaxDocumentCharacters) return false;
        var edit = MarkdownEditing.Apply(tab.Editor.Text, tab.Editor.SelectionStart, tab.Editor.SelectionLength, action);
        if (edit is null) return false;
        Apply(edit);
        return true;
    }

    private void Apply(MarkdownEdit edit)
    {
        var editor = tab.Editor;
        editor.BeginChange();
        try
        {
            editor.Select(edit.Start, edit.Length);
            editor.SelectedText = edit.Text;
        }
        finally { editor.EndChange(); }
        int selection = Math.Clamp(edit.SelectionStart, 0, editor.Text.Length);
        editor.Select(selection, Math.Clamp(edit.SelectionLength, 0, editor.Text.Length - selection));
        editor.Focus();
    }

    private void KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || composing || tab.Kind != TextKind.Markdown) return;
        ModifierKeys modifiers = Keyboard.Modifiers;
        MarkdownEditAction? action = (e.Key, modifiers) switch
        {
            (Key.Enter, ModifierKeys.None) => MarkdownEditAction.Enter,
            (Key.Enter, ModifierKeys.Shift) => MarkdownEditAction.SoftBreak,
            (Key.Tab, ModifierKeys.None) => MarkdownEditAction.Indent,
            (Key.Tab, ModifierKeys.Shift) => MarkdownEditAction.Outdent,
            (Key.Back, ModifierKeys.None) => MarkdownEditAction.Backspace,
            (Key.B, ModifierKeys.Control) => MarkdownEditAction.Bold,
            (Key.I, ModifierKeys.Control) => MarkdownEditAction.Italic,
            (Key.K, ModifierKeys.Control) => MarkdownEditAction.Link,
            (Key.Oem3, ModifierKeys.Control) => MarkdownEditAction.InlineCode,
            (Key.Oem3, ModifierKeys.Control | ModifierKeys.Shift) => MarkdownEditAction.CodeBlock,
            (Key.D1, ModifierKeys.Control | ModifierKeys.Alt) => MarkdownEditAction.Heading1,
            (Key.D2, ModifierKeys.Control | ModifierKeys.Alt) => MarkdownEditAction.Heading2,
            (Key.D3, ModifierKeys.Control | ModifierKeys.Alt) => MarkdownEditAction.Heading3,
            (Key.Q, ModifierKeys.Control | ModifierKeys.Shift) => MarkdownEditAction.Quote,
            (Key.Space, ModifierKeys.Control | ModifierKeys.Shift) => MarkdownEditAction.ToggleTask,
            _ => null
        };
        if (action is { } command && Execute(command)) e.Handled = true;
        // A list cannot be indented without a preceding sibling. Swallow Tab there,
        // rather than inserting a literal tab that changes its Markdown meaning.
        else if (e.Key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift &&
            Markers.Any(m => tab.Editor.CaretIndex >= m.Start && tab.Editor.CaretIndex <= m.ContentStart))
            e.Handled = true;
    }
}
