import AppKit
import Foundation

@main
struct AnnotationPanelWindowTests {
    private static var checks = 0
    static func main() throws {
        let application = NSApplication.shared; application.setActivationPolicy(.prohibited)
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-annotation-panel-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let documentID = UUID(), path = root.appendingPathComponent("正文.md").path
        let store = AnnotationStore(root: root)
        let entry = try store.add(AnnotationDraft(documentID: documentID, path: path, sourceStart: 2, sourceEnd: 7, quote: "原文摘录😀", note: "原始备注", tags: ["待读"], color: .amber))
        let panel = AnnotationPanelWindow(store: store, documentID: documentID, path: path)
        defer { panel.close() }
        guard let content = panel.window?.contentView else { fatalError("批注窗口必须存在") }
        content.layoutSubtreeIfNeeded()
        let views = descendants(content)
        let table = views.compactMap { $0 as? NSTableView }.first!
        expect(table.numberOfRows == 1 && table.selectedRow == 0, "原生列表加载批注并选中第一条")
        expect(table.tableColumns.map(\.title) == ["摘录", "位置状态"], "批注列表同时呈现摘录和锚点状态")
        let quote = views.compactMap { $0 as? NSTextView }.first { $0.accessibilityLabel() == "批注摘录原文" }!
        let note = views.compactMap { $0 as? NSTextView }.first { $0.accessibilityLabel() == "批注备注" }!
        let tags = views.compactMap { $0 as? NSTextField }.first { $0.accessibilityLabel() == "批注标签" }!
        let color = views.compactMap { $0 as? NSPopUpButton }.first { $0.accessibilityLabel() == "批注颜色" }!
        let save = views.compactMap { $0 as? NSButton }.first { $0.title == "保存批注" }!
        let delete = views.compactMap { $0 as? NSButton }.first { $0.title == "删除批注" }!
        let jump = views.compactMap { $0 as? NSButton }.first { $0.title == "回到原文" }!
        let status = views.compactMap { $0 as? NSTextField }.first { $0.accessibilityLabel() == "批注操作状态" }!
        expect(!quote.isEditable && quote.string == "原文摘录😀", "摘录保持只读")
        expect(note.isEditable && note.string == "原始备注" && tags.stringValue == "待读", "备注与标签独立编辑")
        expect(color.numberOfItems == 5 && save.isEnabled && delete.isEnabled, "颜色选择及明确保存删除入口可用")
        var changeCount = 0; panel.onChange = { changeCount += 1 }
        note.string = "更新后的备注"; tags.stringValue = "重点, 复查"; color.selectItem(at: 2)
        expect(store.items[0].note == "原始备注", "输入过程不提前保存批注")
        save.performClick(nil)
        expect(store.items[0].note == "更新后的备注" && store.items[0].tags == ["重点", "复查"] && store.items[0].color == .green, "保存应用备注标签和颜色")
        expect(changeCount == 1 && status.stringValue.contains("已保存"), "保存后通知宿主更新高亮")
        let loaded = AnnotationStore(root: root); _ = loaded.load()
        expect(loaded.items.first?.id == entry.id && loaded.items.first?.note == "更新后的备注", "批注实际写入本机存储")
        var jumped: UUID?; panel.jump = { jumped = $0.id }; jump.performClick(nil)
        expect(jumped == entry.id && table.doubleAction != nil, "回源入口和列表双击均已绑定")
        note.string = String(repeating: "字", count: 4_001); save.performClick(nil)
        expect(status.stringValue.contains("过长") && changeCount == 1, "无效备注显示错误且不宣称保存成功")
        expect(store.items[0].note == "更新后的备注", "验证失败不改动已保存批注")
        let empty = AnnotationPanelWindow(store: store, documentID: UUID(), path: nil)
        defer { empty.close() }
        let emptyViews = descendants(empty.window!.contentView!)
        let emptyTable = emptyViews.compactMap { $0 as? NSTableView }.first!
        let emptySave = emptyViews.compactMap { $0 as? NSButton }.first { $0.title == "保存批注" }!
        let emptyStatus = emptyViews.compactMap { $0 as? NSTextField }.first { $0.accessibilityLabel() == "批注操作状态" }!
        expect(emptyTable.numberOfRows == 0 && !emptySave.isEnabled && emptyStatus.stringValue.contains("没有批注"), "空态有说明且禁用无目标的操作")
        print("AnnotationPanelWindowTests: \(checks) checks passed")
    }
    private static func descendants(_ root: NSView) -> [NSView] { [root] + root.subviews.flatMap(descendants) }
    private static func expect(_ value: @autoclosure () -> Bool, _ message: String) { guard value() else { fatalError(message) }; checks += 1 }
}
