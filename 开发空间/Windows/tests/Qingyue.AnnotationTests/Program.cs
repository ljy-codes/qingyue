using Qingyue.Core;

int checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { checks++; Console.WriteLine("PASS " + name); return; }
    throw new Exception(name);
}

string root = Path.Combine(Path.GetTempPath(), "qingyue-annotations-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var file = new DocumentFile("甲\n乙丙丁\n戊");
    var draft = new AnnotationDraft(
        file.Id, file.Path, 2, 5, "乙丙丁", "甲\n", "\n戊");
    var store = new AnnotationStore(root);
    var added = store.Add(file, draft);
    Check(store.For(file).Count == 1 && added.Quote == "乙丙丁", "新增批注可从当前文档查询");

    string richRoot = Path.Combine(root, "rich-draft");
    var richStore = new AnnotationStore(richRoot);
    var richDraft = new AnnotationDraft(
        file.Id, file.Path, 2, file.Text.Length + 20, "乙丙丁", "甲\n", "\n戊",
        "  我的备注  ", ["重点", "  重点  ", "回顾"], AnnotationColor.Blue);
    var rich = richStore.Add(file, richDraft);
    Check(rich.Note == "我的备注"
        && rich.Tags.SequenceEqual(["重点", "回顾"])
        && rich.Color == AnnotationColor.Blue,
        "新增批注必须采用草稿中的备注、标签和颜色，并与 macOS 一样规范化");
    Check(rich.SourceEnd == file.Text.Length + 20,
        "新增批注与 macOS 一样不要求结束位置落在当前正文长度内");
    var emptyDraftQuote = richStore.Add(file, richDraft with { Quote = "" });
    Check(emptyDraftQuote.Quote.Length == 0, "新增批注与 macOS 一样允许空引文");
    Throws<ArgumentException>(() => richStore.Add(file, richDraft with { Path = "" }),
        "新增批注拒绝空字符串路径而不是把它当作无路径");

    var savedFile = new DocumentFile(file.Text);
    savedFile.Save(Path.Combine(root, "saved.md"));
    var pathSaved = store.Add(savedFile, new AnnotationDraft(
        savedFile.Id, savedFile.Path, 2, 5, "乙丙丁", "甲\n", "\n戊"));
    var pathless = new DocumentFile(savedFile.Text);
    typeof(DocumentFile).GetProperty("Id")!.SetValue(pathless, savedFile.Id);
    Check(store.For(pathless).Single().DocumentId == savedFile.Id,
        "无路径查询按文档标识命中已有批注，与 macOS 一致");
    store.Remove(pathSaved.Id);
    Check(store.Update(added.Id, "备注", ["重点", "回顾"], AnnotationColor.Rose), "备注、标签和颜色可更新");
    Check(store.Items.Single().Note == "备注"
        && store.Items.Single().Tags.SequenceEqual(["重点", "回顾"])
        && store.Items.Single().Color == AnnotationColor.Rose, "更新字段正确");

    var saved = await store.SaveAsync();
    Check(saved.Errors.Count == 0, string.Join("\n", saved.Errors));
    var restored = new AnnotationStore(root);
    await restored.LoadAsync();
    Check(restored.Items.Count == 1 && restored.Items[0].Quote == "乙丙丁", "批注往返保存");

    string newPath = Path.Combine(root, "renamed.md");
    Check(restored.MigratePath(file.Id, file.Path, newPath) == 1, "路径迁移命中当前文档");
    Check(restored.Items[0].Path == Path.GetFullPath(newPath), "路径迁移写入规范化绝对路径");

    Check(restored.ApplyAnchorRepairs(
        [new AnnotationAnchorRepair(added.Id, 3, 6, AnnotationAnchorState.Shifted)],
        DateTimeOffset.UnixEpoch) == 1, "锚点修复可批量应用");
    Check(restored.Items[0].AnchorState == AnnotationAnchorState.Shifted
        && restored.Items[0].SourceStart == 3, "锚点修复字段正确");
    Check(restored.ApplyAnchorRepairs(
        [new AnnotationAnchorRepair(added.Id, -9, -4, AnnotationAnchorState.Uncertain)],
        DateTimeOffset.UnixEpoch.AddSeconds(1)) == 1
        && restored.Items[0].SourceStart == 0
        && restored.Items[0].SourceEnd == 0
        && restored.Items[0].AnchorState == AnnotationAnchorState.Uncertain,
        "锚点修复与 macOS 一样夹紧负值和倒置范围，而不是抛错");

    Throws<ArgumentException>(() => store.Update(added.Id, new string('x', 4001), [], AnnotationColor.Amber),
        "备注超过 4000 字符被拒绝");
    Throws<ArgumentException>(() => store.Update(added.Id, null,
        Enumerable.Range(0, 11).Select(i => "t" + i).ToArray(), AnnotationColor.Amber),
        "标签超过 10 个被拒绝");
    Throws<ArgumentException>(() => store.Update(added.Id, null,
        Enumerable.Repeat("重复", 11).ToArray(), AnnotationColor.Amber),
        "标签去重前超过 10 个也被拒绝，与 macOS 一致");
    Throws<ArgumentException>(() => store.Update(added.Id, null,
        ["有效", "   "], AnnotationColor.Amber),
        "标签 trim 后为空时被拒绝，与 macOS 一致");
    Check(store.Update(added.Id, null, ["Tag", "tag"], AnnotationColor.Amber)
        && store.Items.Single().Tags.SequenceEqual(["Tag", "tag"]),
        "标签去重区分大小写，与 macOS 一致");

    File.WriteAllText(Path.Combine(root, "Annotations.json"), "{bad");
    var damaged = new AnnotationStore(root);
    await damaged.LoadAsync();
    Check(damaged.Items.Count == 0 && damaged.LastWarning is not null
        && Directory.GetFiles(root, "Annotations.json.corrupt-*").Length == 1,
        "损坏文件被隔离而不是静默覆盖");

    static DocumentAnnotation Annotation(
        string text, string quote, string before = "", string after = "",
        AnnotationAnchorState state = AnnotationAnchorState.Exact)
    {
        int start = text.IndexOf(quote, StringComparison.Ordinal);
        return new DocumentAnnotation(
            Guid.NewGuid(), Guid.NewGuid(), null, start, start + quote.Length,
            quote, before, after, "我的备注", ["重点"], AnnotationColor.Blue,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, state);
    }

    string oldMarkdown = "# 第一章\n\n第一段 **核对重点** 后文。";
    var markdownAnnotation = Annotation(oldMarkdown, "核对重点", "第一段 ", " 后文。");
    string newMarkdown = "# 新增标题\n\n# 第一章\n\n第一段 **核对重点** 后文。";
    var repaired = AnnotationAnchorResolver.Repair(
        oldMarkdown, newMarkdown, TextKind.Markdown, [markdownAnnotation], DateTimeOffset.UnixEpoch);
    Check(repaired.Single().AnchorState == AnnotationAnchorState.Shifted
        && repaired.Single().SourceStart != markdownAnnotation.SourceStart
        && newMarkdown.Substring(repaired.Single().SourceStart,
            repaired.Single().SourceEnd - repaired.Single().SourceStart).Contains("核对重点", StringComparison.Ordinal),
        "Markdown 可见引文在新增标题后自动跟随");

    string duplicate = "重复\n\n重复";
    var uncertain = AnnotationAnchorResolver.Repair(
        duplicate, "前言\n\n" + duplicate, TextKind.Plain,
        [Annotation(duplicate, "重复")], DateTimeOffset.UnixEpoch);
    Check(uncertain.Single().AnchorState == AnnotationAnchorState.Uncertain,
        "重复引文不被武断重定位");

    var missing = AnnotationAnchorResolver.Repair(
        "保留这句", "整段删除", TextKind.Plain,
        [Annotation("保留这句", "保留这句")], DateTimeOffset.UnixEpoch);
    Check(missing.Single().AnchorState == AnnotationAnchorState.Missing
        && missing.Single().Quote == "保留这句",
        "引文消失时保留批注并标记 Missing");

    var headings = ReadingModel.Build(
        "# 第一章\n\n第一段。\n\n## 第二节\n\n第二段。", TextKind.Markdown).Headings;
    var briefInput = new ReadingBriefInput(
        "示例 <script>alert(1)</script>", @"D:\阅读\示例.md", TextKind.Markdown,
        "# 第一章\n\n第一段。\n\n## 第二节\n\n第二段。", 42,
        new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.FromHours(8)),
        headings,
        [
            Annotation("# 第一章\n\n第一段。\n\n## 第二节\n\n第二段。", "第一段。") with
            {
                DocumentId = Guid.Empty,
                Note = "<img src=x onerror=alert(1)>",
                Tags = ["重点", "待回顾"],
                AnchorState = AnnotationAnchorState.Exact
            }
        ]);
    string briefMarkdown = ReadingBriefExporter.BuildMarkdown(briefInput);
    string briefHtml = ReadingBriefExporter.BuildHtml(briefInput);
    Check(briefMarkdown.Contains("## 第一章", StringComparison.Ordinal)
        && briefMarkdown.Contains("> 第一段。", StringComparison.Ordinal)
        && briefMarkdown.Contains("重点", StringComparison.Ordinal)
        && briefMarkdown.Contains("42%", StringComparison.Ordinal),
        "Markdown 简报包含章节、引文、标签和进度");
    Check(briefHtml.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", StringComparison.Ordinal)
        && briefHtml.Contains("&lt;img src=x onerror=alert(1)&gt;", StringComparison.Ordinal)
        && !briefHtml.Contains("<script>", StringComparison.Ordinal)
        && briefHtml.Contains("default-src 'none'", StringComparison.Ordinal),
        "HTML 简报编码用户文本并带 CSP");
    Check(briefMarkdown.Contains("生成时间：2026-11-16T02:30:00Z", StringComparison.Ordinal)
        && !briefMarkdown.Contains('\r'),
        "Markdown 简报使用 macOS 一致的 ISO8601 时间与 LF 换行");
    Check(briefHtml.Contains("生成时间：2026-11-16T02:30:00Z", StringComparison.Ordinal)
        && !briefHtml.Contains('\r'),
        "HTML 简报使用 macOS 一致的 ISO8601 时间与 LF 换行");

    string duplicateSource = "# 同名\n甲\n# 同名\n乙";
    var duplicateFirst = new DocumentAnnotation(
        Guid.NewGuid(), Guid.Empty, null, 5, 6, "甲", "", "",
        null, ["重点`回顾"], AnnotationColor.Amber,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, AnnotationAnchorState.Exact);
    var duplicateSecond = new DocumentAnnotation(
        Guid.NewGuid(), Guid.Empty, null, 8, 9, "乙", "", "",
        null, [], AnnotationColor.Amber,
        DateTimeOffset.UnixEpoch.AddSeconds(1), DateTimeOffset.UnixEpoch.AddSeconds(1),
        AnnotationAnchorState.Exact);
    var duplicateInput = new ReadingBriefInput(
        "标题", "/tmp/a`b.md", TextKind.Markdown, duplicateSource, 0,
        DateTimeOffset.UnixEpoch,
        [
            new ReadingHeading("同名", 0, 1),
            new ReadingHeading("同名", 7, 1)
        ],
        [duplicateFirst, duplicateSecond]);
    string duplicateMarkdown = ReadingBriefExporter.BuildMarkdown(duplicateInput);
    Check(duplicateMarkdown.Split("## 同名", StringSplitOptions.None).Length == 3,
        "阅读简报：同名标题按标题索引分组，不合并不同章节");
    Check(duplicateMarkdown.Contains("`` /tmp/a`b.md ``", StringComparison.Ordinal)
        && duplicateMarkdown.Contains("`` 重点`回顾 ``", StringComparison.Ordinal),
        "阅读简报：路径和标签使用能容纳反引号的 Markdown 代码跨度");
    Check(duplicateMarkdown.Contains("第 3 行", StringComparison.Ordinal),
        "阅读简报：行号按 UTF-16 源偏移计算");

    var emptyQuote = duplicateFirst with { Quote = "", Note = "", Tags = [] };
    string emptyMarkdown = ReadingBriefExporter.BuildMarkdown(duplicateInput with
    {
        Annotations = [emptyQuote]
    });
    string emptyHtml = ReadingBriefExporter.BuildHtml(duplicateInput with
    {
        Annotations = [emptyQuote]
    });
    Check(emptyMarkdown.Contains("> \n", StringComparison.Ordinal)
        && emptyHtml.Contains("<blockquote></blockquote>", StringComparison.Ordinal),
        "阅读简报：空行引文仍生成有效的 Markdown 与 HTML 引用块");

    Throws<InvalidOperationException>(() => ReadingBriefExporter.BuildMarkdown(
        duplicateInput with
        {
            Annotations =
            [
                duplicateFirst with
                {
                    Note = new string('x', ReadingBriefExporter.MaximumBytes)
                }
            ]
        }), "阅读简报：Markdown 超过 8 MiB 时明确失败");
    Throws<InvalidOperationException>(() => ReadingBriefExporter.BuildHtml(
        duplicateInput with
        {
            Annotations =
            [
                duplicateFirst with
                {
                    Note = new string('x', ReadingBriefExporter.MaximumBytes)
                }
            ]
        }), "阅读简报：HTML 超过 8 MiB 时明确失败");
}
finally
{
    Directory.Delete(root, recursive: true);
}

Console.WriteLine($"全部通过：{checks} 项");
