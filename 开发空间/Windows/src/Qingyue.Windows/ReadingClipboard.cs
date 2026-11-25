using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Qingyue.Core;
using DocumentList = System.Windows.Documents.List;

namespace Qingyue.Windows;

public sealed record ReadingImageReport(int Embedded, int Pending, int Failed, int Unsupported, int Limited)
{
    public bool HasUnrenderedImages { get; init; }
    public int Omitted => Pending + Failed + Unsupported + Limited;
    public bool Incomplete => Omitted > 0 || HasUnrenderedImages;
    public string Description => string.Join("；", new[]
    {
        Pending > 0 ? $"{Pending} 张图片尚未加载" : null,
        Failed > 0 ? $"{Failed} 张图片加载失败" : null,
        Unsupported > 0 ? $"{Unsupported} 张图片不是受支持的已缓存图片" : null,
        Limited > 0 ? $"{Limited} 张图片超出资源上限" : null,
        HasUnrenderedImages ? "本篇图片超过 32 张，部分图片因资源限制未加载，未包含在本次内容中" : null
    }.Where(s => s is not null));
}

public sealed record ReadingImageWaitProgress(int PendingImages, TimeSpan Elapsed);

public sealed record ReadingClipboardResult(DataObject Data, int EmbeddedImages, int OmittedImages)
{
    public ReadingImageReport Images { get; init; } = new(EmbeddedImages, OmittedImages, 0, 0, 0);
    public string? DegradationReason { get; init; }
}

internal sealed record ReadingClipboardSnapshot(
    string PlainText,
    string Html,
    string? Rtf,
    MemoryStream? XamlPackage,
    BitmapSource? Bitmap,
    ReadingImageReport Images);

public static class ReadingClipboard
{
    private const int MaximumText = 2_000_000, MaximumElements = 20_000, MaximumDepth = 64;
    private const int MaximumImages = 32;
    private const int MaximumEmbeddedPngBytes = 20 * 1024 * 1024;
    private const int MaximumHtmlBytes = 20 * 1024 * 1024;
    private const int MaximumNativeBytes = MaximumHtmlBytes;
    private const int MaximumImagePixels = 16_000_000;
    private const long MaximumTotalPixels = 48_000_000;
    private static readonly ConditionalWeakTable<RichTextBox, Registration> Registrations = new();
    private static readonly ConditionalWeakTable<IDataObject, SelectionPayload> PreparedCopies = new();

    public static DataObject CreateMarkdownData(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (markdown.Length > DocumentFile.MaximumSize) throw new IOException("Markdown 超过 50 MiB 内容上限。");
        return TextData(markdown);
    }

    public static DataObject CreatePlainTextData(RichTextBox preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        preview.VerifyAccess();
        string text = DisplayText(new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text);
        if (text.Length > MaximumText) throw new IOException("阅读正文超过 200 万个 UTF-16 单元复制上限。");
        return TextData(text);
    }

    private static string DisplayText(string text) => text.Replace("\u2028", Environment.NewLine, StringComparison.Ordinal);

