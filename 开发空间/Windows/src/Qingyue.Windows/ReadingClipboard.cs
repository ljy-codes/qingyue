using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Qingyue.Core;
using DocumentList = System.Windows.Documents.List;

namespace Qingyue.Windows;

public sealed record ReadingClipboardResult(DataObject Data, int EmbeddedImages, int OmittedImages);

public static class ReadingClipboard
{
    private const int MaximumText = 500_000, MaximumElements = 20_000, MaximumDepth = 64;
    private const int MaximumImages = 32, MaximumImageBytes = 4 * 1024 * 1024;
    private const int MaximumTotalImageBytes = 12 * 1024 * 1024, MaximumHtmlBytes = 20 * 1024 * 1024;
    private const int MaximumNativeBytes = 64 * 1024 * 1024;
    private static readonly ConditionalWeakTable<RichTextBox, Registration> Registrations = new();
    private static readonly ConditionalWeakTable<IDataObject, SelectionPayload> PreparedCopies = new();

    /// <summary>
    /// Adds HTML to WPF's existing copy data. Does not copy, focus or select anything.
    /// Attach once per preview; repeated calls only replace the enabling predicate.
    /// </summary>
    public static void Attach(RichTextBox preview, Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(enabled);
        preview.VerifyAccess();
        if (Registrations.TryGetValue(preview, out var registration))
            registration.Enabled = enabled;
        else
        {
            registration = new Registration(enabled);
            Registrations.Add(preview, registration);
            DataObject.AddSettingDataHandler(preview, (_, e) =>
            {
                if (e.Format != DataFormats.Xaml || e.IsDragDrop || !ReferenceEquals(e.OriginalSource, preview)
                    || !registration.Enabled() || preview.Selection.IsEmpty) return;
                try
                {
                    var payload = PrepareSelection(preview);
                    PreparedCopies.Remove(e.DataObject);
                    PreparedCopies.Add(e.DataObject, payload);
                    // Standalone WPF XAML cannot carry cached bitmap bytes. Suppress
                    // this stale text-only flavor before WPF puts it into the data
                    // object; Copying below supplies the self-contained XamlPackage.
                    if (payload.Package is not null) e.CancelCommand();
                }
                catch (Exception ex) when (IsExportFailure(ex)) { /* Keep native copy. */ }
            });
            DataObject.AddCopyingHandler(preview, (_, e) =>
            {
                if (e.IsDragDrop || !ReferenceEquals(e.OriginalSource, preview)
                    || !registration.Enabled() || preview.Selection.IsEmpty) return;
                try
                {
                    var payload = PreparedCopies.TryGetValue(e.DataObject, out var prepared)
                        ? prepared : PrepareSelection(preview);
                    e.DataObject.SetData(DataFormats.Html, payload.Html, false);
                    if (payload.Rtf is not null) e.DataObject.SetData(DataFormats.Rtf, payload.Rtf, false);
                    if (payload.Package is not null) e.DataObject.SetData(DataFormats.XamlPackage, payload.Package, false);
                    if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText, false))
                        e.DataObject.SetData(DataFormats.UnicodeText, preview.Selection.Text, false);
                }
                catch (Exception ex) when (IsExportFailure(ex)) { /* Keep native copy. */ }
                finally { PreparedCopies.Remove(e.DataObject); }
            });
        }
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
        try
        {
            var range = new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd);
            var export = new Export(range);
            string html = export.Html(preview.Document);
            // Serialize a detached, whitelisted document. Original image wrappers can
            // be Grids, which WPF's RTF writer does not treat as image objects.
            var safeRange = export.RichRange();
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, range.Text, false);
            data.SetData(DataFormats.Text, range.Text, false);
            data.SetData(DataFormats.Html, html, false);
            data.SetData(DataFormats.Rtf, CreateRtf(safeRange), false);
            data.SetData(DataFormats.XamlPackage, CreatePackage(safeRange), false);
            return new(data, export.EmbeddedImages, export.OmittedImages);
        }
        catch (Exception ex) when (IsExportFailure(ex)) { return null; }
    }

    private sealed class Registration(Func<bool> enabled)
    {
        internal Func<bool> Enabled = enabled;
    }

    private sealed record SelectionPayload(string Html, string? Rtf, MemoryStream? Package);

    private static SelectionPayload PrepareSelection(RichTextBox preview)
    {
        var export = new Export(preview.Selection);
        string html = export.Html(preview.Document);
        if (export.EmbeddedImages + export.OmittedImages == 0) return new(html, null, null);
        var safeRange = export.RichRange();
        string rtf = CreateRtf(safeRange);
        return new(html, rtf, CreatePackage(safeRange));
    }

    private static MemoryStream CreatePackage(TextRange range)
    {
        var stream = new LimitedStream(MaximumNativeBytes);
        try
        {
            range.Save(stream, DataFormats.XamlPackage);
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static string CreateRtf(TextRange range)
    {
        using var stream = new LimitedStream(MaximumNativeBytes);
        range.Save(stream, DataFormats.Rtf);
        return Encoding.ASCII.GetString(stream.ToArray());
    }

    private static bool IsExportFailure(Exception ex) =>
        ex is ExportLimitException or IOException or ArgumentException or InvalidOperationException
            or NotSupportedException or COMException or System.Security.SecurityException;

    private sealed class ExportLimitException : IOException;
    private sealed record ExportedImage(BitmapImage Bitmap, int Width, int Height);

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
        private int textLength, elementCount, imageBytes;
        private long imagePixels;
        internal int EmbeddedImages { get; private set; }
        internal int OmittedImages { get; private set; }
        internal List<XElement> Elements { get; } = [];

        internal TextRange RichRange()
        {
            var document = new FlowDocument();
            foreach (var element in Elements) document.Blocks.Add(RichBlock(element));
            return new TextRange(document.ContentStart, document.ContentEnd);
        }

        internal string Html(FlowDocument document)
        {
            foreach (Block block in document.Blocks)
                if (Block(block, 0) is { } element) Elements.Add(element);
            string fragment = "<div>" + string.Concat(Elements.Select(e => e.ToString(SaveOptions.DisableFormatting))) + "</div>";
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

        private void Visit(int depth)
        {
            if (++elementCount > MaximumElements || depth > MaximumDepth) throw new ExportLimitException();
        }

        private bool Intersects(TextElement element) =>
            range.Start.CompareTo(element.ElementEnd) < 0 && range.End.CompareTo(element.ElementStart) > 0;

        private bool SelectedObject(TextElement element) =>
            range.Start.CompareTo(element.ContentEnd) < 0 && range.End.CompareTo(element.ContentStart) > 0
                && range.Contains(element.ContentStart);

        private XElement? Block(Block block, int depth)
        {
            Visit(depth);
            if (!Intersects(block)) return null;
            switch (block)
            {
                case Paragraph paragraph:
                    var kind = paragraph.Tag as ReadingBlock;
                    string tag = kind?.Kind switch
                    {
                        ReadingBlockKind.Heading => "h" + Math.Clamp(kind.Level, 1, 6),
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
                    int rowIndex = 0;
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
                                var content = Container(rowIndex == 0 ? "th" : "td", cell.Blocks, depth + 3);
                                if (content is null && rectangle)
                                    content = new XElement(rowIndex == 0 ? "th" : "td",
                                        new XElement("p", new XElement("br")));
                                if (content is null) continue;
                                content.SetAttributeValue("style", "border:1px solid #999;padding:6px 10px;text-align:left");
                                if (cell.ColumnSpan > 1) content.SetAttributeValue("colspan", cell.ColumnSpan);
                                if (cell.RowSpan > 1) content.SetAttributeValue("rowspan", cell.RowSpan);
                                tr.Add(content);
                            }
                            if (tr.HasElements) result.Add(tr);
                            rowIndex++;
                        }
                    }
                    return result.HasElements ? result : null;
                case BlockUIContainer ui when SelectedObject(ui):
                    return new XElement("p", ImageContent(ui.Child, depth + 1));
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
                    object content = new XText(text);
                    if (run.TextDecorations.Any(d => d.Location == TextDecorationLocation.Strikethrough))
                        content = new XElement("s", content);
                    if (run.FontStyle == FontStyles.Italic) content = new XElement("em", content);
                    if (run.FontWeight.ToOpenTypeWeight() >= 600) content = new XElement("strong", content);
                    parent.Add(content);
                    break;
                case LineBreak br when SelectedObject(br):
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

        private XElement? ImageContent(UIElement? child, int depth)
        {
            Image? image = FindImage(child, depth);
            if (image is null) return null;
            // Do not dereference URIs, metadata paths, lazy frames, or other image
            // sources. This is exactly the cached source produced by our loader.
            if (image?.Visibility == Visibility.Visible && image.Source is BitmapImage
                { IsFrozen: true, IsDownloading: false, CacheOption: BitmapCacheOption.OnLoad, UriSource: null } bitmap
                && EmbeddedImages < MaximumImages && bitmap.PixelWidth is > 0 and <= 4096
                && bitmap.PixelHeight is > 0 and <= 4096
                && (long)bitmap.PixelWidth * bitmap.PixelHeight <= 16_777_216
                && imagePixels + (long)bitmap.PixelWidth * bitmap.PixelHeight <= 48_000_000)
            {
                try
                {
                    int remaining = Math.Min(MaximumImageBytes, MaximumTotalImageBytes - imageBytes);
                    using var stream = new LimitedStream(remaining);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(stream);
                    string alt = AutomationProperties.GetName(image);
                    if (alt.Length > 1024) alt = alt[..1024];
                    double width = Math.Min(720, bitmap.PixelWidth);
                    if (double.IsFinite(image.ActualWidth) && image.ActualWidth > 0)
                        width = Math.Min(width, image.ActualWidth);
                    if (double.IsFinite(image.ActualHeight) && image.ActualHeight > 0)
                        width = Math.Min(width, image.ActualHeight * bitmap.PixelWidth / bitmap.PixelHeight);
                    int displayWidth = Math.Max(1, (int)Math.Round(width));
                    int displayHeight = Math.Max(1, (int)Math.Round(width * bitmap.PixelHeight / bitmap.PixelWidth));
                    var element = new XElement("img",
                        new XAttribute("src", "data:image/png;base64," + Convert.ToBase64String(stream.ToArray())),
                        new XAttribute("alt", alt),
                        new XAttribute("width", displayWidth),
                        new XAttribute("height", displayHeight),
                        new XAttribute("style", "max-width:100%;height:auto"));
                    element.AddAnnotation(new ExportedImage(bitmap, displayWidth, displayHeight));
                    EmbeddedImages++;
                    imageBytes += (int)stream.Length;
                    imagePixels += (long)bitmap.PixelWidth * bitmap.PixelHeight;
                    return element;
                }
                catch (Exception ex) when (IsExportFailure(ex)) { /* Explicit placeholder below. */ }
            }
            OmittedImages++;
            return new XElement("span", "[图片未复制：尚未加载或超过复制上限]");
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
            inlines.Add(new InlineUIContainer(new Image { Source = image.Bitmap,
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
