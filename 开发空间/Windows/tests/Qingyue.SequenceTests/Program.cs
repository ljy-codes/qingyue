using System.Diagnostics;
using Qingyue.Core;

internal static class Program
{
    private const string ScreenshotSequence = """
        sequenceDiagram
            participant W as 管理台
            participant C as 草稿接口
            participant B as 业务服务
            participant D as 数据库
            participant F as 工作流引擎
            participant Q as 消息队列
            W->>C: POST /api/drafts/save 保存草稿
            C->>B: 保存草稿（中文 URL /工作流/草稿）
            B->>D: INSERT workflow_drafts
            D-->>B: draftId
            B->>F: 创建流程实例
            F-->>B: instanceId
            B->>Q: 发送草稿保存事件
            C-->>B: instanceId
            C-->>W: 保存成功
        """;

    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("screenshot-shaped six participants and exact messages", Screenshot),
            ("all supported arrows retain direction and style", Arrows),
            ("self messages and automatic participants", SelfMessages),
            ("explicit aliases update implicit participants without reordering", Aliases),
            ("keywords ignore case while participant IDs retain case", KeywordCase),
            ("safe Unicode and ordinary participant IDs", UnicodeIds),
            ("notes retain placement and auto-declare participants", Notes),
            ("comments and all common line endings", Comments),
            ("message percent markers remain literal text", MessagePercentMarkers),
            ("participant and actor aliases retain percent markers", AliasPercentMarkers),
            ("note percent markers remain literal text", NotePercentMarkers),
            ("semicolon statements reject the complete diagram", SemicolonStatements),
            ("entity semicolons are rejected explicitly without decoding", EntitySemicolons),
            ("semicolons inside comments do not create statements", SemicolonsInComments),
            ("fullwidth semicolons remain literal text", FullwidthSemicolons),
            ("autonumber flag and unsupported numbering changes", Numbering),
            ("line breaks normalize while HTML remains inert text", LiteralLabels),
            ("null, empty and malformed inputs return Chinese errors", InvalidInput),
            ("unsupported statements reject the complete diagram", Unsupported),
            ("directives cannot disappear as comments", Directives),
            ("activation suffixes are rejected explicitly", Activations),
            ("malformed notes and declarations are rejected", MalformedStatements),
            ("participant limit applies to declarations, messages and notes", ParticipantLimit),
            ("event limit includes both notes and messages", EventLimit),
            ("source and line limits include comments", SourceAndLineLimits),
            ("participant and event label boundaries", LabelLimits),
            ("failed parse leaves no partial result or later state", FailureIsolation),
            ("adversarial and random malformed inputs never throw", Adversarial)
        };
        int failed = 0;
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"Sequence checks: {tests.Length - failed}/{tests.Length} passed");
        return failed == 0 ? 0 : 1;
    }

    private static void Screenshot()
    {
        string original = ScreenshotSequence;
        var diagram = Parse(original);
        Equal(6, diagram.Participants.Count, "six participants");
        Equal("W,C,B,D,F,Q", string.Join(",", diagram.Participants.Select(p => p.Id)), "declared order");
        Equal("管理台", diagram.Participants[0].Label, "as alias");
        Equal(9, diagram.Events.Count, "no message dropped");
        Equal(new SequenceEvent("W", "C", "POST /api/drafts/save 保存草稿", SequenceEventKind.Message),
            diagram.Events[0], "exact request");
        Equal(new SequenceEvent("C", "B", "instanceId", SequenceEventKind.Message, true),
            diagram.Events[7], "exact dashed response");
        Equal("保存草稿（中文 URL /工作流/草稿）", diagram.Events[1].Text, "Chinese and URL");
        Check(!diagram.AutoNumber && diagram.Participants.All(p => !p.IsActor), "default flags");
        Equal(ScreenshotSequence, original, "source remains unchanged");
    }

    private static void Arrows()
    {
        foreach (var (token, dashed, arrow) in new[]
        {
            ("->", false, ">"), ("->>", false, ">>"), ("-->", true, ">"), ("-->>", true, ">>"),
            ("-x", false, "x"), ("--x", true, "x"), ("-)", false, ")"), ("--)", true, ")")
        })
        {
            var message = Parse($"sequenceDiagram\nA{token}B: 消息").Events.Single();
            Equal(new SequenceEvent("A", "B", "消息", SequenceEventKind.Message, dashed, arrow),
                message, token);
        }
        var spaced = Parse("sequenceDiagram\n A  -->>  B : https://站点.test:8443/接口?q=中文");
        Equal("https://站点.test:8443/接口?q=中文", spaced.Events.Single().Text, "colons stay in text");
    }

    private static void SelfMessages()
    {
        var diagram = Parse("sequenceDiagram\n服务->>服务: 自调用\n服务-->>客户端: 完成\n客户端->>服务: 重试");
        Equal("服务,客户端", string.Join(",", diagram.Participants.Select(p => p.Id)), "first-use order");
        Equal("服务", diagram.Participants[0].Label, "implicit label");
        Equal(diagram.Events[0].From, diagram.Events[0].To, "self message");
        Equal(3, diagram.Events.Count, "all events");
    }

    private static void Aliases()
    {
        var diagram = Parse("""
            sequenceDiagram
            W->>C: 保存
            actor W as 管理员
            participant C as 草稿接口
            participant DB
            """);
        Equal("W,C,DB", string.Join(",", diagram.Participants.Select(p => p.Id)), "stable first-use order");
        Equal(new SequenceParticipant("W", "管理员", true), diagram.Participants[0], "actor alias");
        Equal(new SequenceParticipant("C", "草稿接口"), diagram.Participants[1], "participant alias");
        Equal(new SequenceParticipant("DB", "DB"), diagram.Participants[2], "default label");
        Equal("Name as text", Parse("sequenceDiagram\nparticipant A as Name as text").Participants[0].Label,
            "alias text is not split repeatedly");
        Reject("sequenceDiagram\nparticipant A\nactor A", "重复");
    }

    private static void KeywordCase()
    {
        var diagram = Parse("SEQUENCEDIAGRAM\nPaRtIcIpAnT A AS 管理台\nACTOR a as 用户\nAuToNuMbEr\nNoTe RIGHT OF A: 提示\nA->>a: 保存");
        Equal(2, diagram.Participants.Count, "ID case preserved");
        Equal("A", diagram.Events[1].From, "uppercase ID");
        Equal("a", diagram.Events[1].To, "lowercase ID");
        Check(diagram.AutoNumber && diagram.Participants[1].IsActor, "keyword matching");
        Reject("sequenceDiagram\nA-X B: unsupported");
    }

    private static void UnicodeIds()
    {
        foreach (string id in new[] { "W", "A_Z", "123", "_db", "管理台", "用户１", "é", "e\u0301", "𠮷", "api.v1", "workflow-api" })
        {
            var diagram = Parse($"sequenceDiagram\nparticipant {id} as 中文标签\n{id}->>B: 请求");
            Equal(id, diagram.Events.Single().From, "safe ID");
            Equal("中文标签", diagram.Participants[0].Label, "Unicode alias");
        }
        foreach (string id in new[] { "<script>", "A B", "A/B", "A+B", "A;B", "A{B}", "A\u202eB", "\ud800" })
            Reject($"sequenceDiagram\nparticipant {id}\nA->>B: 请求");
    }

    private static void Notes()
    {
        var diagram = Parse("""
            sequenceDiagram
            Note over A,B: 跨角色<br/>第二行
            Note right of A: 右侧
            Note left of B: 左侧
            Note over C: 单角色
            """);
        Equal("A,B,C", string.Join(",", diagram.Participants.Select(p => p.Id)), "note auto-declaration");
        Equal(new SequenceEvent("A", "B", "跨角色\n第二行", SequenceEventKind.Note), diagram.Events[0], "over pair");
        Equal(new SequenceEvent("A", "A", "右侧", SequenceEventKind.Note, Placement: "right"), diagram.Events[1], "right");
        Equal(new SequenceEvent("B", "B", "左侧", SequenceEventKind.Note, Placement: "left"), diagram.Events[2], "left");
        Equal(new SequenceEvent("C", "C", "单角色", SequenceEventKind.Note), diagram.Events[3], "over one");
        Equal("A", Parse("sequenceDiagram\nNote over A , B : 提示").Events[0].From, "spaced note targets");
    }

    private static void Comments()
    {
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            string source = string.Join(newline, new[]
            {
                "\ufeff%% 开头说明", "", "sequenceDiagram %% 图说明", "%% A->>B: 不是消息",
                "participant A as 管理台 %% 别名说明", "A->>B: 请求 %% 行尾说明", "   "
            });
            var diagram = Parse(source);
            Equal(2, diagram.Participants.Count, "comment does not declare");
            Equal("管理台 %% 别名说明", diagram.Participants[0].Label, "alias is text, not an inline comment");
            Equal("请求 %% 行尾说明", diagram.Events.Single().Text, "message is text, not an inline comment");
        }
    }

    private static void MessagePercentMarkers()
    {
        foreach (string text in new[]
        {
            "占位符 %%tenant%% 已替换",
            "%%tenant%%",
            "POST /api/drafts/save?tenant=%%tenant%% 保存草稿",
            "请求 %% B-->>A: 仍是正文",
            "完成 %%"
        })
            Equal(text, Parse("sequenceDiagram\nA->>B: " + text).Events.Single().Text, "complete message text");
    }

    private static void AliasPercentMarkers()
    {
        foreach (string declaration in new[] { "participant", "actor" })
        foreach (string alias in new[] { "管理台 %%tenant%% 别名", "%%tenant%%", "显示 %% 说明" })
        {
            var participant = Parse($"sequenceDiagram\n{declaration} A as {alias}").Participants.Single();
            Equal(alias, participant.Label, "complete alias text");
            Equal(declaration == "actor", participant.IsActor, "declaration kind retained");
        }
    }

    private static void NotePercentMarkers()
    {
        foreach (string placement in new[] { "over A,B", "right of A", "left of A" })
        {
            var note = Parse($"sequenceDiagram\nNote {placement}: %%tenant%% 提示 %% 仍是正文").Events.Single();
            Equal("%%tenant%% 提示 %% 仍是正文", note.Text, "complete note text");
            Equal(SequenceEventKind.Note, note.Kind, "note kind retained");
        }
    }

    private static void SemicolonStatements()
    {
        foreach (string statement in new[]
        {
            "A->>B: 保存; B-->>A: 完成",
            "A->>B: 保存; activate B",
            "A->>B: 保存; loop 重复",
            "A->>B: 保存;",
            "A->>B: %%tenant%%; activate B",
            "participant C as 管理台; participant D as 服务",
            "actor C as 用户; activate C",
            "participant C; participant D",
            "Note over A,B: 提示; activate B",
            "Note right of A: 提示; B-->>A: 完成",
            "Note left of A: 提示;",
            "autonumber; activate A"
        })
        {
            string source = $"sequenceDiagram\nA->>B: 已有消息\n{statement}\nB-->>A: 返回";
            string error = Reject(source, "分号");
            Check(error.Contains("第 3 行", StringComparison.Ordinal), "semicolon error uses the physical line number");
        }
        Reject("sequenceDiagram; participant A", "分号");
    }

    private static void EntitySemicolons()
    {
        foreach (string entity in new[] { "&amp;", "&#59;", "&#x3B;", "#59;", "#infin;", "&unknown;" })
        foreach (string statement in new[]
        {
            $"A->>B: 实体 {entity} 文本",
            $"participant C as 实体 {entity} 标签",
            $"Note over A,B: 实体 {entity} 提示"
        })
            Reject($"sequenceDiagram\nA->>B: 已有消息\n{statement}", "实体");
    }

    private static void SemicolonsInComments()
    {
        var diagram = Parse("""
            %% 注释; participant X; activate X
            sequenceDiagram %% 头部注释; participant X
                %% 忽略; A->>X: 不是消息
            participant A as 管理台
            autonumber %% 编号说明; activate X
            A->>B: 请求
            %% 注释中的实体 &amp; 和 #59; 不生成事件
            """);
        Equal("A,B", string.Join(",", diagram.Participants.Select(p => p.Id)), "comment syntax stays ignored");
        Equal(1, diagram.Events.Count, "comment semicolons do not create events");
        Check(diagram.AutoNumber, "non-text autonumber comment remains supported");
    }

    private static void FullwidthSemicolons()
    {
        var diagram = Parse("""
            sequenceDiagram
            participant A as 管理台；用户
            A->>B: 保存；返回
            Note over A,B: 提示；不分句
            """);
        Equal("管理台；用户", diagram.Participants[0].Label, "fullwidth alias punctuation");
        Equal("保存；返回", diagram.Events[0].Text, "fullwidth message punctuation");
        Equal("提示；不分句", diagram.Events[1].Text, "fullwidth note punctuation");
    }

    private static void Numbering()
    {
        Check(Parse("sequenceDiagram\nautonumber\nA->>B: 请求").AutoNumber, "numbered");
        Check(!Parse("sequenceDiagram\nA->>B: 请求").AutoNumber, "unnumbered");
        foreach (string statement in new[] { "autonumber 2", "autonumber 1 2", "autonumber off" })
            Reject($"sequenceDiagram\n{statement}\nA->>B: 请求", "autonumber");
        Reject("sequenceDiagram\nA->>B: 请求\nautonumber", "autonumber");
    }

    private static void LiteralLabels()
    {
        var diagram = Parse("""
            sequenceDiagram
            participant A as 第一行<br>第二行<br/>第三行<br />第四行
            A->>B: <script>alert("x")</script><br/>https://example.test/a?a=1&b=中文<br><b>粗体文字</b>
            Note over A: <img src="https://example.test/image" onerror="bad()">
            """);
        Equal("第一行\n第二行\n第三行\n第四行", diagram.Participants[0].Label, "participant breaks");
        Equal("<script>alert(\"x\")</script>\nhttps://example.test/a?a=1&b=中文\n<b>粗体文字</b>",
            diagram.Events[0].Text, "HTML preserved without execution");
        Equal("<img src=\"https://example.test/image\" onerror=\"bad()\">", diagram.Events[1].Text, "HTML note");
    }

    private static void InvalidInput()
    {
        foreach (string? source in new string?[]
        {
            null, "", " \r\n\t", "%% comments only", "sequenceDiagram", "flowchart TD\nA-->B", "A->>B: 缺少头部",
            "sequenceDiagramOops\nA->>B: 请求", "sequenceDiagram\nA->>B", "sequenceDiagram\nA->>: 请求",
            "sequenceDiagram\n->>B: 请求", "sequenceDiagram\nA->>B:", "sequenceDiagram\nA->>B:  ",
            "sequenceDiagram\nA--->B: 错误箭头", "sequenceDiagram\nA<--B: 不支持反向",
            "sequenceDiagram\nA->>>B: 错误箭头", "sequenceDiagram\nsequenceDiagram"
        })
            Reject(source);
        string error = Reject("sequenceDiagram\n\n%% ignored\nA->>B: 请求\nbroken");
        Check(error.Contains("5"), "physical line number retained");
    }

    private static void Unsupported()
    {
        foreach (string statement in new[]
        {
            "alt 成功", "else 失败", "end", "loop 3 次", "opt 可选", "par 并行", "and 第二条",
            "critical 关键区", "option 可选", "break 中断", "rect rgb(0,0,0)", "activate A", "deactivate A",
            "create participant D", "destroy A", "box 服务", "title 标题", "link A: 文档 @ https://example.test",
            "accTitle: accessible", "accDescr: description", "<html>指令</html>", "---", "totallyUnknown"
        })
            Reject($"sequenceDiagram\nA->>B: 已有消息\n{statement}\nB-->>A: 返回");
    }

    private static void Directives()
    {
        foreach (string source in new[]
        {
            "%%{init: {'theme':'dark'}}%%\nsequenceDiagram\nA->>B: 请求",
            "sequenceDiagram\n%%{init: {'theme':'dark'}}%%\nA->>B: 请求",
            "sequenceDiagram %%{initialize: {}}%%\nA->>B: 请求",
            "sequenceDiagram\n%%{init:\n%% more\nA->>B: 请求",
            "---\nconfig:\n  theme: dark\n---\nsequenceDiagram\nA->>B: 请求"
        })
            Reject(source);
        Reject("sequenceDiagram\n%%{init: {}}%%\nA->>B: 请求", "初始化");
    }

    private static void Activations()
    {
        foreach (string token in new[] { "->>+", "->>-", "-->>+", "-->>-", "->+", "--)-" })
            Reject($"sequenceDiagram\nA{token}B: 请求", "激活");
    }

    private static void MalformedStatements()
    {
        foreach (string statement in new[]
        {
            "participant", "actor", "participant A as", "actor as 用户", "participant A B",
            "participant A {\"type\":\"database\"}", "participant A@{ \"type\": \"queue\" }",
            "Note over: 空目标", "Note over A,B,C: 三个", "Note right A: 缺少of",
            "Note right of A,B: 多目标", "Note left of: 空目标", "Note over A:",
            "Note over A,B: ", "Note over A,,B: 无效目标", "Note over A,B", "Note below A: 未知位置"
        })
            Reject($"sequenceDiagram\n{statement}");
    }

    private static void ParticipantLimit()
    {
        string twenty = "sequenceDiagram\n" + string.Join("\n", Enumerable.Range(0, 20).Select(i => $"participant P{i}"));
        Equal(20, Parse(twenty).Participants.Count, "20 declared participants accepted");
        Reject(twenty + "\nparticipant P20", "20");
        Reject(twenty + "\nP0->>P20: 请求", "20");
        Reject(twenty + "\nNote over P0,P20: 备注", "20");
        string implicitTwenty = "sequenceDiagram\n" + string.Join("\n", Enumerable.Range(1, 19).Select(i => $"P0->>P{i}: 请求"));
        Equal(20, Parse(implicitTwenty).Participants.Count, "20 implicit participants accepted");
        Reject(implicitTwenty + "\nP0->>P20: 请求", "20");
    }

    private static void EventLimit()
    {
        string events = "sequenceDiagram\n" + string.Join("\n", Enumerable.Range(0, 100).Select(_ => "A->>B: 请求\nNote over A,B: 备注"));
        Equal(200, Parse(events).Events.Count, "200 combined events accepted");
        Reject(events + "\nA->>B: 超限", "200");
        Reject(events + "\nNote over A: 超限", "200");
    }

    private static void SourceAndLineLimits()
    {
        const string prefix = "sequenceDiagram\nA->>B: 请求\n%%";
        Equal(1, Parse(prefix + new string('x', 32_000 - prefix.Length)).Events.Count, "32000 source characters accepted");
        Reject(prefix + new string('x', 32_001 - prefix.Length), "32000");
        string lines = "sequenceDiagram\nA->>B: 请求\n" + string.Join("\n", Enumerable.Repeat("%%", 998));
        Equal(1, Parse(lines).Events.Count, "1000 physical lines accepted");
        Reject(lines + "\n%%", "1000");
        Reject("sequenceDiagram\nA->>B: 请求\n" + new string('\n', 1000), "1000");
    }

    private static void LabelLimits()
    {
        string label = new('文', 2000);
        Equal(label, Parse("sequenceDiagram\nparticipant A as " + label).Participants.Single().Label, "alias boundary");
        Equal(label, Parse("sequenceDiagram\nA->>B: " + label).Events.Single().Text, "message boundary");
        Equal(label, Parse("sequenceDiagram\nNote over A: " + label).Events.Single().Text, "note boundary");
        Reject("sequenceDiagram\nparticipant A as " + label + "文", "2000");
        Reject("sequenceDiagram\nA->>B: " + label + "文", "2000");
        Reject("sequenceDiagram\nNote over A: " + label + "文", "2000");
        Reject("sequenceDiagram\nparticipant " + new string('A', 2001), "2000");
        Reject("sequenceDiagram\nA->>B: " + string.Concat(Enumerable.Repeat("<br>", 501)), "2000");
    }

    private static void FailureIsolation()
    {
        Check(MermaidSequence.TryParse(ScreenshotSequence, out var diagram, out var error), "first parse");
        Check(diagram is not null && error is null, "success outputs");
        Check(!MermaidSequence.TryParse("sequenceDiagram\nA->>B: 已解析\nloop 重复", out diagram, out error), "failure");
        Check(diagram is null && !string.IsNullOrWhiteSpace(error), "no partial diagram");
        var next = Parse("sequenceDiagram\nX->>Y: 新图");
        Equal("X,Y", string.Join(",", next.Participants.Select(p => p.Id)), "no shared participant state");
        Equal(1, next.Events.Count, "no shared events");
    }

    private static void Adversarial()
    {
        var watch = Stopwatch.StartNew();
        foreach (string source in new[]
        {
            "sequenceDiagram\n" + new string('-', 30_000),
            "sequenceDiagram\n" + new string('A', 30_000) + "->>B: 请求",
            "sequenceDiagram\nNote " + new string(' ', 29_000) + ": 请求",
            "sequenceDiagram\nA->>B: " + new string('<', 29_000),
            "sequenceDiagram\nparticipant " + new string('\ud800', 29_000)
        })
            Reject(source);
        var random = new Random(82127);
        const string alphabet = "A_B0->):,% <>中\n\r\ud800";
        for (int sample = 0; sample < 500; sample++)
        {
            string randomText = new(Enumerable.Range(0, random.Next(0, 250))
                .Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            bool parsed = MermaidSequence.TryParse("sequenceDiagram\n" + randomText, out var diagram, out var error);
            Check(parsed ? diagram is not null && error is null : diagram is null && !string.IsNullOrWhiteSpace(error),
                "TryParse output consistency");
        }
        Check(watch.Elapsed < TimeSpan.FromSeconds(10), "bounded work for malformed input");
    }

    private static SequenceDiagram Parse(string source)
    {
        bool parsed = MermaidSequence.TryParse(source, out var diagram, out var error);
        Check(parsed && diagram is not null, "expected success: " + error);
        Check(error is null, "success clears error");
        return diagram!;
    }

    private static string Reject(string? source, string? fragment = null)
    {
        bool parsed = MermaidSequence.TryParse(source!, out var diagram, out var error);
        Check(!parsed && diagram is null, "invalid input returned success or a partial diagram: " + source?[..Math.Min(100, source.Length)]);
        Check(!string.IsNullOrWhiteSpace(error) && error.Any(c => c is >= '\u4e00' and <= '\u9fff'), "Chinese error required");
        if (fragment is not null) Check(error!.Contains(fragment, StringComparison.Ordinal), "error should mention " + fragment + ": " + error);
        return error!;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected '{expected}', got '{actual}'");
}
