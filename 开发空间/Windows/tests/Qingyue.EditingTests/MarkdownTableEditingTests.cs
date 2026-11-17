using System.Reflection;
using Qingyue.Core;

internal static partial class Program
{
    private const string TableFixture = "| A | B |\n| --- | :---: |\n| 中 | 😀 |";

    private static void RunMarkdownTableEditingTests()
    {
        Test("表格Tab选择下一单元格且不替换正文", () =>
        {
            var edit = TableApply(TableFixture, 2, 0, "NextCell")!;
            TableResult(TableFixture, edit, TableFixture, 6, 1);
            Equal(edit.Start, 2);
            Equal(edit.Length, 0);
            Equal(edit.Text, "");
        });
        Test("表格Tab跳过分隔行进入中文单元格", () =>
            TableResult(TableFixture, TableApply(TableFixture, 6, 1, "NextCell"), TableFixture, 28, 1));
        Test("表格ShiftTab返回上一格并跨行跳过分隔行", () =>
        {
            TableResult(TableFixture, TableApply(TableFixture, 32, 2, "PreviousCell"), TableFixture, 28, 1);
            TableResult(TableFixture, TableApply(TableFixture, 28, 0, "PreviousCell"), TableFixture, 6, 1);
            Equal(TableApply(TableFixture, 2, 0, "PreviousCell"), null);
        });
        Test("表格末格Tab只追加空行并选择新首格", () =>
        {
            var edit = TableApply(TableFixture, 32, 2, "NextCell")!;
            TableResult(TableFixture, edit,
                "| A | B |\n| --- | :---: |\n| 中 | 😀 |\n|  |  |", 39, 0);
            Equal(edit.Start, 36);
            Equal(edit.Length, 0);
        });
        Test("表格InsertRow插在当前数据行后保留后续内容", () =>
            TableText("| A | B |\n| --- | :---: |\n| 中 | 😀 |\n| 尾 | 末 |",
                28, "InsertRow",
                "| A | B |\n| --- | :---: |\n| 中 | 😀 |\n|  |  |\n| 尾 | 末 |"));
        Test("表格表头增行插在分隔行之后", () =>
            TableText(TableFixture, 2, "InsertRow",
                "| A | B |\n| --- | :---: |\n|  |  |\n| 中 | 😀 |"));
        Test("表格删除数据行保留表头与对齐分隔行", () =>
        {
            TableText(TableFixture, 28, "DeleteRow", "| A | B |\n| --- | :---: |");
            TableText("| A | B |\n| --- | :---: |\n| 中 | 😀 |\n| 尾 | 末 |",
                28, "DeleteRow", "| A | B |\n| --- | :---: |\n| 尾 | 末 |");
        });
        Test("表格不得隐式删除表头分隔行或最后一列", () =>
        {
            Equal(TableApply(TableFixture, 2, 0, "DeleteRow"), null);
            Equal(TableApply(TableFixture, 12, 0, "DeleteRow"), null);
            Equal(TableApply(TableFixture, 12, 0, "InsertRow"), null);
            Equal(TableApply("| A |\n| --- |\n| 中 |", 16, 0, "DeleteColumn"), null);
        });
        Test("表格插列保留内容与原有列对齐", () =>
            TableText(TableFixture, 28, "InsertColumn",
                "| A |  | B |\n| --- | --- | :---: |\n| 中 |  | 😀 |"));
        Test("表格删列保留其余内容和对齐", () =>
        {
            TableText(TableFixture, 28, "DeleteColumn", "| B |\n| :---: |\n| 😀 |");
            TableText(TableFixture, 32, "DeleteColumn", "| A |\n| --- |\n| 中 |");
        });
        Test("表格对齐仅替换当前列分隔标记", () =>
        {
            TableText(TableFixture, 28, "AlignLeft", "| A | B |\n| :--- | :---: |\n| 中 | 😀 |");
            TableText(TableFixture, 28, "AlignCenter", "| A | B |\n| :---: | :---: |\n| 中 | 😀 |");
            var edit = TableApply(TableFixture, 32, 2, "AlignRight")!;
            TableResult(TableFixture, edit, "| A | B |\n| --- | ---: |\n| 中 | 😀 |", 31, 2);
            Equal(edit.Start, 18);
            Equal(edit.Length, 1);
            Equal(edit.Text, "");
        });
        Test("表格已有对齐不产生正文修改并保留选区", () =>
            TableResult(TableFixture, TableApply(TableFixture, 32, 2, "AlignCenter"),
                TableFixture, 32, 2));
        Test("表格CRLF增行保留所有原换行", () =>
            TableText("| A | B |\r\n| --- | :---: |\r\n| 中 | 😀 |", 30, "InsertRow",
                "| A | B |\r\n| --- | :---: |\r\n| 中 | 😀 |\r\n|  |  |"));
        Test("表格转义竖线和反引号代码竖线不拆列", () =>
        {
            const string text = "| a\\|b | `c|d` |\n| --- | --- |\n| 中文 | ``e`|f`` |";
            var edit = TableApply(text, 2, 0, "NextCell")!;
            TableResult(text, edit, text, 9, 5);
            TableText(text, 2, "DeleteColumn", "| `c|d` |\n| --- |\n| ``e`|f`` |");
        });
        Test("表格偶数反斜杠不转义列分隔符", () =>
            TableText("| a\\\\| B |\n| --- | --- |\n| 中 | 尾 |", 2, "DeleteColumn",
                "| B |\n| --- |\n| 尾 |"));
        Test("表格局部操作保留缩进原始空白和表格外正文", () =>
            TableText("前文\n\n  |A| B  |\n  |---| :---: |\n  |中| 😀 |\n\n后文", 7, "InsertColumn",
                "前文\n\n  |A|  | B  |\n  |---| --- | :---: |\n  |中|  | 😀 |\n\n后文"));
        Test("表格支持省略外侧竖线", () =>
            TableText("A | B\n--- | :---:\n中 | 😀", 0, "InsertColumn",
                "A |  | B\n--- | --- | :---:\n中 |  | 😀"));
        Test("表格代码围栏内所有命令拒绝", () =>
        {
            foreach (string fence in new[] { "```markdown", "~~~~", "  ```" })
            {
                string text = fence + "\n" + TableFixture + "\n" +
                    (fence.Contains('~') ? "~~~~" : "```");
                int caret = text.IndexOf("中", StringComparison.Ordinal);
                foreach (string action in new[] { "NextCell", "InsertRow", "AlignLeft", "InsertTable" })
                    Equal(TableApply(text, caret, 0, action), null);
                Equal(TableTsv(text, caret, 0, "A\tB"), null);
            }
        });
        Test("表格缩进代码和Tab缩进代码拒绝", () =>
        {
            foreach (string indent in new[] { "    ", "\t", " \t" })
            {
                string text = indent + "| A | B |\n" + indent + "| --- | --- |\n" + indent + "| 中 | 尾 |";
                int caret = text.IndexOf("中", StringComparison.Ordinal);
                Equal(TableApply(text, caret, 0, "NextCell"), null);
                Equal(TableApply(text, caret, 0, "InsertTable"), null);
                Equal(TableTsv(text, caret, 0, "A\tB"), null);
            }
        });
        Test("表格不接管普通段落不完整表格或跨格选区", () =>
        {
            Equal(TableApply("正文 | 普通", 2, 0, "NextCell"), null);
            Equal(TableApply("| A | B |\n| --- |\n| 中 | 尾 |", 2, 0, "InsertColumn"), null);
            Equal(TableApply("| A | B |\n| --- | --- |\n| 中 |", 2, 0, "NextCell"), null);
            Equal(TableApply(TableFixture, 2, 5, "DeleteColumn"), null);
            Equal(TableApply(TableFixture, 2, 28, "DeleteRow"), null);
        });
        Test("表格UTF16代理对和CRLF中间边界拒绝", () =>
        {
            Equal(TableApply(TableFixture, 33, 0, "InsertRow"), null);
            Equal(TableApply(TableFixture, 32, 1, "NextCell"), null);
            Equal(TableTsv("😀", 1, 0, "A\tB"), null);
            Equal(TableApply("a\r\nb", 2, 0, "InsertTable"), null);
            Equal(TableTsv("a\r\nb", 2, 0, "A\tB"), null);
        });
        Test("表格Unicode导航保持组合字符和代理对选区完整", () =>
        {
            const string text = "| 中文 | e\u0301😀 |\n| --- | --- |";
            TableResult(text, TableApply(text, 2, 0, "NextCell"), text, 7, 4);
        });
        Test("表格InsertTable空文档给出可编辑模板", () =>
            TableResult("", TableApply("", 0, 0, "InsertTable"),
                "| 列 1 | 列 2 |\n| --- | --- |\n|  |  |", 2, 3));
        Test("表格InsertTable段落中插入隔离块且保留两侧文本", () =>
            TableResult("前后", TableApply("前后", 1, 0, "InsertTable"),
                "前\n\n| 列 1 | 列 2 |\n| --- | --- |\n|  |  |\n\n后", 5, 3));
        Test("表格InsertTable不在已有表格内嵌套", () =>
            Equal(TableApply(TableFixture, 2, 0, "InsertTable"), null));
        Test("表格TSV使用首行为表头转换并保持两侧正文", () =>
            TableResult("前\n\n后", TableTsv("前\n\n后", 3, 0, "姓名\t数量\n中\t2"),
                "前\n\n| 姓名 | 数量 |\n| --- | --- |\n| 中 | 2 |\n\n后", 5, 2));
        Test("表格TSV引号支持Tab双引号及CRLF记录", () =>
            TableResult("", TableTsv("", 0, 0, "\"A\tB\"\t\"He said \"\"hi\"\"\"\r\n中\t😀\r\n"),
                "| A    B | He said \"hi\" |\n| --- | --- |\n| 中 | 😀 |", 2, 6));
        Test("表格TSV拒绝阅读器不能显示的单元格内换行", () =>
        {
            foreach (string tsv in new[] { "\"甲\n乙\"\tB", "\"甲\r乙\"\tB", "\"甲\r\n乙\"\tB" })
                Equal(TableTsv("", 0, 0, tsv), null);
        });
        Test("表格TSV源HTML和Markdown保持文字不激活", () =>
            TableResult("", TableTsv("", 0, 0, "<img src=x onerror=alert(1)>\t![x](file:///x)\n&lt;b&gt;\t`a|b`"),
                "| `<img src=x onerror=alert(1)>` | \\!\\[x\\](file:///x) |\n| --- | --- |\n| `&lt;b&gt;` | \\`a\\|b\\` |",
                2, 30));
        Test("表格TSV的HTML字面量在实际ReadingModel可读", () =>
        {
            var edit = TableTsv("", 0, 0, "A\tB\n<img src=x onerror=alert(1)>\t&lt;b&gt;")!;
            var model = ReadingModel.Build(edit.Text, TextKind.Markdown);
            Equal(model.Blocks.Count, 1);
            Equal(model.Blocks[0].Kind, ReadingBlockKind.Table);
            Equal(model.Blocks[0].Text, "A\tB\n<img src=x onerror=alert(1)>\t&lt;b&gt;");
            Equal(TableTsv("", 0, 0, "<b>`x`</b>\tB"), null);
        });
        Test("表格TSV拒绝畸形引号不齐行空输入和代码覆盖", () =>
        {
            foreach (string tsv in new[] { "", "\"unclosed\tB", "\"A\"x\tB", "A\"B\tC", "A\tB\nC" })
                Equal(TableTsv("", 0, 0, tsv), null);
            Equal(TableTsv(TableFixture, 2, 0, "A\tB"), null);
            Equal(TableTsv("text\n```\ncode\n```", 0, 14, "A\tB"), null);
        });
        Test("表格32列限制拒绝新增列和过宽TSV", () =>
        {
            string header = "| " + string.Join(" | ", Enumerable.Repeat("A", 32)) + " |";
            string separator = "| " + string.Join(" | ", Enumerable.Repeat("---", 32)) + " |";
            string text = header + "\n" + separator;
            Equal(TableApply(text, 2, 0, "InsertColumn"), null);
            if (TableApply(text, 2, 0, "NextCell") is null) throw new Exception("合法32列被拒绝");
            Equal(TableTsv("", 0, 0, string.Join('\t', Enumerable.Repeat("A", 33))), null);
        });
        Test("表格1024单元格预算检查输入及新增行", () =>
        {
            string text = "| A |\n| --- |" + string.Concat(Enumerable.Repeat("\n| x |", 1023));
            if (TableApply(text, 2, 0, "NextCell") is null) throw new Exception("合法1024格被拒绝");
            Equal(TableApply(text, text.Length - 2, 0, "NextCell"), null);
            Equal(TableApply(text + "\n| x |", 2, 0, "DeleteRow"), null);
            Equal(TableTsv("", 0, 0, string.Join('\n', Enumerable.Repeat("x", 1025))), null);
        });
        Test("表格100000字符限制检查源与编码后输出", () =>
        {
            string huge = "| " + new string('x', 100_000) + " |\n| --- |";
            Equal(TableApply(huge, 2, 0, "NextCell"), null);
            Equal(TableTsv("", 0, 0, new string('x', 100_001)), null);
            Equal(TableTsv("", 0, 0, new string('\\', 60_000)), null);
        });
        Test("表格字符预算包含对齐和增行新增语法", () =>
        {
            string text = "| " + new string('x', 99_988) + " |\n| --- |";
            Equal(text.Length, 100_000);
            if (TableApply(text, 2, 0, "NextCell") is not null)
                throw new Exception("预算已满仍追加行");
            Equal(TableApply(text, 2, 0, "AlignLeft"), null);
            Equal(TableApply(text, 2, 0, "InsertColumn"), null);
            if (TableApply(text, 2, 0, "AlignRight") is not null)
                throw new Exception("预算已满仍添加对齐语法");
        });
        Test("表格CRLF删末行和增删列保留换行及代理对", () =>
        {
            const string text = "| A | B |\r\n| --- | :---: |\r\n| 中 | 😀 |\r\n";
            TableText(text, 30, "DeleteRow", "| A | B |\r\n| --- | :---: |\r\n");
            TableText(text, 30, "InsertColumn",
                "| A |  | B |\r\n| --- | --- | :---: |\r\n| 中 |  | 😀 |\r\n");
            TableText(text, 34, "DeleteColumn", "| A |\r\n| --- |\r\n| 中 |\r\n");
        });
        Test("表格围栏关闭后命令可用且长围栏不被短围栏提前关闭", () =>
        {
            const string prefix = "````md\n```\ntext\n````\n\n";
            TableText(prefix + TableFixture, prefix.Length + 28, "AlignRight",
                "````md\n```\ntext\n````\n\n| A | B |\n| ---: | :---: |\n| 中 | 😀 |");
            Equal(TableApply("````\n```\n" + TableFixture, 11, 0, "NextCell"), null);
        });
        Test("表格TSV保留末尾空格子且拒绝非法Unicode与控制字符", () =>
        {
            TableResult("", TableTsv("", 0, 0, "A\tB\nx\t"), "| A | B |\n| --- | --- |\n| x |  |", 2, 1);
            Equal(TableTsv("", 0, 0, "A\t\uD83D"), null);
            Equal(TableTsv("", 0, 0, "A\t\0"), null);
        });
        Test("表格TSV遵循当前CRLF并可替换普通文字选区", () =>
            TableResult("前\r\n待替换\r\n后", TableTsv("前\r\n待替换\r\n后", 3, 3, "A\tB\r\n中\t😀"),
                "前\r\n\r\n| A | B |\r\n| --- | --- |\r\n| 中 | 😀 |\r\n\r\n后", 7, 1));
        Test("表格小范围修改不受整篇文档长度影响", () =>
        {
            string prefix = new('x', 110_000);
            string text = prefix + "\n\n" + TableFixture + "\n\n尾";
            var edit = TableApply(text, prefix.Length + 30, 0, "InsertRow")!;
            Equal(edit.Length, 0);
            Equal(edit.Text, "\n|  |  |");
            Equal(edit.Start, prefix.Length + 38);
        });
        Test("表格边界管道空格和无数据表的导航", () =>
        {
            const string text = "| A | B |\n| --- | --- |";
            TableResult(text, TableApply(text, 0, 0, "NextCell"), text, 6, 1);
            TableResult(text, TableApply(text, 6, 0, "NextCell"),
                "| A | B |\n| --- | --- |\n|  |  |", 26, 0);
        });
        Test("表格无效范围抛出明确参数异常", () =>
        {
            TableThrows<ArgumentOutOfRangeException>(() => TableApply("", -1, 0, "NextCell"));
            TableThrows<ArgumentOutOfRangeException>(() => TableTsv("a", 0, 2, "A"));
        });
    }

