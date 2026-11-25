using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Qingyue.Core;

namespace Qingyue.Windows;

public sealed partial class RenderedReading
{
    private readonly List<(int Offset, Run Run)> runs = [];
    public FlowDocument Document { get; } = new()
    {
        PagePadding = new Thickness(36, 28, 36, 60), ColumnWidth = double.PositiveInfinity,
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"), FontSize = 16,
        LineHeight = ReadingPresentation.Default.LineHeightPixels, TextAlignment = TextAlignment.Left
    };

    public RenderedReading(ReadingModel model, bool renderMermaid = true, string? sourcePath = null,
        string? sourceText = null, ReadingPresentation? presentation = null)
    {
        var normalized = (presentation ?? ReadingPresentation.Default).Normalize();
        Document.FontFamily = new FontFamily(normalized.FontFamily);
        Document.FontSize = normalized.FontSize;
        Document.LineHeight = normalized.LineHeightPixels;
        Document.SetResourceReference(FlowDocument.ForegroundProperty, "TextBrush");
        var diagramBudget = new MermaidBlock.Budget();
        // Core intentionally exposes only visible image labels. The host can supply the
        // original Markdown without changing the model or its selectable mapping Runs.
        var images = sourceText is null ? null : MarkdownImages.Extract(sourceText);
        foreach (var block in model.Blocks)
        {
            var run = new Run(block.Text);
            runs.Add((block.DisplayStart, run));
            var paragraph = new Paragraph(run) { Tag = block, Margin = new Thickness(0, 0, 0, 13) };
            StyleBlock(paragraph, block, normalized);
            if (renderMermaid && MermaidBlock.IsMermaid(block))
            {
                if (diagramBudget.TakeBlock(block))
                    Document.Blocks.Add(new BlockUIContainer(MermaidBlock.Create(block, Document.FontSize,
                        sourceBelow: true, budget: diagramBudget))
                    {
                        Tag = block, Margin = new Thickness(0, 8, 0, 4)
                    });
                else if (diagramBudget.LimitNotice() is { } notice) Document.Blocks.Add(notice);
            }
            if (images is not null && block.Kind is not ReadingBlockKind.Code and not ReadingBlockKind.Metadata)
                foreach (var reference in images.InSourceRange(block.SourceStart, block.SourceLength))
                    if (!reference.IsRemote)
                    {
                        if (MarkdownImageView.Create(reference, sourcePath, images.Budget) is { } view)
                            Document.Blocks.Add(new BlockUIContainer(view)
                            {
                                Tag = block, Margin = new Thickness(0, 8, 0, 12)
                            });
                        else if (images.Budget.TakeLimitNotice())
                            Document.Blocks.Add(new BlockUIContainer(new TextBlock
                            {
                                Text = "图片未显示：本篇图片超过 32 张", FontSize = 13
                            }) { Tag = block, Margin = new Thickness(0, 8, 0, 12) });
                    }
            Document.Blocks.Add(paragraph);
        }
        if (runs.Count == 0)
        {
            var run = new Run("");
            runs.Add((0, run));
            Document.Blocks.Add(new Paragraph(run));
        }
    }

    // Style only: the single Run and its text must stay intact for PointerAt/OffsetAt.
    internal static void StyleBlock(Paragraph paragraph, ReadingBlock block, double fontSize) =>
        StyleBlock(paragraph, block, new ReadingPresentation(
            "Microsoft YaHei UI", fontSize, ReadingPresentation.Default.LineSpacing, 13));

