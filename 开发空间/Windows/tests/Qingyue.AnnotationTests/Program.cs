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

    Throws<ArgumentException>(() => store.Update(added.Id, new string('x', 4001), [], AnnotationColor.Amber),
        "备注超过 4000 字符被拒绝");
    Throws<ArgumentException>(() => store.Update(added.Id, null,
        Enumerable.Range(0, 11).Select(i => "t" + i).ToArray(), AnnotationColor.Amber),
        "标签超过 10 个被拒绝");

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
}
finally
{
    Directory.Delete(root, recursive: true);
}

Console.WriteLine($"全部通过：{checks} 项");
