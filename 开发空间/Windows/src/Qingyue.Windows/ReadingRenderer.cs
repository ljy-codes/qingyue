using System.IO;
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
        LineHeight = 27, TextAlignment = TextAlignment.Left
    };

    public RenderedReading(ReadingModel model, bool renderMermaid = true, string? sourcePath = null,
        string? sourceText = null)
    {
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
            StyleBlock(paragraph, block, Document.FontSize);
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
            if (images is not null && block.Kind != ReadingBlockKind.Code)
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
    internal static void StyleBlock(Paragraph paragraph, ReadingBlock block, double fontSize)
    {
        switch (block.Kind)
        {
            case ReadingBlockKind.Heading:
                int level = Math.Clamp(block.Level, 1, 6);
                paragraph.FontSize = fontSize * (level switch { 1 => 2, 2 => 1.6, 3 => 1.35, 4 => 1.2, 5 => 1.1, _ => 1 });
                paragraph.FontWeight = level == 1 ? FontWeights.Bold : level == 2 ? FontWeights.SemiBold : FontWeights.Medium;
                paragraph.SetResourceReference(Paragraph.ForegroundProperty,
                    level == 1 ? "AccentTextBrush" : level == 2 ? "SyntaxKeyBrush" : "TextBrush");
                paragraph.Margin = new Thickness(0, fontSize * (level switch { 1 => 1.75, 2 => 1.5, _ => 1.25 }), 0, fontSize * .75);
                paragraph.LineHeight = paragraph.FontSize * 1.4;
                paragraph.KeepWithNext = true;
                break;
            case ReadingBlockKind.Code:
                paragraph.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
                paragraph.FontSize = fontSize * .9;
                paragraph.LineHeight = fontSize * 1.6;
                paragraph.Padding = new Thickness(16, 12, 16, 12);
                paragraph.Margin = new Thickness(0, 8, 0, 18);
                paragraph.BorderThickness = new Thickness(1);
                paragraph.SetResourceReference(Paragraph.BorderBrushProperty, "BorderBrush");
                paragraph.SetResourceReference(Paragraph.BackgroundProperty, "PanelBrush");
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
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!ReferenceEquals(host.Document, Document))
            throw new ArgumentException("The host must contain this reading document.", nameof(host));
        ApplyPresentation(host, fontSize, readingWidth);
    }

    internal static void ApplyPresentation(RichTextBox host, double fontSize, double readingWidth)
    {
        host.VerifyAccess();
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!double.IsFinite(readingWidth) || readingWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(readingWidth));
        var document = host.Document;
        double oldFontSize = document.FontSize;
        bool fontChanged = oldFontSize != fontSize || document.LineHeight != fontSize * 1.75;
        var oldPadding = document.PagePadding;
        double vertical = host.VerticalOffset, horizontal = host.HorizontalOffset;
        if (fontChanged)
        {
            document.FontSize = fontSize;
            document.LineHeight = fontSize * 1.75;
            foreach (var block in document.Blocks)
                UpdateBlockPresentation(block, fontSize, fontSize / oldFontSize);
        }
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

    private static void UpdateBlockPresentation(Block block, double fontSize, double ratio)
    {
        switch (block)
        {
            case Paragraph paragraph:
                if (paragraph.Tag is ReadingBlock source)
                {
                    var margin = paragraph.Margin;
                    StyleBlock(paragraph, source, fontSize);
                    // Rich lists put indentation on the outer List, not its paragraphs.
                    if (paragraph.Parent is ListItem) paragraph.Margin = margin;
                }
                UpdateInlinePresentation(paragraph.Inlines, fontSize);
                break;
            case System.Windows.Documents.List list:
                foreach (var item in list.ListItems)
                    foreach (var child in item.Blocks) UpdateBlockPresentation(child, fontSize, ratio);
                break;
            case Table table:
                table.FontSize = fontSize * .9;
                foreach (var group in table.RowGroups)
                    foreach (var row in group.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var child in cell.Blocks) UpdateBlockPresentation(child, fontSize, ratio);
                break;
            case Section section:
                foreach (var child in section.Blocks) UpdateBlockPresentation(child, fontSize, ratio);
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
        StringBuilder? projected = null;
        string prefix = "qyimg" + Guid.NewGuid().ToString("N");
        int copied = 0, lineStart = 0, fenceLength = 0;
        char fenceMarker = '\0';
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
    private int count;
    private bool limitNotice;
    private long bytes, pixels;
    internal bool TakeControl() => ++count <= 32;
    internal bool TakeLimitNotice() => !limitNotice && (limitNotice = true);
    internal bool Reserve(long imageBytes, long imagePixels)
    {
        lock (this)
        {
            if (bytes + imageBytes > 64L * 1024 * 1024 || pixels + imagePixels > 48_000_000) return false;
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

internal sealed class MarkdownImageResult(BitmapSource? bitmap, string? error, Action? release = null) : IDisposable
{
    internal BitmapSource? Bitmap { get; } = bitmap;
    internal string? Error { get; } = error;
    private Action? releaseBudget = release;

    internal void Accept() => Interlocked.Exchange(ref releaseBudget, null);
    public void Dispose() => Interlocked.Exchange(ref releaseBudget, null)?.Invoke();
}

internal static class MarkdownImageLoader
{
    private sealed class ImageSourceException(string message) : Exception(message);
    private sealed record ImageInput(string? Path, byte[]? InlineBytes, MarkdownImageCacheKey CacheKey);
    internal const int MaximumBytes = 16 * 1024 * 1024;
    internal const long MaximumPixels = 32_000_000;
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

        if (MarkdownImageCache.TryGet(input.CacheKey, out var cached))
        {
            if (!budget.Reserve(cached.SourceBytes, cached.DecodedPixels))
                return new(null, "本篇图片总量超过上限（64 MiB / 4800 万解码像素）");
            return new MarkdownImageResult(cached.Bitmap, null,
                () => budget.Release(cached.SourceBytes, cached.DecodedPixels));
        }

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
                    if (bytes.Length > MaximumBytes)
                        return new MarkdownImageResult(null, "图片超过 16 MiB 上限");
                    if (input.Path is not null)
                    {
                        var info = new FileInfo(input.Path);
                        if (!info.Exists || info.Length != input.CacheKey.SourceLength ||
                            info.LastWriteTimeUtc.Ticks != input.CacheKey.LastWriteUtcTicks)
                            return new MarkdownImageResult(null, "图片读取期间发生变化，请刷新预览");
                    }
                    cancellation.ThrowIfCancellationRequested();
                    using var stream = new MemoryStream(bytes, writable: false);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                    var frame = decoder.Frames[0];
                    int width = frame.PixelWidth, height = frame.PixelHeight;
                    if (width < 1 || height < 1 || width > 32768 || height > 32768 || (long)width * height > MaximumPixels)
                        return new MarkdownImageResult(null, "图片像素超过上限（3200 万像素 / 单边 32768）");
                    double scale = Math.Min(1, 4096d / Math.Max(width, height));
                    long pixels = (long)Math.Ceiling(width * scale) * (long)Math.Ceiling(height * scale);
                    if (!budget.Reserve(bytes.Length, pixels))
                        return new MarkdownImageResult(null, "本篇图片总量超过上限（64 MiB / 4800 万解码像素）");
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
                        success = true;
                        MarkdownImageCache.Add(input.CacheKey,
                            new MarkdownImageCacheEntry(bitmap, bytes.Length, pixels));
                        return new MarkdownImageResult(bitmap, null, () => budget.Release(bytes.Length, pixels));
                    }
                    finally { if (!success) budget.Release(bytes.Length, pixels); }
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
            if (!target.StartsWith(header, StringComparison.OrdinalIgnoreCase))
                throw new ImageSourceException("仅支持 data:image/png;base64 内嵌图片");
            if (target.Length - header.Length > ((MaximumBytes + 2) / 3 * 4) + 4096)
                throw new ImageSourceException("图片超过 16 MiB 上限");
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target)));
            return new ImageInput(null, Convert.FromBase64String(target[header.Length..]),
                new MarkdownImageCacheKey("data:" + identity, target.Length, 0, 0, 0));
        }

        string path = LocalPath(target, sourcePath);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException(path);
        if (info.Length > MaximumBytes) throw new ImageSourceException("图片超过 16 MiB 上限");
        return new ImageInput(path, null, new MarkdownImageCacheKey(path, info.Length,
            info.LastWriteTimeUtc.Ticks, 0, 0));
    }

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellation)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaximumBytes) throw new ImageSourceException("图片超过 16 MiB 上限");
        byte[] bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
        if (await file.ReadAsync(new byte[1], cancellation).ConfigureAwait(false) != 0)
            throw new ImageSourceException("图片读取期间发生变化，请刷新预览");
        return bytes;
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
        else path = Uri.UnescapeDataString(target);
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
    private readonly MarkdownImageReference reference;
    private readonly string? sourcePath;
    private readonly MarkdownImageBudget budget;
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
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            loading?.Cancel();
            if (!finished) LoadingStarted = false;
            MarkdownImageScheduler.Unregister(this, host);
            if (host is not null) host.SizeChanged -= HostSizeChanged;
            host = null;
        };
    }

    internal bool LoadingStarted { get; private set; }

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
        MarkdownImageScheduler.Register(this, host);
    }

    internal async void StartLoading()
    {
        if (finished || loading is { IsCancellationRequested: false } || LoadingStarted) return;
        LoadingStarted = true;
        using var request = new CancellationTokenSource();
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
                    result.Accept();
                    image.Visibility = Visibility.Visible;
                    status.Visibility = Visibility.Collapsed;
                }
                else status.Text = "图片加载失败：" + result.Error;
                InvalidateMeasure();
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(loading, request)) loading = null;
            if (request.IsCancellationRequested && !finished) LoadingStarted = false;
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