    internal static void StyleBlock(Paragraph paragraph, ReadingBlock block,
        ReadingPresentation presentation)
    {
        presentation = presentation.Normalize();
        double fontSize = presentation.FontSize;
        paragraph.Margin = new Thickness(0, 0, 0, presentation.ParagraphSpacing);
        switch (block.Kind)
        {
            case ReadingBlockKind.Heading:
                int level = Math.Clamp(block.Level, 1, 6);
                paragraph.FontSize = fontSize * (level switch { 1 => 2, 2 => 1.6, 3 => 1.35, 4 => 1.2, 5 => 1.1, _ => 1 });
                paragraph.FontWeight = level == 1 ? FontWeights.Bold : level == 2 ? FontWeights.SemiBold : FontWeights.Medium;
                paragraph.SetResourceReference(Paragraph.ForegroundProperty,
                    level == 1 ? "AccentTextBrush" : level == 2 ? "SyntaxKeyBrush" : "TextBrush");
                paragraph.Margin = new Thickness(0, fontSize * (level switch { 1 => 1.75, 2 => 1.5, _ => 1.25 }), 0, fontSize * .75);
                paragraph.LineHeight = presentation.LineHeightFor(paragraph.FontSize);
                paragraph.KeepWithNext = true;
                break;
            case ReadingBlockKind.Code:
                paragraph.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
                paragraph.FontSize = fontSize * .9;
                paragraph.LineHeight = presentation.LineHeightFor(paragraph.FontSize);
                paragraph.Padding = new Thickness(16, 12, 16, 12);
                paragraph.Margin = new Thickness(0, 8, 0, 18);
                paragraph.BorderThickness = new Thickness(1);
                paragraph.SetResourceReference(Paragraph.BorderBrushProperty, "BorderBrush");
                paragraph.SetResourceReference(Paragraph.BackgroundProperty, "PanelBrush");
                break;
            case ReadingBlockKind.Metadata:
                paragraph.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
                paragraph.FontSize = fontSize * .9;
                paragraph.LineHeight = presentation.LineHeightFor(paragraph.FontSize);
                paragraph.SetResourceReference(Paragraph.ForegroundProperty, "MutedBrush");
                break;
            case ReadingBlockKind.Quote:
                paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                paragraph.SetResourceReference(Paragraph.BorderBrushProperty, "AccentTextBrush");
                paragraph.SetResourceReference(Paragraph.ForegroundProperty, "MutedBrush");
                paragraph.SetResourceReference(Paragraph.BackgroundProperty, "PanelBrush");
                paragraph.Padding = new Thickness(16, 8, 12, 8);
                paragraph.Margin = new Thickness(0, 8, 0, 16);
                break;
            case ReadingBlockKind.ListItem:
                paragraph.Margin = new Thickness(18 + block.Level * 12, 0, 0, 8);
                break;
            case ReadingBlockKind.Table:
                paragraph.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
                paragraph.FontSize = fontSize * .9;
                paragraph.SetResourceReference(Paragraph.BackgroundProperty, "PanelBrush");
                paragraph.SetResourceReference(Paragraph.BorderBrushProperty, "BorderBrush");
                paragraph.BorderThickness = new Thickness(1);
                paragraph.Padding = new Thickness(12);
                paragraph.Margin = new Thickness(0, 8, 0, 18);
                break;
            case ReadingBlockKind.Separator:
                paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
                paragraph.SetResourceReference(Paragraph.BorderBrushProperty, "BorderBrush");
                paragraph.SetResourceReference(Paragraph.ForegroundProperty, "MutedBrush");
                paragraph.Margin = new Thickness(0, 16, 0, 20);
                break;
        }
    }

