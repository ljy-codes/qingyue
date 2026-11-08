import AppKit
import Foundation

@main
struct ReaderMenusTests {
    private static var checks = 0

    private final class ApplicationActions: NSObject {
        @objc func about(_ sender: Any?) {}
    }

    static func main() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.prohibited)
        let fm = FileManager.default
        let temporary = fm.temporaryDirectory.appendingPathComponent("qingyue-menu-workflows-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: temporary, withIntermediateDirectories: true)
        let defaultsDomain = Bundle.main.bundleIdentifier ?? ProcessInfo.processInfo.processName
        UserDefaults.standard.removePersistentDomain(forName: defaultsDomain)
        UserDefaults.standard.setVolatileDomain([
            "positions": [String: [Double]](),
            "restoreSession": true, "recoveryEnabled": true, "excludedDraftPaths": [String](),
            "fontSize": 14.0, "followSystemAppearance": false, "dark": true,
            "readingWidth": 680.0, "wrapping": true, "lineNumbers": true
        ], forName: UserDefaults.argumentDomain)
        defer {
            UserDefaults.standard.removePersistentDomain(forName: defaultsDomain)
            UserDefaults.standard.removeVolatileDomain(forName: UserDefaults.argumentDomain)
            try? fm.removeItem(at: temporary)
        }

        let reader = ReaderWindow(supportDirectory: temporary.appendingPathComponent("support"))
        let window = try require(reader.window, "reader window exists")
        reader.showWindow(nil)
        defer { window.orderOut(nil) }
        try wait("initial restore") { editableText(in: window) != nil }
        let appTarget = ApplicationActions()
        let menus = ReaderMenus(reader: reader, applicationTarget: appTarget, aboutAction: #selector(ApplicationActions.about(_:)))
        app.mainMenu = menus.mainMenu
        reader.configureWindowMenus(menus.windowGroups)
        window.contentView?.layoutSubtreeIfNeeded()
        let bar = try require(allViews(in: window).compactMap { $0 as? WindowMenuBar }.first, "window menu row is attached")
        let expected = ["文件", "编辑", "搜索", "视图", "标签", "帮助"]
        try expect(menus.windowGroups.map { $0.title } == expected, "six window menu groups are ordered")
        try expect(menus.mainMenu.items.map { $0.title } == ["轻阅"] + expected, "native menu contains the same command groups plus the app menu")
        let titleButtons = descendants(bar).compactMap { $0 as? NSButton }
        try expect(titleButtons.map { $0.title } == expected, "all six headings are directly clickable in the window")
        try expect(titleButtons.allSatisfy { !$0.isHiddenOrHasHiddenAncestor }, "menu headings are visible in normal layout")
        for group in menus.windowGroups {
            try expect(menus.mainMenu.item(withTitle: group.title)?.submenu === group.menu, "\(group.title) shares its menu object with the native menu")
        }
        let allItems = recursiveItems(menus.mainMenu)
        var shortcuts: [String: NSMenuItem] = [:]
        for item in allItems where !item.keyEquivalent.isEmpty {
            let key = "\(item.keyEquivalentModifierMask.rawValue):\(item.keyEquivalent)"
            if let other = shortcuts[key] {
                try expect(item.action == #selector(ReaderWindow.showPreferences(_:)) && other.action == item.action, "only the same Settings command may repeat a shortcut")
            } else { shortcuts[key] = item }
        }
        let fileMenu = try require(menus.mainMenu.item(withTitle: "文件")?.submenu, "file menu exists")
        let searchMenu = try require(menus.mainMenu.item(withTitle: "搜索")?.submenu, "search menu exists")
        let viewMenu = try require(menus.mainMenu.item(withTitle: "视图")?.submenu, "view menu exists")
        let editMenu = try require(menus.mainMenu.item(withTitle: "编辑")?.submenu, "edit menu exists")
        let tabsMenu = try require(menus.mainMenu.item(withTitle: "标签")?.submenu, "tabs menu exists")
        let quick = try menuItem(fileMenu, action: #selector(ReaderWindow.quickOpen(_:)))
        try expect(quick.keyEquivalent == "p" && quick.keyEquivalentModifierMask == [.command, .shift], "quick open retains CmdShiftP")
        let saveAll = try menuItem(fileMenu, action: #selector(ReaderWindow.saveAllDocuments(_:)))
        try expect(saveAll.keyEquivalent == "s" && saveAll.keyEquivalentModifierMask == [.command, .option], "save all retains CmdOptionS")
        let find = try menuItem(searchMenu, action: #selector(ReaderWindow.showFind(_:)))
        try expect(find.target === reader && find.keyEquivalent == "f", "search heading targets the same reader action")
        try expect(try menuItem(fileMenu, action: #selector(ReaderWindow.renameCurrentDocument(_:))).keyEquivalent.isEmpty, "file rename does not claim an existing shortcut")
        try expect(try menuItem(tabsMenu, action: #selector(ReaderWindow.renameCurrentDocument(_:))).keyEquivalent.isEmpty, "tab rename uses the same action without a conflicting shortcut")

        // The top-level side-panel switch must remain outside the hidden sidebar.
        let topSidebar = try require(allViews(in: window).compactMap { $0 as? NSButton }.first { $0.accessibilityLabel() == "收起侧栏" }, "top sidebar switch exists")
        let root = try require(window.contentView, "window has root content")
        let initialEditorX = editableText(in: window)?.enclosingScrollView.map { root.convert($0.bounds, from: $0).minX } ?? -1
        topSidebar.performClick(nil)
        root.layoutSubtreeIfNeeded()
        try expect(topSidebar.title == "展开侧栏" && !topSidebar.isHiddenOrHasHiddenAncestor, "collapse leaves a visible expand button")
        let hiddenEditorX = editableText(in: window)?.enclosingScrollView.map { root.convert($0.bounds, from: $0).minX } ?? -1
        try expect(hiddenEditorX < initialEditorX, "collapsing sidebar expands the actual document area")
        let sidebarMenu = try menuItem(viewMenu, action: #selector(ReaderWindow.toggleSidebar(_:)))
        try expect(reader.validateMenuItem(sidebarMenu) && sidebarMenu.state == .off, "native view menu reflects a collapsed sidebar")
        topSidebar.performClick(nil)
        root.layoutSubtreeIfNeeded()
        try expect(topSidebar.title == "收起侧栏" && !topSidebar.isHiddenOrHasHiddenAncestor, "the visible switch reopens the sidebar")
        try expect(reader.validateMenuItem(sidebarMenu) && sidebarMenu.state == .on, "menu state reflects a reopened sidebar")
        for _ in 0..<6 { topSidebar.performClick(nil); topSidebar.performClick(nil) }
        try expect(topSidebar.title == "收起侧栏" && topSidebar.window === window, "repeated hide/show never loses the toggle")

        // A narrow window must still expose all headings and the expand button.
        window.setContentSize(NSSize(width: 820, height: 600))
        root.layoutSubtreeIfNeeded()
        let menuFrame = root.convert(bar.bounds, from: bar)
        let switchFrame = root.convert(topSidebar.bounds, from: topSidebar)
        try expect(menuFrame.maxX + 8 <= switchFrame.minX, "menu headings and sidebar switch do not overlap at minimum width")
        try expect(menuFrame.minX >= 0 && switchFrame.maxX <= root.bounds.maxX, "window controls remain inside minimum-width bounds")
        try expect(titleButtons.allSatisfy { bar.convert($0.bounds, from: $0).maxX <= bar.bounds.width }, "all menu headings fit their available row")

        reader.toggleFocus(nil)
        try expect(bar.isHiddenOrHasHiddenAncestor && topSidebar.isHiddenOrHasHiddenAncestor, "focus layout hides the menu row as a group")
        let exitFocus = try require(allViews(in: window).compactMap { $0 as? NSButton }.first { $0.title == "退出专注" }, "focus exit action stays available")
        try expect(!exitFocus.isHiddenOrHasHiddenAncestor, "focus mode has a visible exit")
        exitFocus.performClick(nil)
        try expect(!bar.isHiddenOrHasHiddenAncestor && !topSidebar.isHiddenOrHasHiddenAncestor, "exiting focus restores headings and sidebar control")

        let recentMenu = try require(fileMenu.item(withTitle: "最近打开")?.submenu, "recent submenu exists")
        let emptyRecent = recentMenu.items.filter { $0.representedObject is String }
        try expect(emptyRecent.isEmpty, "test starts with no user recent files")
        let jsonURL = temporary.appendingPathComponent("配置.json")
        try Data("{\"value\":1,\"标题\":\"原文 😀\"}\n".utf8).write(to: jsonURL)
        reader.openURLs([jsonURL])
        try wait("open JSON fixture") { window.representedURL == jsonURL }
        let sameRecent = try require(fileMenu.item(withTitle: "最近打开")?.submenu, "recent submenu survives refresh")
        try expect(sameRecent === recentMenu, "refresh reuses the shared recent submenu object")
        try expect(recentMenu.items.contains { ($0.representedObject as? String) == jsonURL.path }, "opening a file updates both menu entry points")
        bar.prepareMenu?(fileMenu)
        try expect(recentMenu.items.filter { ($0.representedObject as? String) == jsonURL.path }.count == 1, "preparing the window menu does not duplicate recent files")

        let editor = try require(editableText(in: window), "JSON source editor is attached")
        let undo = try menuItem(editMenu, action: #selector(ReaderWindow.undoEdit(_:)))
        let redo = try menuItem(editMenu, action: #selector(ReaderWindow.redoEdit(_:)))
        let format = try menuItem(editMenu, action: #selector(ReaderWindow.formatJSON(_:)))
        window.makeFirstResponder(editor)
        try expect(!reader.validateMenuItem(undo) && !reader.validateMenuItem(redo), "clean document has no undo or redo menu action")
        try expect(reader.validateMenuItem(format), "JSON formatting is available while editing JSON")
        editor.setSelectedRange(NSRange(location: 0, length: 0))
        editor.insertText(" ", replacementRange: editor.selectedRange())
        editor.breakUndoCoalescing()
        try expect(reader.validateMenuItem(undo), "source edit enables undo")
        reader.toggleReading(nil)
        try expect(!reader.validateMenuItem(format), "JSON format command is disabled in reading mode")
        try expect(!reader.validateMenuItem(undo) && !reader.validateMenuItem(redo), "source undo and redo are disabled in reading mode")
        reader.toggleReading(nil)
        window.makeFirstResponder(editor)
        try expect(app.sendAction(undo.action!, to: undo.target, from: undo), "native menu action reaches reader undo")
        try expect(editor.string == "{\"value\":1,\"标题\":\"原文 😀\"}\n", "menu undo acts on the expected document")
        try expect(reader.validateMenuItem(redo), "undo enables matching redo action")
        reader.clearRecent(nil)
        try expect(recentMenu.items.filter { $0.representedObject is String }.isEmpty, "clearing history refreshes the same submenu")
        try expect(Data(contentsOf: jsonURL) == Data("{\"value\":1,\"标题\":\"原文 😀\"}\n".utf8), "menu and layout tests do not rewrite source files")

        // Flush only clean test state. No user support directory or preferences
        // domain is used by this executable.
        try expect(reader.confirmQuit(), "clean test documents can persist their isolated session")
        print("ReaderMenusTests: \(checks) checks passed")
    }

    private static func recursiveItems(_ menu: NSMenu) -> [NSMenuItem] {
        menu.items.flatMap { item in [item] + (item.submenu.map(recursiveItems) ?? []) }
    }
    private static func descendants(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap(descendants) }
    private static func allViews(in window: NSWindow) -> [NSView] { window.contentView.map(descendants) ?? [] }
    private static func editableText(in window: NSWindow) -> NSTextView? {
        allViews(in: window).compactMap { $0 as? NSTextView }.first { !$0.isFieldEditor && $0.isEditable && $0.accessibilityLabel() == "文本编辑区" }
    }
    private static func menuItem(_ menu: NSMenu, action: Selector) throws -> NSMenuItem {
        try require(menu.items.first { $0.action == action }, "menu contains \(action)")
    }
    private static func wait(_ description: String, until predicate: () -> Bool) throws {
        let deadline = Date(timeIntervalSinceNow: 6)
        while !predicate() {
            guard Date() < deadline else { throw failure("Timed out: " + description) }
            _ = RunLoop.current.run(mode: .default, before: Date(timeIntervalSinceNow: 0.02))
        }
    }
    private static func require<T>(_ value: T?, _ message: String) throws -> T {
        guard let value = value else { throw failure(message) }
        return value
    }
    private static func expect(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
        guard try condition() else { throw failure(message) }
        checks += 1
    }
    private static func failure(_ message: String) -> NSError { NSError(domain: "ReaderMenusTests", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
}