    private static DataObject TextData(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text, false);
        data.SetData(DataFormats.Text, text, false);
        return data;
    }

    public static ReadingClipboardResult? CreateCurrentCodeBlockData(RichTextBox preview) =>
        CreateCurrentBlockData(preview, code: true);

    public static ReadingClipboardResult? CreateCurrentTableData(RichTextBox preview) =>
        CreateCurrentBlockData(preview, code: false);

    private static Block? CurrentBlock(RichTextBox preview, bool code)
    {
        preview.VerifyAccess();
        Block? Find(TextPointer pointer)
        {
            for (DependencyObject? parent = pointer.Parent; parent is FrameworkContentElement element; parent = element.Parent)
                if (code ? element is Paragraph { Tag: ReadingBlock { Kind: ReadingBlockKind.Code } }
                    : element is Table) return (Block)element;
            return null;
        }
        var first = Find(preview.Selection.Start);
        if (first is null) return null;
        if (preview.Selection.IsEmpty) return first;
        // End is exclusive. A selection ending exactly at the following block's
        // insertion boundary still belongs to this block.
        var end = preview.Selection.End.GetNextInsertionPosition(LogicalDirection.Backward);
        return end is not null && ReferenceEquals(first, Find(end)) ? first : null;
    }

    private static ReadingClipboardResult? CreateCurrentBlockData(RichTextBox preview, bool code)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var block = Registrations.TryGetValue(preview, out var registration) &&
            registration.CommandBlock is { } commandBlock
            ? commandBlock
            : CurrentBlock(preview, code);
        return block is null ? null : CreateData(preview.Document, new TextRange(block.ElementStart, block.ElementEnd));
    }

    /// <summary>Polls only generated local control state. Never starts an image load.</summary>
    public static async Task WaitForImagesAsync(RichTextBox preview, CancellationToken cancellationToken = default,
        IProgress<ReadingImageWaitProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        preview.VerifyAccess();
        var guard = new DocumentGuard(preview);
        var watch = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pending = await preview.Dispatcher.InvokeAsync(() =>
                {
                    guard.Verify();
                    return CountPendingImages(guard.Document);
                }, DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
                progress?.Report(new(pending, watch.Elapsed));
                if (pending == 0 || watch.Elapsed >= TimeSpan.FromSeconds(5))
                {
                    await preview.Dispatcher.InvokeAsync(guard.Verify, DispatcherPriority.Background, cancellationToken)
                        .Task.ConfigureAwait(false);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(250, 5000 - watch.Elapsed.TotalMilliseconds))),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally { await preview.Dispatcher.InvokeAsync(guard.Dispose).Task.ConfigureAwait(false); }
    }

    public static async Task<ReadingClipboardResult?> CreateFullDocumentDataAsync(RichTextBox preview,
        CancellationToken cancellationToken = default, IProgress<ReadingImageWaitProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        preview.VerifyAccess();
        var guard = new DocumentGuard(preview);
        try
        {
            await WaitForImagesAsync(preview, cancellationToken, progress).ConfigureAwait(false);
            return await preview.Dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                guard.Verify();
                return CreateFullDocumentData(preview);
            }, DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
        }
        finally { await preview.Dispatcher.InvokeAsync(guard.Dispose).Task.ConfigureAwait(false); }
    }

    internal sealed class DocumentGuard : IDisposable
    {
        private readonly RichTextBox preview;
        private bool changed;
        internal FlowDocument Document { get; }
        internal DocumentGuard(RichTextBox preview)
        {
            preview.VerifyAccess();
            this.preview = preview;
            Document = preview.Document;
            preview.TextChanged += Changed;
        }
        private void Changed(object sender, TextChangedEventArgs e) => changed = true;
        internal void Verify()
        {
            preview.VerifyAccess();
            if (changed || !ReferenceEquals(preview.Document, Document))
                throw new InvalidOperationException("阅读内容已更新，请重新复制或导出。");
        }
        public void Dispose()
        {
            preview.VerifyAccess();
            preview.TextChanged -= Changed;
        }
    }

    /// <summary>
    /// Adds HTML to WPF's existing copy data. Does not copy, focus or select anything.
    /// Attach once per preview; repeated calls only replace the enabling predicate.
    /// </summary>
    public static void Attach(RichTextBox preview, Func<bool> enabled, Action<string>? report = null,
        Action<string>? command = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(enabled);
        preview.VerifyAccess();
        if (Registrations.TryGetValue(preview, out var registration))
        {
            registration.Enabled = enabled;
            registration.Report = report;
            registration.Command = command;
        }
        else
        {
            registration = new Registration(enabled, report, command);
            Registrations.Add(preview, registration);
            InstallBlockMenu(preview, registration);
            DataObject.AddSettingDataHandler(preview, (_, e) =>
            {
                if (e.Format != DataFormats.Xaml || e.IsDragDrop || !ReferenceEquals(e.OriginalSource, preview)
                    || !registration.Enabled() || preview.Selection.IsEmpty) return;
                PreparedCopies.Remove(e.DataObject);
                try
                {
                    var payload = PrepareSelection(preview);
                    PreparedCopies.Add(e.DataObject, payload);
                    // Never allow WPF to serialize the live document into a
                    // standalone XAML flavor. Copying below publishes the
                    // detached snapshot result instead.
                    e.CancelCommand();
                }
                catch (Exception ex) when (IsExportFailure(ex)) { /* Keep native copy. */ }
            });
            DataObject.AddCopyingHandler(preview, (_, e) =>
            {
                if (e.IsDragDrop || !ReferenceEquals(e.OriginalSource, preview)
                    || !registration.Enabled() || preview.Selection.IsEmpty) return;

                SelectionPayload payload;
                try
                {
                    payload = PreparedCopies.TryGetValue(e.DataObject, out var prepared)
                        ? prepared : PrepareSelection(preview);
                }
                catch (Exception ex) when (IsExportFailure(ex))
                {
                    PreparedCopies.Remove(e.DataObject);
                    return;
                }

                try
                {
                    var result = payload.Result;
                    if (result.DegradationReason is { Length: > 0 } reason)
                    {
                        e.CancelCommand();
                        Clipboard.SetDataObject(result.Data, true);
                        registration.Report?.Invoke(reason);
                        return;
                    }

                    // Read every format from the completed result before
                    // touching WPF's event object. The package is copied so
                    // its position and lifetime are independent of the source.
                    string? unicodeText = result.Data.GetData(DataFormats.UnicodeText, false) as string;
                    string? text = result.Data.GetData(DataFormats.Text, false) as string;
                    string? rtf = result.Data.GetData(DataFormats.Rtf, false) as string;
                    MemoryStream? package = CopyPackage(result.Data);
                    BitmapSource? bitmap = result.Data.GetData(DataFormats.Bitmap, false) as BitmapSource;
                    string? html = result.Data.GetData(DataFormats.Html, false) as string;

                    if (unicodeText is not null) e.DataObject.SetData(DataFormats.UnicodeText, unicodeText, false);
                    if (text is not null) e.DataObject.SetData(DataFormats.Text, text, false);
                    if (rtf is not null) e.DataObject.SetData(DataFormats.Rtf, rtf, false);
                    if (package is not null) e.DataObject.SetData(DataFormats.XamlPackage, package, false);
                    if (bitmap is not null) e.DataObject.SetData(DataFormats.Bitmap, bitmap, false);
                    if (html is not null) e.DataObject.SetData(DataFormats.Html, html, false);
                }
                finally { PreparedCopies.Remove(e.DataObject); }
            });
        }
    }

    /// <summary>
    /// Applies the same eight-command reading menu to tables, images and diagrams
    /// after their generated document has been attached to the preview.
    /// </summary>
    public static void RefreshContextMenus(RichTextBox preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        preview.VerifyAccess();
        if (!Registrations.TryGetValue(preview, out var registration)) return;
        var visited = new HashSet<DependencyObject>();
        void Visit(DependencyObject node)
        {
            if (!visited.Add(node)) return;
            switch (node)
            {
                case Table table:
                    table.ContextMenu = BuildContextMenu(preview, registration,
                        new ReadingContextTarget(Table: table));
                    break;
                case MermaidBlockPanel panel:
                    panel.ContextMenu = BuildContextMenu(preview, registration,
                        new ReadingContextTarget(Diagram: panel));
                    break;
                case MarkdownImageView view:
                    view.ContextMenu = BuildContextMenu(preview, registration,
                        new ReadingContextTarget(Image: view));
                    view.ImageControl.ContextMenu = BuildContextMenu(preview, registration,
                        new ReadingContextTarget(Image: view));
                    break;
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject nested) Visit(nested);
        }
        Visit(preview.Document);
    }

    /// <summary>
    /// Creates an in-memory full-document payload without changing the live selection.
    /// Returns null when the document exceeds the text/structure/output budgets.
    /// Pending or excessive images have explicit placeholders and are counted.
    /// </summary>
    public static ReadingClipboardResult? CreateFullDocumentData(RichTextBox preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        preview.VerifyAccess();
        return CreateData(preview.Document, new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd));
    }

    private static ReadingClipboardResult? CreateData(FlowDocument document, TextRange range)
    {
        try
        {
            return CreateData(CreateSnapshot(document, range));
        }
        catch (Exception ex) when (IsExportFailure(ex))
        {
            try
            {
                string plainText = CreatePlainTextFallback(document, range);
                return DegradedData(plainText, ex);
            }
            catch (Exception plainEx) when (IsExportFailure(plainEx))
            {
                return null;
            }
        }
    }

    internal static ReadingClipboardSnapshot CreateSnapshot(FlowDocument document, TextRange range)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(range);
        var export = new Export(range);
        string html = export.Html(document);
        string plainText = export.PlainText(range.Text);
        var safeRange = export.RichRange();
        return new(
            plainText,
            html,
            TryCreateRtf(safeRange),
            TryCreatePackage(safeRange),
            export.SingleBitmap,
            export.Images);
    }

    private static ReadingClipboardResult CreateData(ReadingClipboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var data = TextData(snapshot.PlainText);
        try
        {
            // Native formats are optional just like macOS RTF/RTFD. A failed or
            // oversized native serializer must never discard the HTML payload.
            if (snapshot.Rtf is not null) TrySetData(data, DataFormats.Rtf, snapshot.Rtf);
            if (snapshot.XamlPackage is not null)
                TrySetData(data, DataFormats.XamlPackage, new MemoryStream(snapshot.XamlPackage.ToArray()));
            if (snapshot.Bitmap is not null) TrySetData(data, DataFormats.Bitmap, snapshot.Bitmap);
            data.SetData(DataFormats.Html, snapshot.Html, false);
            return new(data, snapshot.Images.Embedded, snapshot.Images.Omitted)
            {
                Images = snapshot.Images
            };
        }
        catch (Exception ex) when (IsExportFailure(ex))
        {
            return DegradedData(snapshot.PlainText, ex, snapshot.Images);
        }
    }

    private static void TrySetData(DataObject data, string format, object value)
    {
        try { data.SetData(format, value, false); }
        catch (Exception ex) when (IsExportFailure(ex)) { }
    }

    private static string CreatePlainTextFallback(FlowDocument document, TextRange range)
    {
        var export = new Export(range);
        try { export.Html(document); }
        catch (Exception ex) when (IsExportFailure(ex)) { }
        return export.PlainText(range.Text);
    }

    private static ReadingClipboardResult DegradedData(string plainText, Exception cause,
        ReadingImageReport? images = null)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, plainText, false);
        data.SetData(DataFormats.Text, plainText, false);
        return new(data, images?.Embedded ?? 0, images?.Omitted ?? 0)
        {
            Images = images ?? new(0, 0, 0, 0, 0),
            DegradationReason = string.IsNullOrWhiteSpace(cause.Message)
                ? "富文本格式生成失败，已保留纯文本。"
                : cause.Message
        };
    }

    private sealed class Registration(Func<bool> enabled, Action<string>? report, Action<string>? command)
    {
        internal Func<bool> Enabled = enabled;
        internal Action<string>? Report = report;
        internal Action<string>? Command = command;
        internal TextPointer? ContextPointer;
        internal Block? CommandBlock;
    }

    private static void InstallBlockMenu(RichTextBox preview, Registration registration)
    {
        preview.ContextMenu = BuildContextMenu(preview, registration, null);
        preview.PreviewMouseRightButtonDown += (_, e) =>
            registration.ContextPointer = preview.GetPositionFromPoint(e.GetPosition(preview), true);
    }

    private sealed record ReadingContextTarget(
        Table? Table = null, MarkdownImageView? Image = null, MermaidBlockPanel? Diagram = null);

    private static ContextMenu BuildContextMenu(RichTextBox preview, Registration registration,
        ReadingContextTarget? target)
    {
        var menu = new ContextMenu();
        var copySelection = Add("复制选中图文", "copySelection");
        var copyFull = Add("复制全文（含图片）", "copyReading");
        var copyPlain = Add("复制全文纯文本", "copyPlain");
        var copyMarkdown = Add("复制 Markdown 源文", "copyMarkdown");
        var copyCode = Add("复制当前代码块", "copyCodeBlock");
        var copyTable = Add("复制当前表格", "copyTable");
        var expandTable = Add("展开表格（横向滚动）", "expandTable");
        var viewAttachment = Add("查看图片 / 图表", "viewAttachment");
        menu.Items.Add(copySelection);
        menu.Items.Add(copyFull);
        menu.Items.Add(copyPlain);
        menu.Items.Add(copyMarkdown);
        menu.Items.Add(copyCode);
        menu.Items.Add(copyTable);
        menu.Items.Add(expandTable);
        menu.Items.Add(viewAttachment);
        menu.Opened += (_, _) =>
        {
            bool enabled = registration.Enabled();
            Block? code = ContextBlock(preview, registration, target, code: true);
            Block? table = target?.Table ?? ContextBlock(preview, registration, target, code: false);
            copySelection.IsEnabled = enabled && !preview.Selection.IsEmpty;
            copyFull.IsEnabled = enabled;
            copyPlain.IsEnabled = enabled;
            copyMarkdown.IsEnabled = enabled;
            copyCode.IsEnabled = enabled && code is not null;
            copyTable.IsEnabled = enabled && table is not null;
            expandTable.IsEnabled = enabled && table is Table;
            viewAttachment.IsEnabled = enabled &&
                (target?.Diagram is not null || target?.Image?.IsReady == true);
        };
        copySelection.Click += (_, _) =>
        {
            if (!preview.Selection.IsEmpty) ApplicationCommands.Copy.Execute(null, preview);
        };
        foreach (var item in new[] { copyFull, copyPlain, copyMarkdown })
            item.Click += (_, _) => registration.Command?.Invoke(item.Tag as string ?? "");
        copyCode.Click += (_, _) => InvokeBlockCommand(preview, registration, target, "copyCodeBlock", code: true);
        copyTable.Click += (_, _) => InvokeBlockCommand(preview, registration, target, "copyTable", code: false);
        expandTable.Click += (_, _) =>
        {
            if ((target?.Table ?? ContextBlock(preview, registration, target, code: false)) is Table table)
                ReaderTableWindow.Show(table, Window.GetWindow(preview));
        };
        viewAttachment.Click += (_, _) =>
        {
            if (target?.Diagram is { } diagram)
                ReaderDiagramWindow.Show(diagram, Window.GetWindow(preview));
            else if (target?.Image is { } image)
                ReaderImageWindow.Show(image.ImageControl, image.Caption, Window.GetWindow(preview));
        };
        return menu;

        MenuItem Add(string title, string action) => new() { Header = title, Tag = action };
    }

    private static void InvokeBlockCommand(RichTextBox preview, Registration registration,
        ReadingContextTarget? target, string action, bool code)
    {
        Block? block = target?.Table is { } table && !code
            ? table
            : ContextBlock(preview, registration, target, code);
        if (block is null) return;
        registration.CommandBlock = block;
        try { registration.Command?.Invoke(action); }
        finally { registration.CommandBlock = null; }
    }

    private static Block? ContextBlock(RichTextBox preview, Registration registration,
        ReadingContextTarget? target, bool code)
    {
        if (!code && target?.Table is { } table) return table;
        TextPointer? pointer = target is null
            ? registration.ContextPointer ?? (preview.Selection.IsEmpty ? null : preview.Selection.Start)
            : null;
        return FindBlock(pointer, code) ?? (preview.Selection.IsEmpty ? null :
            FindBlock(preview.Selection.Start, code));
    }

    private static Block? FindBlock(TextPointer? pointer, bool code)
    {
        for (DependencyObject? parent = pointer?.Parent; parent is FrameworkContentElement element;
            parent = element.Parent)
            if (code ? element is Paragraph { Tag: ReadingBlock { Kind: ReadingBlockKind.Code } }
                : element is Table) return (Block)element;
        return null;
    }

    internal sealed record HtmlSnapshot(string Fragment, ReadingImageReport Images);

    internal static HtmlSnapshot? CreateHtmlSnapshot(RichTextBox preview)
    {
        preview.VerifyAccess();
        try
        {
            var export = new Export(new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd));
            return new(export.Fragment(preview.Document), export.Images);
        }
        catch (Exception ex) when (IsExportFailure(ex)) { return null; }
    }

    private sealed record SelectionPayload(ReadingClipboardResult Result);

    private static SelectionPayload PrepareSelection(RichTextBox preview) =>
        new(CreateData(preview.Document, preview.Selection)
            ?? throw new IOException("当前选区超过复制上限，无法生成纯文本快照。"));

    private static MemoryStream? CopyPackage(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.XamlPackage, false)) return null;
        if (data.GetData(DataFormats.XamlPackage, false) is not MemoryStream source)
            throw new InvalidOperationException("XamlPackage 快照不是可复制的内存流。");
        var copy = new MemoryStream(source.ToArray());
        copy.Position = 0;
        return copy;
    }

    private static MemoryStream? TryCreatePackage(TextRange range)
    {
        try
        {
            var stream = new LimitedStream(MaximumNativeBytes);
            try
            {
                range.Save(stream, DataFormats.XamlPackage);
                stream.Position = 0;
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (IsExportFailure(ex)) { return null; }
    }

    private static string? TryCreateRtf(TextRange range)
    {
        try
        {
            using var stream = new LimitedStream(MaximumNativeBytes);
            range.Save(stream, DataFormats.Rtf);
            return Encoding.ASCII.GetString(stream.ToArray());
        }
        catch (Exception ex) when (IsExportFailure(ex)) { return null; }
    }

    private static bool IsExportFailure(Exception ex) =>
        ex is ExportLimitException or IOException or ArgumentException or InvalidOperationException
            or NotSupportedException or COMException or System.Security.SecurityException;

    private sealed class ExportLimitException : IOException;
    private sealed record ExportedImage(BitmapSource Bitmap, int Width, int Height);
    private enum ImageState { Ready, Pending, Failed, Unsupported, Limited }

    private static ImageState ImageStatus(Image image, UIElement? wrapper)
    {
        if (image.Source is BitmapImage { IsFrozen: true, IsDownloading: false,
            CacheOption: BitmapCacheOption.OnLoad, UriSource: null } && image.Visibility == Visibility.Visible)
            return ImageState.Ready;
        if (image.Source is not null) return ImageState.Unsupported;
        int visited = 0;
        string? Status(UIElement? child, int depth = 0)
        {
            if (++visited > MaximumElements || depth > MaximumDepth) throw new ExportLimitException();
            if (child is TextBlock text) return text.Text.StartsWith("图片加载失败", StringComparison.Ordinal)
                || text.Text.StartsWith("图片未显示", StringComparison.Ordinal) ? text.Text : null;
            if (child is Panel panel)
                foreach (UIElement nested in panel.Children)
                    if (Status(nested, depth + 1) is { } found) return found;
            return child is Border border ? Status(border.Child, depth + 1) : null;
        }
        string? status = Status(wrapper);
        if (status?.StartsWith("图片加载失败", StringComparison.Ordinal) == true) return ImageState.Failed;
        if (status is not null) return ImageState.Limited;
        return ImageState.Pending;
    }

    private static int CountPendingImages(FlowDocument document)
    {
        int count = 0, visited = 0;
        void Walk(DependencyObject element, int depth, UIElement? wrapper = null)
        {
            if (++visited > MaximumElements || depth > MaximumDepth)
                throw new IOException("阅读内容超过结构上限，无法准备图片。");
            if (element is Image image)
            {
                if (ImageStatus(image, wrapper) == ImageState.Pending) count++;
                return;
            }
            // Only inspect generated logical children. No URI/source dereference,
            // visual template expansion, reflection, measure, focus or selection.
            foreach (object child in LogicalTreeHelper.GetChildren(element))
                if (child is DependencyObject nested)
                    Walk(nested, depth + 1, element is InlineUIContainer or BlockUIContainer
                        ? nested as UIElement : wrapper);
        }
        Walk(document, 0);
        return count;
    }

    private sealed class LimitedStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckLength(Position + count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckLength(Position + buffer.Length);
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            CheckLength(Position + 1);
            base.WriteByte(value);
        }
        public override void SetLength(long value)
        {
            CheckLength(value);
            base.SetLength(value);
        }
        private void CheckLength(long value)
        {
            if (value > maximum) throw new ExportLimitException();
        }
    }

    private sealed class Export(TextRange range)
    {
        private int textLength, elementCount;
        private long imagePixels;
        internal int EmbeddedImages { get; private set; }
        internal int OmittedImages { get; private set; }
        private BitmapSource? singleBitmap;
        private bool hasVisibleNonImageContent;
        private readonly List<string> mermaidSources = [];
        private readonly HashSet<ReadingBlock> exportedMermaidBlocks = [];
        private int pendingImages, failedImages, unsupportedImages, limitedImages;
        private bool hasUnrenderedImages;
        private bool CanPublishSingleBitmap =>
            EmbeddedImages == 1 && OmittedImages == 0 && textLength == 0 && !hasVisibleNonImageContent;
        internal BitmapSource? SingleBitmap =>
            CanPublishSingleBitmap ? singleBitmap : null;
        internal ReadingImageReport Images => new(EmbeddedImages, pendingImages, failedImages, unsupportedImages, limitedImages)
            { HasUnrenderedImages = hasUnrenderedImages };
        internal List<XElement> Elements { get; } = [];

        internal TextRange RichRange()
        {
            var document = new FlowDocument();
            foreach (var element in Elements) document.Blocks.Add(RichBlock(element));
            return new TextRange(document.ContentStart, document.ContentEnd);
        }

        internal string PlainText(string text)
        {
            text = DisplayText(text);
            foreach (string source in mermaidSources.Where(source => !text.Contains(source, StringComparison.Ordinal)))
                text = string.IsNullOrWhiteSpace(text)
                    ? source
                    : text.TrimEnd() + Environment.NewLine + Environment.NewLine + source;
            if (text.Length > MaximumText) throw new ExportLimitException();
            return text;
        }

        internal string Html(FlowDocument document)
        {
            string fragment = Fragment(document);
            const string prefix = "<html><head><meta charset=\"utf-8\" /></head><body><!--StartFragment-->";
            const string suffix = "<!--EndFragment--></body></html>";
            const string format = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\n"
                + "StartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            int header = string.Format(CultureInfo.InvariantCulture, format, 0, 0, 0, 0).Length;
            int start = header + Encoding.UTF8.GetByteCount(prefix);
            int end = start + Encoding.UTF8.GetByteCount(fragment);
            int total = end + Encoding.UTF8.GetByteCount(suffix);
            if (total > MaximumHtmlBytes) throw new ExportLimitException();
            return string.Format(CultureInfo.InvariantCulture, format, header, total, start, end)
                + prefix + fragment + suffix;
        }

        internal string Fragment(FlowDocument document)
        {
            foreach (Block block in document.Blocks)
                if (Block(block, 0) is { } element) Elements.Add(element);
            string fragment = "<div>" + string.Concat(Elements.Select(e =>
                e.ToString(SaveOptions.DisableFormatting).Replace("<br />", "<br>", StringComparison.Ordinal))) + "</div>";
            if (Encoding.UTF8.GetByteCount(fragment) > MaximumHtmlBytes) throw new ExportLimitException();
            return fragment;
        }

        private void Visit(int depth)
        {
            if (++elementCount > MaximumElements || depth > MaximumDepth) throw new ExportLimitException();
        }

        private bool Intersects(TextElement element) =>
            range.Start.CompareTo(element.ElementEnd) < 0 && range.End.CompareTo(element.ElementStart) > 0;

        private bool SelectedObject(TextElement element) =>
            range.Start.CompareTo(element.ContentEnd) < 0 && range.End.CompareTo(element.ContentStart) > 0
                && range.Contains(element.ContentStart);

        private bool SelectedBlock(BlockUIContainer block) =>
            (range.Start.CompareTo(block.ElementStart) <= 0 &&
                range.End.CompareTo(block.ElementEnd) >= 0) || SelectedObject(block);

        private XElement? Block(Block block, int depth)
        {
            Visit(depth);
            if (!Intersects(block)) return null;
            switch (block)
            {
                case Paragraph paragraph:
                    var kind = paragraph.Tag as ReadingBlock;
                    if (kind is not null && MermaidBlock.IsMermaid(kind)
                        && exportedMermaidBlocks.Contains(kind)) return null;
                    string tag = kind?.Kind switch
                    {
                        ReadingBlockKind.Heading => "h2",
                        ReadingBlockKind.Code => "pre",
                        ReadingBlockKind.Quote => "blockquote",
                        _ => "p"
                    };
                    var p = new XElement(tag);
                    foreach (Inline inline in paragraph.Inlines) Inline(inline, p, depth + 1);
                    if (!p.Nodes().Any())
                    {
                        if (!SelectedObject(paragraph)) return null;
                        p.Add(new XElement("br"));
                    }
                    if (tag == "pre") p = new XElement("pre", new XElement("code", p.Nodes().ToArray()));
                    else p.SetAttributeValue("style", "white-space:pre-wrap");
                    return p;
                case Section section:
                    return Container("div", section.Blocks, depth);
                case DocumentList list:
                    bool ordered = list.MarkerStyle is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin
                        or TextMarkerStyle.UpperLatin or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman;
                    var ul = new XElement(ordered ? "ol" : "ul");
                    int index = 0, first = -1;
                    foreach (ListItem item in list.ListItems)
                    {
                        Visit(depth + 1);
                        if (Intersects(item) && Container("li", item.Blocks, depth + 1) is { } li)
                        {
                            if (first < 0) first = index;
                            ul.Add(li);
                        }
                        index++;
                    }
                    if (!ul.HasElements) return null;
                    if (ordered) ul.SetAttributeValue("start", (long)list.StartIndex + first);
                    return ul;
                case Table table:
                    var result = new XElement("table", new XAttribute("style", "border-collapse:collapse"));
                    var firstCell = AncestorCell(range.Start);
                    var lastCell = AncestorCell(range.End.GetNextInsertionPosition(LogicalDirection.Backward));
                    bool rectangle = firstCell is not null && lastCell is not null && firstCell != lastCell
                        && ReferenceEquals(CellTable(firstCell), table) && ReferenceEquals(CellTable(lastCell), table);
                    int left = rectangle ? Math.Min(CellColumn(firstCell!), CellColumn(lastCell!)) : 0;
                    int right = rectangle ? Math.Max(CellColumn(firstCell!), CellColumn(lastCell!)) : int.MaxValue;
                    foreach (TableRowGroup group in table.RowGroups)
                    {
                        Visit(depth + 1);
                        foreach (TableRow row in group.Rows)
                        {
                            Visit(depth + 2);
                            var tr = new XElement("tr");
                            int column = 0;
                            foreach (TableCell cell in row.Cells)
                            {
                                Visit(depth + 3);
                                int cellColumn = column;
                                column += cell.ColumnSpan;
                                if (!Intersects(cell)) continue;
                                if (rectangle)
                                {
                                    // Markdown tables have no merged cells. Refuse an
                                    // ambiguous merged rectangular selection rather
                                    // than exporting any unselected cell.
                                    if (cell.RowSpan != 1 || cell.ColumnSpan != 1) throw new ExportLimitException();
                                    if (cellColumn < left || cellColumn > right) continue;
                                }
                                var content = Container("td", cell.Blocks, depth + 3);
                                if (content is null && rectangle)
                                    content = new XElement("td",
                                        new XElement("p", new XElement("br")));
                                if (content is null) continue;
                                var alignment = cell.Blocks.OfType<Paragraph>()
                                    .Select(paragraph => paragraph.TextAlignment).FirstOrDefault();
                                string align = alignment switch
                                {
                                    TextAlignment.Center => "center",
                                    TextAlignment.Right => "right",
                                    _ => "left"
                                };
                                content.SetAttributeValue("style",
                                    $"border:1px solid #999;padding:6px 10px;text-align:{align}");
                                if (cell.ColumnSpan > 1) content.SetAttributeValue("colspan", cell.ColumnSpan);
                                if (cell.RowSpan > 1) content.SetAttributeValue("rowspan", cell.RowSpan);
                                tr.Add(content);
                            }
                            if (tr.HasElements) result.Add(tr);
                        }
                    }
                    return result.HasElements ? result : null;
                case BlockUIContainer ui when SelectedBlock(ui):
                    return new XElement("p", ImageContent(ui.Child, depth + 1, ui.Tag as ReadingBlock));
                default:
                    return null;
            }
        }

        private XElement? Container(string tag, BlockCollection blocks, int depth)
        {
            var element = new XElement(tag);
            foreach (Block block in blocks)
                if (Block(block, depth + 1) is { } child) element.Add(child);
            return element.HasElements ? element : null;
        }

        private static TableCell? AncestorCell(TextPointer? pointer)
        {
            DependencyObject? parent = pointer?.Parent;
            while (parent is FrameworkContentElement element)
            {
                if (element is TableCell cell) return cell;
                parent = element.Parent;
            }
            return null;
        }

        private static Table? CellTable(TableCell cell) => (cell.Parent as TableRow)?.Parent is TableRowGroup group
            ? group.Parent as Table : null;

        private static int CellColumn(TableCell cell)
        {
            int column = 0;
            foreach (TableCell sibling in ((TableRow)cell.Parent).Cells)
            {
                if (ReferenceEquals(sibling, cell)) return column;
                column += sibling.ColumnSpan;
            }
            return column;
        }

        private void Inline(Inline inline, XElement parent, int depth)
        {
            Visit(depth);
            if (!Intersects(inline)) return;
            switch (inline)
            {
                case Run run:
                    TextPointer start = range.Start.CompareTo(run.ContentStart) > 0 ? range.Start : run.ContentStart;
                    TextPointer end = range.End.CompareTo(run.ContentEnd) < 0 ? range.End : run.ContentEnd;
                    // A cell's first insertion position also normalizes backwards
                    // to the preceding selected cell boundary. Probe after its first
                    // text symbol to distinguish an unselected adjacent cell.
                    if (start.CompareTo(end) >= 0 || !range.Contains(start.GetPositionAtOffset(1)!)) return;
                    int offset = run.ContentStart.GetOffsetToPosition(start);
                    int length = start.GetOffsetToPosition(end);
                    if ((textLength += length) > MaximumText) throw new ExportLimitException();
                    string text = run.Text.Substring(offset, length);
                    var nodes = TextNodes(text);
                    XElement? content = null;
                    if (run.TextDecorations.Any(d => d.Location == TextDecorationLocation.Strikethrough))
                        content = new XElement("s", content ?? (object)nodes);
                    if (run.FontStyle == FontStyles.Italic)
                        content = new XElement("em", content ?? (object)nodes);
                    if (run.FontWeight.ToOpenTypeWeight() >= 600)
                        content = new XElement("strong", content ?? (object)nodes);
                    if (content is null) parent.Add(nodes);
                    else parent.Add(content);
                    break;
                case LineBreak br when SelectedObject(br):
                    hasVisibleNonImageContent = true;
                    parent.Add(new XElement("br"));
                    break;
                case Span span:
                    var children = new XElement("span");
                    foreach (Inline child in span.Inlines) Inline(child, children, depth + 1);
                    if (children.Nodes().Any()) parent.Add(children.Nodes());
                    break;
                case InlineUIContainer ui when SelectedObject(ui):
                    parent.Add(ImageContent(ui.Child, depth + 1));
                    break;
            }
        }

        private static List<XNode> TextNodes(string text)
        {
            var nodes = new List<XNode>();
            int start = 0;
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] != '\u2028') continue;
                if (index > start) nodes.Add(new XText(text[start..index]));
                nodes.Add(new XElement("br"));
                start = index + 1;
            }
            if (start < text.Length) nodes.Add(new XText(text[start..]));
            if (nodes.Count == 0) nodes.Add(new XText(text));
            return nodes;
        }

        private XElement? ImageContent(UIElement? child, int depth, ReadingBlock? sourceBlock = null)
        {
            FrameworkElement? mermaid = FindMermaid(child, depth);
            if (mermaid is not null)
            {
                if (sourceBlock is { } source && MermaidBlock.IsMermaid(source))
                {
                    mermaidSources.Add(source.Text);
                    exportedMermaidBlocks.Add(source);
                    hasVisibleNonImageContent = true;
                    return new XElement("pre", new XElement("code", source.Text));
                }
                return null;
            }

            Image? image = FindImage(child, depth);
            if (image is null)
            {
                // The renderer uses one plain notice after its image-control
                // budget is exhausted. Preserve it without inventing a count.
                if (FindNotice(child, "图片未显示：本篇图片超过 32 张") is { } limitNotice)
                {
                    hasUnrenderedImages = true;
                    return new XElement("span", "[图片未包含：" + limitNotice + "]");
                }
                if (FindNotice(child, "图表暂未显示") is { } mermaidNotice)
                {
                    if (sourceBlock is { } source && MermaidBlock.IsMermaid(source))
                        mermaidSources.Add(source.Text);
                    hasVisibleNonImageContent = true;
                    return new XElement("span", mermaidNotice);
                }
                return null;
            }
            var state = ImageStatus(image, child);
            // Do not dereference URIs, metadata paths, lazy frames, or other image
            // sources. This is exactly the cached source produced by our loader.
            if (state == ImageState.Ready && image.Source is BitmapImage
                { IsFrozen: true, IsDownloading: false, CacheOption: BitmapCacheOption.OnLoad, UriSource: null } bitmap
                && EmbeddedImages < MaximumImages && bitmap.PixelWidth is > 0 and <= 4096
                && bitmap.PixelHeight is > 0 and <= 4096
                && (long)bitmap.PixelWidth * bitmap.PixelHeight <= MaximumImagePixels
                && imagePixels + (long)bitmap.PixelWidth * bitmap.PixelHeight <= MaximumTotalPixels)
            {
                try
                {
                    byte[] png = MarkdownImageView.TryGetDecodedPng(image, out var loadedPng)
                        ? loadedPng
                        : EncodePng(bitmap);
                    if (png.Length > MaximumEmbeddedPngBytes) throw new ExportLimitException();
                    string alt = AutomationProperties.GetName(image);
                    if (alt.Length > 1024) alt = alt[..1024];
                    if (alt.Contains("file:", StringComparison.OrdinalIgnoreCase) || alt.Contains(":/", StringComparison.Ordinal)
                        || alt.Contains(":\\", StringComparison.Ordinal)
                        || alt.Contains("\\\\", StringComparison.Ordinal) || alt.StartsWith('/')) alt = "图片";
                    double width = Math.Min(720, bitmap.PixelWidth);
                    if (double.IsFinite(image.ActualWidth) && image.ActualWidth > 0)
                        width = Math.Min(width, image.ActualWidth);
                    if (double.IsFinite(image.ActualHeight) && image.ActualHeight > 0)
                        width = Math.Min(width, image.ActualHeight * bitmap.PixelWidth / bitmap.PixelHeight);
                    int displayWidth = Math.Max(1, (int)Math.Round(width));
                    int displayHeight = Math.Max(1, (int)Math.Round(width * bitmap.PixelHeight / bitmap.PixelWidth));
                    BitmapSource frozen = FreezeBitmap(bitmap);
                    var element = new XElement("img",
                        new XAttribute("src", "data:image/png;base64," + Convert.ToBase64String(png)),
                        new XAttribute("alt", alt),
                        new XAttribute("width", displayWidth),
                        new XAttribute("height", displayHeight),
                        new XAttribute("style", "max-width:100%;height:auto"));
                    element.AddAnnotation(new ExportedImage(frozen, displayWidth, displayHeight));
                    EmbeddedImages++;
                    singleBitmap = CanPublishSingleBitmap ? frozen : null;
                    imagePixels += (long)bitmap.PixelWidth * bitmap.PixelHeight;
                    return element;
                }
                catch (Exception ex) when (IsExportFailure(ex)) { /* Explicit placeholder below. */ }
            }
            switch (state)
            {
                case ImageState.Pending: pendingImages++; break;
                case ImageState.Failed: failedImages++; break;
                case ImageState.Unsupported: unsupportedImages++; break;
                default: limitedImages++; break;
            }
            OmittedImages++;
            string reason = state switch
            {
                ImageState.Pending => "尚未加载",
                ImageState.Failed => "加载失败",
                ImageState.Unsupported => "不支持的图片来源",
                _ => "超过资源上限"
            };
            return new XElement("span", "[图片未包含：" + reason + "]");
        }

        private XElement EmbedBitmap(BitmapSource bitmap, string alt, int? displayWidth = null,
            int? displayHeight = null)
        {
            if (bitmap.PixelWidth is <= 0 or > 4096 || bitmap.PixelHeight is <= 0 or > 4096 ||
                (long)bitmap.PixelWidth * bitmap.PixelHeight > MaximumImagePixels ||
                EmbeddedImages >= MaximumImages ||
                imagePixels + (long)bitmap.PixelWidth * bitmap.PixelHeight > MaximumTotalPixels)
            {
                OmittedImages++;
                limitedImages++;
                return new XElement("span", "[图片未包含：超过资源上限]");
            }

            byte[] png;
            BitmapSource frozen;
            try
            {
                frozen = FreezeBitmap(bitmap);
                png = EncodePng(frozen);
            }
            catch (Exception ex) when (IsExportFailure(ex))
            {
                OmittedImages++;
                limitedImages++;
                return new XElement("span", "[图片未包含：图片编码失败]");
            }
            if (png.Length > MaximumEmbeddedPngBytes)
            {
                OmittedImages++;
                limitedImages++;
                return new XElement("span", "[图片未包含：超过资源上限]");
            }

            int width = displayWidth ?? Math.Min(720, bitmap.PixelWidth);
            int height = displayHeight ?? Math.Max(1, (int)Math.Round(
                width * (double)bitmap.PixelHeight / bitmap.PixelWidth));
            var element = new XElement("img",
                new XAttribute("src", "data:image/png;base64," + Convert.ToBase64String(png)),
                new XAttribute("alt", alt),
                new XAttribute("width", width),
                new XAttribute("height", height),
                new XAttribute("style", "max-width:100%;height:auto"));
            element.AddAnnotation(new ExportedImage(frozen, width, height));
            EmbeddedImages++;
            singleBitmap = CanPublishSingleBitmap ? frozen : null;
            imagePixels += (long)bitmap.PixelWidth * bitmap.PixelHeight;
            return element;
        }

        private static byte[] EncodePng(BitmapSource bitmap)
        {
            using var stream = new LimitedStream(MaximumEmbeddedPngBytes);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            return stream.ToArray();
        }

        private Image? FindImage(UIElement? element, int depth)
        {
            Visit(depth);
            if (element is Image image) return image;
            if (element is Panel panel)
            {
                foreach (UIElement child in panel.Children)
                    if (FindImage(child, depth + 1) is { } found) return found;
            }
            else if (element is Border border) return FindImage(border.Child, depth + 1);
            return null;
        }

        private FrameworkElement? FindMermaid(UIElement? element, int depth)
        {
            Visit(depth);
            if (element is MermaidSequenceView or MermaidFlowchartView) return (FrameworkElement)element;
            foreach (object child in LogicalTreeHelper.GetChildren(element))
                if (child is UIElement nested && FindMermaid(nested, depth + 1) is { } found)
                    return found;
            return null;
        }

        private string? FindNotice(UIElement? element, string prefix, int depth = 0)
        {
            Visit(depth);
            if (element is TextBlock text && text.Text.StartsWith(prefix, StringComparison.Ordinal))
                return text.Text;
            foreach (object child in LogicalTreeHelper.GetChildren(element))
                if (child is UIElement nested && FindNotice(nested, prefix, depth + 1) is { } found)
                    return found;
            return null;
        }
    }

    private static BitmapSource FreezeBitmap(BitmapSource bitmap)
    {
        if (bitmap.IsFrozen) return bitmap;
        var clone = bitmap.Clone();
        clone.Freeze();
        return clone;
    }

    // Native full-copy formats are produced by WPF from a detached document containing
    // only text, formatting and direct Image objects with already decoded pixels.
    private static Block RichBlock(XElement element)
    {
        string name = element.Name.LocalName;
        if (name == "table")
        {
            var table = new Table();
            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            foreach (var row in element.Elements("tr"))
            {
                var tr = new TableRow();
                foreach (var cell in row.Elements())
                {
                    var td = new TableCell { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                        Padding = new Thickness(6), ColumnSpan = (int?)cell.Attribute("colspan") ?? 1,
                        RowSpan = (int?)cell.Attribute("rowspan") ?? 1 };
                    foreach (var block in cell.Elements()) td.Blocks.Add(RichBlock(block));
                    if (cell.Name == "th") td.FontWeight = FontWeights.Bold;
                    tr.Cells.Add(td);
                }
                group.Rows.Add(tr);
            }
            return table;
        }
        if (name is "ul" or "ol")
        {
            var list = new DocumentList { MarkerStyle = name == "ol" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc };
            if (name == "ol") list.StartIndex = (int?)element.Attribute("start") ?? 1;
            foreach (var li in element.Elements("li"))
            {
                var item = new ListItem();
                foreach (var block in li.Elements()) item.Blocks.Add(RichBlock(block));
                list.ListItems.Add(item);
            }
            return list;
        }
        if (name == "div")
        {
            var section = new Section();
            foreach (var block in element.Elements()) section.Blocks.Add(RichBlock(block));
            return section;
        }
        var paragraph = new Paragraph();
        if (name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6')
        {
            paragraph.FontWeight = FontWeights.Bold;
            paragraph.FontSize = 14 + (7 - (name[1] - '0')) * 2;
        }
        if (name == "pre") paragraph.FontFamily = new FontFamily("Consolas");
        foreach (var node in element.Nodes()) AddRichInline(paragraph.Inlines, node);
        return paragraph;
    }

    private static void AddRichInline(InlineCollection inlines, XNode node)
    {
        if (node is XText text) { inlines.Add(new Run(text.Value)); return; }
        if (node is not XElement element) return;
        if (element.Name == "img" && element.Annotation<ExportedImage>() is { } image)
        {
            inlines.Add(new InlineUIContainer(new Image { Source = FreezeBitmap(image.Bitmap),
                Width = image.Width, Height = image.Height }));
            return;
        }
        if (element.Name == "br") { inlines.Add(new LineBreak()); return; }
        Span span = element.Name.LocalName switch { "strong" => new Bold(), "em" => new Italic(), _ => new Span() };
        if (element.Name == "s") span.TextDecorations = TextDecorations.Strikethrough;
        foreach (var child in element.Nodes()) AddRichInline(span.Inlines, child);
        inlines.Add(span);
    }
}
