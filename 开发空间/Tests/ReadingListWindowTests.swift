import AppKit

@main
struct ReadingListWindowTests {
    private static var checks = 0
    static func main() throws {
        let app = NSApplication.shared; app.setActivationPolicy(.prohibited)
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-reading-list-window-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let alpha = root.appendingPathComponent("甲.txt"), beta = root.appendingPathComponent("乙.md")
        try Data("alpha source\n".utf8).write(to: alpha); try Data("beta source\n".utf8).write(to: beta)
        let store = ReadingListStore(root: root.appendingPathComponent("Support"))
        let original = try store.upsert(path: alpha.path, pinned: true, progress: 0.42, sourceAnchor: 7, now: Date(timeIntervalSince1970: 1))
        _ = try store.upsert(path: beta.path, completed: true, progress: 0.9, sourceAnchor: 5, now: Date(timeIntervalSince1970: 2)); try store.save()
        var opened: [ReadingListItem] = []
        let controller = ReadingListWindow(store: store) { opened.append($0) }
        controller.showWindow(nil); defer { controller.window?.orderOut(nil) }
        let content = controller.window!.contentView!; content.layoutSubtreeIfNeeded()
        let table = descendants(content).compactMap { $0 as? NSTableView }.first!
        expect(table.accessibilityLabel() == "阅读清单文件列表" && table.numberOfRows == 2, "native table lists reading records")
        expect(table.tableColumns.map { $0.title } == ["文件", "文件夹", "固定", "状态", "阅读进度"], "table exposes pinned, completion and progress metadata")
        expect(table.doubleAction != nil, "row double click opens the source")
        let open = button("打开", content), unpin = button("取消固定", content)
        let read = button("标为已读", content), remove = button("移除记录", content)
        expect(unpin.isEnabled && read.isEnabled && remove.isEnabled, "record actions are available on selection")
        expect(cell(controller, table, column: 0, row: 0) == "甲.txt", "pinned file precedes a newer unpinned file")
        expect(cell(controller, table, column: 4, row: 0) == "42%", "actual reading progress is displayed")
        open.performClick(nil)
        expect(opened.count == 1 && opened[0].id == original.id && opened[0].sourceAnchor == 7, "open callback retains the file identity and source anchor")
        unpin.performClick(nil)
        expect(store.item(for: alpha.path)?.pinned == false && button("固定到顶部", content).isEnabled, "pin action toggles shared list state and label")
        let reloaded = ReadingListStore(root: root.appendingPathComponent("Support")); _ = reloaded.load()
        expect(reloaded.item(for: alpha.path)?.pinned == false, "unpin persists to the local reading list")
        read.performClick(nil)
        expect(store.item(for: alpha.path)?.completed == true && button("标为未读", content).isEnabled, "completion can be changed independently of progress")
        expect(store.item(for: alpha.path)?.progress == 0.42, "marking read does not invent a new reading position")
        button("标为未读", content).performClick(nil)
        expect(store.item(for: alpha.path)?.completed == false, "completed record can return to unread")
        remove.performClick(nil)
        expect(table.numberOfRows == 1 && store.item(for: alpha.path) == nil, "remove updates the actual table and store")
        try expect(try Data(contentsOf: alpha) == Data("alpha source\n".utf8), "removing a record never deletes or rewrites source")
        remove.performClick(nil)
        expect(table.numberOfRows == 0 && !open.isEnabled && !remove.isEnabled, "empty list disables unavailable record actions")
        let empty = ReadingListStore(root: root.appendingPathComponent("Support")); _ = empty.load()
        expect(empty.items.isEmpty, "record removals survive reload")
        expect(status(content).stringValue.contains("清单为空"), "empty state explains how to add a record")

        // A failing disk write keeps the edited state visible, with an explicit
        // retry action and a useful error instead of silently claiming success.
        let blocker = root.appendingPathComponent("not-a-directory"); try Data("block".utf8).write(to: blocker)
        let failingStore = ReadingListStore(root: blocker)
        _ = try failingStore.upsert(path: beta.path)
        let failing = ReadingListWindow(store: failingStore)
        defer { failing.window?.orderOut(nil) }
        let failingContent = failing.window!.contentView!
        button("固定到顶部", failingContent).performClick(nil)
        expect(status(failingContent).stringValue.contains("保存失败") && status(failingContent).stringValue.contains("尚未写入磁盘"), "persistence errors remain visible")
        expect(button("重试保存", failingContent).isEnabled, "failed saves expose an enabled retry")
        expect(failingStore.item(for: beta.path)?.pinned == true, "failed save retains pending list state for retry")
        try FileManager.default.removeItem(at: blocker)
        button("重试保存", failingContent).performClick(nil)
        expect(!button("重试保存", failingContent).isEnabled && !status(failingContent).stringValue.contains("保存失败"), "retry saves after storage becomes available")
        let repaired = ReadingListStore(root: blocker); _ = repaired.load()
        expect(repaired.item(for: beta.path)?.pinned == true, "retried metadata is actually persisted")

        let missingStore = ReadingListStore(root: root.appendingPathComponent("Missing"))
        _ = try missingStore.upsert(path: root.appendingPathComponent("missing.txt").path)
        let missing = ReadingListWindow(store: missingStore) { opened.append($0) }
        defer { missing.window?.orderOut(nil) }
        let missingContent = missing.window!.contentView!
        button("打开", missingContent).performClick(nil)
        expect(opened.count == 1 && status(missingContent).stringValue.contains("文件不存在"), "missing file is reported without calling open")
        print("ReadingListWindowTests: \(checks) checks passed")
    }
    private static func descendants(_ root: NSView) -> [NSView] { [root] + root.subviews.flatMap(descendants) }
    private static func button(_ title: String, _ root: NSView) -> NSButton { descendants(root).compactMap { $0 as? NSButton }.first { $0.title == title }! }
    private static func status(_ root: NSView) -> NSTextField { descendants(root).compactMap { $0 as? NSTextField }.first { $0.accessibilityLabel() == "阅读清单状态" }! }
    private static func cell(_ owner: ReadingListWindow, _ table: NSTableView, column: Int, row: Int) -> String { (owner.tableView(table, viewFor: table.tableColumns[column], row: row) as? NSTextField)?.stringValue ?? "" }
    private static func expect(_ condition: @autoclosure () throws -> Bool, _ message: String) rethrows { guard try condition() else { fatalError(message) }; checks += 1 }
}
