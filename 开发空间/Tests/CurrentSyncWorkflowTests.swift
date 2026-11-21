import AppKit

@main
struct CurrentSyncWorkflowTests {
    static var checks = 0
    static func main() throws {
        _ = NSApplication.shared; NSApp.setActivationPolicy(.prohibited)
        UserDefaults.standard.setVolatileDomain(["restoreSession": false, "recent": [String](), "positions": [String: [Double]](), "followSystemAppearance": false], forName: UserDefaults.argumentDomain)
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-current-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root); UserDefaults.standard.removeVolatileDomain(forName: UserDefaults.argumentDomain) }
        let reader = ReaderWindow(supportDirectory: root.appendingPathComponent("Support"))
        let window = reader.window!; reader.showWindow(nil); defer { window.orderOut(nil) }
        let editor = descendants(window.contentView!).compactMap { $0 as? EditorTextView }.first!
        let preview = descendants(window.contentView!).compactMap { $0 as? ReadingTextView }.first!
        try wait { editor.isEditable }
        for index in 0..<4 {
            let url = root.appendingPathComponent("\(index).md")
            try Data(("# \(index)\n\n" + String(repeating: "正文 😀\n\n", count: 30)).utf8).write(to: url)
            reader.openURLs([url]); try wait { window.representedURL == url }
            expect(reader.retainedPresentationCountForTests == 1, "只有可见标签保留阅读树")
        }
        reader.previousTab(nil)
        expect(preview.string.contains("正文 😀") && reader.retainedPresentationCountForTests == 1, "返回标签按需重建预览")
        if let view = window.contentView, let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) {
            view.cacheDisplay(in: view.bounds, to: bitmap)
            try bitmap.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: "/private/tmp/qingyue-build8-layout.png"))
        }
        reader.toggleReading(nil)
        expect(reader.retainedPresentationCountForTests == 0 && preview.string.isEmpty, "离开预览释放模型与文本存储")
        editor.setSelectedRange(NSRange(location: 0, length: editor.string.utf16.count))
        editor.insertText("| A | B |\n| --- | --- |\n| 甲 | 😀 |", replacementRange: editor.selectedRange())
        RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.05))
        editor.setSelectedRange((editor.string as NSString).range(of: "甲"))
        let text = editor.string
        editor.insertTab(nil)
        expect(editor.string == text, "表格 Tab 只导航不改正文")
        expect((editor.string as NSString).substring(with: editor.selectedRange()) == "😀", "Tab 选择 emoji 单元格内容")
        editor.insertNewline(nil)
        RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.05))
        expect(editor.string.components(separatedBy: "\n").count == 4, "表格 Enter 插入一行")
        reader.undoEdit(nil)
        expect(editor.string == text, "表格插行单次撤销恢复精确源码")
        reader.addCurrentToReadingList(nil)
        let list = ReadingListStore(root: root.appendingPathComponent("Support")); _ = list.load()
        expect(list.items.count == 1 && list.items[0].pinned, "真实菜单动作把当前文件加入持久阅读清单")
        reader.toggleBookmark(nil)
        let bookmarks = BookmarkStore(root: root.appendingPathComponent("Support")); _ = bookmarks.load()
        expect(bookmarks.items.count == 1, "真实书签动作保存当前位置")
        let annotations = AnnotationStore(root: root.appendingPathComponent("Annotations"))
        let annotation = try annotations.add(AnnotationDraft(documentID: UUID(), path: nil, sourceStart: 0, sourceEnd: 2, quote: "摘录"))
        expect(AnnotationAnchorResolver.repair(annotation, in: "摘录正文").anchorState == .exact, "批注原位置仍在时保持准确")
        let shifted = AnnotationAnchorResolver.repair(annotation, in: "前言\n摘录正文")
        expect(shifted.sourceStart == 3 && shifted.anchorState == .shifted, "唯一摘录可跟随插入移动")
        expect(AnnotationAnchorResolver.repair(annotation, in: "完全不同").anchorState == .missing, "删除引文标记缺失")
        expect(AnnotationAnchorResolver.repair(annotation, in: "前摘录中摘录尾").anchorState == .uncertain, "重复引文保持待核对状态")
        print("CurrentSyncWorkflowTests: \(checks) checks passed")
    }
    static func descendants(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap(descendants) }
    static func wait(_ condition: () -> Bool) throws {
        let end = Date(timeIntervalSinceNow: 10)
        while !condition() { if Date() > end { throw NSError(domain: "CurrentSync", code: 1, userInfo: [NSLocalizedDescriptionKey: "等待控件状态超时"]) }; RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.01)) }
    }
    static func expect(_ condition: @autoclosure () -> Bool, _ message: String) { guard condition() else { fatalError(message) }; checks += 1 }
}