    /// <summary>
    /// Apply the reading gutter once, after Document is attached to its host.
    /// Call on the host's Loaded/SizeChanged as well as reading-width changes.
    /// This only reflows the existing document; text and navigation stay intact.
    /// </summary>
    public void UpdateLayout(RichTextBox host, double readingWidth)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!ReferenceEquals(host.Document, Document))
            throw new ArgumentException("The host must contain this reading document.", nameof(host));
        ApplyPageLayout(host, readingWidth);
    }

    /// <summary>
    /// Update typography and the reading gutter in place. The host must already
    /// contain Document. Selection pointers, mapping and decoded images stay valid.
    /// </summary>
    public void UpdatePresentation(RichTextBox host, double fontSize, double readingWidth)
        => UpdatePresentation(host, new ReadingPresentation(
            "Microsoft YaHei UI", fontSize, ReadingPresentation.Default.LineSpacing, 13), readingWidth);

    public void UpdatePresentation(RichTextBox host, ReadingPresentation presentation, double readingWidth)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!ReferenceEquals(host.Document, Document))
            throw new ArgumentException("The host must contain this reading document.", nameof(host));
        ApplyPresentation(host, presentation, readingWidth);
    }

    internal static void ApplyPresentation(RichTextBox host, double fontSize, double readingWidth)
        => ApplyPresentation(host, new ReadingPresentation(
            "Microsoft YaHei UI", fontSize, ReadingPresentation.Default.LineSpacing, 13), readingWidth);

    internal static void ApplyPresentation(RichTextBox host, ReadingPresentation value, double readingWidth)
    {
        host.VerifyAccess();
        var presentation = value.Normalize();
        double fontSize = presentation.FontSize;
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!double.IsFinite(readingWidth) || readingWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(readingWidth));
        var document = host.Document;
        double oldFontSize = document.FontSize;
        bool fontChanged = oldFontSize != fontSize
            || document.LineHeight != presentation.LineHeightPixels
            || !string.Equals(document.FontFamily.Source, presentation.FontFamily, StringComparison.OrdinalIgnoreCase);
        var oldPadding = document.PagePadding;
        double vertical = host.VerticalOffset, horizontal = host.HorizontalOffset;
        if (fontChanged)
        {
            document.FontFamily = new FontFamily(presentation.FontFamily);
            document.FontSize = fontSize;
            document.LineHeight = presentation.LineHeightPixels;
            foreach (var block in document.Blocks)
                UpdateBlockPresentation(block, presentation, fontSize / oldFontSize);
        }
        else
        {
            foreach (var block in document.Blocks)
                UpdateBlockPresentation(block, presentation, 1);
        }
        host.FontFamily = new FontFamily(presentation.FontFamily);
        if (host.FontSize != fontSize) host.FontSize = fontSize;
        ApplyPageLayout(host, readingWidth);
        if (!fontChanged && oldPadding == document.PagePadding) return;
        // Reflow before restoring offsets: WPF can otherwise bring the selected
        // caret into view during formatting. No deferred callback can steal a
        // subsequent user scroll. ScrollViewer clamps at the new document end.
        host.UpdateLayout();
        host.ScrollToHorizontalOffset(horizontal);
        host.ScrollToVerticalOffset(vertical);
        host.UpdateLayout();
    }

    private static readonly object ImageCaptionTag = new();

    internal static void StyleImageCaption(Run caption, double fontSize)
    {
        caption.Tag = ImageCaptionTag;
        caption.FontSize = Math.Max(11, fontSize * .8);
    }

    private static void UpdateBlockPresentation(Block block, ReadingPresentation presentation, double ratio)
    {
        double fontSize = presentation.FontSize;
        switch (block)
        {
            case Paragraph paragraph:
                if (paragraph.Tag is ReadingBlock source)
                {
                    var margin = paragraph.Margin;
                    StyleBlock(paragraph, source, presentation);
                    // Rich lists put indentation on the outer List, not its paragraphs.
                    if (paragraph.Parent is ListItem) paragraph.Margin = margin;
                }
                UpdateInlinePresentation(paragraph.Inlines, fontSize);
                break;
            case System.Windows.Documents.List list:
                foreach (var item in list.ListItems)
                    foreach (var child in item.Blocks) UpdateBlockPresentation(child, presentation, ratio);
                break;
            case Table table:
                table.FontSize = fontSize * .9;
                foreach (var group in table.RowGroups)
                    foreach (var row in group.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var child in cell.Blocks) UpdateBlockPresentation(child, presentation, ratio);
                break;
            case Section section:
                foreach (var child in section.Blocks) UpdateBlockPresentation(child, presentation, ratio);
                break;
            case BlockUIContainer { Tag: ReadingBlock diagramSource, Child: FrameworkElement diagram }
                when MermaidBlock.IsMermaid(diagramSource):
                // The diagram's fonts and vector geometry were measured together.
                // Scale both rather than rebuilding it or changing only the labels.
                double scale = diagram.LayoutTransform is ScaleTransform transform ? transform.ScaleX : 1;
                diagram.LayoutTransform = new ScaleTransform(scale * ratio, scale * ratio);
                break;
        }
    }

    private static void UpdateInlinePresentation(InlineCollection inlines, double fontSize)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run && ReferenceEquals(run.Tag, ImageCaptionTag))
                StyleImageCaption(run, fontSize);
            else if (inline is Span span) UpdateInlinePresentation(span.Inlines, fontSize);
        }
    }

    internal static void ApplyPageLayout(RichTextBox host, double readingWidth)
    {
        if (!double.IsFinite(readingWidth) || readingWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(readingWidth));
        // Control.Padding also shrinks the ScrollViewer outside PagePadding.
        // Keeping both nonzero doubles the gutter, especially after maximizing.
        if (host.Padding != new Thickness(0)) host.Padding = new Thickness(0);
        // ViewportWidth can still refer to the previous arrange during resize.
        double available = Math.Max(0, host.ActualWidth - host.BorderThickness.Left - host.BorderThickness.Right);
        double margin = Math.Max(24, (available - readingWidth) / 2);
        var padding = new Thickness(margin, 28, margin, 60);
        if (host.Document.PagePadding != padding) host.Document.PagePadding = padding;
    }

    public TextPointer PointerAt(int offset)
    {
        if (richMap is not null) return richMap.PointerAt(offset);
        var entry = runs[0];
        foreach (var candidate in runs)
        {
            if (candidate.Offset > offset) break;
            entry = candidate;
        }
        return entry.Run.ContentStart.GetPositionAtOffset(Math.Clamp(offset - entry.Offset, 0, entry.Run.Text.Length))
            ?? entry.Run.ContentEnd;
    }

    public int OffsetAt(TextPointer pointer)
    {
        if (richMap is not null) return richMap.OffsetAt(pointer);
        foreach (var entry in runs)
        {
            if (pointer.CompareTo(entry.Run.ContentStart) < 0) return entry.Offset;
            if (pointer.CompareTo(entry.Run.ContentEnd) <= 0)
                return entry.Offset + new TextRange(entry.Run.ContentStart, pointer).Text.Length;
        }
        var last = runs[^1];
        return last.Offset + last.Run.Text.Length;
    }
}

// Image projection and loading stay with the renderer for source-link harnesses;
// interactive hosts also include ReaderImageWindow.cs and ReaderTableWindow.cs.
internal sealed record MarkdownImageReference(int Start, int Length, string Alt, string Target,
    string Key, int ProjectedStart, int ProjectedLength)
{
    internal bool IsRemote => MarkdownImages.IsRemote(Target);
}

internal sealed class MarkdownImages
{
    internal string Text { get; }
    internal IReadOnlyList<MarkdownImageReference> References { get; }
    internal MarkdownImageBudget Budget { get; } = new();
    private readonly Dictionary<string, MarkdownImageReference> byKey;
    private readonly Dictionary<string, string> captions = new(StringComparer.Ordinal);
    private readonly string markerPrefix;
    private readonly int[] sourceDeltas;

    private MarkdownImages(string text, List<MarkdownImageReference> references)
    {
        Text = text;
        References = references;
        byKey = references.ToDictionary(r => r.Key, StringComparer.Ordinal);
        markerPrefix = references.Count == 0 ? "" : "[图片：" + references[0].Key[..^1];
        sourceDeltas = new int[references.Count + 1];
        for (int i = 0; i < references.Count; i++)
            sourceDeltas[i + 1] = sourceDeltas[i] + references[i].Length - references[i].ProjectedLength;
    }

