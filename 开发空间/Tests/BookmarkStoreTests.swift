import Foundation

@main
struct BookmarkStoreTests {
    private static var checks = 0
    static func main() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-bookmarks-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); defer { try? FileManager.default.removeItem(at: root) }
        let documentID = UUID(); let path = root.appendingPathComponent("a.md").path; let text = "第一行\n正文段落\n"
        let store = BookmarkStore(root: root.appendingPathComponent("Support")); let item = try store.add(documentID: documentID, path: path, sourceAnchor: 999, text: text)
        expect(item.sourceAnchor == text.utf16.count && item.label == "正文段落", "书签限制位置并自动生成段落标签")
        try store.save(); let reloaded = BookmarkStore(root: root.appendingPathComponent("Support")); _ = reloaded.load()
        expect(reloaded.forDocument(documentID: documentID, path: path).count == 1, "书签重启后可按文档路径查询")
        let oldPath = path; let newPath = root.appendingPathComponent("renamed.md").path
        expect(reloaded.migratePath(documentID: documentID, oldPath: oldPath, newPath: newPath) == 1, "重命名后迁移书签路径")
        expect(reloaded.repair(documentID: documentID, path: newPath, oldText: text, newText: "新增\n第一行\n正文段落\n") == 1, "外部更新后修复书签锚点")
        expect(reloaded.remove(id: item.id), "书签可以删除")
        try reloaded.save(); expect(reloaded.items.isEmpty, "删除后保存为空")
        try reopenedIdentityChecks(root: root)
        print("BookmarkStoreTests: \(checks) checks passed")
    }
    private static func reopenedIdentityChecks(root: URL) throws {
        let support = root.appendingPathComponent("ReopenedSupport")
        let store = BookmarkStore(root: support)
        let firstID = UUID(), reopenedID = UUID(), path = root.appendingPathComponent("persistent.md").path
        let bookmark = try store.add(documentID: firstID, path: path, sourceAnchor: 3, label: String(repeating: "😀", count: 100), text: "abc\n目标")
        expect(bookmark.label.utf16.count <= 160, "emoji 标签按 UTF-16 预算截断")
        try store.save()
        let reload = BookmarkStore(root: support); _ = reload.load()
        expect(reload.forDocument(documentID: reopenedID, path: path).map(\.id) == [bookmark.id], "同一路径重新打开 UUID 改变仍能查询书签")
        expect(reload.forDocument(documentID: reopenedID, path: path + "-other").isEmpty, "书签不会跟随错误路径")
        expect(reload.repair(documentID: reopenedID, path: path, oldText: "abc\n目标", newText: "新增\nabc\n目标") == 1, "跨重新打开仍按路径修复源锚点")
        expect(reload.migratePath(documentID: reopenedID, oldPath: path, newPath: path + "-renamed") == 1, "重新打开后重命名同步书签")
        try reload.save()
        expect(reload.items[0].documentID == reopenedID, "迁移更新持久化 UUID")
        do { _ = try store.add(documentID: firstID, path: "", sourceAnchor: 0); fatalError("空路径应拒绝") } catch { checks += 1 }
        let target = root.appendingPathComponent("bookmark-target.json"); try Data("keep".utf8).write(to: target)
        let unsafeRoot = root.appendingPathComponent("UnsafeBookmark")
        try FileManager.default.createDirectory(at: unsafeRoot, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: unsafeRoot.appendingPathComponent("Bookmarks.json"), withDestinationURL: target)
        let unsafe = BookmarkStore(root: unsafeRoot)
        expect(!unsafe.load().isEmpty, "书签链接不会当作本机 sidecar 读取")
        let data = try Data(contentsOf: target)
        expect(data == Data("keep".utf8), "损坏隔离不改写链接目标")
    }
    private static func expect(_ value: @autoclosure () -> Bool, _ message: String) { guard value() else { fatalError(message) }; checks += 1 }
}
