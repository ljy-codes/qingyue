using Qingyue.Core;

internal static partial class Program
{
    static partial void RunMarkdownEditingTests()
    {
        Test("Markdown回车续项并保留标准源码", () =>
            Edit("- 中文", 4, 0, "Enter", "- 中文\n- ", 7));
        Test("Markdown有序和任务回车不继承完成状态", () =>
        {
            Edit("9. 中文", 5, 0, "Enter", "9. 中文\n10. ", 10);
            Edit("- [x] 完成", 8, 0, "Enter", "- [x] 完成\n- [ ] ", 15);
        });
        Test("Markdown空项逐级退出与顶层退出", () =>
        {
            Edit("- 父\n  - ", 8, 0, "Enter", "- 父\n- ", 6);
            Edit("- ", 2, 0, "Enter", "", 0);
        });
        Test("Markdown缩进携带子项且保持选区", () =>
        {
            Edit("- 一\n- 二\n  - 子\n- 三", 6, 0, "Indent",
                "- 一\n  - 二\n    - 子\n- 三", 8);
            Edit("- 一\n  - 二\n    - 子\n- 三", 8, 0, "Outdent",
                "- 一\n- 二\n  - 子\n- 三", 6);
        });
        Test("Markdown软换行属于父项且CRLF偏移正确", () =>
            Edit("- 中文\r\n- 尾", 4, 0, "SoftBreak", "- 中文  \r\n  \r\n- 尾", 10));
        Test("Markdown代码块与普通文本不接管列表按键", () =>
        {
            NoEdit("```\n- 代码\n```", 8, "Enter");
            NoEdit("普通正文", 4, "Enter");
            NoEdit("    - 缩进代码", 10, "Enter");
        });
        Test("Markdown粗体可切换并保持中文选区", () =>
        {
            Edit("中文", 0, 2, "Bold", "**中文**", 2, 2);
            Edit("**中文**", 2, 2, "Bold", "中文", 0, 2);
        });
        Test("Markdown链接和代码围栏不损坏多行内容", () =>
        {
            Edit("中文", 0, 2, "Link", "[中文](https://)", 5, 8);
            Edit("a\nb", 0, 3, "CodeBlock", "```\na\nb\n```", 4, 3);
        });
        Test("Markdown标题和引用只改变选中逻辑行", () =>
        {
            Edit("正文\n尾", 0, 2, "Heading2", "## 正文\n尾", 3, 2);
            Edit("正文\n尾", 0, 2, "Quote", "> 正文\n尾", 2, 2);
        });
        Test("Markdown任务切换可逆且不改其他行", () =>
        {
            Edit("- [ ] 待办\n尾", 6, 0, "ToggleTask", "- [x] 待办\n尾", 6);
            Edit("- [x] 待办\n尾", 6, 0, "ToggleTask", "- [ ] 待办\n尾", 6);
        });
        Test("Markdown即时标记识别三层、空项并排除代码", () =>
        {
            var type = typeof(DocumentFile).Assembly.GetType("Qingyue.Core.MarkdownEditing")
                ?? throw new Exception("缺少Markdown编辑语义");
            var markers = (System.Collections.IEnumerable)type.GetMethod("ListMarkers")!
                .Invoke(null, ["- 父\n  - 子\n    - 孙\n- \n```\n- literal\n```"])!;
            var levels = markers.Cast<object>().Select(m => (int)m.GetType().GetProperty("Level")!.GetValue(m)!).ToArray();
            Equal(string.Join(",", levels), "0,1,2,0");
        });
        Test("Markdown四空格子项一次升回父级且携带孙项", () =>
            Edit("- 父\n    - 子\n        - 孙", 10, 0, "Outdent", "- 父\n- 子\n    - 孙", 6));
        Test("Markdown有序列表降级按父项正文缩进", () =>
            Edit("10. 父\n11. 子", 11, 0, "Indent", "10. 父\n    11. 子", 15));
        Test("Markdown续段回车开始同级新项", () =>
            Edit("- 父\n  续段", 8, 0, "Enter", "- 父\n  续段\n- ", 11));
        Test("Markdown越过普通段落后不沿用旧列表上下文", () =>
            NoEdit("- 父\n\n正文\n    代码", 14, "Enter"));
        Test("Markdown续段后列表标记与阅读层级一致", () =>
        {
            const string text = "- 父\n无缩进续行\n    - 子";
            var markers = Qingyue.Core.MarkdownEditing.ListMarkers(text);
            Equal(string.Join(",", markers.Select(m => m.Level)), "0,1");
            Equal(string.Join(",", ReadingModel.Build(text, TextKind.Markdown).Blocks
                .Where(b => b.Kind == ReadingBlockKind.ListItem).Select(b => b.Level)), "0,1");
            Equal(Qingyue.Core.MarkdownEditing.ListMarkers("- 父\n\n独立正文\n    - 代码").Count, 1);
        });
        Test("Markdown分隔线不绘制成列表圆点", () =>
        {
            Equal(Qingyue.Core.MarkdownEditing.ListMarkers("- - -\n* * *\n_ _ _").Count, 0);
            NoEdit("- - -", 5, "Enter");
        });
        Test("Markdown跨层选区升层携带未选中的同级后代", () =>
        {
            const string text = "- 父\n  - 子\n    - 甲\n    - 乙";
            Equal(Edited(text, 4, text.IndexOf("\n    - 乙", StringComparison.Ordinal) - 4, "Outdent"),
                "- 父\n- 子\n  - 甲\n  - 乙");
        });
        Test("Markdown父项降级携带所属围栏代码和后续子项", () =>
        {
            const string text = "- 一\n- 二\n  ```\n  code\n  ```\n  - 子";
            Equal(Edited(text, 7, 0, "Indent"),
                "- 一\n  - 二\n    ```\n    code\n    ```\n    - 子");
        });
        Test("Markdown含Tab的列表按显示列降级", () =>
        {
            const string text = "- 父\n\t- 甲\n\t- 乙";
            string result = Edited(text, text.Length, 0, "Indent");
            var markers = Qingyue.Core.MarkdownEditing.ListMarkers(result);
            Equal(markers[^1].Indent, 6);
            Equal(markers[^1].Level, 2);
        });
        Test("Markdown批量标题跳过独立缩进代码和空白行", () =>
        {
            const string text = "正文\n\n    code";
            Equal(Edited(text, 0, text.Length, "Heading1"), "# 正文\n\n    code");
            NoEdit("\tcode", 5, "Bold");
        });
        Test("Markdown空项跨行选区回车保留替换语义", () =>
        {
            Equal(Edited("- \nabc", 2, 4, "Enter"), "- \n- ");
            Equal(Edited("- \r\nabc", 2, 5, "Enter"), "- \r\n- ");
        });
        Test("Markdown父项移动携带lazy续行及其后代", () =>
            Equal(Edited("- a\n- b\nlazy\n  - c", 4, 0, "Indent"), "- a\n  - b\n  lazy\n    - c"));
        Test("Markdown标记后Tab按正文列计算缩进和续段归属", () =>
        {
            Equal(Edited("-\tparent\n- child", 9, 0, "Indent"), "-\tparent\n    - child");
            const string text = "- parent\n\t- child\n\n    parent continuation";
            Equal(Edited(text, text.Length, 0, "Enter"), text + "\n- ");
        });
        Test("Markdown两空格任务续段继续同级任务", () =>
        {
            const string text = "- [ ] task\n\n  continuation";
            Equal(Edited(text, text.Length, 0, "Enter"), text + "\n- [ ] ");
        });
        Test("Markdown批量格式保留列表容器内缩进代码", () =>
        {
            const string text = "- item\n\n      code\n- next";
            Equal(Edited(text, 0, text.Length, "Heading1"), "# - item\n\n      code\n# - next");
            NoEdit(text, text.IndexOf("code", StringComparison.Ordinal) + 4, "Bold");
        });
        Test("Markdown围栏后外部段落不随列表缩进", () =>
        {
            const string text = "- prev\n- item\n  ~~~\n  code\n  ~~~\noutside";
            Equal(Edited(text, 7, 0, "Indent"),
                "- prev\n  - item\n    ~~~\n    code\n    ~~~\noutside");
        });
        Test("Markdown无空行深缩进续行仍允许格式操作", () =>
        {
            const string text = "- item\n      continuation";
            Equal(Edited(text, 13, 12, "Bold"), "- item\n      **continuation**");
            Equal(Edited(text, 0, text.Length, "Heading1"), "# - item\n      # continuation");
        });
    }

