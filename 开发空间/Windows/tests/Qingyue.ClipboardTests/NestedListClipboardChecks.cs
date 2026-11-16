using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Xml.Linq;
using Qingyue.Windows;
using DocumentList = System.Windows.Documents.List;

internal static class NestedListClipboardChecks
{
    internal static void Structure()
    {
        const string source = "- 父\n  3. 有序\n     - [x] 任务\n\n  父续段\n- 尾";
        var preview = new MarkdownPreview(source, 16, 680);
        var root = Export(preview);
        var list = root.Element("ul");
        Check(list is not null && list.Elements("li").Count() == 2, "root list item count: " + root);
        var parent = list!.Elements("li").First();
        var ordered = parent.Element("ol");
        Check(ordered?.Attribute("start")?.Value == "3", "ordered list must be inside its parent li: " + root);
        Check(ordered!.Element("li")?.Element("ul")?.Value == "☑ 任务", "third-level task HTML: " + root);
        Check(parent.Elements("p").Last().Value == "父续段", "continuation belongs to parent li: " + root);
        Check(list.Elements("li").Last().Value == "尾", "tail sibling must stay outside nested lists");
    }

    internal static void Numbering()
    {
        var preview = new MarkdownPreview("- 父\n  0) 零\n  4. 四\n  9. 九\n  10. 十\n- 尾", 16, 680);
        var parent = Export(preview).Element("ul")?.Elements("li").First();
        Check(parent is not null, "root parent");
        Check(parent!.Element("ul")?.Value == "0) 零", "zero marker stays literal in nested child: " + parent);
        Check(parent.Elements("ol").Select(e => e.Attribute("start")?.Value).SequenceEqual(["4", "9"]),
            "discontinuous numbering must retain explicit starts: " + parent);
        Check(parent.Elements("ol").Last().Elements("li").Select(e => e.Value).SequenceEqual(["九", "十"]),
            "following contiguous items reuse their native numbering");
    }

    internal static void Partial()
    {
        var preview = new MarkdownPreview("- 父\n  - 子一\n  - 子二\n- 尾", 16, 680);
        var list = preview.Document.Blocks.OfType<DocumentList>().Single();
        var child = list.ListItems.FirstListItem!.Blocks.OfType<DocumentList>().SingleOrDefault();
        Check(child is not null, "partial copy needs a real child list");
        var paragraph = (Paragraph)child!.ListItems.LastListItem!.Blocks.FirstBlock;
        var run = paragraph.Inlines.OfType<Run>().Single();
        preview.Selection.Select(run.ContentStart, run.ContentEnd);
        string selected = preview.Selection.Text;
        var start = preview.Selection.Start;
        var end = preview.Selection.End;
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, preview.Selection.Text, false);
        ReadingClipboard.Attach(preview, () => true);
        preview.RaiseEvent(new DataObjectCopyingEventArgs(data, false));
        var fragment = Fragment(data);
        Check(fragment.Value == "子二" && fragment.Descendants("ul").Count() == 2,
            "selection must preserve nesting while excluding all unselected text: " + fragment);
        Check(preview.Selection.Text == selected && preview.Selection.Start.CompareTo(start) == 0 &&
            preview.Selection.End.CompareTo(end) == 0, "copy must retain selection");
    }

    private static XElement Export(MarkdownPreview preview)
    {
        var result = ReadingClipboard.CreateFullDocumentData(preview);
        Check(result is not null, "full-document clipboard export");
        return Fragment(result!.Data);
    }

    private static XElement Fragment(IDataObject data)
    {
        Check(data.GetDataPresent(DataFormats.Html, false), "nested export requires CF_HTML");
        string cf = (string)data.GetData(DataFormats.Html, false);
        int Offset(string key) => int.Parse(cf.AsSpan(cf.IndexOf(key + ":", StringComparison.Ordinal) + key.Length + 1, 10));
        byte[] bytes = Encoding.UTF8.GetBytes(cf);
        return XElement.Parse(Encoding.UTF8.GetString(bytes, Offset("StartFragment"), Offset("EndFragment") - Offset("StartFragment")));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
