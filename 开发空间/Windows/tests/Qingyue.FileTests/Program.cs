using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Qingyue.Core;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var tests = new (string Name, Func<Task> Run)[]
{
    ("精确编码 BOM 与 Unicode 往返", () => Run(EncodingRoundTrips)),
    ("严格解码 二进制 与大小限制", () => Run(RejectInvalidInput)),
    ("Ordinal 脏判断与内部 LF", () => Run(DirtyAndNormalization)),
    ("外部冲突与明确覆盖确实写盘", () => Run(ExternalConflict)),
    ("另存不覆盖与新文档无覆盖权", () => Run(SaveAsNoOverwrite)),
    ("混合换行与快照缺失原字节保护", () => Run(MixedProtection)),
    ("只读和独占句柄保留正文基线", () => Run(LockedAndReadonly)),
    ("重命名不保存脏正文与同名冲突", () => Run(RenameSafety)),
    ("Windows 非法文件名设备名", () => Run(InvalidNames)),
    ("快照恢复与损坏快照拒绝", () => Run(SnapshotRoundTrip)),
    ("硬链接拒绝原位写与另存", () => Run(HardLinkSafety)),
    ("会话干净重读与脏稿优先", SessionAndDraft),
    ("快照集合复制与串行保存", SnapshotCopy),
    ("损坏草稿隔离并继续恢复", QuarantineDraft),
    ("孤立草稿找回与重复路径副本", OrphansAndDuplicates),
    ("版本与重复 ID 校验", InvalidWorkspace),
    ("200文档及150MiB草稿限额", Limits),
    ("先草稿后会话再清理与失败保护", SaveFailureOrder),
    ("清除草稿阻止已排队写复活", ClearQueue),
    ("干净 UTF16 无 BOM 会话恢复保留编码", CleanEncodingSession),
    ("干净 Mixed 会话恢复仍能原字节保存", CleanMixedSession),
    ("恢复时拒绝草稿与会话路径错配", DraftPathMismatch),
    ("符号链接与父目录重解析点拒绝写入", () => Run(ReparseSafety)),
    ("保存保留目标受保护 ACL", () => Run(AclPreservation)),
    ("不成对代理字符保存拒绝并保留编辑", () => Run(InvalidSurrogates)),
};
int failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}\n{ex}"); }
}
Console.WriteLine($"RESULT {tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static Task Run(Action action) { action(); return Task.CompletedTask; }
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static T Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T ex) { return ex; }
    throw new Exception("预期异常未发生：" + typeof(T).Name);
}
static void EqualBytes(string path, byte[] bytes) =>
    Check(File.ReadAllBytes(path).SequenceEqual(bytes), "磁盘字节不符：" + path);
static WorkspaceSession Session(params DocumentFile[] docs) =>
    new(1, docs.Select(d => new WorkspaceEntry(d.Id, d.Path, true, 0, 0, 0)).ToList(), docs.FirstOrDefault()?.Id);
