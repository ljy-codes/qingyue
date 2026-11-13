import AppKit
import Foundation

/// Geometry and state checks for the Windows-derived application layout. All
/// document and recovery I/O stays under an isolated temporary directory.
@main
struct LayoutParityTests {
    private static var checks = 0
    static func main() {
        do { try run() }
        catch {
            FileHandle.standardError.write(Data(("LayoutParityTests failed: " + error.localizedDescription + "\n").utf8))
            exit(1)
        }
    }
    static func run() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.prohibited)
        let domain = Bundle.main.bundleIdentifier ?? ProcessInfo.processInfo.processName
        UserDefaults.standard.setVolatileDomain([
            "recent": [String](), "positions": [String: [Double]](), "fontSize": 14.0,
            "dark": true, "followSystemAppearance": false, "readingWidth": 720.0,
            "wrapping": true, "lineNumbers": true, "restoreSession": true,
            "recoveryEnabled": true, "excludedDraftPaths": [String]()
        ], forName: UserDefaults.argumentDomain)
        let rootDirectory = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-layout-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: rootDirectory, withIntermediateDirectories: true)
        defer {
            UserDefaults.standard.removePersistentDomain(forName: domain)
            UserDefaults.standard.removeVolatileDomain(forName: UserDefaults.argumentDomain)
            try? FileManager.default.removeItem(at: rootDirectory)
        }
        let markdownURL = rootDirectory.appendingPathComponent("布局.md")
        let markdown = "# 布局标题\n\n正文 needle。\n\n## 第二节\n\n| 文件 | 内容 |\n| --- | --- |\n| TXT | 阅读 |\n"
        let jsonURL = rootDirectory.appendingPathComponent("布局.json")
        let json = "{\"name\":\"布局 😀\",\"port\":8080}\n"
        let plainURL = rootDirectory.appendingPathComponent("布局.txt")
        try Data(markdown.utf8).write(to: markdownURL)
        try Data(json.utf8).write(to: jsonURL)
        try Data("普通正文\n".utf8).write(to: plainURL)
        let reader = ReaderWindow(supportDirectory: rootDirectory.appendingPathComponent("Support"))
        let window = try required(reader.window, "reader window exists")
        reader.showWindow(nil)
        defer { window.orderOut(nil) }
        try wait("initial recovery") { sourceView(window)?.isEditable == true }
        window.setContentSize(NSSize(width: 1120, height: 760))
        let root = try required(window.contentView, "root content exists")
        root.layoutSubtreeIfNeeded()
        let menu = try region("QingyueMenuRow", root)
        let header = try region("QingyueHeader", root)
        let tabs = try region("QingyueTabRow", root)
        let find = try region("QingyueSearchPanel", root)
        let body = try region("QingyueBody", root)
        let sidebar = try region("QingyueSidebar", root)
        let tools = try region("QingyueContextToolbar", root)
        let footer = try region("QingyueFooter", root)
        for pair in [(menu, header), (header, tabs), (tabs, body)] {
            try expect(frame(pair.0, root).minY >= frame(pair.1, root).maxY - 1, "full-width rows are in the Windows top-to-bottom order")
        }
        try expect(frame(body, root).minY >= frame(footer, root).maxY - 1, "footer is below the document workspace")
        for row in [menu, header, tabs, body, footer] {
            try expect(abs(frame(row, root).minX) <= 1 && abs(frame(row, root).maxX - root.bounds.width) <= 1, "structural rows span the complete application width")
        }
        try expect(abs(sidebar.frame.width - 218) <= 1, "normal sidebar uses the Windows 218-point width")
        try expect(frame(sidebar, root).maxY <= frame(tabs, root).minY + 1, "tabs are above both sidebar and document")
        try expect(frame(tools, root).minX >= frame(sidebar, root).maxX - 1, "context toolbar belongs only to the right document area")
        let headerButtons = visibleButtons(header)
        let mainActions = headerButtons.filter { !$0.title.isEmpty }.map { $0.title }
        try expect(mainActions.count == 4 && mainActions[0] == "打开文件" && mainActions[1] == "保存" && mainActions[3] == "专注", "header contains only the Windows four primary actions")
        let sidebarToggle = try required(headerButtons.first { $0.accessibilityLabel() == "显示或隐藏侧栏" || $0.title.isEmpty }, "sidebar icon remains in the brand header")
        try expect(abs(sidebarToggle.frame.width - 36) <= 1, "sidebar control uses the compact 36-point icon layout")
        let branding = descendants(header).compactMap { $0 as? NSTextField }
        try expect(branding.contains { $0.stringValue == "轻阅" && abs(($0.font?.pointSize ?? 0) - 22) < 0.1 }, "brand title uses 22-point type")
        try expect(branding.contains { $0.stringValue == "本机文本 · 阅读与编辑" && abs(($0.font?.pointSize ?? 0) - 11) < 0.1 }, "brand subtitle uses the corresponding 11-point type")
        let readingAction = try required(headerButtons.first { ["阅读模式", "编辑原文", "返回预览"].contains($0.title) } as? ActionButton, "reading action exists")
        try expect(readingAction.isEmphasized, "reading action is the accented primary action")
        let plus = try required(visibleButtons(tabs).first { $0.title == "+" }, "tab-row plus is directly clickable")
        let plusFrame = frame(plus, root)
        try expect(plusFrame.maxX >= root.bounds.width - 20 && plusFrame.minY >= frame(tabs, root).minY, "new-document plus belongs to the far right of the full-width tab row")
        try expect(!headerButtons.contains { $0.title == "新建" || $0.title == "设置" || $0.title.contains("导出") }, "legacy extra header actions are absent")
        try expect(visibleButtons(sidebar).prefix(2).map { $0.title } == ["最近打开", "文档目录"], "sidebar categories follow Windows ordering")
        try checkFooter(footer, root: root)
        try expect(sidebar as? ColorView != nil && (sidebar as! ColorView).radius == 0, "workspace sidebar does not retain the old rounded-card shape")

        reader.openURLs([markdownURL])
        try wait("open Markdown") { window.representedURL?.lastPathComponent == markdownURL.lastPathComponent && readingView(window)?.isHiddenOrHasHiddenAncestor == false }
        root.layoutSubtreeIfNeeded()
        var toolButtons = visibleButtons(tools)
        try expect(toolButtons.contains { $0.title == "分屏编辑" }, "Markdown offers split editing in the context toolbar")
        try expect(toolButtons.contains { $0.title == "复制全文（含图片）" }, "Markdown preview puts rich full-copy in the document toolbar")
        try expect(!toolButtons.contains { $0.title == "JSON 结构" }, "Markdown does not expose an unrelated JSON structure action")
        try expect(descendants(tools).contains { $0.accessibilityLabel() == "复制 / 导出" && !$0.isHiddenOrHasHiddenAncestor }, "Markdown copy/export choices are grouped beside full copy")
        try expect(!toolButtons.contains { ["A−", "A+", "自动换行 ✓", "目录", "查找"].contains($0.title) }, "legacy per-document toolbar controls are moved to the matching menu groups")
        try checkFooter(footer, root: root)
        reader.toggleSplitPreview(nil)
        root.layoutSubtreeIfNeeded()
        try expect(visibleButtons(tools).contains { $0.title == "关闭分屏" }, "split mode updates its contextual action label")
        try expect(sourceView(window)?.isHiddenOrHasHiddenAncestor == false && readingView(window)?.isHiddenOrHasHiddenAncestor == false, "native split editor and preview remain accessible")
        reader.toggleSplitPreview(nil)
        root.layoutSubtreeIfNeeded()
        try expect(!visibleButtons(tools).contains { $0.title == "复制全文（含图片）" }, "source-only Markdown hides the preview full-copy shortcut")
        reader.toggleReading(nil)
        root.layoutSubtreeIfNeeded()
        try expect(visibleButtons(tools).contains { $0.title == "复制全文（含图片）" }, "preview Markdown restores the rich full-copy shortcut")

        reader.showFind(nil)
        root.layoutSubtreeIfNeeded()
        let search = try required(descendants(find).compactMap { $0 as? NSSearchField }.first, "search field exists in the full-width search panel")
        let replacement = try required(descendants(find).compactMap { $0 as? NSTextField }.first { $0.accessibilityLabel() == "替换为" }, "replacement field remains available for replace mode")
        try expect(!find.isHiddenOrHasHiddenAncestor && replacement.isHiddenOrHasHiddenAncestor, "plain Find does not expose replacement controls")
        try expect(frame(find, root).minX <= 1 && frame(find, root).maxX >= root.bounds.maxX - 1, "find panel spans the entire window below tabs")
        let findFrame = frame(find, root)
        try expect(abs(frame(tabs, root).minY - findFrame.maxY) <= 1 && abs(frame(body, root).maxY - findFrame.minY) <= 1, "find panel is between full-width tabs and workspace")
        try expect(app.sendAction(NSSelectorFromString("showReplace:"), to: reader, from: nil), "native replace menu action dispatches")
        root.layoutSubtreeIfNeeded()
        try expect(!replacement.isHiddenOrHasHiddenAncestor, "Replace reveals its own text field")
        try expect(frame(replacement, root).maxY < frame(search, root).minY, "replacement text is on a separate line beneath the search query")
        let searchButtons = visibleButtons(find)
        try expect(searchButtons.contains { $0.title == "关闭" } && searchButtons.contains { $0.title == "全部替换" }, "search and replace rows use the Windows action labels")
        let fieldEditor = try required(window.fieldEditor(true, for: search) as? NSTextView, "find input uses a native field editor")
        _ = reader.control(search, textView: fieldEditor, doCommandBy: #selector(NSResponder.cancelOperation(_:)))
        root.layoutSubtreeIfNeeded()
        try expect(find.isHiddenOrHasHiddenAncestor || find.frame.height < 1, "Esc collapses the complete search panel")

        sidebarToggle.performClick(nil)
        root.layoutSubtreeIfNeeded()
        try expect(sidebar.isHiddenOrHasHiddenAncestor && !sidebarToggle.isHiddenOrHasHiddenAncestor, "hidden sidebar keeps its header reopening control")
        sidebarToggle.performClick(nil)
        root.layoutSubtreeIfNeeded()
        try expect(!sidebar.isHiddenOrHasHiddenAncestor && abs(sidebar.frame.width - 218) < 1, "sidebar reopening restores its reserved width")
        reader.toggleFocus(nil)
        root.layoutSubtreeIfNeeded()
        try expect(menu.isHiddenOrHasHiddenAncestor && tabs.isHiddenOrHasHiddenAncestor && sidebar.isHiddenOrHasHiddenAncestor, "Windows-style focus hides menus, tabs and sidebar")
        try expect(!header.isHiddenOrHasHiddenAncestor && !tools.isHiddenOrHasHiddenAncestor, "focus retains brand operations and context toolbar")
        let exitFocus = try required(visibleButtons(header).first { $0.title == "退出专注" }, "focus exit stays in the header")
        try checkFooter(footer, root: root)
        exitFocus.performClick(nil)
        root.layoutSubtreeIfNeeded()
        try expect(!menu.isHiddenOrHasHiddenAncestor && !tabs.isHiddenOrHasHiddenAncestor && !sidebar.isHiddenOrHasHiddenAncestor, "focus exit restores the normal row hierarchy")

        reader.openURLs([jsonURL])
        try wait("open JSON") { window.representedURL?.lastPathComponent == jsonURL.lastPathComponent }
        root.layoutSubtreeIfNeeded()
        toolButtons = visibleButtons(tools)
        try expect(toolButtons.contains { $0.title == "JSON 结构" } && !toolButtons.contains { $0.title == "分屏编辑" }, "JSON receives structure controls instead of Markdown controls")
        try expect(!toolButtons.contains { $0.title == "复制全文（含图片）" }, "JSON does not expose the Markdown copy shortcut")
        for width: CGFloat in [820, 1120] {
            window.setContentSize(NSSize(width: width, height: 700))
            root.layoutSubtreeIfNeeded()
            try checkFooter(footer, root: root)
            for button in visibleButtons(header) + visibleButtons(tools) {
                let rect = frame(button, root)
                try expect(rect.minX >= -1 && rect.maxX <= root.bounds.maxX + 1, "visible action stays inside the \(Int(width))-point window")
            }
            try expect(abs(frame(tabs, root).width - width) < 1, "tab row remains full width after resizing")
        }
        let darkTheme = ReaderTheme(dark: true)
        try expect(rgb((sidebar as! ColorView).color) == "333333", "dark sidebar matches the Windows graphite panel")
        try expect(rgb(darkTheme.editor) == "3F3F3F" && rgb(darkTheme.text) == "DCDCCC", "dark text and editor tokens match Windows")
        reader.toggleTheme(nil)
        root.layoutSubtreeIfNeeded()
        try expect(rgb((sidebar as! ColorView).color) == "E9EDF3", "light sidebar matches the Windows light panel")
        try expect(rgb(ReaderTheme(dark: false).editor) == "FFFFFF", "light editor uses the common white canvas")

        try expect(try Data(contentsOf: markdownURL) == Data(markdown.utf8), "layout and navigation never rewrite Markdown")
        try expect(try Data(contentsOf: jsonURL) == Data(json.utf8), "layout and mode changes never rewrite JSON")
        try expect(reader.confirmQuit(), "clean isolated documents can persist without modal prompts")
        print("LayoutParityTests: \(checks) checks passed")
    }
    private static func checkFooter(_ footer: NSView, root: NSView) throws {
        let popups = descendants(footer).compactMap { $0 as? NSPopUpButton }.filter { !$0.isHiddenOrHasHiddenAncestor }.sorted { frame($0, root).minX < frame($1, root).minX }
        try expect(popups.count == 3, "language, codec and line-ending controls remain visible in the footer")
        guard popups.count == 3 else { return }
        try expect(popups.map { $0.accessibilityLabel() ?? "" } == ["文档语言", "保存编码", "文件换行方式"], "footer uses Windows language/encoding/line-ending order")
        for (popup, width) in zip(popups, [150.0, 138.0, 178.0]) {
            try expect(abs(Double(popup.frame.width) - width) <= 1, "footer dropdown uses the matching width")
            let rect = frame(popup, root)
            try expect(rect.minX >= 0 && rect.maxX <= root.bounds.maxX + 1, "footer option stays inside the window")
        }
        try expect(frame(popups[0], root).maxX + 7 <= frame(popups[1], root).minX && frame(popups[1], root).maxX + 7 <= frame(popups[2], root).minX, "footer dropdowns preserve their spacing")
    }
    private static func descendants(_ root: NSView) -> [NSView] { [root] + root.subviews.flatMap(descendants) }
    private static func visibleButtons(_ root: NSView) -> [NSButton] { descendants(root).compactMap { $0 as? NSButton }.filter { !($0 is NSPopUpButton) && !$0.isHiddenOrHasHiddenAncestor } }
    private static func frame(_ view: NSView, _ root: NSView) -> NSRect { view.convert(view.bounds, to: root) }
    private static func region(_ id: String, _ root: NSView) throws -> NSView { try required(descendants(root).first { $0.identifier?.rawValue == id }, "layout region \(id) exists") }
    private static func sourceView(_ window: NSWindow) -> NSTextView? { window.contentView.flatMap { descendants($0).compactMap { $0 as? NSTextView }.first { $0.accessibilityLabel() == "文本编辑区" } } }
    private static func readingView(_ window: NSWindow) -> NSTextView? { window.contentView.flatMap { descendants($0).compactMap { $0 as? NSTextView }.first { $0.accessibilityLabel() == "阅读区" } } }
    private static func rgb(_ value: NSColor) -> String {
        guard let c = value.usingColorSpace(.sRGB) else { return "invalid" }
        return String(format: "%02X%02X%02X", Int((c.redComponent * 255).rounded()), Int((c.greenComponent * 255).rounded()), Int((c.blueComponent * 255).rounded()))
    }
    private static func required<T>(_ value: T?, _ message: String) throws -> T { guard let value = value else { throw error(message) }; return value }
    private static func expect(_ value: @autoclosure () throws -> Bool, _ message: String) throws { guard try value() else { throw error(message) }; checks += 1 }
    private static func wait(_ description: String, until predicate: () -> Bool) throws {
        let deadline = Date(timeIntervalSinceNow: 10)
        while !predicate() { guard Date() < deadline else { throw error("Timed out: " + description) }; _ = RunLoop.current.run(mode: .default, before: Date(timeIntervalSinceNow: 0.02)) }
    }
    private static func error(_ message: String) -> NSError { NSError(domain: "LayoutParityTests", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
}
