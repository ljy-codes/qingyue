import AppKit

@main
struct WorkspaceWindowTests {
    private static var checks = 0
    static func main() throws {
        let app = NSApplication.shared; app.setActivationPolicy(.prohibited)
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-workspace-window-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root.appendingPathComponent("nested"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try Data("needle first\n".utf8).write(to: root.appendingPathComponent("nested/a.md"))
        try Data("needle second\n".utf8).write(to: root.appendingPathComponent("b.txt"))
        let browser = WorkspaceBrowserWindow(rootURL: root)
        browser.showWindow(nil); defer { browser.close() }
        let content = browser.window!.contentView!
        let outline = descendants(content).compactMap { $0 as? NSOutlineView }.first!
        expect(outline.accessibilityLabel() == "工作区文件树", "工作区提供真正的原生文件树")
        try wait { outline.numberOfRows == 2 }
        let nameFilter = descendants(content).compactMap { $0 as? NSSearchField }.first { $0.accessibilityLabel() == "工作区文件名过滤" }!
        let extensionFilter = descendants(content).compactMap { $0 as? NSSearchField }.first { $0.accessibilityLabel() == "工作区类型过滤" }!
        nameFilter.stringValue = "a.md"; browser.controlTextDidChange(Notification(name: NSControl.textDidChangeNotification, object: nameFilter))
        try wait { outline.numberOfRows == 2 && descendants(content).compactMap { $0 as? NSTextField }.contains { $0.stringValue == "显示 1 / 2 个文件" } }
        expect(outline.isExpandable(outline.item(atRow: 0)!), "文件树保留目录层次并可展开")
        nameFilter.stringValue = ""; extensionFilter.stringValue = "txt"; browser.controlTextDidChange(Notification(name: NSControl.textDidChangeNotification, object: extensionFilter))
        try wait { outline.numberOfRows == 1 }
        expect(!outline.isExpandable(outline.item(atRow: 0)!), "类型过滤显示匹配文本文件")
        expect(descendants(content).compactMap { $0 as? NSPopUpButton }.contains { $0.accessibilityLabel() == "收藏目录" }, "收藏目录具有原生可见入口")

        let search = GlobalSearchWindow(rootURL: root)
        search.showWindow(nil); defer { search.close() }
        let searchContent = search.window!.contentView!
        let query = descendants(searchContent).compactMap { $0 as? NSSearchField }.first!
        let resultTable = descendants(searchContent).compactMap { $0 as? NSTableView }.first!
        let searchButton = descendants(searchContent).compactMap { $0 as? NSButton }.first { $0.title == "搜索" }!
        let previewButton = descendants(searchContent).compactMap { $0 as? NSButton }.first { $0.title == "预览替换" }!
        let apply = descendants(searchContent).compactMap { $0 as? NSButton }.first { $0.title == "应用预览替换…" }!
        let replacement = descendants(searchContent).compactMap { $0 as? NSTextField }.first { $0.accessibilityLabel() == "工作区替换内容" }!
        expect(descendants(searchContent).compactMap { $0 as? NSButton }.filter { ["正则", "全词", "区分大小写"].contains($0.title) }.count == 3, "全局搜索支持正则全词和大小写开关")
        query.stringValue = "needle"; searchButton.performClick(nil)
        try wait { resultTable.numberOfRows == 2 }
        expect(resultTable.doubleAction != nil && !apply.isEnabled, "搜索结果可双击定位且不会直接改写文件")
        replacement.stringValue = "changed"; previewButton.performClick(nil)
        try wait { resultTable.numberOfRows == 2 && apply.isEnabled }
        try expect(try String(contentsOf: root.appendingPathComponent("b.txt"), encoding: .utf8) == "needle second\n", "原生替换预览保持磁盘内容")
        query.stringValue = "other"; search.controlTextDidChange(Notification(name: NSControl.textDidChangeNotification, object: query))
        expect(!apply.isEnabled && resultTable.numberOfRows == 0, "修改搜索条件淘汰旧替换预览")
        print("WorkspaceWindowTests: \(checks) checks passed")
    }
    private static func descendants(_ root: NSView) -> [NSView] { [root] + root.subviews.flatMap(descendants) }
    private static func expect(_ condition: @autoclosure () throws -> Bool, _ message: String) rethrows { guard try condition() else { fatalError(message) }; checks += 1 }
    private static func wait(_ predicate: () -> Bool) throws {
        let deadline = Date(timeIntervalSinceNow: 10)
        while !predicate() {
            if Date() > deadline { throw NSError(domain: "WorkspaceWindowTests", code: 1, userInfo: [NSLocalizedDescriptionKey: "异步窗口更新超时"]) }
            _ = RunLoop.current.run(mode: .default, before: Date(timeIntervalSinceNow: 0.02))
        }
    }
}
