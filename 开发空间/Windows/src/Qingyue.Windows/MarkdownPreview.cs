using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Qingyue.Core;
using DocumentList = System.Windows.Documents.List;

namespace Qingyue.Windows;

/// <summary>
/// An independent, read-only Markdown pane. Creates only local document/control state:
/// no navigation, remote media, preferences, source edits, timers, or application events.
/// Construct and Update on the WPF UI thread; the host owns update debouncing.
/// Source/display navigation continues to use the separate, unmodified RenderedReading map.
/// </summary>
public sealed class MarkdownPreview : RichTextBox
{
    private double readingWidth;
    private readonly string? sourcePath;
    private readonly bool includeDiagramSource;

    public MarkdownPreview(string text, double fontSize, double readingWidth, string? sourcePath = null,
        bool includeDiagramSource = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!double.IsFinite(fontSize) || fontSize <= 0) throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!double.IsFinite(readingWidth) || readingWidth <= 0) throw new ArgumentOutOfRangeException(nameof(readingWidth));
        this.readingWidth = readingWidth;
        this.sourcePath = sourcePath;
        this.includeDiagramSource = includeDiagramSource;
        FontSize = fontSize;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        IsReadOnly = true;
        IsUndoEnabled = false;
        IsDocumentEnabled = false;
        BorderThickness = new Thickness(0);
        // The RichTextBox template also applies control Padding outside PagePadding.
        // Keep the gutter in the document once, including after WPF's load transfer.
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(BackgroundProperty, "EditorBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(this, "Markdown 格式预览");
        SizeChanged += (_, _) => UpdatePadding();
        Loaded += (_, _) => UpdatePadding();
        Update(text);
    }

    /// <summary>
    /// Synchronously replaces the preview; recoverable parsing/formatting failures show the
    /// original text. Null arguments and calls outside the UI thread remain contract errors.
    /// </summary>
    public void Update(string text) => Update(text, BuildDocument);

    /// <summary>
    /// Reflow the existing document and synchronize local type styles without
    /// rebuilding text, images or selection. Later Update(text) uses these settings.
    /// </summary>
    public void UpdatePresentation(double fontSize, double readingWidth)
    {
        VerifyAccess();
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!double.IsFinite(readingWidth) || readingWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(readingWidth));
        this.readingWidth = readingWidth;
        RenderedReading.ApplyPresentation(this, fontSize, readingWidth);
    }

    // Keep failure handling at the document-building boundary; the delegate also lets the
    // independent harness reproduce parser timeouts without mutating shared parser state.
    internal void Update(string text, Func<string, FlowDocument> buildDocument)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(buildDocument);
        VerifyAccess();
        FlowDocument document;
        try { document = buildDocument(text); }
        catch (Exception error) when (error is RegexMatchTimeoutException or ArgumentException or
            FormatException or OverflowException or InvalidOperationException or IndexOutOfRangeException)
        {
            // Keep the current edit readable even if formatting fails. Image destinations,
            // especially long data URIs, must never become hundreds of thousands of glyphs.
            string safeText = MarkdownImages.Extract(text).FallbackText(text);
            document = new FlowDocument(new Paragraph(new Run(safeText)) { Margin = new Thickness(0) })
            {
                FontFamily = FontFamily, FontSize = FontSize, LineHeight = FontSize * 1.75,
                ColumnWidth = double.PositiveInfinity, TextAlignment = TextAlignment.Left
            };
            document.SetResourceReference(FlowDocument.ForegroundProperty, "TextBrush");
        }
        Document = document;
        IsDocumentEnabled = document.Blocks.OfType<BlockUIContainer>().Any() || HasImages(document);
        UpdatePadding();
    }

    private FlowDocument BuildDocument(string text)
    {
        // Extract destinations before Core's length fallback, so a long embedded PNG is
        // represented by a short private token, never by hundreds of thousands of text glyphs.
        var images = MarkdownImages.Extract(text);
        string projected = images.Text;
        var model = ReadingModel.Build(projected, TextKind.Markdown);
        // Navigation belongs to the host's original model, whose image labels have the
        // real alt length. Private image tokens are only used to construct the document.
        IReadOnlyDictionary<int, ReadingBlock>? sourceBlocks = null;
        if (images.References.Count > 0)
        {
            try { sourceBlocks = ReadingModel.Build(text, TextKind.Markdown).Blocks.ToDictionary(b => b.SourceStart); }
            catch (RegexMatchTimeoutException) { /* Keep the safe projected rendering. */ }
        }
        // Do not decorate the mapped reader's live Runs. This document is private to the
        // preview, and its temporary renderer/map is deliberately never exposed or reused.
        var document = new RenderedReading(model, renderMermaid: false).Document;
        document.FontSize = FontSize;
        document.LineHeight = FontSize * 1.75;
        var paragraphs = document.Blocks.Cast<Paragraph>().ToArray();
        var diagramBudget = new MermaidBlock.Budget();
        DocumentList? list = null;
        int displayStart = 0;
        for (int i = 0; i < model.Blocks.Count; i++)
        {
            var parsedBlock = model.Blocks[i];
            var block = images.OriginalRange(parsedBlock) with
            {
                Text = images.RestoreLabels(parsedBlock.Text), DisplayStart = displayStart
            };
            if (sourceBlocks?.TryGetValue(block.SourceStart, out var original) == true &&
                original.Kind == block.Kind && original.SourceLength == block.SourceLength)
                block = original;
            displayStart = block.DisplayStart + block.Text.Length + 1;
            var paragraph = paragraphs[i];
            paragraph.Tag = block;
            RenderedReading.StyleBlock(paragraph, block, FontSize);
            string source = projected.Substring(parsedBlock.SourceStart, parsedBlock.SourceLength).TrimEnd('\r', '\n').ReplaceLineEndings("\n");
            if (MermaidBlock.IsMermaid(block))
            {
                if (diagramBudget.TakeBlock(block))
                {
                    document.Blocks.InsertBefore(paragraph, new BlockUIContainer(
                        MermaidBlock.Create(block, FontSize, sourceBelow: includeDiagramSource, budget: diagramBudget))
                    {
                        Tag = block,
                        Margin = new Thickness(0, 8, 0, 18)
                    });
                    if (!includeDiagramSource) document.Blocks.Remove(paragraph);
                }
                else if (diagramBudget.LimitNotice() is { } notice)
                    document.Blocks.InsertBefore(paragraph, notice);
            }
            else if (block.Kind == ReadingBlockKind.Table)
            {
                var table = TablePreview(parsedBlock.Text, images);
                table.Tag = block;
                document.Blocks.InsertBefore(paragraph, table);
                document.Blocks.Remove(paragraph);
            }
            else if (block.Kind is not ReadingBlockKind.Code and not ReadingBlockKind.Separator)
                DecorateInline(paragraph, InlineSource(source, block.Kind), parsedBlock.Text, images);

            if (block.Kind == ReadingBlockKind.ListItem)
            {
                var marker = MarkdownSyntax.ListPattern.Match(source);
                var body = source[marker.Length..];
                bool task = TaskPattern.IsMatch(body);
                var markerText = marker.ValueSpan.TrimStart();
                bool ordered = markerText.Length > 0 && char.IsAsciiDigit(markerText[0]);
                int number = 1;
                int digitEnd = ordered ? markerText.IndexOfAny('.', ')') : -1;
                bool literalNumber = ordered && (digitEnd <= 0 ||
                    !int.TryParse(markerText[..digitEnd], NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
                    number < 1);
                // WPF StartIndex requires a positive int. Keep zero/unsupported markers
                // literal even if the parser later accepts more than its current 9 digits.
                if (literalNumber)
                {
                    var prefix = new Run(marker.Value.TrimStart());
                    if (paragraph.Inlines.FirstInline is Inline first) paragraph.Inlines.InsertBefore(first, prefix);
                    else paragraph.Inlines.Add(prefix);
                }
                var style = task || literalNumber ? TextMarkerStyle.None : ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                if (list is null || list.MarkerStyle != style || literalNumber ||
                    ordered && number != (long)list.StartIndex + list.ListItems.Count)
                {
                    list = new DocumentList
                    {
                        MarkerStyle = style, StartIndex = literalNumber ? 1 : number, MarkerOffset = 6,
                        Margin = new Thickness(24 + block.Level * 12, 0, 0, 8), Padding = new Thickness(0)
                    };
                    document.Blocks.InsertBefore(paragraph, list);
                }
                document.Blocks.Remove(paragraph);
                paragraph.Margin = new Thickness(0, 0, 0, 5);
                list.ListItems.Add(new ListItem(paragraph));
            }
            else list = null;
        }
        return document;
    }

    private void UpdatePadding() => RenderedReading.ApplyPageLayout(this, readingWidth);

    private static readonly Regex TaskPattern = new(@"^\[([ xX])\][ \t]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static string InlineSource(string source, ReadingBlockKind kind)
    {
        if (kind == ReadingBlockKind.Heading)
        {
            var heading = MarkdownSyntax.HeadingPattern.Match(source.Split('\n')[0]);
            if (!heading.Success) return source.Split('\n')[0].Trim(); // Setext.
            string title = heading.Groups[2].Value.TrimEnd();
            int end = title.Length;
            while (end > 0 && title[end - 1] == '#') end--;
            return end == 0 || end < title.Length && char.IsWhiteSpace(title[end - 1]) ? title[..end].TrimEnd() : title;
        }
        if (kind == ReadingBlockKind.Quote)
            return string.Join("\n", source.Split('\n').Select(line => MarkdownSyntax.QuotePattern.Replace(line, "", 1)));
        if (kind == ReadingBlockKind.ListItem)
        {
            string body = MarkdownSyntax.ListPattern.Replace(source, "", 1);
            var task = TaskPattern.Match(body);
            return task.Success ? (task.Groups[1].Value == " " ? "☐ " : "☑ ") + body[task.Length..] : body;
        }
        return source;
    }

    [Flags]
    private enum InlineFormat { None = 0, Code = 1, Strong = 2, Emphasis = 4, Strike = 8, Link = 16, Image = 32 }
    private readonly record struct Fragment(string Text, InlineFormat Format, string? ImageKey = null);

    private void DecorateInline(Paragraph paragraph, string source, string expected, MarkdownImages images)
    {
        var fragments = new List<Fragment>();
        ParseInline(source, fragments);
        // ReadingModel is authoritative. If its grammar changes or falls back to raw text,
        // keep its original Run rather than risk dropping, adding or moving visible text.
        if (!string.Equals(string.Concat(fragments.Select(f => f.Text)), expected, StringComparison.Ordinal)) return;
        paragraph.Inlines.Clear();
        foreach (var fragment in fragments)
        {
            if (fragment.ImageKey is { } key && images.TryGet(key, out var reference))
            {
                AppendImage(paragraph, reference, images);
                continue;
            }
            var run = new Run(fragment.Text);
            var format = fragment.Format;
            if (format.HasFlag(InlineFormat.Strong)) run.FontWeight = FontWeights.Bold;
            if (format.HasFlag(InlineFormat.Emphasis)) run.FontStyle = FontStyles.Italic;
            var decorations = new TextDecorationCollection();
            if (format.HasFlag(InlineFormat.Strike)) decorations.Add(TextDecorations.Strikethrough);
            if (format.HasFlag(InlineFormat.Link)) decorations.Add(TextDecorations.Underline);
            if (decorations.Count > 0) run.TextDecorations = decorations;
            if (format.HasFlag(InlineFormat.Image)) run.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
            if (format.HasFlag(InlineFormat.Link)) run.SetResourceReference(TextElement.ForegroundProperty, "SyntaxKeyBrush");
            if (format.HasFlag(InlineFormat.Code))
            {
                run.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
                run.SetResourceReference(TextElement.BackgroundProperty, "PanelBrush");
                run.SetResourceReference(TextElement.ForegroundProperty, "SyntaxStringBrush");
            }
            paragraph.Inlines.Add(run);
        }
    }

    private static void ParseInline(string text, List<Fragment> output, InlineFormat format = InlineFormat.None, int depth = 0)
    {
        if (depth >= 8) { output.Add(new(text, format)); return; }
        int cursor = 0;
        foreach (Match match in MarkdownSyntax.InlinePattern.Matches(text))
        {
            if (match.Index > cursor) output.Add(new(text[cursor..match.Index], format));
            cursor = match.Index + match.Length;
            if (match.Value[0] == '\\') output.Add(new(match.Value[1..], format));
            else if (match.Value.StartsWith("![", StringComparison.Ordinal))
            {
                var title = new List<Fragment>();
                ParseInline(match.Groups[1].Value, title, format | InlineFormat.Image, depth + 1);
                output.Add(new("[图片：" + string.Concat(title.Select(f => f.Text)) + "]", format | InlineFormat.Image,
                    match.Groups[1].Value));
            }
            else if (match.Value[0] == '[') ParseInline(match.Groups[2].Value, output, format | InlineFormat.Link, depth + 1);
            else if (match.Value[0] == '`') output.Add(new(match.Groups[3].Value, format | InlineFormat.Code));
            else if (MarkdownSyntax.IsIntrawordUnderscore(text, match)) output.Add(new(match.Value, format));
            else
            {
                for (int group = 4; group < match.Groups.Count; group++)
                {
                    if (!match.Groups[group].Success) continue;
                    var decoration = group <= 5 ? InlineFormat.Strong : group <= 7 ? InlineFormat.Emphasis : InlineFormat.Strike;
                    ParseInline(match.Groups[group].Value, output, format | decoration, depth + 1);
                    break;
                }
            }
        }
        if (cursor < text.Length) output.Add(new(text[cursor..], format));
    }

    private void AppendImage(Paragraph paragraph, MarkdownImageReference reference, MarkdownImages images)
    {
        if (reference.IsRemote)
        {
            var label = new Run(images.Caption(reference)) { ToolTip = "网络图片未加载" };
            label.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
            paragraph.Inlines.Add(label);
        }
        else
        {
            paragraph.Inlines.Add(new InlineUIContainer(new MarkdownImageView(reference, sourcePath, images.Budget))
            {
                BaselineAlignment = BaselineAlignment.Center
            });
            // Keep a real selectable label for the authoritative model's [图片：alt]
            // text, including table cells and list items.
            var label = new Run(images.Caption(reference));
            RenderedReading.StyleImageCaption(label, FontSize);
            label.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
            paragraph.Inlines.Add(label);
        }
    }

    private static bool HasImages(FlowDocument document) =>
        document.Blocks.Any(ContainsImages);

    private static bool ContainsImages(Block block) => block switch
    {
        Paragraph paragraph => paragraph.Inlines.Any(inline => inline is InlineUIContainer),
        DocumentList list => list.ListItems.Any(item => item.Blocks.Any(ContainsImages)),
        Table table => table.RowGroups.Any(group => group.Rows.Any(row => row.Cells.Any(cell => cell.Blocks.Any(ContainsImages)))),
        _ => false
    };

    private Table TablePreview(string text, MarkdownImages images)
    {
        var rows = text.Split('\n').Select(line => line.Split('\t')).ToArray();
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 8, 0, 18), FontSize = FontSize * .9 };
        table.SetResourceReference(Block.BorderBrushProperty, "BorderBrush");
        table.BorderThickness = new Thickness(1, 1, 0, 0);
        var weights = Enumerable.Repeat(MinTableColumnWeight, rows[0].Length).ToArray();
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (int index = 0; index < rows.Length; index++)
        {
            var row = new TableRow();
            row.SetResourceReference(TextElement.BackgroundProperty, index % 2 == 0 ? "PanelBrush" : "EditorBrush");
            if (index == 0)
            {
                row.FontWeight = FontWeights.SemiBold;
                row.SetResourceReference(TextElement.ForegroundProperty, "AccentTextBrush");
            }
            int column = 0;
            foreach (string value in rows[index])
            {
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                int cursor = 0;
                foreach (var reference in images.References)
                {
                    string marker = "[图片：" + reference.Key + "]";
                    int at = value.IndexOf(marker, cursor, StringComparison.Ordinal);
                    if (at < 0) continue;
                    paragraph.Inlines.Add(new Run(value[cursor..at]));
                    AppendImage(paragraph, reference, images);
                    cursor = at + marker.Length;
                }
                paragraph.Inlines.Add(new Run(value[cursor..]));
                var cell = new TableCell(paragraph)
                {
                    Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 0, 1, 1)
                };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "BorderBrush");
                row.Cells.Add(cell);
                if (column < weights.Length)
                    weights[column] = Math.Max(weights[column], TableCellWeight(paragraph));
                column++;
            }
            group.Rows.Add(row);
        }
        foreach (double weight in weights)
            table.Columns.Add(new TableColumn { Width = new GridLength(weight, GridUnitType.Star) });
        return table;
    }

    private const double MinTableColumnWeight = 10;
    private const double MaxTableColumnWeight = 40;

    private static double TableCellWeight(Paragraph paragraph)
    {
        // Approximate displayed width without measuring every glyph. Include room for
        // cell padding; cap the scan as well as the weight so huge paths stay cheap.
        // Star units retain narrow-screen reflow instead of imposing fixed pixel minima.
        double weight = 4;
        int scanned = 0;
        foreach (Inline inline in paragraph.Inlines)
        {
            if (inline is InlineUIContainer) return MaxTableColumnWeight;
            if (inline is not Run run) continue;
            foreach (var rune in run.Text.EnumerateRunes())
            {
                if (++scanned > 128) return Math.Max(MinTableColumnWeight, weight);
                if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or
                    UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
                weight += rune.Value >= 0x2e80 ? 2 : 1;
                if (weight >= MaxTableColumnWeight) return MaxTableColumnWeight;
            }
        }
        return Math.Max(MinTableColumnWeight, weight);
    }
}
