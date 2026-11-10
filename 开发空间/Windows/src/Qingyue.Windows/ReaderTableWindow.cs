using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Qingyue.Windows;

/// <summary>Independent wide presentation; the reader's real Table stays selectable in place.</summary>
internal sealed class ReaderTableWindow : Window
{
    private ReaderTableWindow(Table source, Window? owner)
    {
        ReaderWindowTheme.Apply(this, owner);
        Title = "展开表格";
        Width = 1100;
        Height = 720;
        MinWidth = 380;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "EditorBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var table = Clone(source);
        // A wider page alone leaves fixed source columns narrow. Give the cloned
        // columns explicit readable widths, then size the page around those widths.
        double width = Math.Max(960, table.Columns.Sum(column => column.Width.Value) +
            48 + table.Margin.Left + table.Margin.Right + table.BorderThickness.Left +
            table.BorderThickness.Right + (table.Columns.Count + 1) * table.CellSpacing);
        var document = new FlowDocument(table)
        {
            PageWidth = width, ColumnWidth = double.PositiveInfinity,
            PagePadding = new Thickness(24), FontSize = source.FontSize, FontFamily = source.FontFamily
        };
        var host = new RichTextBox
        {
            Document = document, IsReadOnly = true, IsUndoEnabled = false, IsDocumentEnabled = true,
            BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        host.SetResourceReference(BackgroundProperty, "EditorBrush");
        host.SetResourceReference(ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(host, "可横向滚动的展开表格");
        Content = host;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    internal static void Attach(Table table)
    {
        var item = new MenuItem { Header = "展开表格（横向滚动）" };
        item.Click += (_, _) =>
        {
            var owner = FindHost(table) is { } host ? Window.GetWindow(host) : null;
            var window = new ReaderTableWindow(table, owner);
            window.Show();
        };
        table.ContextMenu = new ContextMenu { Items = { item } };
        table.ToolTip = "右键展开表格，可横向滚动查看";
    }

    private static RichTextBox? FindHost(DependencyObject source)
    {
        for (DependencyObject? parent = source; parent is not null;)
        {
            if (parent is RichTextBox host) return host;
            parent = parent is FrameworkContentElement content ? content.Parent :
                LogicalTreeHelper.GetParent(parent) ?? (parent is Visual ? VisualTreeHelper.GetParent(parent) : null);
        }
        return null;
    }

    // Whitelisted cloning creates new WPF nodes instead of moving live document
    // content or serializing event handlers/paths. Frozen decoded bitmaps alone may
    // be shared, so expansion does not load an image or consume another decode budget.
    private static Table Clone(Table source)
    {
        var table = new Table
        {
            CellSpacing = source.CellSpacing, FontSize = source.FontSize, FontFamily = source.FontFamily,
            Margin = source.Margin, BorderThickness = source.BorderThickness, BorderBrush = source.BorderBrush
        };
        double smallestStar = source.Columns.Where(column => column.Width.IsStar && column.Width.Value > 0)
            .Select(column => column.Width.Value).DefaultIfEmpty(1).Min();
        int columns = Math.Max(source.Columns.Count, source.RowGroups.SelectMany(group => group.Rows)
            .Select(row => row.Cells.Sum(cell => cell.ColumnSpan)).DefaultIfEmpty(0).Max());
        for (int index = 0; index < columns; index++)
        {
            var original = index < source.Columns.Count ? source.Columns[index].Width : GridLength.Auto;
            double width = original.IsAbsolute ? Math.Max(200, original.Value) :
                original.IsStar ? 200 * Math.Clamp(original.Value / smallestStar, 1, 4) : 200;
            table.Columns.Add(new TableColumn { Width = new GridLength(width) });
        }
        foreach (var group in source.RowGroups)
        {
            var copy = new TableRowGroup();
            table.RowGroups.Add(copy);
            foreach (var row in group.Rows)
            {
                var rowCopy = new TableRow { FontWeight = row.FontWeight, Background = row.Background, Foreground = row.Foreground };
                copy.Rows.Add(rowCopy);
                foreach (var cell in row.Cells)
                {
                    var cellCopy = new TableCell
                    {
                        Padding = cell.Padding, BorderThickness = cell.BorderThickness, BorderBrush = cell.BorderBrush,
                        TextAlignment = cell.TextAlignment
                    };
                    rowCopy.Cells.Add(cellCopy);
                    foreach (var paragraph in cell.Blocks.OfType<Paragraph>())
                    {
                        var paragraphCopy = new Paragraph { Margin = paragraph.Margin, TextAlignment = paragraph.TextAlignment };
                        cellCopy.Blocks.Add(paragraphCopy);
                        foreach (var inline in paragraph.Inlines)
                        {
                            if (inline is Run run)
                                paragraphCopy.Inlines.Add(new Run(run.Text)
                                {
                                    FontFamily = run.FontFamily, FontSize = run.FontSize, FontWeight = run.FontWeight,
                                    FontStyle = run.FontStyle, Foreground = run.Foreground, Background = run.Background,
                                    TextDecorations = run.TextDecorations?.Clone()
                                });
                            else if (inline is InlineUIContainer { Child: DependencyObject child })
                            {
                                var bitmap = FindImage(child);
                                if (bitmap?.Source is System.Windows.Media.Imaging.BitmapSource decoded)
                                {
                                    var image = new Image
                                    {
                                        Source = decoded, MaxWidth = 320, MaxHeight = 240, Stretch = Stretch.Uniform
                                    };
                                    ReaderImageWindow.Attach(image, image, "表格图片");
                                    paragraphCopy.Inlines.Add(new InlineUIContainer(image));
                                }
                            }
                        }
                    }
                }
            }
        }
        return table;
    }

    private static Image? FindImage(DependencyObject node)
    {
        if (node is Image image) return image;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindImage(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }
}