    internal bool TryGet(string key, out MarkdownImageReference reference) => byKey.TryGetValue(key, out reference!);

    internal string Caption(MarkdownImageReference reference)
    {
        if (captions.TryGetValue(reference.Key, out var caption)) return caption;
        // Match Core's inline alt formatting without parsing the potentially huge target.
        var label = ReadingModel.Build("![" + reference.Alt + "](image)", TextKind.Markdown);
        return captions[reference.Key] = label.VisibleText;
    }

    internal string RestoreLabels(string text)
    {
        if (References.Count == 0) return text;
        StringBuilder? restored = null;
        int cursor = 0, scan = 0;
        while ((scan = text.IndexOf(markerPrefix, scan, StringComparison.Ordinal)) >= 0)
        {
            int end = text.IndexOf(']', scan + markerPrefix.Length);
            if (end < 0) break;
            if (byKey.TryGetValue(text[(scan + 4)..end], out var reference))
            {
                restored ??= new StringBuilder(text.Length);
                restored.Append(text, cursor, scan - cursor).Append(Caption(reference));
                cursor = end + 1;
            }
            scan = end + 1;
        }
        return restored is null ? text : restored.Append(text, cursor, text.Length - cursor).ToString();
    }

    internal string FallbackText(string source)
    {
        if (References.Count == 0) return source;
        var output = new StringBuilder();
        int cursor = 0;
        foreach (var reference in References)
        {
            output.Append(source, cursor, reference.Start - cursor);
            output.Append("[图片：").Append(reference.Alt).Append(']');
            cursor = reference.Start + reference.Length;
        }
        return output.Append(source, cursor, source.Length - cursor).ToString();
    }

    internal ReadingBlock OriginalRange(ReadingBlock block)
    {
        int start = OriginalOffset(block.SourceStart);
        return block with { SourceStart = start, SourceLength = OriginalOffset(block.SourceStart + block.SourceLength) - start };
    }

