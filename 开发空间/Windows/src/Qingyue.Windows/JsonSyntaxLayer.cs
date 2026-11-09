using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Qingyue.Core;

namespace Qingyue.Windows;

// A display-only layer: TextBox continues to own input, selection, IME and undo.
internal sealed class JsonSyntaxLayer : FrameworkElement
{
    private readonly EditorTab tab;
    private readonly DocumentSyntaxCache cache = new();
    private bool composing;
    private bool active;
    private bool nativeHidden;
    private bool rendering;
    public JsonSyntaxLayer(EditorTab tab)
    {
        this.tab = tab;
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        tab.Editor.TextChanged += (_, _) => Refresh();
        tab.Editor.SizeChanged += (_, _) => Refresh();
        tab.Editor.Loaded += (_, _) => Refresh();
        tab.Editor.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => InvalidateVisual()));
        System.Windows.Input.TextCompositionManager.AddPreviewTextInputStartHandler(tab.Editor,
            (_, _) => { composing = true; Refresh(); });
        System.Windows.Input.TextCompositionManager.AddPreviewTextInputHandler(tab.Editor,
            (_, _) => { composing = false; Refresh(); });
        tab.Editor.LostKeyboardFocus += (_, _) => { composing = false; Refresh(); };
    }

    public void Refresh()
    {
        active = !composing && cache.Update(tab.Editor.Text, tab.Kind);
        // Never hide native text until a complete, affordable viewport has been prepared.
        SetHighlighted(false);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (rendering)
        {
            SetHighlighted(false);
            return;
        }
        rendering = true;
        try { RenderHighlight(dc); }
        finally { rendering = false; }
    }

    private void RenderHighlight(DrawingContext dc)
    {
        var editor = tab.Editor;
        var text = editor.Text;
        var kind = tab.Kind;
        // LineCount/GetRect can force layout in an earlier TextChanged subscriber,
        // before this layer receives TextChanged. Synchronize before ANY layout query.
        active = !composing && cache.Update(text, kind);
        if (!active || !editor.IsLoaded || editor.LineCount <= 0 ||
            editor.FlowDirection != FlowDirection.LeftToRight || editor.TextAlignment != TextAlignment.Left)
        {
            SetHighlighted(false);
            return;
        }
        int first = editor.GetFirstVisibleLineIndex(), last = editor.GetLastVisibleLineIndex();
        if (first < 0 || last < first || last >= editor.LineCount ||
            last - first + 1 > DocumentSyntaxCache.MaxVisibleLines)
        {
            SetHighlighted(false);
            return;
        }
        var rows = new List<(int Start, int Length, Rect Bounds)>();
        var lengths = new List<int>();
        // Preflight every visible row before constructing FormattedText or hiding the TextBox.
        // Offsets are into Editor.Text, including CRLF; visual wraps must not reset lexing.
        for (int line = first; line <= last; line++)
        {
            int start = editor.GetCharacterIndexFromLineIndex(line);
            int length = editor.GetLineLength(line);
            if (start < 0 || start > text.Length || length < 0)
            {
                SetHighlighted(false);
                return;
            }
            length = Math.Min(length, text.Length - start);
            while (length > 0 && text[start + length - 1] is '\r' or '\n') length--;
            lengths.Add(length);
            if (!DocumentSyntaxCache.CanRenderViewport(lengths))
            {
                SetHighlighted(false);
                return;
            }
            if (length == 0) continue;
            // FormattedText tab stops are not TextBox tab stops. Keep native metrics in this case.
            if (text.AsSpan(start, length).Contains('\t'))
            {
                SetHighlighted(false);
                return;
            }
            var bounds = editor.GetRectFromCharacterIndex(start, false);
            if (bounds.IsEmpty)
            {
                SetHighlighted(false);
                return;
            }
            rows.Add((start, length, bounds));
        }
        var editorOrigin = editor.TranslatePoint(new Point(), this);
        var scroller = editor.Template?.FindName("PART_ContentHost", editor) as ScrollViewer;
        double right = editor.ActualWidth - editor.Padding.Right -
            (scroller?.ComputedVerticalScrollBarVisibility == Visibility.Visible ? SystemParameters.VerticalScrollBarWidth : 0);
        double bottom = editor.ActualHeight - (scroller?.ComputedHorizontalScrollBarVisibility == Visibility.Visible
            ? SystemParameters.HorizontalScrollBarHeight : 0);
        var typeface = new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch);
        double dpi = VisualTreeHelper.GetDpi(editor).PixelsPerDip;
        // Layout may itself reenter editing/Refresh. Discard those rows if their source
        // changed; never query a different cache with offsets from this render snapshot.
        if (composing || kind != tab.Kind || !string.Equals(text, editor.Text, StringComparison.Ordinal))
        {
            SetHighlighted(false);
            InvalidateVisual();
            return;
        }
        if (!cache.Update(text, kind))
        {
            SetHighlighted(false);
            return;
        }
        var drawings = new List<(FormattedText Text, Point Origin)>();
        foreach (var row in rows)
        {
            var formatted = new FormattedText(text.Substring(row.Start, row.Length),
                CultureInfo.CurrentUICulture, editor.FlowDirection,
                typeface, editor.FontSize, Brush("TextBrush"), dpi);
            foreach (var token in cache.TokensInRange(row.Start, row.Length))
                formatted.SetForegroundBrush(Brush(token.ResourceKey), token.Start, token.Length);
            drawings.Add((formatted, new Point(editorOrigin.X + row.Bounds.X, editorOrigin.Y + row.Bounds.Y)));
        }
        SetHighlighted(true);
        dc.PushClip(new RectangleGeometry(new Rect(
            editorOrigin.X + editor.Padding.Left, editorOrigin.Y + editor.Padding.Top,
            Math.Max(0, right - editor.Padding.Left), Math.Max(0, bottom - editor.Padding.Top))));
        foreach (var drawing in drawings) dc.DrawText(drawing.Text, drawing.Origin);
        dc.Pop();
    }

    private void SetHighlighted(bool highlighted)
    {
        if (nativeHidden == highlighted) return;
        nativeHidden = highlighted;
        if (highlighted) tab.Editor.Foreground = Brushes.Transparent;
        else tab.Editor.SetResourceReference(Control.ForegroundProperty, "TextBrush");
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.White;
}