    // Reflection lets the first test run prove missing behavior before production types exist.
    private static object? MarkdownEdit(string text, int start, int length, string action)
    {
        var type = typeof(DocumentFile).Assembly.GetType("Qingyue.Core.MarkdownEditing")
            ?? throw new Exception("缺少Markdown编辑命令");
        var actionType = type.Assembly.GetType("Qingyue.Core.MarkdownEditAction")!;
        return type.GetMethod("Apply")!.Invoke(null, [text, start, length, Enum.Parse(actionType, action)]);
    }

    private static void Edit(string text, int start, int length, string action,
        string expected, int selection, int selectionLength = 0)
    {
        var edit = MarkdownEdit(text, start, length, action) ?? throw new Exception("命令未生效");
        object Value(string key) => edit.GetType().GetProperty(key)!.GetValue(edit)!;
        string result = text.Remove((int)Value("Start"), (int)Value("Length"))
            .Insert((int)Value("Start"), (string)Value("Text"));
        Equal(result, expected);
        Equal((int)Value("SelectionStart"), selection);
        Equal((int)Value("SelectionLength"), selectionLength);
    }
    private static void NoEdit(string text, int caret, string action) =>
        Equal(MarkdownEdit(text, caret, 0, action), null);

    private static string Edited(string text, int start, int length, string action)
    {
        var edit = (Qingyue.Core.MarkdownEdit)(MarkdownEdit(text, start, length, action)
            ?? throw new Exception("命令未生效"));
        return text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
    }
}
