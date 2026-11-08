import AppKit

@main
struct WindowMenuBarTests {
    private static var checks = 0

    private final class RecordingMenu: NSMenu {
        var popupCount = 0
        var popupPoint = NSPoint.zero
        weak var popupView: NSView?
        var titlesAtPopup: [String] = []
        override func popUp(positioning item: NSMenuItem?, at location: NSPoint, in view: NSView?) -> Bool {
            popupCount += 1
            popupPoint = location
            popupView = view
            titlesAtPopup = items.map { $0.title }
            return false
        }
    }

    static func main() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.prohibited)
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 820, height: 400), styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        let root = NSView()
        window.contentView = root
        window.makeKeyAndOrderFront(nil)
        defer { window.orderOut(nil) }
        let editor = NSTextView(frame: NSRect(x: 0, y: 0, width: 500, height: 200))
        editor.string = "保留编辑选区 😀"
        root.addSubview(editor)
        let bar = WindowMenuBar(frame: .zero)
        root.addSubview(bar)
        NSLayoutConstraint.activate([
            bar.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            bar.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            bar.topAnchor.constraint(equalTo: root.topAnchor),
            bar.heightAnchor.constraint(equalToConstant: 30)
        ])
        let names = ["文件", "编辑", "搜索", "视图", "标签", "帮助"]
        let menus = names.map { RecordingMenu(title: $0) }
        for menu in menus { menu.addItem(withTitle: "原始命令", action: nil, keyEquivalent: "") }
        bar.setGroups(zip(names, menus).map { WindowMenuGroup(title: $0.0, menu: $0.1) })
        root.layoutSubtreeIfNeeded()
        var buttons = allViews(bar).compactMap { $0 as? NSButton }
        expect(buttons.map { $0.title } == names, "all six menu titles are visible and ordered")
        expect(buttons.map { $0.accessibilityLabel() ?? "" } == names.map { $0 + "菜单" }, "each menu has a useful accessibility name")
        expect(buttons.allSatisfy { $0.image == nil && $0 is NSPopUpButton == false }, "menu titles do not add dropdown arrows or icons")
        expect(bar.intrinsicContentSize.height == 30, "menu bar keeps a compact height")
        expect(bar.intrinsicContentSize.width <= 820, "all menu titles fit in the minimum reader width")
        expect(buttons.allSatisfy { $0.frame.width >= 48 && $0.frame.height == 28 }, "click targets have usable compact geometry")
        expect(buttons.last.map { bar.convert($0.bounds, from: $0).maxX <= 820 } == true, "layout does not clip the last title")

        var preparedTitles: [String] = []
        bar.prepareMenu = { menu in
            preparedTitles.append(menu.title)
            let recent = NSMenuItem(title: "最近打开", action: nil, keyEquivalent: "")
            recent.submenu = NSMenu(title: "最近打开")
            recent.submenu?.addItem(withTitle: "最新文件.md", action: nil, keyEquivalent: "")
            menu.addItem(recent)
        }
        window.makeFirstResponder(editor)
        editor.setSelectedRange(NSRange(location: 2, length: 2))
        let originalSelection = editor.selectedRange()
        buttons[0].performClick(nil)
        expect(preparedTitles == ["文件"], "menu preparation is called before display")
        expect(menus[0].popupCount == 1 && menus[1].popupCount == 0, "the clicked title opens its supplied command menu")
        expect(menus[0].titlesAtPopup == ["原始命令", "最近打开"], "dynamic menu contents are refreshed before opening")
        expect(menus[0].item(at: 1)?.submenu?.item(at: 0)?.title == "最新文件.md", "recent-file submenus survive the window entry point")
        expect(menus[0].popupPoint.y < 0 && menus[0].popupPoint.x == 0, "dropdown is anchored below its title")
        expect(menus[0].popupView === buttons[0], "dropdown anchor uses the correct button")
        expect(window.firstResponder === editor, "clicking a menu title leaves the text first responder intact")
        expect(editor.selectedRange() == originalSelection, "opening a menu keeps the user's source selection")

        // A prohibited headless application is not the user's active app, so
        // NSApp does not provide its normal key-window nil-target lookup here.
        // Check the retained responder directly; real menu routing is verified
        // by the desktop integration test.
        expect(app.sendAction(#selector(NSText.selectAll(_:)), to: window.firstResponder, from: nil), "standard text commands remain available on the retained responder")
        expect(editor.selectedRange() == NSRange(location: 0, length: editor.string.utf16.count), "the standard command acts on the text document")
        expect(editor.string == "保留编辑选区 😀", "menu interaction never rewrites the source")

        buttons[1].isEnabled = false
        buttons[1].performClick(nil)
        expect(menus[1].popupCount == 0 && preparedTitles == ["文件"], "disabled menu titles cannot open or prepare a menu")
        buttons[1].isEnabled = true
        buttons[1].performClick(nil)
        expect(menus[1].popupCount == 1 && preparedTitles.last == "编辑", "reenabling a menu restores its action")

        let firstGeneration = buttons
        let replacement = RecordingMenu(title: "文件")
        bar.setGroups([WindowMenuGroup(title: "文件", menu: replacement)])
        root.layoutSubtreeIfNeeded()
        buttons = allViews(bar).compactMap { $0 as? NSButton }
        expect(buttons.count == 1 && buttons[0].title == "文件", "replacing groups does not duplicate menu titles")
        expect(firstGeneration.allSatisfy { $0.superview == nil }, "old menu controls are removed from the view hierarchy")
        buttons[0].performClick(nil)
        expect(replacement.popupCount == 1 && menus[0].popupCount == 1, "replacement titles use the new menu object")

        bar.applyAppearance(foreground: .white, background: .black, highlight: .systemBlue)
        expect(window.firstResponder === editor, "theme changes do not steal text focus")
        expect(allViews(bar).compactMap { $0 as? NSButton }.count == 1, "theme changes reuse existing menu controls")
        bar.setGroups([])
        root.layoutSubtreeIfNeeded()
        expect(allViews(bar).compactMap { $0 as? NSButton }.isEmpty, "an empty menu list is safe")
        expect(bar.intrinsicContentSize.width == 12, "empty menu geometry remains bounded")

        print("WindowMenuBarTests: \(checks) checks passed")
    }

    private static func allViews(_ root: NSView) -> [NSView] { [root] + root.subviews.flatMap(allViews) }
    private static func expect(_ condition: @autoclosure () -> Bool, _ message: String) {
        guard condition() else { fatalError(message) }
        checks += 1
    }
}