    private int OriginalOffset(int offset)
    {
        int low = 0, high = References.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (References[middle].ProjectedStart <= offset) low = middle + 1;
            else high = middle - 1;
        }
        if (high >= 0 && offset < References[high].ProjectedStart + References[high].ProjectedLength)
            return References[high].Start;
        return offset + sourceDeltas[low];
    }

    internal IEnumerable<MarkdownImageReference> InSourceRange(int start, int length)
    {
        int low = 0, high = References.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (References[middle].Start < start) low = middle + 1;
            else high = middle;
        }
        for (int i = low; i < References.Count && References[i].Start < (long)start + length; i++)
            yield return References[i];
    }

    internal static MarkdownImages Extract(string source, CancellationToken token = default)
    {
        var references = new List<MarkdownImageReference>();
        var definitions = MarkdownLinks.References(source);
        StringBuilder? projected = null;
        string prefix = "qyimg" + Guid.NewGuid().ToString("N");
        int copied = 0, lineStart = 0, fenceLength = 0;
        char fenceMarker = '\0';
        MarkdownImageReference ProjectReferenceImage(int start, int end, string alt, string target)
        {
            projected ??= new StringBuilder(source.Length > 200_000 ? 4096 : source.Length);
            projected.Append(source, copied, start - copied);
            string key = prefix + references.Count;
            string replacement = "![" + key + "](" + key + ")";
            int projectedStart = projected.Length;
            projected.Append(replacement);
            return new(start, end - start, alt, target, key, projectedStart, replacement.Length);
        }
        while (lineStart < source.Length)
        {
            token.ThrowIfCancellationRequested();
            int lineEnd = source.AsSpan(lineStart).IndexOfAny('\r', '\n');
            lineEnd = lineEnd < 0 ? source.Length : lineStart + lineEnd;
            int next = lineEnd;
            if (next < source.Length && source[next++] == '\r' && next < source.Length && source[next] == '\n') next++;
            int first = lineStart;
            while (first < lineEnd && first - lineStart < 4 && source[first] == ' ') first++;
            int markerEnd = first;
            if (first - lineStart <= 3 && first < lineEnd && source[first] is '`' or '~')
            {
                while (markerEnd < lineEnd && source[markerEnd] == source[first]) markerEnd++;
                int count = markerEnd - first;
                if (count >= 3)
                {
                    if (fenceLength == 0) { fenceMarker = source[first]; fenceLength = count; }
                    else if (source[first] == fenceMarker && count >= fenceLength &&
                        source.AsSpan(markerEnd, lineEnd - markerEnd).Trim().IsEmpty) fenceLength = 0;
                    lineStart = next;
                    continue;
                }
            }
            if (fenceLength == 0)
            {
                for (int i = lineStart; i < lineEnd; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    if (source[i] == '\\') { i++; continue; }
                    if (source[i] == '`')
                    {
                        int end = i + 1;
                        while (end < lineEnd && source[end] == '`') end++;
                        int ticks = end - i;
                        bool closed = false;
                        for (int j = end; j < lineEnd;)
                        {
                            if (source[j] != '`') { j++; continue; }
                            int close = j + 1;
                            while (close < lineEnd && source[close] == '`') close++;
                            if (close - j == ticks) { i = close - 1; closed = true; break; }
                            j = close;
                        }
                        // Conservatively leave the remainder literal while a code span is edited.
                        if (!closed) break;
                        continue;
                    }
                    if (source[i] != '!' || i + 1 >= lineEnd || source[i + 1] != '[') continue;
                    int altEnd = i + 2;
                    while (altEnd < lineEnd && source[altEnd] != ']')
                    {
                        if (source[altEnd] == '\\') altEnd++;
                        altEnd++;
                    }
                    // Later starts on this line share the same first closing bracket.
                    // Leave incomplete syntax literal and never rescan its whole tail.
                    if (altEnd >= lineEnd) break;
                    if (altEnd + 1 >= lineEnd || source[altEnd + 1] != '(')
                    {
                        string alt = source[(i + 2)..altEnd];
                        if (altEnd + 1 < lineEnd && source[altEnd + 1] == '[')
                        {
                            int refEnd = altEnd + 2;
                            while (refEnd < lineEnd && source[refEnd] != ']')
                            {
                                if (source[refEnd] == '\\') refEnd++;
                                refEnd++;
                            }
                            if (refEnd >= lineEnd) break;
                            string keyLabel = source[(altEnd + 2)..refEnd];
                            if (keyLabel.Length == 0) keyLabel = alt;
                            if (definitions.TryGetValue(MarkdownLinks.NormalizeReferenceLabel(keyLabel),
                                out var definition))
                            {
                                references.Add(ProjectReferenceImage(i, refEnd + 1, alt,
                                    definition.Destination));
                                copied = refEnd + 1;
                                i = refEnd;
                            }
                            else i = refEnd;
                            continue;
                        }
                        if (definitions.TryGetValue(MarkdownLinks.NormalizeReferenceLabel(alt),
                            out var shortcut))
                        {
                            references.Add(ProjectReferenceImage(i, altEnd + 1, alt,
                                shortcut.Destination));
                            copied = altEnd + 1;
                            i = altEnd;
                            continue;
                        }
                        i = altEnd;
                        continue;
                    }
                    int targetStart = altEnd + 2, targetEnd = targetStart, depth = 0;
                    bool angle = targetStart < lineEnd && source[targetStart] == '<';
                    for (; targetEnd < lineEnd; targetEnd++)
                    {
                        char value = source[targetEnd];
                        if (value == '\\' && targetEnd + 1 < lineEnd && source[targetEnd + 1] is '(' or ')')
                        { targetEnd++; continue; }
                        if (angle)
                        {
                            if (value == '>') angle = false;
                            continue;
                        }
                        if (value == '(') depth++;
                        else if (value == ')' && depth-- == 0) break;
                    }
                    if (targetEnd == lineEnd) break;
                    string target = source[targetStart..targetEnd].Trim();
                    if (target.StartsWith('<'))
                    {
                        int close = target.IndexOf('>');
                        if (close < 0) continue;
                        target = target[1..close];
                    }
                    else
                    {
                        // Accept the common optional Markdown title, while allowing bare spaces
                        // in Windows file names. URL percent escapes are decoded by the loader.
                        int title = target.LastIndexOf(" \"", StringComparison.Ordinal);
                        if (title >= 0 && target.EndsWith('"')) target = target[..title].TrimEnd();
                        else
                        {
                            title = target.LastIndexOf(" '", StringComparison.Ordinal);
                            if (title >= 0 && target.EndsWith('\'')) target = target[..title].TrimEnd();
                        }
                    }
                    target = target.Replace(@"\(", "(").Replace(@"\)", ")");
                    projected ??= new StringBuilder(source.Length > 200_000 ? 4096 : source.Length);
                    projected.Append(source, copied, i - copied);
                    string key = prefix + references.Count;
                    string replacement = "![" + key + "](" + key + ")";
                    int projectedStart = projected.Length;
                    projected.Append(replacement);
                    references.Add(new(i, targetEnd + 1 - i, source[(i + 2)..altEnd], target,
                        key, projectedStart, replacement.Length));
                    copied = targetEnd + 1;
                    i = targetEnd;
                }
            }
            lineStart = next;
        }
        if (projected is null) return new(source, references);
        projected.Append(source, copied, source.Length - copied);
        return new(projected.ToString(), references);
    }

    internal static bool IsRemote(string target)
    {
        if (target.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal)) return true;
        int colon = target.IndexOf(':');
        return colon > 1 && !target.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class MarkdownImageBudget
{
    internal const int MaximumImages = 32;
    internal const long MaximumBytes = 32L * 1024 * 1024;
    internal const long MaximumPixels = 48_000_000;
    private int count;
    private bool limitNotice;
    private long bytes, pixels;
    internal bool TakeControl() => ++count <= MaximumImages;
    internal bool TakeLimitNotice() => !limitNotice && (limitNotice = true);
    internal bool Reserve(long imageBytes, long imagePixels)
    {
        lock (this)
        {
            if (bytes + imageBytes > MaximumBytes || pixels + imagePixels > MaximumPixels) return false;
            bytes += imageBytes;
            pixels += imagePixels;
            return true;
        }
    }
    internal void Release(long imageBytes, long imagePixels)
    {
        lock (this) { bytes -= imageBytes; pixels -= imagePixels; }
    }
}