    // Keep the RED build valid before the production enum and class have been added.
    private static Type TableType() => typeof(DocumentFile).Assembly.GetType("Qingyue.Core.MarkdownTableEditing")
        ?? throw new Exception("缺少Markdown表格编辑行为");

    private static Qingyue.Core.MarkdownEdit? TableApply(string text, int start, int length, string action)
    {
        var type = TableType();
        var actionType = type.Assembly.GetType("Qingyue.Core.MarkdownTableAction")
            ?? throw new Exception("缺少Markdown表格动作");
        return (Qingyue.Core.MarkdownEdit?)TableInvoke(type.GetMethod("Apply")!,
            [text, start, length, Enum.Parse(actionType, action)]);
    }

    private static Qingyue.Core.MarkdownEdit? TableTsv(string text, int start, int length, string tsv) =>
        (Qingyue.Core.MarkdownEdit?)TableInvoke(TableType().GetMethod("FromTsv")!, [text, start, length, tsv]);

    private static object? TableInvoke(MethodInfo method, object[] args)
    {
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }

    private static void TableResult(string text, Qingyue.Core.MarkdownEdit? result, string expected,
        int selection, int selectionLength)
    {
        var edit = result ?? throw new Exception("表格命令未生效");
        string actual = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        Equal(actual, expected);
        Equal(edit.SelectionStart, selection);
        Equal(edit.SelectionLength, selectionLength);
        if (edit.SelectionStart < 0 || edit.SelectionStart + edit.SelectionLength > actual.Length)
            throw new Exception("选区越界");
        foreach (int boundary in new[] { edit.Start, edit.Start + edit.Length })
            if (boundary > 0 && boundary < text.Length && char.IsHighSurrogate(text[boundary - 1]) &&
                char.IsLowSurrogate(text[boundary])) throw new Exception("替换切断代理对");
    }

    private static void TableText(string text, int caret, string action, string expected)
    {
        var edit = TableApply(text, caret, 0, action) ?? throw new Exception("表格命令未生效");
        Equal(text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text), expected);
    }

    private static void TableThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("未抛出预期参数异常");
    }
}