public enum JsonTokenColor { Key, String, Value, Comment }
public readonly record struct JsonColorToken(int Start, int Length, JsonTokenColor Kind);

public static class JsonSyntax
{
    // Tolerant lexer: incomplete JSON remains editable and never invokes a parser on a keystroke.
    public static IEnumerable<JsonColorToken> Tokens(string text)
    {
        for (int i = 0; i < text.Length;)
        {
            int start = i;
            if (text[i] == '"')
            {
                i++;
                while (i < text.Length)
                {
                    char character = text[i++];
                    if (character == '\\' && i < text.Length) i++;
                    else if (character == '"') break;
                }
                int following = SkipTrivia(text, i);
                yield return new(start, i - start, following < text.Length && text[following] == ':'
                    ? JsonTokenColor.Key : JsonTokenColor.String);
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i += 2;
                while (i < text.Length && text[i] is not '\r' and not '\n') i++;
                yield return new(start, i - start, JsonTokenColor.Comment);
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')) i++;
                i = Math.Min(text.Length, i + 2);
                yield return new(start, i - start, JsonTokenColor.Comment);
            }
            else if (char.IsAsciiDigit(text[i]) || text[i] == '-' || char.IsAsciiLetter(text[i]))
            {
                i++;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '.' or '-' or '+')) i++;
                yield return new(start, i - start, JsonTokenColor.Value);
            }
            else i++;
        }
    }

    private static int SkipTrivia(string text, int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] != '/' || i + 1 == text.Length) break;
            if (text[i + 1] == '/')
            {
                i += 2;
                while (i < text.Length && text[i] is not '\r' and not '\n') i++;
            }
            else if (text[i + 1] == '*')
            {
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')) i++;
                i = Math.Min(text.Length, i + 2);
            }
            else break;
        }
        return i;
    }
}

/// <summary>A foreground range measured in UTF-16 code units, never normalized source text.</summary>
public readonly record struct SyntaxColorToken(int Start, int Length, string ResourceKey);

/// <summary>Full-document lexical state, reused across scrolling, wrapping and font changes.</summary>
public sealed class DocumentSyntaxCache
{
    public const int MaxVisibleLines = 250;
    private const int MaxDocumentCharacters = 1_000_000;
    private const int MaxLogicalLineCharacters = 16_384;
    private const int MaxTokens = 100_000;
    private string? source;
    private TextKind kind;
    private bool supported;
    public IReadOnlyList<SyntaxColorToken> Tokens { get; private set; } = Array.Empty<SyntaxColorToken>();