internal sealed class MarkdownImageResult(
    BitmapSource? bitmap, string? error, byte[]? decodedPng = null, Action? release = null) : IDisposable
{
    internal BitmapSource? Bitmap { get; } = bitmap;
    internal string? Error { get; } = error;
    internal byte[]? DecodedPng { get; } = decodedPng;
    private Action? releaseBudget = release;

    internal void Accept() => Interlocked.Exchange(ref releaseBudget, null);
    public void Dispose() => Interlocked.Exchange(ref releaseBudget, null)?.Invoke();
}

internal static class MarkdownImageLoader
{
    private sealed class ImageSourceException(string message) : Exception(message);
    private sealed record ImageInput(
        string? Path, byte[]? InlineBytes, long SourceLength, long LastWriteUtcTicks);
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal const int MaximumDecodedPngBytes = 32 * 1024 * 1024;
    internal const long MaximumPixels = 16_000_000;
    internal const int MaximumDecodedSide = 4096;
    private static readonly SemaphoreSlim DecodeSlots = new(2);

    internal static async Task<MarkdownImageResult> LoadAsync(string target, string? sourcePath,
        MarkdownImageBudget budget, CancellationToken cancellation)
    {
        ImageInput input;
        try { input = await Task.Run(() => PrepareInput(target, sourcePath), cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException) { return new(null, "找不到图片文件"); }
        catch (DirectoryNotFoundException) { return new(null, "找不到图片目录"); }
        catch (ImageSourceException error) { return new(null, error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or
            FormatException or ArgumentException or OverflowException)
        { return new(null, "图片缺失、损坏或无法读取"); }

        await DecodeSlots.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // File resolution, base64 conversion, WIC header inspection and decoding all stay
            // off the dispatcher. Only a fully loaded, frozen bitmap crosses back to WPF.
            return await Task.Run(async () =>
            {
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    byte[] bytes = input.InlineBytes ?? await ReadFileAsync(input.Path!, cancellation);
                    if (input.Path is not null && bytes.Length > MaximumBytes)
                        return new MarkdownImageResult(null, "图片超过 8 MiB 上限");
                    if (input.Path is not null)
                    {
                        var info = new FileInfo(input.Path);
                        if (!info.Exists || info.Length != input.SourceLength ||
                            info.LastWriteTimeUtc.Ticks != input.LastWriteUtcTicks)
                            return new MarkdownImageResult(null, "图片读取期间发生变化，请刷新预览");
                    }
                    var cacheKey = MarkdownImageCacheKey.FromBytes(bytes);
                    if (MarkdownImageCache.TryGet(cacheKey, out var cached))
                    {
                        if (!budget.Reserve(cached.SourceBytes, cached.OriginalPixels))
                            return new(null, "本篇图片总量超过上限（32 MiB / 4800 万原图像素）");
                        return new MarkdownImageResult(cached.Bitmap, null, cached.DecodedPng,
                            () => budget.Release(cached.SourceBytes, cached.OriginalPixels));
                    }
                    cancellation.ThrowIfCancellationRequested();
                    using var stream = new MemoryStream(bytes, writable: false);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                    var frame = decoder.Frames[0];
                    int width = frame.PixelWidth, height = frame.PixelHeight;
                    if (width < 1 || height < 1 || width > 32768 || height > 32768 || (long)width * height > MaximumPixels)
                        return new MarkdownImageResult(null, "图片像素超过上限（1600 万像素 / 单边 32768）");
                    long originalPixels = (long)width * height;
                    double scale = Math.Min(1, MaximumDecodedSide / (double)Math.Max(width, height));
                    if (!budget.Reserve(bytes.Length, originalPixels))
                        return new MarkdownImageResult(null, "本篇图片总量超过上限（32 MiB / 4800 万原图像素）");
                    bool success = false;
                    try
                    {
                        stream.Position = 0;
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        if (scale < 1)
                        {
                            if (width >= height) bitmap.DecodePixelWidth = 4096;
                            else bitmap.DecodePixelHeight = 4096;
                        }
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        cancellation.ThrowIfCancellationRequested();
                        bitmap.Freeze();
                        byte[] decodedPng = EncodePng(bitmap, cancellation);
                        if (decodedPng.Length > MaximumDecodedPngBytes)
                            return new MarkdownImageResult(null, "解码图片超过 32 MiB 上限");
                        long decodedPixels = (long)bitmap.PixelWidth * bitmap.PixelHeight;
                        success = true;
                        MarkdownImageCache.Add(cacheKey, new MarkdownImageCacheEntry(
                            bitmap, decodedPng, bytes.Length, originalPixels, decodedPixels,
                            decodedPng.Length + decodedPixels * 4));
                        return new MarkdownImageResult(bitmap, null, decodedPng,
                            () => budget.Release(bytes.Length, originalPixels));
                    }
                    finally { if (!success) budget.Release(bytes.Length, originalPixels); }
                }
                catch (FileNotFoundException) { return new(null, "找不到图片文件"); }
                catch (DirectoryNotFoundException) { return new(null, "找不到图片目录"); }
                catch (ImageSourceException error) { return new(null, error.Message); }
                catch (NotSupportedException) { return new(null, "图片格式不受支持或文件损坏"); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or
                    FormatException or ArgumentException or OverflowException or COMException or InvalidOperationException)
                { return new(null, "图片缺失、损坏或无法读取"); }
            }, cancellation).ConfigureAwait(false);
        }
        finally { DecodeSlots.Release(); }
    }

