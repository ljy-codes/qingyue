using System.Windows.Documents;
using Qingyue.Core;
using Qingyue.Windows;

// Read-only audit against the actual UI renderer. Creates document objects only:
// no window, settings, session or source file is modified.
internal static class WpfAudit
{
    [STAThread]
    public static int Main()
    {
        int failed = 0;
        foreach (var (source, kind) in new[] {
            ("甲\n乙\n\n丙😀", TextKind.Plain),
            ("# 标题😀\n\n第一行\n第二行\n\n尾段", TextKind.Markdown),
            ("| A | B |\n| --- | --- |\n| 甲 | 乙 |", TextKind.Markdown) })
        {
            var model = ReadingModel.Build(source, kind);
            var rendered = new RenderedReading(model);
            for (int offset = 0; offset <= model.VisibleText.Length; offset++)
            {
                if (offset > 0 && offset < model.VisibleText.Length &&
                    char.IsHighSurrogate(model.VisibleText[offset - 1]) && char.IsLowSurrogate(model.VisibleText[offset])) continue;
                int actual = rendered.OffsetAt(rendered.PointerAt(offset));
                if (actual == offset) continue;
                failed++;
                Console.Error.WriteLine($"FAIL {kind} pointer roundtrip: display={offset}; actual={actual}");
            }
            Console.WriteLine($"{kind} visible={Escape(model.VisibleText)}; WPF={Escape(new TextRange(rendered.Document.ContentStart, rendered.Document.ContentEnd).Text)}");
        }
        Console.WriteLine($"WPF renderer mapping audit: {failed} failures");
        return failed == 0 ? 0 : 1;
    }

    private static string Escape(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
