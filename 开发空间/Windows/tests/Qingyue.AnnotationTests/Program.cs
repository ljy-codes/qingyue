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
}
finally
{
    Directory.Delete(root, recursive: true);
}

Console.WriteLine($"全部通过：{checks} 项");