    private static ImageInput PrepareInput(string target, string? sourcePath)
    {
        if (target.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            const string header = "data:image/png;base64,";
            const string invalid = "仅支持有界的内嵌 PNG。";
            if (!target.StartsWith(header, StringComparison.OrdinalIgnoreCase) ||
                Encoding.UTF8.GetByteCount(target) > MaximumBytes * 4 / 3 + 128)
                throw new ImageSourceException(invalid);
            byte[] bytes = DecodeEmbeddedPng(target[header.Length..]);
            return new ImageInput(null, bytes, 0, 0);
        }

        string path = LocalPath(target, sourcePath);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException(path);
        if (info.Length > MaximumBytes) throw new ImageSourceException("图片超过 8 MiB 上限");
        return new ImageInput(path, null, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static byte[] DecodeEmbeddedPng(string encoded)
    {
        // Foundation's .ignoreUnknownCharacters accepts wrapped or decorated
        // base64 and missing trailing padding. Keep the same tolerance without
        // accepting a non-PNG payload.
        var filtered = new StringBuilder(encoded.Length);
        foreach (char value in encoded)
            if (value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=')
                filtered.Append(value);
        while (filtered.Length > 0 && filtered[filtered.Length - 1] == '=') filtered.Length--;
        int padding = (4 - filtered.Length % 4) % 4;
        if (padding > 0) filtered.Append('=', padding);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(filtered.ToString()); }
        catch (FormatException) { throw new ImageSourceException("仅支持有界的内嵌 PNG。"); }
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!bytes.AsSpan().StartsWith(signature))
            throw new ImageSourceException("仅支持有界的内嵌 PNG。");
        return bytes;
    }

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellation)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaximumBytes) throw new ImageSourceException("图片超过 8 MiB 上限");
        byte[] bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
        if (await file.ReadAsync(new byte[1], cancellation).ConfigureAwait(false) != 0)
            throw new ImageSourceException("图片读取期间发生变化，请刷新预览");
        return bytes;
    }

    private static byte[] EncodePng(BitmapSource bitmap, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
        cancellation.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static string LocalPath(string target, string? sourcePath)
    {
        if (target.Length > 32767) throw new ImageSourceException("图片路径过长");
        if (MarkdownImages.IsRemote(target)) throw new ImageSourceException("网络图片未加载");
        string path;
        if (target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc ||
                !string.IsNullOrEmpty(uri.Host))
                throw new ImageSourceException("网络图片未加载");
            path = uri.LocalPath;
        }
        else
        {
            path = Uri.UnescapeDataString(target);
        }
        path = path.Replace('/', Path.DirectorySeparatorChar);
        if (MarkdownImages.IsRemote(path)) throw new ImageSourceException("网络图片未加载");
        if (string.IsNullOrWhiteSpace(path)) throw new ImageSourceException("图片路径为空");
        if (!Path.IsPathFullyQualified(path))
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ImageSourceException("相对图片需要文档路径");
            string fullSource = Path.GetFullPath(sourcePath);
            if (MarkdownImages.IsRemote(fullSource)) throw new ImageSourceException("网络图片未加载");
            path = Path.GetFullPath(path, Path.GetDirectoryName(fullSource)!);
        }
        else path = Path.GetFullPath(path);
        if (MarkdownImages.IsRemote(path) || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            throw new ImageSourceException("网络图片未加载");
        return path;
    }
}

internal sealed class MarkdownImageView : Grid
{
    private sealed record LoadedPngPayload(WeakReference<BitmapSource> Bitmap, byte[] Png);
    private static readonly ConditionalWeakTable<Image, LoadedPngPayload> DecodedPngPayloads = new();
    private static int nextSequence;
    private readonly MarkdownImageReference reference;
    private readonly string? sourcePath;
    private readonly MarkdownImageBudget budget;
    private readonly int sequence = Interlocked.Increment(ref nextSequence);
    private readonly Image image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private CancellationTokenSource? loading;
    private RichTextBox? host;
    private bool finished;

    internal static MarkdownImageView? Create(MarkdownImageReference reference, string? sourcePath, MarkdownImageBudget budget) =>
        budget.TakeControl() ? new MarkdownImageView(reference, sourcePath, budget) : null;