    public bool Update(string text, TextKind language)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (kind == language && string.Equals(source, text, StringComparison.Ordinal)) return supported;
        source = text;
        kind = language;
        supported = false;
        Tokens = Array.Empty<SyntaxColorToken>();
        if (language is not (TextKind.Json or TextKind.Jsonc or TextKind.Markdown) ||
            text.Length > MaxDocumentCharacters) return false;
        int lineLength = 0;
        foreach (char c in text)
        {
            if (c is '\r' or '\n') lineLength = 0;
            else if (++lineLength > MaxLogicalLineCharacters) return false;
        }
        IEnumerable<SyntaxColorToken> scan = language == TextKind.Markdown ? MarkdownTokens(text) :
            JsonSyntax.Tokens(text).Select(t => new SyntaxColorToken(t.Start, t.Length, t.Kind switch
            {
                JsonTokenColor.Key => "SyntaxKeyBrush",
                JsonTokenColor.String => "SyntaxStringBrush",
                JsonTokenColor.Value => "SyntaxValueBrush",
                _ => "SyntaxCommentBrush"
            }));
        var tokens = new List<SyntaxColorToken>();
        foreach (var token in scan)
        {
            if (tokens.Count == MaxTokens) return false;
            tokens.Add(token);
        }
        Tokens = tokens.AsReadOnly();
        return supported = true;
    }

    /// <summary>Intersect cached tokens with a visual row, returning offsets relative to that row.</summary>
    public IReadOnlyList<SyntaxColorToken> TokensInRange(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (source is null || start > source.Length || length > source.Length - start)
            throw new ArgumentOutOfRangeException(nameof(length));
        var result = new List<SyntaxColorToken>();
        int end = start + length, low = 0, high = Tokens.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            var token = Tokens[middle];
            if (token.Start + token.Length <= start) low = middle + 1;
            else high = middle;
        }
        for (int i = low; i < Tokens.Count && Tokens[i].Start < end; i++)
        {
            var token = Tokens[i];
            int clippedStart = Math.Max(start, token.Start);
            int clippedEnd = Math.Min(end, token.Start + token.Length);
            if (clippedEnd > clippedStart)
                result.Add(new(clippedStart - start, clippedEnd - clippedStart, token.ResourceKey));
        }
        return result;
    }

    public static bool CanRenderViewport(IReadOnlyList<int> lineLengths)
    {
        if (lineLengths.Count > MaxVisibleLines) return false;
        int total = 0;
        foreach (int length in lineLengths)
            if (length < 0 || length > 8192 || (total += length) > 32_768) return false;
        return true;
    }

    private static IEnumerable<SyntaxColorToken> MarkdownTokens(string text)
    {
        char fence = '\0';
        int fenceLength = 0;
        for (int start = 0; start < text.Length;)
        {
            int end = start;
            while (end < text.Length && text[end] is not '\r' and not '\n') end++;
            string line = text[start..end];
            int markerStart = 0;
            while (markerStart < line.Length && markerStart < 4 && line[markerStart] == ' ') markerStart++;
            int markerEnd = markerStart;
            if (markerStart < 4 && markerStart < line.Length && line[markerStart] is '`' or '~')
                while (markerEnd < line.Length && line[markerEnd] == line[markerStart]) markerEnd++;
            int markers = markerEnd - markerStart;
            if (fence != '\0')
            {
                if (line.Length > 0) yield return new(start, line.Length, "SyntaxStringBrush");
                if (markers >= fenceLength && line[markerStart] == fence &&
                    string.IsNullOrWhiteSpace(line[markerEnd..])) fence = '\0';
            }
            else if (markers >= 3 &&
                (line[markerStart] != '`' || !line.AsSpan(markerEnd).Contains('`')))
            {
                fence = line[markerStart];
                fenceLength = markers;
                yield return new(start, line.Length, "SyntaxStringBrush");
            }
            else if (line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t'))
            {
                yield return new(start, line.Length, "SyntaxStringBrush");
            }
            else
            {
                foreach (var token in MarkdownSyntax.Tokens(line))
                    yield return new(start + token.Start, token.Length, token.ResourceKey);
            }
            start = end;
            if (start < text.Length && text[start++] == '\r' && start < text.Length && text[start] == '\n') start++;
        }
    }
}
