using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Qingyue.Core;

// Reflection allows the same real behavior tests to report missing APIs before
// the implementation exists. No doubles or external test packages are used.
var tests = new List<(string Name, Action Run)>
{
    ("kind/extensions", () =>
    {
        foreach (var (path, kind) in new[] { ("a.txt", TextKind.Plain), ("a.MD", TextKind.Markdown),
            ("a.markdown", TextKind.Markdown), ("a.json", TextKind.Json), ("a.jsonc", TextKind.Jsonc),
            ("a.yaml", TextKind.Yaml), ("a.yml", TextKind.Yaml), ("a.ini", TextKind.Ini), ("a.xml", TextKind.Xml) })
            Equal(kind, Detect(path, ""));
        Equal(TextKind.Xml, Detect("app.config", "<?xml version=\"1.0\"?><root/>"));
        Equal(TextKind.Ini, Detect("app.config", "[服务]\nx=1"));
        Equal(TextKind.Yaml, Detect("app.config", "服务:\n  端口: 80"));
        Equal(TextKind.Plain, Detect(null, "中文😀"));
    }),
    ("quick open/macOS filtering and ranking contract", () =>
    {
        QuickOpenItem[] items =
        [
            new("recent-path", "config.yaml", "/项目/说明文档/config.yaml", false),
            new("open-path", "notes.md", "/项目/说明文档/notes.md", true),
            new("recent-title", "说明文档.md", "/资料/说明文档.md", false),
            new("open-title", "说明文档.md", "/另一份/说明文档.md", true),
            new("recent-fold", "Café.JSON", "/資料/Ｃａｆｅ́.json", false),
            new("emoji", "😀笔记.md", "/中文/😀笔记.md", false)
        ];
        string Ids(string query) =>
            string.Join(",", QuickOpenRanker.Filter(items, query).Select(item => item.Id));
        Equal("open-path,open-title,recent-path,recent-title,recent-fold,emoji", Ids(""));
        Equal(Ids(""), Ids("  \t\n"));
        Equal("open-title,open-path,recent-title,recent-path", Ids("说明文档"));
        Equal("open-title,recent-title", Ids("说明文档.md"));
        Equal("open-title", Ids("另一份"));
        Equal("recent-fold", Ids("cafe.json"));
        Equal("recent-fold", Ids("ＣＡＦＥ"));
        Equal("emoji", Ids("😀"));
        Equal("open-path,recent-path", Ids("说明 项目"));
        Equal("", Ids("does-not-exist"));
        Check(QuickOpenRanker.Filter([], "a").Count == 0, "空数据源应安全返回空结果");
        Equal(2, QuickOpenRanker.Filter([items[0], items[0], items[1]], "").Count);

        QuickOpenItem[] ranking =
        [
            new("contains", "my-report.txt", "/x", false),
            new("prefix", "report-new.txt", "/x", false),
            new("exact", "report", "/x", false),
            new("path", "other", "/report", false)
        ];
        Equal("exact,prefix,contains,path",
            string.Join(",", QuickOpenRanker.Filter(ranking, "report").Select(item => item.Id)));
        Equal("open-path,open-title,recent-title,emoji", Ids(" md"));
    }),
    ("reading list/macOS order and path migration", () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "qingyue-reading-list-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string first = Path.Combine(root, "a.md");
            string second = Path.Combine(root, "b.md");
            var store = new ReadingListStore(root);
            store.Upsert(first, 10, 3);
            store.Upsert(second, 20, 8);
            store.MarkRead(first, true);
            Equal(Path.GetFullPath(first), store.Items[0].Path);

            var migrated = store.Upsert(first, 88, 42, isRead: true);
            store.SetPinned(first, true);
            store.Upsert(second, 15, 6);
            var migratePath = typeof(ReadingListStore).GetMethod("MigratePath")
                ?? throw new MissingMethodException("缺失功能：ReadingListStore.MigratePath");
            int changed = (int)migratePath.Invoke(store, [first, second])!;
            Check(changed > 0, "阅读清单路径迁移应报告实际变更");
            Check(store.Find(first) is null, "迁移后旧路径记录必须删除");
            var target = store.Find(second) ?? throw new Exception("迁移后新路径记录不存在");
            Check(target.IsPinned && target.IsRead && target.ProgressPercent == 88 &&
                target.SourceAnchor == 42, "迁移必须合并固定、已读、进度与锚点");
            Equal(1, store.Items.Count);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }),
    ("workspace/clear only the selected document draft", () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "qingyue-clear-draft-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var first = new DocumentFile("第一份未保存草稿");
            first.Rename("第一份.md");
            var second = new DocumentFile("第二份未保存草稿");
            second.Rename("第二份.md");
            var store = new WorkspaceStore(root);
            var session = new WorkspaceSession(1,
            [
                new WorkspaceEntry(first.Id, null, false, 0, 0, 0),
                new WorkspaceEntry(second.Id, null, false, 0, 0, 0)
            ], first.Id);
            var saved = store.SaveAsync(session,
                [first.CaptureSnapshot(), second.CaptureSnapshot()]).GetAwaiter().GetResult();
            Check(saved.Errors.Count == 0, "测试草稿首次保存失败：" + string.Join(";", saved.Errors));
            var clearDraft = typeof(WorkspaceStore).GetMethod("ClearDraftAsync", [typeof(Guid)])
                ?? throw new MissingMethodException("缺失功能：WorkspaceStore.ClearDraftAsync(Guid)");
            ((Task)clearDraft.Invoke(store, [first.Id])!).GetAwaiter().GetResult();
            var loaded = new WorkspaceStore(root).LoadAsync().GetAwaiter().GetResult();
            Check(loaded.Documents.Count == 1 && loaded.Documents[0].File.Id == second.Id,
                "清除单文档草稿不得删除其他文档草稿");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }),
    ("format/json lexical fidelity and duplicate keys", () =>
    {
        const string input = "{\"n\":123456789012345678901234567890,\"n\":1.2300e+100,\"small\":-0.000000000000000000001,\"s\":\"中文😀\",\"negative\":-0}";
        var output = Format(input);
        Check(output.Contains('\n'), "格式化应产生缩进");
        using var doc = JsonDocument.Parse(output);
        var properties = doc.RootElement.EnumerateObject().ToArray();
        Equal(5, properties.Length);
        Equal("123456789012345678901234567890", properties[0].Value.GetRawText());
        Equal("1.2300e+100", properties[1].Value.GetRawText());
        Equal("-0.000000000000000000001", properties[2].Value.GetRawText());
        Equal("-0", properties[4].Value.GetRawText());
        Equal("中文😀", properties[3].Value.GetString());
    }),
    ("format/json readable unicode and raw string escapes", () =>
    {
        const string input = """{"中文":"你好😀","escaped":"\u4e2d\n\/","n":1e400,"n":-0}""";
        var output = Format(input);
        Check(output.Contains("\"中文\": \"你好😀\""), "直接输入的中文应直接显示");
        Check(output.Contains(""""escaped": "\u4e2d\n\/""""), "已有转义应原样保留");
        Check(output.Contains("\"n\": 1e400") && output.Contains("\"n\": -0"), "数值原文必须保留");
        Equal(output, Format(output));
    }),
    ("format/json envdock two-space nested array layout", () =>
    {
        Equal("{\n  \"user\": null,\n  \"data\": [\n    1,\n    true,\n    {}\n  ]\n}",
            Format("{\"user\":null,\"data\":[1,true,{}]}"));
        Equal("[\n  {},\n  [\n    []\n  ]\n]", Format("[{},[[]]]"));
        Equal("{}", Format("{ \r\n }"));
        Equal("null", Format(" null \r\n"));
        foreach (string invalid in new[] { "[,]", "{,}", "[1,,]", "{\"a\":1,,}", "{\"a\":undefined}", "" })
            Reject(() => Format(invalid), "JSON");
    }),
    ("kind/untitled markdown content", () =>
    {
        Equal(TextKind.Markdown, Detect(null, "# 无路径标题😀\n正文"));
        Equal(TextKind.Markdown, Detect("", "\n标题\n---\n正文"));
        Equal(TextKind.Markdown, Detect(null, "```txt\n代码\n```"));
        Equal(TextKind.Plain, Detect("a.txt", "# 保持显式文本"));
        Equal(TextKind.Plain, Detect(null, "#不是标题"));
        Equal("无路径标题😀", Build("# 无路径标题😀\n正文", Detect(null, "# 无路径标题😀\n正文")).Headings.Single().Title);
    }),
    ("kind/structured extensions and content fallback", () =>
    {
        foreach (var (path, kind) in new[] { ("a.toml", Kind("Toml")), ("a.env", TextKind.Ini),
            (".env.local", TextKind.Ini), ("a.html", Kind("Html")), ("a.htm", Kind("Html")) })
            Equal(kind, Detect(path, ""));
        Equal(TextKind.Json, Detect(null, "{\"a\":1}"));
        Equal(TextKind.Json, Detect("unknown.data", "[1,2]"));
        Equal(TextKind.Jsonc, Detect(null, "// comment\n{\"a\":1,}"));
        Equal(TextKind.Xml, Detect(null, "<root/>"));
        Equal(TextKind.Ini, Detect(null, "[main]\na=1"));
        Equal(TextKind.Plain, Detect("a.txt", "<root/>"));
    }),
    ("kind/common languages explicit extensions override content", () =>
    {
        foreach (var (path, name) in new[] { ("a.JS", "JavaScript"), ("a.mjs", "JavaScript"),
            ("a.ts", "TypeScript"), ("a.tsx", "TypeScript"), ("a.jsx", "JavaScript"),
            ("a.py", "Python"), ("a.java", "Java"), ("a.cs", "CSharp"), ("a.sql", "Sql"),
            ("a.html", "Html"), ("a.css", "Css"), ("a.go", "Go"), ("a.rs", "Rust"),
            ("a.sh", "Shell"), ("a.ps1", "PowerShell"), ("a.psm1", "PowerShell"),
            ("a.toml", "Toml"), ("a.csv", "Csv"), ("a.tsv", "Csv") })
            Equal(Kind(name), Detect(path, "{[not JSON"));
        Equal(Kind("Shell"), Detect(".bashrc", ""));
        Equal(Kind("Shell"), Detect(null, "#!/usr/bin/env bash\nprintf '中文'"));
        Equal(Kind("Python"), Detect(null, "#!/usr/bin/python3\nprint('中文')"));
        // These values are serialized into existing drafts and must never move.
        foreach (var (name, value) in new[] { ("Plain", 0), ("Markdown", 1), ("Json", 2),
            ("Jsonc", 3), ("Yaml", 4), ("Ini", 5), ("Xml", 6) })
            Equal(value, (int)Kind(name));
    }),
    ("format/plain whitespace preserves paragraphs inline spacing and indent levels", () =>
    {
        const string input = "标题  \r\n\t第一段  保持  空格\t \r\n \t\t嵌套\t内容 \r\n\r\n \t\r\n\r\n尾段  \r\n";
        const string expected = "标题\n    第一段  保持  空格\n        嵌套\t内容\n\n尾段\n";
        Equal(expected, FormatKind(input, TextKind.Plain));
        Equal(expected, FormatKind(expected, TextKind.Plain));
        Equal("\n内容\n", FormatKind("\r\n\r\n内容 \r\n\r\n", TextKind.Plain));
        Equal("", FormatKind("", TextKind.Plain));
    }),
    ("format/jsonc comments trailing commas and data survive", () =>
    {
        const string input = "//前言\r\n{/*保留*/\"中\":123456789012345678901234567890,\"中\":1e400,\"s\":\"//不是注释 /* x */\",//行尾\r\n\"list\":[-0,],} //尾注";
        var output = FormatKind(input, TextKind.Jsonc);
        Equal(null, Diagnostic(output, TextKind.Jsonc));
        Check(!output.Contains('\r'), "所有格式化输出均为 LF");
        foreach (var token in new[] { "//前言", "/*保留*/", "//行尾", "//尾注",
            "123456789012345678901234567890", "1e400", "-0", "\"//不是注释 /* x */\"" })
            Check(output.Contains(token), "JSONC 原始内容丢失：" + token);
        using var parsed = JsonDocument.Parse(output, new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Equal(4, parsed.RootElement.EnumerateObject().Count());
        Equal(output, FormatKind(output, TextKind.Jsonc));
        Reject(() => FormatKind("{/*保留*/\"a\":,}", TextKind.Jsonc), "JSONC");
    }),
    ("format/xml indentation preserves mixed content significant whitespace and cdata", () =>
    {
        const string input = "<root><item a=\"中文\">文本  内容</item><empty/></root>";
        var output = FormatKind(input, TextKind.Xml);
        Check(output.Contains("\n  <item"), "XML 子元素应缩进");
        Equal(null, Diagnostic(output, TextKind.Xml));
        Equal(output, FormatKind(output, TextKind.Xml));
        foreach (var xml in new[] { "<p><b>甲</b> 乙 <i>丙</i></p>",
            "<p> <b>甲</b> </p>", "<r xml:space=\"preserve\"> <a/>  <b/> </r>",
            "<r><![CDATA[  中文 <x>  ]]></r>" })
        {
            var before = System.Xml.Linq.XElement.Parse(xml, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var after = System.Xml.Linq.XElement.Parse(FormatKind(xml, TextKind.Xml), System.Xml.Linq.LoadOptions.PreserveWhitespace);
            Equal(before.Value, after.Value);
        }
        Reject(() => FormatKind("<!DOCTYPE x [<!ENTITY a SYSTEM \"file:///missing\">]><x>&a;</x>", TextKind.Xml), "XML");
        Reject(() => FormatKind("<root>", TextKind.Xml), "XML");
    }),
    ("format/xml declaration entities and document comments are stable", () =>
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<!--说明-->\r\n<root a=\"&#xD;\">甲&#xD;乙</root>\r\n";
        var formatted = FormatKind(xml, TextKind.Xml);
        Check(!formatted.Contains('\r'), "XML 返回值必须为 LF");
        Check(formatted.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>"), "保留 XML 声明编码");
        Check(formatted.Contains("<!--说明-->"), "保留文档注释");
        Equal(formatted, FormatKind(formatted, TextKind.Xml));
        var parsed = System.Xml.Linq.XElement.Parse(formatted);
        Equal("甲\r乙", parsed.Value);
        Equal("\r", parsed.Attribute("a")!.Value);
    }),
    ("format/code whitespace is explicit and semantic formats are guarded", () =>
    {
        foreach (var name in new[] { "JavaScript", "TypeScript", "Python", "Java", "CSharp", "Sql",
            "Html", "Css", "Go", "Rust", "Shell", "PowerShell", "Toml", "Csv" })
            Reject(() => FormatKind("x  \r\n", Kind(name)), "整理空白");
        // Explicit cleanup retains all indentation, tabs and blank lines in code/literals.
        const string code = "\ttext = '''a  \r\n\r\n\r\nb'''\t \r\n";
        Equal("\ttext = '''a\n\n\nb'''\n", Invoke("TextServices", "NormalizeWhitespace", code));
        Reject(() => FormatKind("a: |\n  b\n", TextKind.Yaml), "整理空白");
    }),
    ("format/markdown gentle heading list paragraphs and idempotence", () =>
    {
        const string source = "#   中文标题\n\n\n普通段落  空格\n\n\n-   第一项\n-  第二项\n\n1.   有序列表\n2.  第二条\n";
        const string expected = "# 中文标题\n\n普通段落  空格\n\n- 第一项\n- 第二项\n\n1. 有序列表\n2. 第二条\n";
        var output = FormatKind(source, TextKind.Markdown);
        Equal(expected, output);
        Equal(expected, FormatKind(output, TextKind.Markdown));
        Equal(Build(source, TextKind.Markdown).VisibleText, Build(output, TextKind.Markdown).VisibleText);
        Equal("#不是标题\n---\n标题\n===\n- - -\n", FormatKind("#不是标题\n---\n标题\n===\n- - -\n", TextKind.Markdown));
    }),
    ("format/markdown protected syntax stays byte identical after LF normalization", () =>
    {
        foreach (string source in new[] {
            "---\ntitle:  中文\nlist:\n  -   原样\n\n\n---\n",
            "```markdown\n#   围栏内容\n-   不改\n\n\n```\n",
            "~~~txt\n#   围栏\n\n\n~~~\n",
            "    #   缩进代码\n    -   不改\n\n\n    最后\n",
            "| 标题 | 值 |\n| --- | --- |\n| [链接](https://example.test/a?q=1) |  中文  |\n",
            "第一行  \n第二行\\\n[中文链接](https://example.test \"两个  空格\")\n",
            "-   父项\n    -   子项\n        ```\n        #   内层\n        ```\n",
            "正文\n---\n\n[ref]: https://example.test \"链接  标题\"\n",
            "```未闭合\n#   不改\n\n\n尾部\n" })
        {
            Equal(source, FormatKind(source.Replace("\n", "\r\n"), TextKind.Markdown));
            Equal(source, FormatKind(source, TextKind.Markdown));
        }
    }),
    ("catalog/format and validation capabilities are honest", () =>
    {
        foreach (TextKind kind in Enum.GetValues<TextKind>())
        {
            var definition = Invoke("LanguageCatalog", "Get", kind);
            string Name(string property) => (string)definition.GetType().GetProperty(property)!.GetValue(definition)!;
            bool Flag(string property) => (bool)definition.GetType().GetProperty(property)!.GetValue(definition)!;
            Check(Name("DisplayName").Length > 0, "语言应有显示名");
            Check(Name("FormattingDescription").Length > 0 && Name("ValidationDescription").Length > 0, "能力应注明范围");
            Equal(kind is TextKind.Plain or TextKind.Markdown or TextKind.Json or TextKind.Jsonc or TextKind.Xml, Flag("SupportsFormatting"));
            Equal(kind is TextKind.Json or TextKind.Jsonc or TextKind.Xml, Flag("SupportsValidation"));
        }
        var csv = Invoke("LanguageCatalog", "Get", Kind("Csv"));
        Check(((IReadOnlyList<string>)csv.GetType().GetProperty("Extensions")!.GetValue(csv)!).Contains(".csv"), "目录应提供扩展名");
        Reject(() => FormatKind("a", (TextKind)999), "类型");
    }),
    ("format/jsonc diagnostics and strict JSON compatibility entry", () =>
    {
        const string input = "{/*注释*/\"a\":1,}";
        Equal(null, Diagnostic(input, TextKind.Jsonc));
        Check(Diagnostic(input, TextKind.Json) is not null, "标准 JSON 应拒绝注释");
        Reject(() => Format(input));
        Check(Diagnostic("{broken}", TextKind.Jsonc) is not null, "JSONC 应检查语法");
    }),
    ("format/macOS input depth node and output budgets", () =>
    {
        Equal(500_000, FormatKind(new string('a', 500_000), TextKind.Plain).Length);
        Reject(() => FormatKind(new string('a', 500_001), TextKind.Plain), "500000");

        string JsonDepth(int depth) => new string('[', depth) + "0" + new string(']', depth);
        Check(Format(JsonDepth(256)).Length > 0, "标准 JSON 应允许 256 层嵌套");
        Reject(() => Format(JsonDepth(257)), "JSON");
        Check(FormatKind(JsonDepth(128), TextKind.Jsonc).Length > 0, "JSONC 应允许 128 层嵌套");
        Reject(() => FormatKind(JsonDepth(129), TextKind.Jsonc), "JSONC");

        string jsoncNodes = "[" + string.Join(',', Enumerable.Repeat("0", 99_999)) + "]";
        Check(FormatKind(jsoncNodes, TextKind.Jsonc).Length > 0, "JSONC 应允许 100000 个值节点");
        Reject(() => FormatKind("[" + string.Join(',', Enumerable.Repeat("0", 100_000)) + "]",
            TextKind.Jsonc), "JSONC");

        string XmlDepth(int elements) =>
            string.Concat(Enumerable.Repeat("<a>", elements)) +
            string.Concat(Enumerable.Repeat("</a>", elements));
        Check(FormatKind(XmlDepth(129), TextKind.Xml).Length > 0, "XML 应允许 128 层子嵌套");
        Reject(() => FormatKind(XmlDepth(130), TextKind.Xml), "128");
    }),
    ("format/yaml indentation only and xml safety", () =>
    {
        Check(Diagnostic("a:\n\tb: 1", TextKind.Yaml) is not null, "Tab 缩进应提示");
        Equal(null, Diagnostic("a: \"a\tb\"", TextKind.Yaml));
        Equal(null, Diagnostic("a: [not complete", TextKind.Yaml));
        Check(Diagnostic("<root>", TextKind.Xml) is not null, "XML 应检查闭合");
        Check(Diagnostic("<!DOCTYPE x [<!ENTITY a SYSTEM \"file:///missing\">]><x>&a;</x>", TextKind.Xml) is not null,
            "XML 不应解析外部实体");
    }),
    ("search/literal dollar backslash and utf16", () =>
    {
        const string input = "中文😀 中文😀";
        var matches = Find(input, "😀", "$1\\x", new());
        Equal(2, matches.Count);
        Equal(2, matches[0].Start);
        Equal(2, matches[0].Length);
        Equal("中文$1\\x 中文$1\\x", Replace(input, matches));
        Equal(4, Find("Foo foo FOO", "foo", "", new(CaseSensitive: true)).Single().Start);
        Equal(3, Find("Foo foo FOO", "foo", "", new()).Count);
        Equal(0, Find(input, "", "", new()).Count);
    }),
    ("search/regex captures and zero width", () =>
    {
        var matches = Find("甲12 乙34", @"(?<字>[\p{L}]+)(?<数>\d+)", "${字}:${数}:$$", new(Regex: true));
        Equal("甲:12:$ 乙:34:$", Replace("甲12 乙34", matches));
        Equal("|甲|乙|", Replace("甲乙", Find("甲乙", "(?=.)|$", "|", new(Regex: true))));
        Equal(2, Find("😀", "(?=)", "|", new(Regex: true)).Count);
        Reject(() => Find("a", "[", "", new(Regex: true)));
    }),
    ("search/regex follows macOS scalar and whole-word contracts", () =>
    {
        const string scalarInput = "😀中";
        var scalarMatches = Find(scalarInput, ".", "x", new(Regex: true));
        Equal(2, scalarMatches.Count);
        Equal((0, 2), (scalarMatches[0].Start, scalarMatches[0].Length));
        Equal((2, 1), (scalarMatches[1].Start, scalarMatches[1].Length));
        Equal("xx", Replace(scalarInput, scalarMatches));

        const string wholeInput = "abc abcdef";
        var wholeCapture = Find(
            wholeInput,
            "(a)(bc)",
            "$2-$1",
            new(Regex: true, WholeWord: true));
        Equal("bc-a abcdef", Replace(wholeInput, wholeCapture));
    }),
    ("search/unicode whole word", () =>
    {
        Equal(2, Find("猫 猫咪 (猫) _猫 猫2", "猫", "", new(WholeWord: true)).Count);
        Equal(1, Find("é e\u0301 e", "e", "", new(WholeWord: true)).Count);
        Equal(0, Find("𐐀abc", "abc", "", new(WholeWord: true)).Count);
        Equal(2, Find("😀 😀", "😀", "", new(WholeWord: true)).Count);
        Equal(1, Find(" cat cats", "cat", "", new(Regex: true, WholeWord: true)).Count);
    }),
    ("search/invalid replacements are atomic", () =>
    {
        foreach (var matches in new IReadOnlyList<SearchMatch>[] {
            new[] { new SearchMatch(-1, 1, "x") }, new[] { new SearchMatch(0, int.MaxValue, "x") },
            new[] { new SearchMatch(0, 3, "x"), new SearchMatch(2, 1, "y") },
            new[] { new SearchMatch(2, 1, "x") }, new[] { new SearchMatch(1, 1, "x") },
            new[] { new SearchMatch(3, 1, "x"), new SearchMatch(0, 1, "y") } })
            Equal("a😀b", Replace("a😀b", matches));
        Equal("xya😀b", Replace("a😀b", new[] {
            new SearchMatch(0, 0, "x"), new SearchMatch(0, 0, "y") }));
        Equal("a😀x", Replace("a😀b", new[] { new SearchMatch(3, 1, "x") }));
        Equal("a😀b", Replace("a😀b", Enumerable.Repeat(new SearchMatch(0, 0, ""), 100_001).ToArray()));
    }),
    ("search/resource guards", () =>
    {
        Reject(() => Find(new string('a', 5_000_001), "a", "", new()));
        Reject(() => Find("a", new string('a', 4097), "", new()));
        Reject(() => Find("a", "a", new string('b', 1_000_001), new()));
        Reject(() => Find(new string('a', 100_001), "a", "", new()));
        Equal("aaaaaaaaaaa", Replace("aaaaaaaaaaa", new[] { new SearchMatch(0, 1, new string('x', 1_000_000)),
            new SearchMatch(1, 1, new string('x', 1_000_000)), new SearchMatch(2, 1, new string('x', 1_000_000)),
            new SearchMatch(3, 1, new string('x', 1_000_000)), new SearchMatch(4, 1, new string('x', 1_000_000)),
            new SearchMatch(5, 1, new string('x', 1_000_000)), new SearchMatch(6, 1, new string('x', 1_000_000)),
            new SearchMatch(7, 1, new string('x', 1_000_000)), new SearchMatch(8, 1, new string('x', 1_000_000)),
            new SearchMatch(9, 1, new string('x', 1_000_000)), new SearchMatch(10, 1, new string('x', 1_000_000)) }));
    }),
    ("search/regex timeout", () =>
    {
        var watch = Stopwatch.StartNew();
        Reject(() => Find(new string('a', 25_000) + "!", "(a+)+$", "", new(Regex: true)), "超时");
        Check(watch.Elapsed < TimeSpan.FromSeconds(5), "正则超时应及时终止");
    }),
    ("search/capture token semantics and bounded expansion", () =>
    {
        const string input = "ab12cd";
        foreach (string replacement in new[] { "$1/$2/${1}/${2}/$01/${01}/$0/$&/$$", "$`:$':$_:$+", "$99/${missing}/$x/$" })
        {
            var reference = new System.Text.RegularExpressions.Regex(@"([a-z]+)(\d+)");
            string expected = reference.Replace(input, replacement);
            Equal(expected, Replace(input, Find(input, @"([a-z]+)(\d+)", replacement, new(Regex: true))));
        }
        Reject(() => Find(new string('a', 5001), "a+", string.Concat(Enumerable.Repeat("$&", 201)), new(Regex: true)));
        Equal(100_000, Find(new string('a', 100_000), "a", "", new()).Count);
        Equal(10_000_000, Replace(new string('a', 10), Find(new string('a', 10), "a", new string('b', 1_000_000), new())).Length);
        Equal(0, Find("😀", "\ud83d", "", new()).Count);
        Equal(1, Find("😀", ".", "", new(Regex: true)).Count);
    }),
    ("search/named captures have numeric aliases and sparse numbers", () =>
    {
        foreach (var (pattern, replacement) in new[] {
            (@"(?<字>甲)(?<数>\d+)", "$1/${01}/$2/${数}/$+"),
            (@"(?<10>甲)(\d+)", "$1/$10/${10}/$+"),
            (@"(?<字>甲)(?<数>\d+)?", "$1/$2/$+") })
        {
            var reference = new System.Text.RegularExpressions.Regex(pattern);
            Equal(reference.Replace("甲12 甲", replacement),
                Replace("甲12 甲", Find("甲12 甲", pattern, replacement, new(Regex: true))));
        }
    }),
    ("reading/plain and structured identity", () =>
    {
        foreach (var kind in new[] { TextKind.Plain, TextKind.Json, TextKind.Jsonc, TextKind.Ini, TextKind.Yaml, TextKind.Xml })
        {
            const string input = "甲😀\r\n乙\n";
            var model = Build(input, kind);
            Equal(input, model.VisibleText);
            Equal(0, model.SourceToDisplay(-99));
            Equal(input.Length, model.DisplayToSource(int.MaxValue));
            Equal(1, model.SourceToDisplay(2));
            Equal(1, model.DisplayToSource(2));
            Equal(input.Length, model.SourceToDisplay(input.Length));
            VerifyBlocks(model);
        }
    }),
    ("reading/markdown visible text and mapping", () =>
    {
        const string input = "# 标题😀\n\n**加粗**与*斜体*、`代码`、[链接](https://example.test) ![图](https://example.test/a.png)\n";
        var model = Build(input, TextKind.Markdown);
        Equal("标题😀\n加粗与斜体、代码、链接 [图片：图]", model.VisibleText);
        Equal("标题😀", model.Headings.Single().Title);
        Equal(0, model.SourceToDisplay(0));
        Equal(0, model.DisplayToSource(0));
        Equal(model.VisibleText.Length, model.SourceToDisplay(input.Length));
        Equal(input.Length, model.DisplayToSource(model.VisibleText.Length));
        foreach (var offset in Enumerable.Range(0, input.Length + 1))
            Boundary(model.VisibleText, model.SourceToDisplay(offset));
        foreach (var offset in Enumerable.Range(0, model.VisibleText.Length + 1))
            Boundary(input, model.DisplayToSource(offset));
        VerifyBlocks(model);
        var found = Find(model.VisibleText, "加粗", "", new()).Single();
        Equal("加粗", model.VisibleText.Substring(found.Start, found.Length));
    }),
    ("reading/markdown mapping uses macOS whole-block and next-visible semantics", () =>
    {
        const string input = "[hidden]: https://example.test/hidden\n\n# 标题😀\n\n正文 **重点** 与[链接](https://example.test/hidden)\n\n```txt\n第一行😀\n```\n";
        var model = Build(input, TextKind.Markdown);
        var title = model.Blocks.Single(block => block.Text == "标题😀");
        var body = model.Blocks.Single(block => block.Text.StartsWith("正文 ", StringComparison.Ordinal));
        var code = model.Blocks.Single(block => block.Kind == ReadingBlockKind.Code);

        // Metadata and block separators precede the first visible block.
        Equal(title.DisplayStart, model.SourceToDisplay(0));
        Equal(title.DisplayStart, model.SourceToDisplay(input.IndexOf("[hidden]", StringComparison.Ordinal)));
        Equal(code.DisplayStart, model.SourceToDisplay(input.IndexOf("```txt", StringComparison.Ordinal)));
        Equal(title.SourceStart, model.DisplayToSource(0));

        // A point anywhere inside one rendered paragraph maps to the whole block.
        int emphasis = input.IndexOf("重点", StringComparison.Ordinal);
        Equal(body.DisplayStart, model.SourceToDisplay(emphasis));
        Equal(body.DisplayStart, model.SourceToDisplay(emphasis + 1));
        Equal(body.SourceStart, model.DisplayToSource(body.DisplayStart + 5));
        Equal((body.SourceStart, body.SourceLength), model.SourceRangeForDisplay(body.DisplayStart + 5));
        Equal((body.DisplayStart, body.Text.Length), model.DisplayRangeForSource(emphasis + 1));

        // Hidden Markdown punctuation remains part of the annotation source range.
        string quote = input.Substring(body.SourceStart, body.SourceLength);
        Check(quote.Contains("**重点**", StringComparison.Ordinal) &&
              quote.Contains("https://example.test/hidden", StringComparison.Ordinal),
            "阅读块反向映射必须保留原始 Markdown 标记");

        Equal(model.VisibleText.Length, model.SourceToDisplay(input.Length));
        Equal(input.Length, model.DisplayToSource(model.VisibleText.Length));
        Equal((input.Length, 0), model.SourceRangeForDisplay(model.VisibleText.Length));
        Equal((model.VisibleText.Length, 0), model.DisplayRangeForSource(input.Length));

        var crlf = Build("```\r\n甲😀\r\n```\r\n\r\n乙", TextKind.Markdown);
        Equal(0, crlf.SourceToDisplay(0));
        Equal(crlf.Blocks[1].DisplayStart, crlf.SourceToDisplay(crlf.Blocks[0].SourceStart + crlf.Blocks[0].SourceLength));
        var hiddenOnly = Build("```", TextKind.Markdown);
        Equal(0, hiddenOnly.VisibleText.Length);
        Equal(hiddenOnly.SourceText.Length, hiddenOnly.DisplayToSource(0));
        Equal(hiddenOnly.SourceText.Length, hiddenOnly.DisplayToSource(int.MaxValue));
        Equal((hiddenOnly.SourceText.Length, 0), hiddenOnly.SourceRangeForDisplay(0));
        var emptyMarkdown = Build("", TextKind.Markdown);
        Equal(0, emptyMarkdown.DisplayToSource(0));
        Equal(0, emptyMarkdown.DisplayToSource(int.MaxValue));
        for (int offset = -1; offset <= input.Length + 1; offset++)
        {
            int display = model.SourceToDisplay(offset);
            Check(display >= 0 && display <= model.VisibleText.Length, "源到显示范围应安全");
            Boundary(model.VisibleText, display);
        }
        for (int offset = -1; offset <= model.VisibleText.Length + 1; offset++)
        {
            int source = model.DisplayToSource(offset);
            Check(source >= 0 && source <= input.Length, "显示到源范围应安全");
            Boundary(input, source);
        }
    }),
    ("reading/markdown GFM autolinks and reference links", () =>
    {
        const string input = """
            [guide]: https://example.test/guide "指南"

            <https://example.test> <mailto:reader@example.test>
            [文档][guide]
            [未解析][missing]
            """;
        var model = Build(input, TextKind.Markdown);
        Check(model.VisibleText.Contains("https://example.test", StringComparison.Ordinal),
            "HTTP autolink should expose its readable URL");
        Check(model.VisibleText.Contains("mailto:reader@example.test", StringComparison.Ordinal),
            "mailto autolink should expose its readable address");
        Check(model.VisibleText.Contains("文档", StringComparison.Ordinal) &&
              !model.VisibleText.Contains("[文档][guide]", StringComparison.Ordinal),
            "reference link should resolve to its label");
        Check(model.VisibleText.Contains("[未解析][missing]", StringComparison.Ordinal),
            "unresolved reference link should remain literal");
        Check(!model.VisibleText.Contains("[guide]:", StringComparison.Ordinal),
            "reference definition should not become visible paragraph text");
        VerifyBlocks(model);
    }),
    ("reading/markdown block kinds and fenced headings", () =>
    {
        var model = Build("Setext\n===\n\n- [x] done\n- [ ] todo\n> quote\n\n```md\n# hidden\n```\n\n---\n\n普通段落\n下一行", TextKind.Markdown);
        Equal("Setext", model.Headings.Single().Title);
        Check(model.Blocks.Any(b => b.Kind == ReadingBlockKind.Code && b.Text == "# hidden"), "围栏应保留代码");
        Check(model.Blocks.Any(b => b.Kind == ReadingBlockKind.ListItem && b.Text == "☑ done"), "已完成任务");
        Check(model.Blocks.Any(b => b.Kind == ReadingBlockKind.ListItem && b.Text == "☐ todo"), "未完成任务");
        Check(model.Blocks.Any(b => b.Kind == ReadingBlockKind.Quote && b.Text == "quote"), "引用块");
        Check(model.Blocks.Any(b => b.Kind == ReadingBlockKind.Separator), "分隔线");
        Check(model.Blocks.Any(b => b.Kind == ReadingBlockKind.Paragraph && b.Text == "普通段落\n下一行"), "多行段落");
        VerifyBlocks(model);
    }),
    ("reading/markdown table and limits", () =>
    {
        var model = Build("| 名称 | 值 |\n| --- | :---: |\n| **中文** | 😀 |\n| a\\|b | `x` |", TextKind.Markdown);
        var table = model.Blocks.Single(b => b.Kind == ReadingBlockKind.Table);
        Equal("名称\t值\n中文\t😀\na|b\tx", table.Text);
        VerifyBlocks(model);
        var wide = string.Join("|", Enumerable.Repeat("x", 33)) + "\n" + string.Join("|", Enumerable.Repeat("---", 33));
        Check(!Build(wide, TextKind.Markdown).Blocks.Any(b => b.Kind == ReadingBlockKind.Table), "超宽表格应退回普通文本");
        var huge = "| a | b |\n| --- | --- |\n" + string.Concat(Enumerable.Repeat("| x | y |\n", 600));
        Check(Build(huge, TextKind.Markdown).Blocks.Where(b => b.Kind == ReadingBlockKind.Table)
            .All(b => b.Text.Split('\n').Sum(row => row.Split('\t').Length) <= 1024), "表格单元格上限");
    }),
    ("reading/table accepts one-dash separators and alignment markers", () => VerifyTableSeparators(1)),
    ("reading/table accepts two-dash separators and alignment markers", () => VerifyTableSeparators(2)),
    ("reading/table accepts three-dash separators and alignment markers", () => VerifyTableSeparators(3)),
    ("reading/table rejects malformed and incomplete separators", () =>
    {
        foreach (string invalid in new[] { "", ":", "::", "- -", "--x", "=-", "::--", "--::", @"\--" })
        {
            var model = Build($"| A | B |\n| {invalid} | --- |\n| x | y |", TextKind.Markdown);
            Check(!model.Blocks.Any(b => b.Kind == ReadingBlockKind.Table), "非法分隔单元格不能识别为表格：" + invalid);
        }
        foreach (string input in new[]
        {
            "| A | B |", "| A | B |\n|", "| A | B |\n| :", "| A | B |\n| -- |",
            "| A | B |\n| -- | :", "| A | B |\n| -- | -- | -- |",
            "| A | B |\n\n| -- | -- |", "A | B\n---"
        })
            Check(!Build(input, TextKind.Markdown).Blocks.Any(b => b.Kind == ReadingBlockKind.Table),
                "缺少分隔行、未完成单元格或列数不等不能识别为表格");
    }),
    ("reading/table escaped pipes and code pipes retain cell boundaries", () =>
    {
        const string input = "| A\\|B | C |\n| --- | --- |\n| x\\|y | tail\\|\n| `p|q` | z |\n| left\\\\| right |";
        var model = Build(input, TextKind.Markdown);
        Equal("A|B\tC\nx|y\ttail|\np|q\tz\nleft\\\tright", model.Blocks.Single().Text);
        Equal(ReadingBlockKind.Table, model.Blocks.Single().Kind);
        VerifyBlocks(model);
    }),
    ("reading/table column and cell count boundaries", () =>
    {
        foreach (int columns in new[] { 1, 32, 33 })
        {
            string input = "|" + string.Join("|", Enumerable.Repeat("H", columns)) + "|\n|" +
                string.Join("|", Enumerable.Repeat("---", columns)) + "|";
            var tables = Build(input, TextKind.Markdown).Blocks.Where(b => b.Kind == ReadingBlockKind.Table).ToArray();
            Equal(columns <= 32 ? 1 : 0, tables.Length);
            if (tables.Length == 1) Equal(columns, tables[0].Text.Split('\t').Length);
        }
        string atLimit = "| H |\n| --- |\n" + string.Concat(Enumerable.Repeat("| x |\n", 1023));
        var table = Build(atLimit, TextKind.Markdown).Blocks.Single(b => b.Kind == ReadingBlockKind.Table);
        Equal(1024, table.Text.Split('\n').Length);
        var bounded = Build(atLimit + "| overflow |\n", TextKind.Markdown);
        var boundedTable = bounded.Blocks.Single(b => b.Kind == ReadingBlockKind.Table);
        Equal(1024, boundedTable.Text.Split('\n').Length);
        Check(bounded.VisibleText.Contains("| overflow |", StringComparison.Ordinal),
            "超出单元格预算的后续行必须保留为普通正文");
        VerifyBlocks(bounded);
    }),
    ("reading/table ragged rows pad missing cells and merge excess cells", () =>
    {
        const string input = "A | B\n--- | ---\nonly |\nextra | data | retained\n";
        var model = Build(input, TextKind.Markdown);
        var table = model.Blocks.Single();
        Equal(ReadingBlockKind.Table, table.Kind);
        Equal("A\tB\nonly\t\nextra\tdata | retained", table.Text);
        Equal(3, table.Text.Split('\n').Length);
        Equal(input.Length, table.SourceStart + table.SourceLength);
        VerifyBlocks(model);
    }),
    ("reading/table unclosed code span keeps pipes as delimiters", () =>
    {
        const string input = "| A | `open | tail |\n| --- | --- | --- |\n| x | y | z |";
        var table = Build(input, TextKind.Markdown).Blocks.Single();
        Equal(ReadingBlockKind.Table, table.Kind);
        Equal("A\t`open\ttail\nx\ty\tz", table.Text);
    }),
    ("reading/frontmatter is per-line metadata outside the outline", () =>
    {
        const string input = "---\ntitle: demo\nauthor: 😀\n---\n# 可见\n";
        var model = Build(input, TextKind.Markdown);
        var metadata = model.Blocks.Where(block => block.Kind == ReadingBlockKind.Metadata).ToArray();
        Equal(4, metadata.Length);
        Check(metadata.Select(block => block.Text).SequenceEqual(["---", "title: demo", "author: 😀", "---"]),
            "Frontmatter must retain each source line as its own metadata block");
        Equal("可见", model.Headings.Single().Title);
        VerifyBlocks(model);
    }),
    ("reading/table adjacent paragraph empty body and block boundaries", () =>
    {
        const string input = "before\nA | B\n--- | :---:\n\n# After\n\n```\nliteral | value\n```";
        var model = Build(input, TextKind.Markdown);
        Equal(4, model.Blocks.Count);
        Equal("before", model.Blocks[0].Text);
        Equal(ReadingBlockKind.Table, model.Blocks[1].Kind);
        Equal("A\tB", model.Blocks[1].Text);
        Equal(ReadingBlockKind.Heading, model.Blocks[2].Kind);
        Equal(ReadingBlockKind.Code, model.Blocks[3].Kind);
        Equal("literal | value", model.Blocks[3].Text);
        VerifyBlocks(model);
    }),
    ("reading/fences interrupt tables and remain literal", () =>
    {
        const string input = "A | B\n--- | ---\n~~~text | info\n# hidden\n| literal | code |\n~~~\n# Visible";
        var model = Build(input, TextKind.Markdown);
        Equal("Visible", model.Headings.Single().Title);
        Equal("# hidden\n| literal | code |", model.Blocks.Single(b => b.Kind == ReadingBlockKind.Code).Text);
        Equal("A\tB", model.Blocks.Single(b => b.Kind == ReadingBlockKind.Table).Text);
        VerifyBlocks(model);
    }),
    ("reading/fallback and heading cap", () =>
    {
        var input = "# 标题\n" + new string('文', 500_001);
        var model = Build(input, TextKind.Markdown);
        Equal(input, model.VisibleText);
        Equal(2, model.SourceToDisplay(2));
        Equal(0, model.Headings.Count);
        VerifyBlocks(model);
        var headings = Build(string.Concat(Enumerable.Range(1, 700).Select(i => $"# 第{i}章\n")), TextKind.Markdown);
        Equal(500, headings.Headings.Count);
    }),
    ("reading/structured and txt outlines", () =>
    {
        Equal("第一章 起点", Build("第一章 起点\n内容\n第二章 终点", TextKind.Plain).Headings[0].Title);
        Equal("服务", Build("[服务]\nx=1", TextKind.Ini).Headings.Single().Title);
        Equal("服务", Build("服务:\n  端口: 80\n", TextKind.Yaml).Headings.Single().Title);
        var json = Build("{\"中文\":{\"嵌套\":1},\"list\":[]}", TextKind.Json);
        Check(json.Headings.Any(h => h.Title == "中文") && json.Headings.Any(h => h.Title == "嵌套"), "JSON 键目录");
        Equal("{\"中文\":{\"嵌套\":1},\"list\":[]}", json.VisibleText);
        Equal(0, Build("```\n# hidden\n", TextKind.Markdown).Headings.Count);
    }),
    ("reading/html is inert text", () =>
    {
        var model = Build("<script>alert('x')</script>\n\n![远程](https://invalid.test/p.png)", TextKind.Markdown);
        Check(model.VisibleText.Contains("<script>"), "HTML 应作为文本");
        Check(model.VisibleText.Contains("[图片：远程]") && !model.VisibleText.Contains("https://"), "图片只显示占位");
    }),
    ("reading/empty boundaries unicode outline and marker extremes", () =>
    {
        foreach (string input in new[] { "", "\n\n", "# 😀\r\n\r\n尾😀", "```\n\n```\n", "#\n\ntext", "😀\n===\n\n😀", "\r\n# 标题\r\n" })
        {
            var model = Build(input, TextKind.Markdown);
            VerifyBlocks(model);
            int previous = -1;
            for (int i = -1; i <= input.Length + 1; i++)
            {
                int mapped = model.SourceToDisplay(i);
                Boundary(model.VisibleText, mapped);
                Check(mapped >= previous, "源到显示应单调");
                previous = mapped;
            }
            previous = -1;
            for (int i = -1; i <= model.VisibleText.Length + 1; i++)
            {
                int mapped = model.DisplayToSource(i);
                Boundary(input, mapped);
                Check(mapped >= previous, "显示到源应单调");
                previous = mapped;
            }
        }
        const string json = "{\"😀\":1,\"中文\":2}";
        var outline = Build(json, TextKind.Json).Headings;
        Equal(8, outline[1].SourceStart);
        Equal(8, outline[1].DisplayStart);
        Equal("a|b\t代码", Build("| a\\|b | `代码` |\n| --- | --- |", TextKind.Markdown).Blocks.Single().Text);
        Check(Build("# h\n" + new string('文', 499_996), TextKind.Markdown).Headings.Count == 1, "50 万以内应富文本");
    }),
    ("bookmark/path matching and deletion", () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "qingyue-bookmark-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "a.md");
            var file = new DocumentFile("标题\n正文");
            file.Save(path);
            var store = new BookmarkStore(root);
            var item = store.Add(file, 3, "正文");
            Check(item.SourceAnchor == 3 && item.Path == file.Path, "书签保存规范化路径和源位置");
            Check(store.For(file).Count == 1, "书签按当前文件路径查询");

            string parityRoot = Path.Combine(root, "parity");
            var parityStore = new BookmarkStore(parityRoot);
            var first = parityStore.Add(file, 0, "第一条");
            var second = parityStore.Add(file, 1, "第二条");
            Check(parityStore.Items.Select(entry => entry.Id).SequenceEqual([first.Id, second.Id]),
                "书签内部存储顺序与 macOS 一样按新增追加");
            string emojiLabel = "a" + string.Concat(Enumerable.Repeat("😀", 100));
            var emoji = parityStore.Add(file, 2, emojiLabel);
            Check(emoji.Label.Length == 159 && char.IsLowSurrogate(emoji.Label[^1])
                && !char.IsHighSurrogate(emoji.Label[^1]),
                "书签标签按 UTF-16 预算截断且不得切断 emoji 代理对");
            var empty = new DocumentFile("");
            Check(parityStore.Add(empty, 0).Label == "位置 0",
                "空正文书签默认标签与 macOS 一样使用“位置 0”");

            Check(store.Remove(item.Id), "书签可以删除");
            Check(store.For(file).Count == 0, "删除后查询为空");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }),
    ("bookmark/macOS limit and pathless identity", () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "qingyue-bookmark-parity-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var file = new DocumentFile("标题\n正文");
            file.Save(Path.Combine(root, "a.md"));
            var store = new BookmarkStore(root);
            var item = store.Add(file, 3, "正文");

            var pathless = new DocumentFile(file.Text);
            typeof(DocumentFile).GetProperty("Id")!.SetValue(pathless, file.Id);
            Check(store.For(pathless).Single().Id == item.Id,
                "无路径查询按文档标识命中已有书签，与 macOS 一致");
            Check(store.Repair(file.Id, null, file.Text, "前言\n" + file.Text) == 1,
                "无路径书签修复按文档标识命中，与 macOS 一致");

            for (int index = 1; index < 500; index++) store.Add(file, index, "书签 " + index);
            Check(store.Items.Count == 500, "书签可达到 500 条上限");
            bool rejected = false;
            try { store.Add(file, 0, "第 501 条"); }
            catch (InvalidOperationException error) { rejected = error.Message.Contains("500"); }
            Check(rejected && store.Items.Count == 500,
                "达到 500 条后拒绝新增且保留全部旧书签，与 macOS 一致");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }),
    ("bookmark store survives reload", () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "qingyue-bookmark-reload-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "a.txt");
            File.WriteAllText(path, "第一行\n第二行");
            var file = DocumentFile.Open(path);
            var first = new BookmarkStore(root);
            var item = first.Add(file, 4, "第二行");
            Check(first.SaveAsync().GetAwaiter().GetResult().Errors.Count == 0, "书签首次写入成功");
            var second = new BookmarkStore(root);
            second.LoadAsync().GetAwaiter().GetResult();
            Check(second.Items.Any(x => x.Id == item.Id && x.Label == "第二行"),
                "书签重启后可恢复");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }),
    ("search/macOS large text progress cancellation and adaptive limit", () =>
    {
        SearchEngine.ValidateTextLength(SearchEngine.MaximumTextLength);
        Reject(() => SearchEngine.ValidateTextLength(SearchEngine.MaximumTextLength + 1), "50,000,000");
        Check(SearchEngine.TimeLimitFor(5_000_000) == TimeSpan.FromMilliseconds(350),
            "500 万文本使用快速默认查找预算");
        Check(SearchEngine.TimeLimitFor(5_000_001) == TimeSpan.FromSeconds(2),
            "超过 500 万文本使用 2 秒预算");
        Check(SearchEngine.TimeLimitFor(20_000_001) == TimeSpan.FromSeconds(4),
            "超过 2000 万文本使用 4 秒预算");

        string tail = new string('a', 6_000_000) + "needle";
        var tailMatches = SearchEngine.Find(tail, "needle", "", new SearchOptions());
        Equal(1, tailMatches.Count);
        Equal(6_000_000, tailMatches[0].Start);

        string boundary = new string('x', SearchEngine.ProgressChunkSize - 1) + "É";
        var boundaryProgress = new List<SearchProgress>();
        var boundaryMatches = SearchEngine.FindWithProgress(
            boundary, "é", "", new SearchOptions(), boundaryProgress.Add);
        Equal(1, boundaryMatches.Count);
        Equal(SearchEngine.ProgressChunkSize - 1, boundaryMatches[0].Start);
        Check(boundaryProgress.Count >= 2 &&
            boundaryProgress.All(item => item.CompletedUtf16 >= 0 &&
                item.CompletedUtf16 <= item.TotalUtf16 &&
                item.Fraction is >= 0 and <= 1),
            "跨分块非 ASCII 查找报告有界进度");

        string large = string.Concat(Enumerable.Repeat("中文😀", 350_000)) + "尾针";
        int progressEvents = 0;
        var largeMatches = SearchEngine.FindWithProgress(
            large, "尾针", "", new SearchOptions(CaseSensitive: true),
            _ => progressEvents++);
        Equal(1, largeMatches.Count);
        Check(progressEvents > 1, "大文本查找按固定分块报告进度");

        using var cancellation = new CancellationTokenSource();
        bool cancelledFromProgress = false;
        try
        {
            _ = SearchEngine.FindWithProgress(
                string.Concat(Enumerable.Repeat("中文😀", 1_000_000)), "尾针", "",
                new SearchOptions(CaseSensitive: true),
                progress =>
                {
                    if (progress.CompletedUtf16 < SearchEngine.ProgressChunkSize) return;
                    cancelledFromProgress = true;
                    cancellation.Cancel();
                },
                cancellation.Token);
            throw new Exception("大文本查找应支持协作取消");
        }
        catch (OperationCanceledException)
        {
            Check(cancelledFromProgress, "进度回调可以中断运行中的大文本查找");
        }
    }),
    ("anchor mapping follows unchanged paragraph", () =>
    {
        const string oldText = "前言\n\n目标段落\n第二行\n\n结尾";
        const string newText = "新增说明\n\n前言\n\n目标段落\n第二行\n\n结尾";
        int oldAnchor = oldText.IndexOf("目标段落", StringComparison.Ordinal);
        int mapped = TextAnchorMapper.Map(oldText, newText, oldAnchor);
        Check(newText.IndexOf("目标段落", StringComparison.Ordinal) == mapped,
            "外部前置插入后锚点跟随原段落");
        Check(TextAnchorMapper.Map(oldText, "短文", 999) == "短文".Length, "映射结果限制在新文本长度");
    }),
    ("anchor mapping keeps repeated line occurrence", () =>
    {
        const string line = "正文 **中文** text";
        string oldText = "# Old title\n\n" + string.Join("\n\n", Enumerable.Repeat(line, 80));
        string newText = oldText.Replace("Old", "New", StringComparison.Ordinal);
        int anchor = oldText.IndexOf(line, StringComparison.Ordinal) + line.Length - 1;
        Check(TextAnchorMapper.Map(oldText, newText, anchor) == anchor,
            "重复段落行尾映射不能跳到下一处同名段落");
    })
};

int failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {error.GetType().Name}: {error.Message}"); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed; {failed} failed");
return failed == 0 ? 0 : 1;

static object Invoke(string type, string method, params object?[] args)
{
    var target = typeof(TextKind).Assembly.GetType("Qingyue.Core." + type)
        ?? throw new MissingMethodException($"缺失功能：{type}");
    try { return target.GetMethod(method)!.Invoke(null, args)!; }
    catch (TargetInvocationException error) when (error.InnerException is not null)
    { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
}
static TextKind Detect(string? path, string text) => (TextKind)Invoke("TextServices", "DetectKind", path, text);
static string Format(string text) => (string)Invoke("TextServices", "FormatJson", text);
static string FormatKind(string text, TextKind kind) => (string)Invoke("TextServices", "Format", text, kind);
static TextKind Kind(string name) => Enum.TryParse<TextKind>(name, out var kind) ? kind
    : throw new Exception("语言尚未支持：" + name);
static string? Diagnostic(string text, TextKind kind) => (string?)Invoke("TextServices", "Diagnostic", text, kind);
static IReadOnlyList<SearchMatch> Find(string text, string query, string replacement, SearchOptions options) =>
    (IReadOnlyList<SearchMatch>)Invoke("SearchEngine", "Find", text, query, replacement, options);
static string Replace(string text, IReadOnlyList<SearchMatch> matches) => (string)Invoke("SearchEngine", "ReplaceAll", text, matches);
static View Build(string text, TextKind kind) => new(Invoke("ReadingModel", "Build", text, kind));
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected <{expected}>; actual <{actual}>"); }
static void Boundary(string text, int index)
{
    Check(index >= 0 && index <= text.Length, "偏移越界");
    Check(index == 0 || index == text.Length || !char.IsHighSurrogate(text[index - 1]) || !char.IsLowSurrogate(text[index]), "偏移切断 surrogate");
}
static void Reject(Action action, string? contains = null)
{
    try { action(); }
    catch (Exception error)
    {
        Check(error is ArgumentException or InvalidOperationException or FormatException or JsonException, "应抛可处理的异常");
        Check(error.Message.Any(c => c >= '\u4e00' && c <= '\u9fff'), "应有中文错误提示");
        if (contains is not null) Check(error.Message.Contains(contains), "缺少错误类型提示：" + contains);
        return;
    }
    throw new Exception("应拒绝操作");
}
static void VerifyBlocks(View model)
{
    Equal(model.VisibleText, string.Join("\n", model.Blocks.Select(b => b.Text)));
    int start = 0;
    foreach (var block in model.Blocks)
    {
        Equal(start, block.DisplayStart);
        Equal(block.Text, model.VisibleText.Substring(block.DisplayStart, block.Text.Length));
        start += block.Text.Length + 1;
    }
}

static void VerifyTableSeparators(int dashes)
{
    string marker = new('-', dashes);
    foreach (var (leading, trailing) in new[] { ("| ", " |"), ("", ""), ("| ", ""), ("", " |") })
    {
        string input = $"{leading}A | B | C | D{trailing}\r\n" +
            $"{leading}{marker} | :{marker} | {marker}: | :{marker}:{trailing}\r\n" +
            $"{leading}a | b | c | d{trailing}";
        var model = Build(input, TextKind.Markdown);
        Check(model.Blocks.Count == 1 && model.Blocks[0].Kind == ReadingBlockKind.Table,
            $"{dashes} 短横及左右/居中冒号应识别为一个表格（首尾管道可选）");
        Equal("A\tB\tC\tD\na\tb\tc\td", model.Blocks[0].Text);
        Equal(input.Length, model.Blocks[0].SourceLength);
        VerifyBlocks(model);
        int previous = -1;
        for (int offset = 0; offset <= input.Length; offset++)
        {
            int display = model.SourceToDisplay(offset);
            Check(display >= previous, "表格源到显示偏移应单调");
            Boundary(model.VisibleText, display);
            previous = display;
        }
        Equal(input.Length, model.DisplayToSource(model.VisibleText.Length));
    }
}

sealed class View(object model)
{
    public string VisibleText => (string)model.GetType().GetProperty("VisibleText")!.GetValue(model)!;
    public string SourceText => (string)model.GetType().GetProperty("SourceText")!.GetValue(model)!;
    public IReadOnlyList<ReadingBlock> Blocks => (IReadOnlyList<ReadingBlock>)model.GetType().GetProperty("Blocks")!.GetValue(model)!;
    public IReadOnlyList<ReadingHeading> Headings => (IReadOnlyList<ReadingHeading>)model.GetType().GetProperty("Headings")!.GetValue(model)!;
    public int SourceToDisplay(int offset) => (int)model.GetType().GetMethod("SourceToDisplay")!.Invoke(model, [offset])!;
    public int DisplayToSource(int offset) => (int)model.GetType().GetMethod("DisplayToSource")!.Invoke(model, [offset])!;
    public (int Start, int Length) SourceRangeForDisplay(int offset) =>
        ((int Start, int Length))model.GetType().GetMethod("SourceRangeForDisplay")!.Invoke(model, [offset])!;
    public (int Start, int Length) DisplayRangeForSource(int offset) =>
        ((int Start, int Length))model.GetType().GetMethod("DisplayRangeForSource")!.Invoke(model, [offset])!;
}
