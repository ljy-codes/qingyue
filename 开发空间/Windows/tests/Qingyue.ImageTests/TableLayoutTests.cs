using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Qingyue.Core;
using Qingyue.Windows;

internal static partial class Program
{
    private static void TableLayoutChecks()
    {
        Test("table gives a short index less space and a long path more usable space", ContentWeightedTable);
        Test("long tables and unbroken paths reflow on narrow screens without changing search mapping", LongTableResize);
        Test("weighted tables keep scaled text inside the viewport", ScaledTableResize);
    }

    private static void ContentWeightedTable()
    {
        const string path = "/configuration/integration/services/approval/production/settings.json";
        const string source = "| 序号 | 项目 | 说明/路径 |\n| --- | --- | --- |\n" +
            "| 1 | 接入配置 | " + path + " |\n| 2 | 回调配置 | 路径说明 |\n\n表后正文";
        var preview = new MarkdownPreview(source, 16, 680);
        Layout(preview, 1000);
        var table = preview.Document.Blocks.OfType<Table>().Single();
        var row = table.RowGroups[0].Rows[1];
        double indexStart = CellStart(row.Cells[0]).Left;
        double nameStart = CellStart(row.Cells[1]).Left;
        double pathStart = CellStart(row.Cells[2]).Left;
        double indexWidth = nameStart - indexStart;
        double nameWidth = pathStart - nameStart;
        double right = preview.ViewportWidth - preview.Document.PagePadding.Right;
        double pathWidth = right - pathStart + 12;
        Console.WriteLine($"  table columns: index {indexWidth:F1}, name {nameWidth:F1}, path {pathWidth:F1}");
        Check(indexWidth < 120 && nameWidth >= 100 && pathWidth > 300 && pathWidth > indexWidth * 2.5,
            $"序号列与说明/路径列仍等宽或挤压项目列：{indexWidth:F1}/{nameWidth:F1}/{pathWidth:F1}");
        var pathRun = ((Paragraph)row.Cells[2].Blocks.FirstBlock).Inlines.OfType<Run>().Single();
        var first = pathRun.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var fortieth = pathRun.ContentStart.GetPositionAtOffset(40)!.GetCharacterRect(LogicalDirection.Forward);
        Check(Math.Abs(first.Top - fortieth.Top) < 1, "可用宽度充足时路径仍被过度折行");
        Check(Text(preview.Document).Contains(path), "列宽调整改变了路径正文");
    }

    private static void LongTableResize()
    {
        string longPath = "/very/long/unbroken/" + new string('x', 5000) + "/终点😀";
        var source = new StringBuilder("| 序号 | 项目 | 路径 | 状态 |\n| --- | --- | --- | --- |\n");
        for (int i = 1; i <= 120; i++)
            source.Append("| ").Append(i).Append(" | 项目").Append(i).Append(" | /config/service/")
                .Append(i).Append("/settings.json | 正常 |\n");
        source.Append("| 121 | 长路径项目 | ").Append(longPath).Append(" | 待检查 |\n\n表后定位");
        string markdown = source.ToString();
        var model = ReadingModel.Build(markdown, TextKind.Markdown);
        var reading = RenderedReading.Markdown(model, markdown, null, 16, 680);
        var document = reading.Document;
        var table = document.Blocks.OfType<Table>().Single();
        var tag = table.Tag;
        var host = new RichTextBox
        {
            Document = document, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        host.SizeChanged += (_, _) => reading.UpdateLayout(host, 680);
        reading.UpdateLayout(host, 680);
        foreach (double width in new[] { 1000d, 420, 320, 1000 })
        {
            Layout(host, width);
            Check(ReferenceEquals(document, reading.Document) && ReferenceEquals(tag, table.Tag),
                "长表 resize 重建了文档或源块 Tag");
            double right = host.ViewportWidth - document.PagePadding.Right;
            var header = table.RowGroups[0].Rows[0];
            var lastHeader = CellStart(header.Cells[^1]);
            Check(lastHeader.Left < right && lastHeader.Right <= right + 1,
                $"窄屏表格列越过视口：宽 {width}，末列 {lastHeader}，右界 {right}");
            double firstWidth = CellStart(header.Cells[1]).Left - CellStart(header.Cells[0]).Left;
            Console.WriteLine($"  long table viewport {width}, index column {firstWidth:F1}");
            Check(firstWidth >= 30, $"长路径挤没了序号列：视图 {width}，列宽 {firstWidth:F1}");
            foreach (string token in new[] { "长路径项目", "/终点😀", "待检查", "表后定位" })
            {
                int offset = model.VisibleText.IndexOf(token, StringComparison.Ordinal);
                var begin = reading.PointerAt(offset);
                Check(new TextRange(begin, reading.PointerAt(offset + token.Length)).Text == token &&
                    reading.OffsetAt(begin) == offset, "长表或长路径 resize 破坏映射：" + token);
            }
            var lastRow = table.RowGroups[0].Rows[^1];
            var pathRun = ((Paragraph)lastRow.Cells[2].Blocks.FirstBlock).Inlines.OfType<Run>().Single();
            var end = pathRun.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            Check(end.Right <= right + 1 && end.Left >= document.PagePadding.Left,
                $"长行没有在窄屏内折行：宽 {width}，行尾 {end}，右界 {right}");
        }
        Check(Text(document).Contains(longPath), "长表折行截断了源文");
    }

    private static Rect CellStart(TableCell cell) =>
        ((Paragraph)cell.Blocks.FirstBlock).ContentStart.GetCharacterRect(LogicalDirection.Forward);

    private static void ScaledTableResize()
    {
        const string source = "| 序号 | 项目 | 说明/路径 |\n| --- | --- | --- |\n" +
            "| 1 | 中文名称😀 | /configuration/integration/services/approval/settings.json |\n\n尾段";
        foreach (double fontSize in new[] { 12d, 24, 32 })
        {
            var preview = new MarkdownPreview(source, fontSize, 680);
            var model = ReadingModel.Build(source, TextKind.Markdown);
            var map = new RichDocumentMap(preview.Document, model);
            foreach (double width in new[] { 1000d, 320, 1000 })
            {
                Layout(preview, width);
                var table = preview.Document.Blocks.OfType<Table>().Single();
                var header = table.RowGroups[0].Rows[0];
                double right = preview.ViewportWidth - preview.Document.PagePadding.Right;
                for (int column = 0; column < header.Cells.Count; column++)
                {
                    var start = CellStart(header.Cells[column]);
                    double boundary = column + 1 < header.Cells.Count
                        ? CellStart(header.Cells[column + 1]).Left : right;
                    Check(start.Right <= boundary + 1,
                        $"字体缩放导致表格文字越界：字体 {fontSize}，宽 {width}，列 {column}");
                }
                SelectFragment(map, model.VisibleText, "中文名称😀");
                SelectFragment(map, model.VisibleText, "/configuration/integration/services/approval/settings.json");
            }
        }
    }
}