    private MarkdownImageView(MarkdownImageReference reference, string? sourcePath, MarkdownImageBudget budget)
    {
        this.reference = reference;
        this.sourcePath = sourcePath;
        this.budget = budget;
        HorizontalAlignment = HorizontalAlignment.Left;
        string alt = reference.Alt.Length > 160 ? reference.Alt[..160] + "…" : reference.Alt;
        AutomationProperties.SetName(image, string.IsNullOrWhiteSpace(alt) ? "Markdown 图片" : alt);
        AutomationProperties.SetName(this, "Markdown 图片：" + alt);
        status.Text = "图片加载中：" + alt;
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        image.Visibility = Visibility.Collapsed;
        Children.Add(image);
        Children.Add(status);
        ReaderImageWindow.Attach(this, image, alt);
        MarkdownImageScheduler.Register(this, budget);
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            loading?.Cancel();
            if (!finished) LoadingStarted = false;
            MarkdownImageScheduler.Unregister(this, budget);
            if (host is not null) host.SizeChanged -= HostSizeChanged;
            host = null;
        };
    }

    internal bool LoadingStarted { get; private set; }
    internal bool IsReady => finished && image.Source is BitmapSource;
    internal bool IsFailed => finished && image.Source is null;
    internal int SourceStart => reference.Start;
    internal int Sequence => sequence;
    internal MarkdownImageBudget Budget => budget;
    internal Image ImageControl => image;
    internal string Caption => AutomationProperties.GetName(image);

    internal static void RememberDecodedPng(Image target, BitmapSource? bitmap, byte[]? png)
    {
        DecodedPngPayloads.Remove(target);
        if (bitmap is not null && png is not null)
            DecodedPngPayloads.Add(target, new LoadedPngPayload(new WeakReference<BitmapSource>(bitmap), png));
    }

    internal static bool TryGetDecodedPng(Image target, out byte[] png)
    {
        if (DecodedPngPayloads.TryGetValue(target, out var payload) &&
            payload.Bitmap.TryGetTarget(out var bitmap) && ReferenceEquals(target.Source, bitmap))
        {
            png = payload.Png;
            return true;
        }
        png = [];
        return false;
    }

    public void Retry()
    {
        if (!IsFailed) return;
        loading?.Cancel();
        loading = null;
        LoadingStarted = false;
        finished = false;
        DecodedPngPayloads.Remove(image);
        image.Source = null;
        image.Visibility = Visibility.Collapsed;
        status.Text = "图片加载中：" + (reference.Alt.Length > 160 ? reference.Alt[..160] + "…" : reference.Alt);
        status.Visibility = Visibility.Visible;
        InvalidateMeasure();
        MarkdownImageScheduler.Retry(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (host is null)
        {
            for (DependencyObject? parent = this; parent is not null;)
            {
                if (parent is RichTextBox box) { host = box; host.SizeChanged += HostSizeChanged; break; }
                parent = parent is FrameworkContentElement content ? content.Parent :
                    LogicalTreeHelper.GetParent(parent) ?? (parent is Visual ? VisualTreeHelper.GetParent(parent) : null);
            }
        }
        InvalidateMeasure();
        if (finished || loading is { IsCancellationRequested: false }) return;
        MarkdownImageScheduler.Activate(this);
    }

    internal void MarkTimedOut()
    {
        if (finished) return;
        finished = true;
        LoadingStarted = false;
        image.Visibility = Visibility.Collapsed;
        status.Text = "图片准备超过 5 秒，未完成的图片保留说明，可重试。";
        status.Visibility = Visibility.Visible;
        InvalidateMeasure();
    }

    internal async Task StartLoadingAsync(CancellationToken batchCancellation = default)
    {
        if (finished || loading is { IsCancellationRequested: false } || LoadingStarted) return;
        LoadingStarted = true;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(batchCancellation);
        loading = request;
        try
        {
            using var result = await MarkdownImageLoader.LoadAsync(reference.Target, sourcePath, budget, request.Token);
            if (request.IsCancellationRequested || !ReferenceEquals(loading, request)) return;
            // Loaded can also be raised by a host without a DispatcherSynchronizationContext.
            await Dispatcher.InvokeAsync(() =>
            {
                if (request.IsCancellationRequested || !ReferenceEquals(loading, request)) return;
                finished = true;
                if (result.Bitmap is not null)
                {
                    image.Source = result.Bitmap;
                    RememberDecodedPng(image, result.Bitmap, result.DecodedPng);
                    result.Accept();
                    image.Visibility = Visibility.Visible;
                    status.Visibility = Visibility.Collapsed;
                }
                else status.Text = "图片加载失败：" + result.Error + "  · 点击重试";
                InvalidateMeasure();
            });
        }
        catch (OperationCanceledException)
        {
            if (batchCancellation.IsCancellationRequested)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(loading, request) && !finished) MarkTimedOut();
                });
            }
        }
        finally
        {
            if (ReferenceEquals(loading, request))
            {
                loading = null;
                if (request.IsCancellationRequested && !finished) LoadingStarted = false;
            }
        }
    }

    private void HostSizeChanged(object sender, SizeChangedEventArgs args) => InvalidateMeasure();

    protected override Size MeasureOverride(Size constraint)
    {
        double width = double.IsFinite(constraint.Width) ? Math.Max(1, constraint.Width) : 680;
        if (host is { ActualWidth: > 0 })
        {
            double available = host.ActualWidth - host.Document.PagePadding.Left - host.Document.PagePadding.Right - 24;
            width = Math.Min(width, Math.Max(1, available));
        }
        if (image.Source is BitmapSource bitmap)
        {
            image.Width = Math.Min(width, bitmap.PixelWidth);
            image.Height = image.Width * bitmap.PixelHeight / bitmap.PixelWidth;
        }
        status.MaxWidth = width;
        return base.MeasureOverride(new Size(width, constraint.Height));
    }
}
