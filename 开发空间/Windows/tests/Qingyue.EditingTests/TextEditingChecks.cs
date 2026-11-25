using Qingyue.Core;

internal static partial class Program
{
    private static void RunTextEditingTests()
    {
        Test("普通文本Tab插入两个空格并保留Unicode边界", () =>
        {
            Indent("", 0, 0, false, "  ", 2, 0);
            Indent("甲😀乙", 3, 0, false, "甲😀  乙", 5, 0);
        });
        Test("普通文本缩进完整选区覆盖行且不包含结束行首", () =>
        {
            Indent("a\nb\nc", 0, 4, false, "  a\n  b\nc", 0, 8);
            Indent("a\nb\nc", 0, 3, false, "  a\n  b\nc", 0, 7);
            Indent("a\nb\nc", 1, 1, false, "  a\nb\nc", 3, 1);
            Indent("abc\ndef", 1, 1, false, "  abc\ndef", 3, 1);
        });
        Test("普通文本CRLF、末尾和Unicode缩进与macOS一致", () =>
        {
            Indent("甲😀\n乙\n", 0, 6, false, "  甲😀\n  乙\n", 0, 10);
            Indent("last\n", 5, 0, false, "last\n  ", 7, 0);
            Indent("a\r\nb\r\nc", 0, 6, false, "  a\r\n  b\r\nc", 0, 10);
            Indent("x\ny", 0, 3, false, "    x\n    y", 0, 11, 4);
        });
        Test("普通文本Shift+Tab删除一个Tab或最多两个前导空格", () =>
        {
            Indent("  one\n    two\n\tthree", 0, 20, true, "one\n  two\nthree", 0, 15);
            Indent("  text", 1, 0, true, "text", 0, 0);
            Indent("    text", 4, 0, true, "  text", 2, 0);
            Indent(" text", 0, 2, true, "text", 0, 1);
            Indent("\t text", 2, 0, true, " text", 1, 0);
            Indent(" \ttext", 3, 0, true, "\ttext", 2, 0);
            Indent("  甲\n  😀\n  乙", 0, 9, true, "甲\n😀\n  乙", 0, 5);
            Equal(null, TextEditing.Indent("text", 2, 0, true));
            Equal(null, TextEditing.Indent("", 0, 0, true));
        });
        Test("普通文本缩进拒绝非法范围、切断代理对和零宽度", () =>
        {
            foreach (var (start, length) in new[] {
                (-1, 0), (0, -1), (0, int.MaxValue), (5, 0), (2, 0), (1, 1) })
                Equal(null, TextEditing.Indent("甲😀乙", start, length, false));
            Equal(null, TextEditing.Indent("x", 0, 0, false, 0));
            Equal(null, TextEditing.Indent("x", 0, 0, false, -1));
        });
        Test("普通文本全文缩进与取消缩进逐字节可逆", () =>
        {
            ulong state = 0x312AFA;
            int Random(int count)
            {
                state = state * 6364136223846793005UL + 1442695040888963407UL;
                return (int)((state >> 32) % (uint)count);
            }
            string[] newlines = ["\n", "\r\n", "\r"];
            string[] values = ["甲😀乙", "e\u0301", "key: value", ""];
            for (int iteration = 0; iteration < 500; iteration++)
            {
                int count = Random(12) + 1;
                string newline = newlines[Random(3)];
                string[] lines = Enumerable.Range(0, count)
                    .Select(_ => new string(' ', Random(7)) + values[Random(4)]).ToArray();
                string text = string.Join(newline, lines);
                int width = Random(6) + 1;
                var indented = TextEditing.Indent(text, 0, text.Length, false, width)
                    ?? throw new Exception("全文缩进不应失败");
                string changed = Apply(text, indented);
                var outdented = TextEditing.Indent(changed, indented.SelectionStart,
                    indented.SelectionLength, true, width)
                    ?? throw new Exception("全文取消缩进不应失败");
                Equal(text, Apply(changed, outdented));
                CheckBoundary(changed, indented.SelectionStart);
                CheckBoundary(changed, indented.SelectionStart + indented.SelectionLength);
            }
        });
    }

    private static void Indent(string text, int start, int length, bool remove,
        string expected, int selectionStart, int selectionLength, int spaces = 2)
    {
        var edit = TextEditing.Indent(text, start, length, remove, spaces)
            ?? throw new Exception($"缺少缩进结果：{text.Replace("\r", "\\r").Replace("\n", "\\n")}");
        Equal(expected, Apply(text, edit));
        Equal(selectionStart, edit.SelectionStart);
        Equal(selectionLength, edit.SelectionLength);
        CheckBoundary(expected, edit.SelectionStart);
        CheckBoundary(expected, edit.SelectionStart + edit.SelectionLength);
    }

    private static string Apply(string text, TextEditResult edit) =>
        text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];

    private static void CheckBoundary(string text, int offset)
    {
        if (offset < 0 || offset > text.Length ||
            offset > 0 && offset < text.Length &&
            char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]))
            throw new Exception("选区切断了Unicode代理对");
    }
}
