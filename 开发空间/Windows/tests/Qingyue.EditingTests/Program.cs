using System.Diagnostics;
using Qingyue.Core;

internal static partial class Program
{
    private static int passed, failed;

    [STAThread]
    private static int Main()
    {
        Test("新文件编号跳过已打开与恢复名称且关闭后不复用", () =>
        {
            var names = new DocumentNames();
            Equal(names.Next(["新文件 1", "新文件 4", "readme.md"]), "新文件 5");
            Equal(names.Next([]), "新文件 6");
            Equal(names.Next(["新文件 18"]), "新文件 19");
        });
        Test("首个新文件编号与无关标题", () =>
            Equal(new DocumentNames().Next(["新文件 abc", "新文件 8.txt"]), "新文件 1"));
        Test("双击选中文连续片段而不越过标点", () =>
        {
            const string text = "首行\n你好世界，这是测试。\n末行";
            Selection(text, 6, false, "你好世界");
            Selection(text, 10, false, "这是测试");
            Selection(text, 7, false, "，");
        });
        Test("双击空白与中英文片段、代理对和组合字符边界", () =>
        {
            Selection("alpha beta", 7, false, "beta");
            Selection("alpha  beta", 5, false, "  ");
            Selection("😀你好 test", 1, false, "😀你好");
            Selection("a\u0301bc,def", 1, false, "a\u0301bc");
            Selection("hello", 5, false, "hello");
        });
        Test("三击选择完整逻辑行，保留CRLF原始偏移，不包含行尾", () =>
        {
            Selection("one\r\n中文长行\r\nlast", 7, true, "中文长行");
            Selection("one\r\n中文长行\r\nlast", 10, true, "中文长行");
            Selection("one\r\n", 5, true, "");
            Selection("", 0, true, "");
        });
        Test("相同内容与仅换行表示变化不标记", () =>
        {
            Changes(new LineChangeTracker("a\nb"), "a\r\nb");
            Changes(new LineChangeTracker("a\nb"), "a\rb");
        });
        Test("修改行橙色且撤销回磁盘基线清除", () =>
        {
            var tracker = new LineChangeTracker("a\nb\nc");
            Changes(tracker, "a\nB\nc", (2, LineChangeKind.Unsaved, false));
            Changes(tracker, "a\nb\nc");
        });
        Test("插入只标新行，后续原文行保持对齐", () =>
            Changes(new LineChangeTracker("a\nb\nc"), "a\nnew\nb\nc", (2, LineChangeKind.Unsaved, false)));
        Test("删除行在后继行边界提示，末尾删除仍可见", () =>
        {
            Changes(new LineChangeTracker("a\nb\nc"), "a\nc", (2, LineChangeKind.Unsaved, true));
            Changes(new LineChangeTracker("a\nb\nc"), "a\nb", (2, LineChangeKind.Unsaved, true));
            Changes(new LineChangeTracker("a"), "", (1, LineChangeKind.Unsaved, false));
        });
        Test("重复行、多个不相邻修改与尾部换行正确对齐", () =>
        {
            Changes(new LineChangeTracker("a\nx\nx\nz"), "a\nx\nnew\nx\nz", (3, LineChangeKind.Unsaved, false));
            Changes(new LineChangeTracker("a\nb\nc\nd\ne"), "A\nb\nc\nd\nE",
                (1, LineChangeKind.Unsaved, false), (5, LineChangeKind.Unsaved, false));
            Changes(new LineChangeTracker("a\n"), "a", (1, LineChangeKind.Unsaved, true));
        });
        Test("保存橙转绿，原始未改行不标记", () =>
        {
            var tracker = new LineChangeTracker("a\nb\nc").WithSavedBaseline("a\nB\nc");
            Changes(tracker, "a\nB\nc", (2, LineChangeKind.Saved, false));
            Changes(tracker, "a\nBB\nc", (2, LineChangeKind.Unsaved, false));
        });
        Test("保存后新增行使绿条随原文下移，橙色优先", () =>
        {
            var tracker = new LineChangeTracker("a\nb\nc").WithSavedBaseline("a\nB\nc");
            Changes(tracker, "a\nnew\nB\nc",
                (2, LineChangeKind.Unsaved, false), (3, LineChangeKind.Saved, false));
            Changes(tracker, "a\nc", (2, LineChangeKind.Unsaved, true));
        });
        Test("保存后撤销是未保存修改，再次保存初始原文清除全部标记", () =>
        {
            var tracker = new LineChangeTracker("a\nb").WithSavedBaseline("a\nB");
            Changes(tracker, "a\nb", (2, LineChangeKind.Unsaved, false));
            Changes(tracker.WithSavedBaseline("a\nb"), "a\nb");
        });
        Test("已保存删除提示与未保存插入同时保留", () =>
        {
            var tracker = new LineChangeTracker("a\nb\nc").WithSavedBaseline("a\nc");
            Changes(tracker, "new\na\nc",
                (1, LineChangeKind.Unsaved, false), (3, LineChangeKind.Saved, true));
        });
        Test("大文档两端小修改不误标中间原文", () =>
        {
            var baseline = string.Join('\n', Enumerable.Range(0, 40_000).Select(i => "line " + i));
            var text = "changed\n" + baseline[(baseline.IndexOf('\n') + 1)..] + "\nnew";
            var elapsed = Stopwatch.StartNew();
            Changes(new LineChangeTracker(baseline), text,
                (1, LineChangeKind.Unsaved, false), (40_001, LineChangeKind.Unsaved, false));
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("比对超过5秒");
        });
        Test("大量不连续修改保留中间未改行", () =>
        {
            string baseline = string.Join('\n', Enumerable.Range(0, 1200).Select(i => "original " + i));
            string current = string.Join('\n', Enumerable.Range(0, 1200).Select(i => i % 2 == 0 ? "edited " + i : "original " + i));
            var expected = Enumerable.Range(1, 1200).Where(i => i % 2 == 1)
                .Select(i => (i, LineChangeKind.Unsaved, false)).ToArray();
            Changes(new LineChangeTracker(baseline), current, expected);
        });
        Test("取消比对不会返回过时标记", () =>
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { new LineChangeTracker("a").Compare("b", cancelled.Token); }
            catch (OperationCanceledException) { return; }
            throw new Exception("未遵守取消信号");
        });
        Test("未修改大文本直接返回，不分配逐行diff数组", () =>
        {
            string baseline = string.Join('\n', Enumerable.Repeat("unchanged text", 50_000));
            var tracker = new LineChangeTracker(baseline);
            long start = GC.GetAllocatedBytesForCurrentThread();
            Equal(tracker.Compare(baseline).Count, 0);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            if (allocated > 16_384) throw new Exception("未修改文本仍分配了diff数据：" + allocated);
        });
        RunWpfTests();
        Console.WriteLine($"RESULT {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void Test(string name, Action run)
    {
        try { run(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + "\n" + ex); }
    }

    private static void Equal<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new Exception($"期望 {expected}，实际 {actual}");
    }

    private static void Selection(string text, int offset, bool line, string expected)
    {
        var range = line ? TextSelection.LogicalLineAt(text, offset) : TextSelection.SegmentAt(text, offset);
        Equal(text.Substring(range.Start, range.Length), expected);
    }

    private static void Changes(LineChangeTracker tracker, string text,
        params (int Line, LineChangeKind Kind, bool Deletion)[] expected)
    {
        var actual = tracker.Compare(text).Select(c => (c.Line, c.Kind, c.IsDeletion)).ToArray();
        if (!actual.SequenceEqual(expected))
            throw new Exception($"期望 [{string.Join(", ", expected)}]，实际 [{string.Join(", ", actual)}]");
    }

    static partial void RunWpfTests();
}
