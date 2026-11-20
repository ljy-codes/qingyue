import Foundation

@main
struct WorkspaceParityTests {
    private static var checks = 0
    static func main() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-workspace-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root.appendingPathComponent("src"), withIntermediateDirectories: true)
        try Data("needle one\n".utf8).write(to: root.appendingPathComponent("src/a.md"))
        try Data("other needle\n".utf8).write(to: root.appendingPathComponent("src/b.txt"))
        try Data("ignored needle\n".utf8).write(to: root.appendingPathComponent("src/c.swift"))
        let files = try WorkspaceScanner.scan(root: root)
        expect(files.count == 2, "工作区扫描默认只收录文本扩展名")
        let matches = try GlobalSearchEngine.search(files: files, query: "needle")
        expect(matches.count == 2 && matches.allSatisfy { $0.line == 1 && $0.column > 0 }, "全局搜索返回文件、行列和预览")
        let filtered = try GlobalSearchEngine.search(files: files, query: "needle", options: WorkspaceSearchOptions(fileNameContains: "a.md"))
        expect(filtered.count == 1 && filtered[0].file.relativePath.hasSuffix("a.md"), "全局搜索支持文件名过滤")
        let store = ReadingListStore(root: root.appendingPathComponent("Support"))
        let item = try store.upsert(path: root.appendingPathComponent("src/a.md").path, pinned: true, progress: 0.42, sourceAnchor: 7)
        try store.save()
        let reloaded = ReadingListStore(root: root.appendingPathComponent("Support")); _ = reloaded.load()
        expect(reloaded.items.first?.id == item.id && reloaded.items.first?.pinned == true && reloaded.items.first?.progress == 0.42, "阅读清单重启后保留固定和进度")
        _ = reloaded.remove(path: item.path); expect(reloaded.items.isEmpty, "阅读清单可以移除路径")
        try extendedChecks(root: root)
        try? FileManager.default.removeItem(at: root)
        print("WorkspaceParityTests: \(checks) checks passed")
    }
    private static func extendedChecks(root: URL) throws {
        let fixtures = root.appendingPathComponent("fixtures")
        try FileManager.default.createDirectory(at: fixtures.appendingPathComponent(".git"), withIntermediateDirectories: true)
        try Data("needle ignored".utf8).write(to: fixtures.appendingPathComponent(".git/secret.md"))
        let unicode = "首行\r\n😀 needle\r\n末行"
        try TextCodec.utf16LE.encode(unicode).write(to: fixtures.appendingPathComponent("utf16.md"))
        try TextCodec.gb18030.encode("中文 needle\n").write(to: fixtures.appendingPathComponent("gb.txt"))
        try Data([0, 0, 1, 2]).write(to: fixtures.appendingPathComponent("binary.txt"))
        try FileManager.default.createSymbolicLink(at: fixtures.appendingPathComponent("linked.md"), withDestinationURL: fixtures.appendingPathComponent("utf16.md"))
        try FileManager.default.createSymbolicLink(at: fixtures.appendingPathComponent("linked-folder"), withDestinationURL: root.appendingPathComponent("src"))
        let files = try WorkspaceScanner.scan(root: fixtures)
        expect(files.count == 3 && !files.contains { $0.relativePath.contains("linked") || $0.relativePath.contains(".git") }, "扫描排除链接与版本控制目录")
        let results = try GlobalSearchEngine.search(files: files, query: "needle")
        expect(results.count == 2, "搜索支持 UTF-16/BOM 与 GB18030，跳过二进制")
        let utf16 = results.first { $0.file.relativePath == "utf16.md" }!
        expect(utf16.line == 2 && utf16.column == 4 && utf16.range.location == 6, "UTF-16 位置与编辑器规范化 LF/emoji 坐标一致")
        var context = WorkspaceSearchOptions(); context.contextLines = 1
        let around = try GlobalSearchEngine.search(files: files, query: "needle", options: context)
        expect(around.first { $0.file.relativePath == "utf16.md" }!.preview.contains("首行\n😀 needle\n末行"), "搜索上下文包含相邻行")
        var bytes = WorkspaceSearchOptions(); bytes.maximumFileBytes = 1
        try expect(try GlobalSearchEngine.search(files: files, query: "needle", options: bytes).isEmpty, "读取前执行单文件字节预算")
        do { var regex = WorkspaceSearchOptions(); regex.search.regularExpression = true; _ = try GlobalSearchEngine.search(files: [], query: "[", options: regex); fatalError("应拒绝无效正则") }
        catch { checks += 1 }
        let many = fixtures.appendingPathComponent("many.txt")
        try Data(String(repeating: "needle\n", count: 5_000).utf8).write(to: many)
        let manyFiles = try WorkspaceScanner.scan(root: fixtures)
        var bounded = WorkspaceSearchOptions(); bounded.fileNameContains = "many"; bounded.maximumResults = 100
        let boundedMatches = try GlobalSearchEngine.search(files: manyFiles, query: "needle", options: bounded)
        expect(boundedMatches.count == 100 && boundedMatches.last?.line == 100, "大量匹配使用线性索引并受结果预算限制")
        let store = ReadingListStore(root: root.appendingPathComponent("FreshSupport"))
        let path = root.appendingPathComponent("src/a.md").path
        _ = try store.upsert(path: path, progress: 0.5)
        try store.save()
        _ = try store.upsert(path: path, completed: true, progress: 1)
        try store.save()
        let reload = ReadingListStore(root: root.appendingPathComponent("FreshSupport")); _ = reload.load()
        expect(reload.item(for: path)?.completed == true && reload.item(for: path)?.progress == 1, "首次和覆盖保存均可重新读取")
        do { _ = try reload.upsert(path: ""); fatalError("空路径应被拒绝") } catch { checks += 1 }
        do { _ = try reload.upsert(path: "relative.md"); fatalError("相对路径应被拒绝") } catch { checks += 1 }
        let previous = reload.items
        do { _ = try reload.upsert(path: path, progress: .nan); fatalError("NaN 进度应被拒绝") } catch { checks += 1 }
        expect(reload.items == previous, "无效更新不会部分修改清单")
        let replaceURL = fixtures.appendingPathComponent("replace.txt")
        try TextCodec.utf16LE.encode("needle\r\nkeep\r\n").write(to: replaceURL)
        let replacementFiles = try WorkspaceScanner.scan(root: fixtures)
        var replaceOptions = WorkspaceSearchOptions(); replaceOptions.fileNameContains = "replace.txt"
        let plan = try GlobalSearchEngine.previewReplacements(files: replacementFiles, query: "needle", replacement: "changed", options: replaceOptions)
        expect(plan.count == 1 && plan[0].matchCount == 1, "替换预览生成不可变文件基线")
        let beforePreview = try TextFile.open(replaceURL)
        expect(beforePreview.text.hasPrefix("needle"), "替换预览不改磁盘文件")
        try GlobalSearchEngine.apply(plan[0])
        let afterReplacement = try TextFile.open(replaceURL)
        expect(afterReplacement.text == "changed\nkeep\n" && afterReplacement.codec == .utf16LE && afterReplacement.lineEnding == .crlf, "应用替换保留 UTF-16 编码 BOM 和 CRLF")
        do { try GlobalSearchEngine.apply(plan[0]); fatalError("同一预览不能重复应用") } catch { checks += 1 }
        let conflictPlan = try GlobalSearchEngine.previewReplacements(files: replacementFiles, query: "changed", replacement: "replaced", options: replaceOptions)
        try TextCodec.utf16LE.encode("external\r\n").write(to: replaceURL)
        do { try GlobalSearchEngine.apply(conflictPlan[0]); fatalError("应拒绝外部变更") } catch { checks += 1 }
        let untouched = try TextFile.open(replaceURL)
        expect(untouched.text == "external\n", "磁盘冲突后保留外部文本")
        let target = root.appendingPathComponent("outside.json")
        try Data("keep".utf8).write(to: target)
        let unsafeRoot = root.appendingPathComponent("UnsafeSupport")
        try FileManager.default.createDirectory(at: unsafeRoot, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: unsafeRoot.appendingPathComponent("ReadingList.json"), withDestinationURL: target)
        let unsafe = ReadingListStore(root: unsafeRoot)
        try expect(!unsafe.load().isEmpty && (try Data(contentsOf: target)) == Data("keep".utf8), "清单链接隔离不读取或改写链接目标")
    }
    private static func expect(_ value: @autoclosure () throws -> Bool, _ message: String) rethrows { guard try value() else { fatalError(message) }; checks += 1 }
}