static async Task Saved(WorkspaceStore store, WorkspaceSession session, params DocumentSnapshot[] snapshots)
{
    var result = await store.SaveAsync(session, snapshots);
    Check(result.Errors.Count == 0, string.Join("\n", result.Errors));
}
static void EncodingRoundTrips()
{
    using var tmp = new TempDirectory();
    const string text = "e\u0301 😀 中 \U00020000\r\nnext\r\n";
    foreach (var (codec, encoding, bom) in new (TextCodec, Encoding, bool)[]
    {
        (TextCodec.Utf8, new UTF8Encoding(false, true), false),
        (TextCodec.Utf8Bom, new UTF8Encoding(true, true), true),
        (TextCodec.Utf16LE, new UnicodeEncoding(false, true, true), true),
        (TextCodec.Utf16BE, new UnicodeEncoding(true, true, true), true),
        (TextCodec.Utf16LE, new UnicodeEncoding(false, false, true), false),
        (TextCodec.Utf16BE, new UnicodeEncoding(true, false, true), false),
        (TextCodec.Gb18030, Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), false)
    })
    {
        var expected = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        var path = tmp.File(Guid.NewGuid() + ".txt");
        File.WriteAllBytes(path, expected);
        var file = DocumentFile.Open(path, bom || codec == TextCodec.Utf8 ? null : codec);
        Check(file.Text == text.Replace("\r\n", "\n"), "解码或组合 Unicode 被改变");
        Check(file.Codec == codec && file.LineEnding == LineEnding.CrLf && !file.IsDirty, "编码/换行/基线不正确");
        file.Save();
        EqualBytes(path, expected);
        file.Text += "新增\n";
        file.Save();
        EqualBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(text + "新增\r\n")).ToArray());
    }
    foreach (var ending in new[] { "\r", "\n", "\r\n" })
    {
        var path = tmp.File(Guid.NewGuid() + ".txt");
        var expected = Encoding.UTF8.GetBytes("a" + ending + "b" + ending);
        File.WriteAllBytes(path, expected);
        DocumentFile.Open(path).Save();
        EqualBytes(path, expected);
    }
}
static void RejectInvalidInput()
{
    using var tmp = new TempDirectory();
    var path = tmp.File("invalid.txt");
    File.WriteAllBytes(path, [0xc3, 0x28]);
    Throws<InvalidDataException>(() => DocumentFile.Open(path, TextCodec.Utf8));
    File.WriteAllBytes(path, [0xff, 0xfe, 0x00, 0xd8]);
    Throws<InvalidDataException>(() => DocumentFile.Open(path));
    File.WriteAllBytes(path, [0x61, 0, 0x62, 1]);
    Throws<InvalidDataException>(() => DocumentFile.Open(path));
    using (var stream = File.Create(path)) stream.SetLength(50L * 1024 * 1024 + 1);
    Throws<InvalidDataException>(() => DocumentFile.Open(path));
    var large = new DocumentFile(new string('a', 50 * 1024 * 1024 + 1));
    Throws<InvalidDataException>(() => large.Save(tmp.File("too-large.txt")));
    Check(!File.Exists(tmp.File("too-large.txt")), "超限文件不应落盘");
}
static void DirtyAndNormalization()
{
    using var tmp = new TempDirectory();
    var path = tmp.File("ordinal.txt");
    File.WriteAllText(path, "é\n", new UTF8Encoding(false));
    var file = DocumentFile.Open(path);
    file.Text = "e\u0301\r\n";
    Check(file.IsDirty && file.Text == "e\u0301\n", "必须按 Ordinal 比较且归一化 LF");
    file.Text = "é\r";
    Check(!file.IsDirty, "回到基线应干净");
    file.Codec = TextCodec.Utf8Bom;
    Check(file.IsDirty, "编码变更应脏");
    file.Codec = TextCodec.Utf8;
    file.LineEnding = LineEnding.CrLf;
    Check(file.IsDirty, "换行变更应脏");
}
static void ExternalConflict()
{
    using var tmp = new TempDirectory();
    var path = tmp.File("conflict.txt");
    File.WriteAllText(path, "one");
    var file = DocumentFile.Open(path);
    file.Text = "edit";
    var before = file.CaptureSnapshot();
    var time = File.GetLastWriteTimeUtc(path);
    File.WriteAllText(path, "two");
    File.SetLastWriteTimeUtc(path, time);
    Check(file.DiskChanged(), "相同大小时间的内容变动也必须发现");
    Throws<FileConflictException>(() => file.Save());
    Check(file.CaptureSnapshot() == before && file.IsDirty, "失败不得更新基线");
    file.Save(allowOverwrite: true);
    Check(File.ReadAllText(path) == "edit" && !file.IsDirty && !file.DiskChanged(), "明确覆盖必须实际写盘");
    File.Delete(path);
    Check(file.DiskChanged(), "删除应视为变化");
    Throws<FileConflictException>(() => file.Save());
}
static void SaveAsNoOverwrite()
{
    using var tmp = new TempDirectory();
    var path = tmp.File("existing.txt");
    File.WriteAllText(path, "original");
    var fresh = new DocumentFile("new");
    fresh.Rename("existing.txt");
    Throws<FileConflictException>(() => fresh.Save(path));
    Check(fresh.Path is null && File.ReadAllText(path) == "original", "建议名不得获取覆盖权");
    fresh.Save(path, true);
    Check(File.ReadAllText(path) == "new", "显式覆盖目标文件必须写入");
    var other = tmp.File("other.txt");
    File.WriteAllText(other, "other");
    Throws<FileConflictException>(() => fresh.Save(other));
    var copy = tmp.File("copy.txt");
    fresh.Save(copy);
    Check(fresh.Path == copy && File.ReadAllText(path) == "new", "另存应更新路径且保留原文件");
}
static void MixedProtection()
{
    using var tmp = new TempDirectory();
    var path = tmp.File("mixed.txt");
    byte[] bytes = Encoding.UTF8.GetBytes("a\r\nb\nc\rd");
    File.WriteAllBytes(path, bytes);
    var file = DocumentFile.Open(path);
    Check(file.LineEnding == LineEnding.Mixed && file.Text == "a\nb\nc\nd", "未识别混合换行");
    file.Save();
    EqualBytes(path, bytes);
    var snapshot = file.CaptureSnapshot();
    var restored = DocumentFile.Restore(snapshot);
    Throws<MixedLineEndingsException>(() => restored.Save());
    file.Text += "edit";
    Throws<MixedLineEndingsException>(() => file.Save());
    file.LineEnding = LineEnding.Lf;
    file.Save();
    Check(File.ReadAllText(path) == "a\nb\nc\ndedit", "显式转换失败");
}
static void LockedAndReadonly()
{
    using var tmp = new TempDirectory();
    var path = tmp.File("protected.txt");
    File.WriteAllText(path, "original");
    var file = DocumentFile.Open(path);
    file.Text = "edited";
    var before = file.CaptureSnapshot();
    File.SetAttributes(path, FileAttributes.ReadOnly);
    try { Throws<IOException>(() => file.Save()); }
    finally { File.SetAttributes(path, FileAttributes.Normal); }
    Check(file.CaptureSnapshot() == before, "只读失败丢失编辑或基线");
    using (var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Throws<IOException>(() => file.Save());
    Check(file.CaptureSnapshot() == before && File.ReadAllText(path) == "original", "占用失败改变原文件");
    Check(!Directory.EnumerateFiles(tmp.Root, "*.tmp").Any(), "失败遗留临时文件");
    file.Save();
    Check(File.ReadAllText(path) == "edited", "解除占用后应能保存");
}
static void RenameSafety()
{
    using var tmp = new TempDirectory();
    var path = tmp.File("original.txt");
    File.WriteAllText(path, "disk");
    var file = DocumentFile.Open(path);
    file.Text = "dirty";
    file.Rename("renamed.txt");
    Check(file.Path == tmp.File("renamed.txt") && file.IsDirty && file.Text == "dirty", "重命名丢失编辑");
    Check(File.ReadAllText(file.Path!) == "disk" && !File.Exists(path), "重命名不得保存正文");
    File.WriteAllText(tmp.File("occupied.txt"), "other");
    Throws<IOException>(() => file.Rename("occupied.txt"));
    file.Rename("RENAMED.txt");
    Check(System.IO.Path.GetFileName(file.Path) == "RENAMED.txt", "大小写重命名失败");
    Check(Directory.GetFiles(tmp.Root).Length == 2, "大小写改名遗留文件");
    File.WriteAllText(file.Path!, "external");
    Throws<FileConflictException>(() => file.Rename("last.txt"));
}
static void InvalidNames()
{
    foreach (var name in new[] { "", ".", "..", "CON", "nul.txt", "COM1.log", "LPT9", "aux .txt", "CONIN$", "COM¹.txt", "a/b", @"a\b", "a:b", "a.", "a ", "x\0y", "a?" })
    {
        var file = new DocumentFile("dirty");
        Throws<IOException>(() => file.Rename(name));
        Check(file.SuggestedName is null && file.Path is null, "非法名不应改变文档");
    }
    var valid = new DocumentFile();
    valid.Rename("你好.md");
    Check(valid.SuggestedName == "你好.md" && valid.Title == "你好.md" && valid.Path is null, "新文档只应设置建议名");
}
static void SnapshotRoundTrip()
{
    using var tmp = new TempDirectory();
    var path = tmp.File("snapshot.txt");
    File.WriteAllText(path, "base");
    var file = DocumentFile.Open(path);
    file.Text = "修改 e\u0301😀";
    file.Codec = TextCodec.Utf8Bom;
    var snapshot = file.CaptureSnapshot();
    var copy = DocumentFile.Restore(snapshot);
    Check(copy.CaptureSnapshot() == snapshot && copy.IsDirty, "快照未完整往返");
    copy.Save();
    EqualBytes(path, [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(file.Text)]);
    Throws<InvalidDataException>(() => DocumentFile.Restore(snapshot with { Version = 99 }));
    Throws<InvalidDataException>(() => DocumentFile.Restore(snapshot with { Id = Guid.Empty }));
    Throws<InvalidDataException>(() => DocumentFile.Restore(snapshot with { Codec = (TextCodec)999 }));
    Throws<InvalidDataException>(() => DocumentFile.Restore(snapshot with { BaselineDigest = "invalid" }));
}
static void HardLinkSafety()
{
    if (!OperatingSystem.IsWindows()) return;
    using var tmp = new TempDirectory();
    var path = tmp.File("target.txt");
    File.WriteAllText(path, "base");
    Check(Native.CreateHardLink(tmp.File("link.txt"), path, IntPtr.Zero), "创建真实硬链接失败：" + Marshal.GetLastWin32Error());
    var file = DocumentFile.Open(path);
    file.Text = "dirty";
    Throws<IOException>(() => file.Save());
    Check(File.ReadAllText(path) == "base" && file.IsDirty, "硬链接原位保存改变了原文件");
    file.Save(tmp.File("safe-copy.txt"));
    Check(File.ReadAllText(tmp.File("safe-copy.txt")) == "dirty", "硬链接应允许另存为");
}
static async Task SessionAndDraft()
{
    using var tmp = new TempDirectory();
    var cleanPath = tmp.File("clean.txt");
    var dirtyPath = tmp.File("dirty.txt");
    File.WriteAllText(cleanPath, "old");
    File.WriteAllText(dirtyPath, "disk");
    var clean = DocumentFile.Open(cleanPath);
    var dirty = DocumentFile.Open(dirtyPath);
    dirty.Text = "draft";
    var store = new WorkspaceStore(tmp.File("workspace"));
    await Saved(store, Session(clean, dirty), clean.CaptureSnapshot(), dirty.CaptureSnapshot());
    Check(!File.ReadAllText(tmp.File("workspace/Session.json")).Contains("\"Text\""), "Session.json 泄漏正文");
    Check(Directory.GetFiles(tmp.File("workspace/Drafts"), "*.json").Length == 1, "只应保存脏草稿");
    File.WriteAllText(cleanPath, "latest");
    var load = await store.LoadAsync();
    Check(load.Documents.Count == 2 && load.Warnings.Count == 0, string.Join("\n", load.Warnings));
    Check(load.Documents.Single(d => d.Entry.Id == clean.Id).File.Text == "latest", "干净文件必须重读磁盘");
    var restored = load.Documents.Single(d => d.Entry.Id == dirty.Id).File;
    Check(restored.Text == "draft" && restored.IsDirty && File.ReadAllText(dirtyPath) == "disk", "恢复草稿不得改原文件");
    Check(load.ActiveId == clean.Id && restored.Id == dirty.Id, "文档 ID 或活动标签未保持");
}
static async Task SnapshotCopy()
{
    using var tmp = new TempDirectory();
    var store = new WorkspaceStore(tmp.Root);
    var first = new DocumentFile("first");
    var second = new DocumentFile("second");
    var session = Session(first);
    var snapshots = new List<DocumentSnapshot> { first.CaptureSnapshot() };
    var task = store.SaveAsync(session, snapshots);
    session.Entries.Clear();
    snapshots.Clear();
    await task;
    var load = await store.LoadAsync();
    Check(load.Documents.Any(d => d.File.Text == "first"), "排队时未复制调用方集合");
    var tasks = Enumerable.Range(0, 8).Select(_ => store.SaveAsync(Session(second), [second.CaptureSnapshot()])).ToArray();
    await Task.WhenAll(tasks);
    load = await store.LoadAsync();
    Check(load.Documents.Count == 1 && load.Documents[0].File.Text == "second", "并发保存损坏会话");
}
static async Task QuarantineDraft()
{
    using var tmp = new TempDirectory();
    var good = new DocumentFile("good");
    var bad = new DocumentFile("bad");
    var store = new WorkspaceStore(tmp.Root);
    await Saved(store, Session(good, bad), good.CaptureSnapshot(), bad.CaptureSnapshot());
    File.WriteAllText(tmp.File($"Drafts/{bad.Id}.json"), "{bad");
    var load = await store.LoadAsync();
    Check(load.Documents.Any(d => d.File.Text == "good") && load.Warnings.Count > 0, "坏草稿阻止其他草稿恢复");
    Check(Directory.GetFiles(tmp.File("Quarantine")).Length == 1, "坏草稿没有隔离");
}
static async Task OrphansAndDuplicates()
{
    using var tmp = new TempDirectory();
    var path = tmp.File("same.txt");
    File.WriteAllText(path, "base");
    var first = DocumentFile.Open(path);
    var second = DocumentFile.Open(path);
    first.Text = "first";
    second.Text = "second";
    var store = new WorkspaceStore(tmp.File("workspace"));
    await Saved(store, Session(first, second), first.CaptureSnapshot(), second.CaptureSnapshot());
    var load = await store.LoadAsync();
    Check(load.Documents.Count == 2 && load.Documents.Count(d => d.File.Path is null) == 1, "重复路径草稿必须恢复成独立副本");
    Check(load.Documents.Select(d => d.File.Text).ToHashSet().SetEquals(["first", "second"]), "重复路径丢失正文");
    File.Delete(tmp.File("workspace/Session.json"));
    load = await store.LoadAsync();
    Check(load.Documents.Count == 2, "会话丢失后孤立草稿应可找回");
}
static async Task InvalidWorkspace()
{
    using var tmp = new TempDirectory();
    var store = new WorkspaceStore(tmp.Root);
    var doc = new DocumentFile("valid");
    await Saved(store, Session(doc), doc.CaptureSnapshot());
    var old = File.ReadAllBytes(tmp.File("Session.json"));
    var invalid = Session(doc);
    invalid.Entries.Add(invalid.Entries[0]);
    Check((await store.SaveAsync(invalid, [doc.CaptureSnapshot()])).Errors.Count > 0, "重复 ID 未拒绝");
    Check((await store.SaveAsync(Session(doc) with { Version = 9 }, [doc.CaptureSnapshot()])).Errors.Count > 0, "版本未校验");
    Check((await store.SaveAsync(Session(doc), [doc.CaptureSnapshot(), doc.CaptureSnapshot()])).Errors.Count > 0, "重复草稿 ID 未拒绝");
    EqualBytes(tmp.File("Session.json"), old);
    File.WriteAllText(tmp.File("Session.json"), JsonSerializer.Serialize(Session(doc) with { Version = 9 }));
    var load = await store.LoadAsync();
    Check(load.Warnings.Count > 0 && load.Documents.Any(d => d.File.Text == "valid"), "坏版本会话应警告且找回草稿");
}
static async Task Limits()
{
    using var tmp = new TempDirectory();
    var store = new WorkspaceStore(tmp.Root);
    var docs = Enumerable.Range(0, 201).Select(i => new DocumentFile(i.ToString())).ToArray();
    Check((await store.SaveAsync(Session(docs), docs.Select(d => d.CaptureSnapshot()).ToArray())).Errors.Count > 0, "201文档未拒绝");
    var text = new string('界', 14 * 1024 * 1024);
    var big = Enumerable.Range(0, 4).Select(_ => new DocumentFile(text)).ToArray();
    Check((await store.SaveAsync(Session(big), big.Select(d => d.CaptureSnapshot()).ToArray())).Errors.Count > 0, "150MiB草稿总量未拒绝");
    Check(!File.Exists(tmp.File("Session.json")), "超限不应修改会话");
}
static async Task SaveFailureOrder()
{
    using var tmp = new TempDirectory();
    var store = new WorkspaceStore(tmp.Root);
    var first = new DocumentFile("old valid");
    await Saved(store, Session(first), first.CaptureSnapshot());
    var oldDraft = File.ReadAllBytes(tmp.File($"Drafts/{first.Id}.json"));
    var oldSession = File.ReadAllBytes(tmp.File("Session.json"));
    first.Text = "new text";
    using (var handle = new FileStream(tmp.File("Session.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var result = await store.SaveAsync(Session(first), [first.CaptureSnapshot()]);
        Check(result.Errors.Count > 0, "占用会话时写入应失败");
    }
    EqualBytes(tmp.File("Session.json"), oldSession);
    EqualBytes(tmp.File($"Drafts/{first.Id}.json"), oldDraft);
    var next = new DocumentFile("next");
    using (var handle = new FileStream(tmp.File("Session.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        Check((await store.SaveAsync(Session(next), [next.CaptureSnapshot()])).Errors.Count > 0, "会话失败不得清理旧草稿");
    EqualBytes(tmp.File($"Drafts/{first.Id}.json"), oldDraft);
    await Saved(store, Session(next), next.CaptureSnapshot());
    Check(!File.Exists(tmp.File($"Drafts/{first.Id}.json")), "会话成功后应清理旧草稿");
}
static async Task ClearQueue()
{
    using var tmp = new TempDirectory();
    var store = new WorkspaceStore(tmp.Root);
    var doc = new DocumentFile("pending");
    var saves = Enumerable.Range(0, 12).Select(_ => store.SaveAsync(Session(doc), [doc.CaptureSnapshot()])).ToArray();
    var clear = store.ClearDraftsAsync();
    await Task.WhenAll(saves);
    await clear;
    var load = await store.LoadAsync();
    Check(load.Documents.Count == 0, "关闭清除后排队草稿重新生成");
    Check(!Directory.Exists(tmp.File("Drafts")) || Directory.GetFiles(tmp.File("Drafts"), "*.json").Length == 0, "草稿未清空");
}
static async Task CleanEncodingSession()
{
    using var tmp = new TempDirectory();
    var path = tmp.File("no-bom.txt");
    var bytes = new UnicodeEncoding(true, false, true).GetBytes("正文\r\ne\u0301");
    File.WriteAllBytes(path, bytes);
    var doc = DocumentFile.Open(path, TextCodec.Utf16BE);
    var store = new WorkspaceStore(tmp.File("workspace"));
    await Saved(store, Session(doc), doc.CaptureSnapshot());
    var load = await store.LoadAsync();
    Check(load.Documents.Count == 1 && load.Documents[0].File.Codec == TextCodec.Utf16BE, "无 BOM 编码提示丢失");
    load.Documents[0].File.Save();
    EqualBytes(path, bytes);
}
static async Task CleanMixedSession()
{
    using var tmp = new TempDirectory();
    var path = tmp.File("mixed.txt");
    var bytes = Encoding.UTF8.GetBytes("a\r\nb\rc\n");
    File.WriteAllBytes(path, bytes);
    var doc = DocumentFile.Open(path);
    var store = new WorkspaceStore(tmp.File("workspace"));
    await Saved(store, Session(doc), doc.CaptureSnapshot());
    var load = await store.LoadAsync();
    Check(load.Documents.Count == 1 && load.Documents[0].File.Id == doc.Id, "干净混合换行会话未恢复");
    load.Documents[0].File.Save();
    EqualBytes(path, bytes);
}
static async Task DraftPathMismatch()
{
    using var tmp = new TempDirectory();
    var firstPath = tmp.File("first.txt");
    var secondPath = tmp.File("second.txt");
    File.WriteAllText(firstPath, "first");
    File.WriteAllText(secondPath, "second");
    var first = DocumentFile.Open(firstPath);
    first.Text = "draft";
    var store = new WorkspaceStore(tmp.File("workspace"));
    await Saved(store, Session(first), first.CaptureSnapshot());
    var forged = first.CaptureSnapshot() with { Path = secondPath };
    File.WriteAllText(tmp.File($"workspace/Drafts/{first.Id}.json"), JsonSerializer.Serialize(forged));
    var load = await store.LoadAsync();
    Check(load.Warnings.Count > 0 && load.Documents.All(d => d.File.Path != secondPath), "错配草稿获得了其他文件路径权限");
    Check(File.ReadAllText(firstPath) == "first" && File.ReadAllText(secondPath) == "second", "恢复过程改写原文件");
}
static void ReparseSafety()
{
    if (!OperatingSystem.IsWindows()) return;
    using var tmp = new TempDirectory();
    var target = tmp.File("target");
    var junction = tmp.File("junction");
    Directory.CreateDirectory(target);
    var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add("/d");
    info.ArgumentList.Add("/c");
    info.ArgumentList.Add("mklink");
    info.ArgumentList.Add("/J");
    info.ArgumentList.Add(junction);
    info.ArgumentList.Add(target);
    using (var process = Process.Start(info)!)
    {
        process.WaitForExit();
        Check(process.ExitCode == 0, "无法建立测试目录联接：" + process.StandardError.ReadToEnd());
    }
    var path = System.IO.Path.Combine(target, "text.txt");
    File.WriteAllText(path, "base");
    var doc = DocumentFile.Open(System.IO.Path.Combine(junction, "text.txt"));
    doc.Text = "dirty";
    Throws<IOException>(() => doc.Save());
    Throws<IOException>(() => new DocumentFile("new").Save(System.IO.Path.Combine(junction, "new.txt")));
    Check(File.ReadAllText(path) == "base" && doc.IsDirty, "目录联接保护未生效");
    doc.Save(tmp.File("safe-copy.txt"));
    // Remove the junction before recursive cleanup removes its target directory.
    Directory.Delete(junction, false);
}
static void AclPreservation()
{
    if (!OperatingSystem.IsWindows()) return;
    using var tmp = new TempDirectory();
    var path = tmp.File("acl.txt");
    File.WriteAllText(path, "before");
    var security = new FileSecurity();
    security.SetAccessRuleProtection(true, false);
    security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
    new FileInfo(path).SetAccessControl(security);
    var before = new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    var doc = DocumentFile.Open(path);
    doc.Text = "after";
    doc.Save();
    var after = new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    Check(before == after && File.ReadAllText(path) == "after", "替换未保留受保护的目标 ACL");
}
static void InvalidSurrogates()
{
    using var tmp = new TempDirectory();
    var doc = new DocumentFile("\ud800");
    var before = doc.CaptureSnapshot();
    Throws<InvalidDataException>(() => doc.Save(tmp.File("invalid.txt")));
    Check(doc.CaptureSnapshot() == before && !File.Exists(tmp.File("invalid.txt")), "有损编码失败改变文档");
}

sealed class TempDirectory : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Qingyue.FileTests-" + Guid.NewGuid());
    public TempDirectory() => Directory.CreateDirectory(Root);
    public string File(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, true);
    }
}
static class Native
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateHardLink(string fileName, string existing, IntPtr security);
}
