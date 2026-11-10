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
            new[] { new SearchMatch(0, 0, "x"), new SearchMatch(0, 0, "y") },
            new[] { new SearchMatch(3, 1, "x"), new SearchMatch(0, 1, "y") } })
            UnchangedOrRejected("a😀b", matches);
        Equal("a😀x", Replace("a😀b", new[] { new SearchMatch(3, 1, "x") }));
    }),
    ("search/resource guards", () =>
    {
        Reject(() => Find(new string('a', 5_000_001), "a", "", new()));
        Reject(() => Find("a", new string('a', 4097), "", new()));
        Reject(() => Find("a", "a", new string('b', 1_000_001), new()));
        Reject(() => Find(new string('a', 100_001), "a", "", new()));
        Reject(() => Replace("aaaaaaaaaaa", new[] { new SearchMatch(0, 1, new string('x', 1_000_000)),
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
        Equal(0, Find("😀", ".", "", new(Regex: true)).Count);
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
        Check(!Build(atLimit + "| overflow |\n", TextKind.Markdown).Blocks.Any(b => b.Kind == ReadingBlockKind.Table),
            "1025 个单元格应退回，不能截断正文");
    }),
    ("reading/table mismatched body row ends table without losing source", () =>
    {
        const string input = "| A | B |\n| --- | --- |\n| x | y |\n| stop |\n| u | v |";
        var model = Build(input, TextKind.Markdown);
        Equal(2, model.Blocks.Count);
        Equal(ReadingBlockKind.Table, model.Blocks[0].Kind);
        Equal("A\tB\nx\ty", model.Blocks[0].Text);
        Equal(ReadingBlockKind.Paragraph, model.Blocks[1].Kind);
        Equal("| stop |\n| u | v |", model.Blocks[1].Text);
        Equal(input.Length, model.Blocks[1].SourceStart + model.Blocks[1].SourceLength);
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
static void UnchangedOrRejected(string text, IReadOnlyList<SearchMatch> matches)
{
    try { Equal(text, Replace(text, matches)); }
    catch (ArgumentException error) { Check(error.Message.Any(c => c >= '\u4e00' && c <= '\u9fff'), "异常应中文"); }
    catch (InvalidOperationException error) { Check(error.Message.Any(c => c >= '\u4e00' && c <= '\u9fff'), "异常应中文"); }
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
    public IReadOnlyList<ReadingBlock> Blocks => (IReadOnlyList<ReadingBlock>)model.GetType().GetProperty("Blocks")!.GetValue(model)!;
    public IReadOnlyList<ReadingHeading> Headings => (IReadOnlyList<ReadingHeading>)model.GetType().GetProperty("Headings")!.GetValue(model)!;
    public int SourceToDisplay(int offset) => (int)model.GetType().GetMethod("SourceToDisplay")!.Invoke(model, [offset])!;
    public int DisplayToSource(int offset) => (int)model.GetType().GetMethod("DisplayToSource")!.Invoke(model, [offset])!;
}
