import AppKit
import Foundation

@main
struct UIWorkflowTests {
    private static var checks = 0

    private final class QuitProbe: NSObject, NSApplicationDelegate {
        var terminationRequests = 0
        func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
            terminationRequests += 1
            return .terminateCancel
        }
    }

    static func main() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.prohibited)
        let quitProbe = QuitProbe()
        app.delegate = quitProbe

        // This executable has no LightReader bundle identifier. The support
        // override also keeps all recovery I/O out of the user's real app data.
        try expect(Bundle.main.bundleIdentifier != "local.xiaoyu.LightReader", "workflow tests use their own preferences domain")
        let defaultsDomain = Bundle.main.bundleIdentifier ?? ProcessInfo.processInfo.processName
        UserDefaults.standard.setVolatileDomain([
            "recent": [String](), "positions": [String: [Double]](),
            "fontSize": 14.0, "dark": true, "followSystemAppearance": false,
            "readingWidth": 680.0, "wrapping": true, "lineNumbers": true
        ], forName: UserDefaults.argumentDomain)

        let fm = FileManager.default
        let directory = fm.temporaryDirectory.appendingPathComponent("qingyue-ui-workflows-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: directory, withIntermediateDirectories: true)
        defer {
            UserDefaults.standard.removePersistentDomain(forName: defaultsDomain)
            UserDefaults.standard.removeVolatileDomain(forName: UserDefaults.argumentDomain)
            try? fm.removeItem(at: directory)
        }
        let support = directory.appendingPathComponent("isolated-support", isDirectory: true)
        let markdownURL = directory.appendingPathComponent("reading-fixture.md")
        let jsonURL = directory.appendingPathComponent("structured-fixture.json")
        let eofURL = directory.appendingPathComponent("no-final-newline.txt")
        let markdown = "# 阅读标题 😀\n\n第一段 **needle**，继续阅读。\n\n[可见链接文字](https://hidden.invalid/needle-hidden)\n\n## 第二节\n\n第二段 needle。\n"
        let json = "{\"port\":8080,\"标题\":\"中文 😀\"}\n"
        try Data(markdown.utf8).write(to: markdownURL)
        try Data(json.utf8).write(to: jsonURL)
        let eofText = (0..<100).map { "第 \($0) 行，长文定位 😀" }.joined(separator: "\n") + "\n最后一段 EOF_MARKER"
        try Data(eofText.utf8).write(to: eofURL)

        let automaticFile = TextFile(text: "# 保存后的类型", url: directory.appendingPathComponent("automatic.txt"))
        let automaticTab = ReaderTab(automaticFile)
        try expect(automaticTab.automaticKind && automaticTab.kind == .plain, "explicit TXT extension starts with automatic plain text")
        automaticFile.url = directory.appendingPathComponent("automatic.md")
        automaticTab.updateKindAfterPathChange()
        try expect(automaticTab.kind == .markdown, "saving an automatic TXT document as MD updates its type")
        automaticFile.url = directory.appendingPathComponent("automatic.json")
        automaticFile.text = "{\"saved\":true}"
        automaticTab.updateKindAfterPathChange()
        try expect(automaticTab.kind == .json, "saving with a JSON extension updates automatic recognition")
        automaticTab.automaticKind = false
        automaticTab.kind = .yaml
        automaticFile.url = directory.appendingPathComponent("manual.md")
        automaticTab.updateKindAfterPathChange()
        try expect(automaticTab.kind == .yaml && !automaticTab.automaticKind, "path changes preserve an explicitly selected type")
        automaticTab.automaticKind = true
        automaticTab.updateKindAfterPathChange()
        try expect(automaticTab.kind == .markdown, "returning to automatic recognition uses the current path")

        let reader = ReaderWindow(supportDirectory: support)
        let window = try require(reader.window, "reader window exists")
        reader.showWindow(nil)
        window.contentView?.layoutSubtreeIfNeeded()
        defer { window.orderOut(nil) }

        reader.openURLs([markdownURL])
        try wait("open Markdown fixture") { window.representedURL?.lastPathComponent == markdownURL.lastPathComponent }
        try expect(try editor(in: window).string == markdown, "opened source text is exact")
        reader.toggleReading(nil)
        let firstReadingView = try readingView(in: window)
        try expect(firstReadingView.string.contains("阅读标题 😀"), "reading view displays the heading")
        try expect(!firstReadingView.string.contains("**needle**"), "reading view renders inline Markdown")
        try expect(!firstReadingView.string.contains("needle-hidden"), "hidden link destinations are absent from reading text")
        try expect(!firstReadingView.isEditable, "reading text is not editable")

        reader.showFind(nil)
        let search = try searchField(in: window)
        try expect(!search.isHiddenOrHasHiddenAncestor, "find bar is visible")
        try expect(try readingView(in: window) === firstReadingView, "opening find keeps the reading view")
        search.stringValue = "needle"
        reader.controlTextDidChange(Notification(name: NSControl.textDidChangeNotification, object: search))
        reader.findNext(nil)
        try wait("asynchronous reading search") {
            selectedText(firstReadingView) == "needle" && fields(in: window).contains { $0.stringValue.contains("/ 2 处") }
        }
        try expect(try readingView(in: window) === firstReadingView, "a reading search result does not switch to source editing")
        try expect(fields(in: window).contains { $0.stringValue.contains("/ 2 处") }, "reading search counts visible text, not hidden Markdown destinations")
        let firstMatch = firstReadingView.selectedRange()
        reader.findNext(nil)
        let secondMatch = firstReadingView.selectedRange()
        try expect(firstMatch != secondMatch && selectedText(firstReadingView) == "needle", "next match advances within the reading view")
        reader.findPrevious(nil)
        try expect(firstReadingView.selectedRange() == firstMatch, "previous match restores the prior reading match")

        let fieldEditor = try require(window.fieldEditor(true, for: search) as? NSTextView, "find field has a native field editor")
        let escaped = reader.control(search, textView: fieldEditor, doCommandBy: #selector(NSResponder.cancelOperation(_:)))
        try expect(escaped, "Esc is handled by the find field delegate")
        try expect(search.isHiddenOrHasHiddenAncestor, "Esc closes the find bar")
        try expect(try readingView(in: window) === firstReadingView, "Esc leaves the document in reading mode")
        try expect(firstReadingView.selectedRange() == firstMatch, "Esc preserves the current reading match")

        // Dispatch the same Objective-C action as an actual outline menu item;
        // no modal pop-up or private Swift member access is needed.
        let headingItem = NSMenuItem(title: "第二节", action: nil, keyEquivalent: "")
        headingItem.representedObject = (markdown as NSString).range(of: "## 第二节").location
        try expect(app.sendAction(NSSelectorFromString("jumpHeading:"), to: reader, from: headingItem), "native menu dispatch reaches the heading action")
        try expect(try readingView(in: window) === firstReadingView, "outline navigation keeps the reading view")
        let displayedHeading = (firstReadingView.string as NSString).range(of: "第二节")
        try expect(displayedHeading.location != NSNotFound && firstReadingView.selectedRange().location == displayedHeading.location, "outline source offset maps to the visible heading")

        reader.openURLs([jsonURL])
        try wait("open structured fixture") { window.representedURL?.lastPathComponent == jsonURL.lastPathComponent }
        try expect(try editor(in: window).string == json, "a newly opened structured document starts in source mode")
        reader.toggleReading(nil)
        let structuredView = try readingView(in: window)
        try expect(structuredView.string == json, "structured reading preserves source layout")
        let firstFont = structuredView.textStorage?.attribute(.font, at: 0, effectiveRange: nil) as? NSFont
        try expect(firstFont?.isFixedPitch == true, "structured reading uses a monospaced font")

        reader.previousTab(nil)
        try expect(window.representedURL?.lastPathComponent == markdownURL.lastPathComponent, "previous tab selects Markdown")
        _ = try readingView(in: window)
        reader.toggleReading(nil)
        try expect(try editor(in: window).string == markdown, "Markdown can independently return to source mode")
        reader.nextTab(nil)
        try expect(window.representedURL?.lastPathComponent == jsonURL.lastPathComponent, "next tab selects structured text")
        _ = try readingView(in: window)
        try expect(try readingView(in: window).string == json, "other tab retains its reading mode")
        reader.previousTab(nil)
        try expect(try editor(in: window).string == markdown, "returning to Markdown retains its source mode")

        reader.nextTab(nil)
        _ = try readingView(in: window)
        reader.newDocument(nil)
        try expect(window.representedURL == nil && (try editor(in: window)).string.isEmpty, "new document starts as a blank editable tab")
        reader.previousTab(nil)
        try expect(try readingView(in: window).string == json, "new document does not change the previous tab's reading mode")
        let numbered = NSMenuItem()
        numbered.tag = 0
        reader.selectNumberedTab(numbered)
        try expect(window.representedURL?.lastPathComponent == markdownURL.lastPathComponent, "numbered tab command selects the first document")
        _ = try editor(in: window)
        numbered.tag = 1
        reader.selectNumberedTab(numbered)
        _ = try readingView(in: window)
        numbered.tag = 8
        reader.selectNumberedTab(numbered)
        try expect(window.representedURL == nil && (try editor(in: window)).string.isEmpty, "last-tab command selects the new document")

        // Focus layout is independent of the document's reading/source mode.
        numbered.tag = 1
        reader.selectNumberedTab(numbered)
        reader.toggleFocus(nil)
        try expect(buttons(in: window).contains { $0.title == "退出专注" }, "focus layout has an explicit exit action")
        let focusedPreview = try readingView(in: window)
        try expect(reader.textView(focusedPreview, doCommandBy: #selector(NSResponder.cancelOperation(_:))), "Esc exits focus when find is closed")
        try expect(buttons(in: window).contains { $0.title == "专注" }, "focus exit restores normal layout")
        _ = try readingView(in: window)

        reader.openURLs([eofURL])
        try wait("open a document without final newline") { window.representedURL?.lastPathComponent == eofURL.lastPathComponent }
        reader.toggleReading(nil)
        let eofView = try readingView(in: window)
        try expect(eofView.string == eofText && !eofView.string.hasSuffix("\n"), "plain reading preserves the absence of a final newline")
        let eofItem = NSMenuItem(title: "文档末尾", action: nil, keyEquivalent: "")
        eofItem.representedObject = eofText.utf16.count
        try expect(app.sendAction(NSSelectorFromString("jumpHeading:"), to: reader, from: eofItem), "menu navigation accepts an EOF source anchor")
        try expect(eofView.selectedRange() == NSRange(location: eofText.utf16.count, length: 0), "EOF navigation selects the actual end of text")
        try expect(eofView.visibleRect.minY > 0, "EOF navigation does not jump back to the top")
        if let lm = eofView.layoutManager, let tc = eofView.textContainer {
            let marker = (eofView.string as NSString).range(of: "EOF_MARKER")
            let glyphs = lm.glyphRange(forCharacterRange: marker, actualCharacterRange: nil)
            let markerRect = lm.boundingRect(forGlyphRange: glyphs, in: tc)
                .offsetBy(dx: eofView.textContainerOrigin.x, dy: eofView.textContainerOrigin.y)
            try expect(eofView.visibleRect.intersects(markerRect), "the final line is visible after EOF navigation")
        } else { throw failure("EOF reading view has a text layout manager") }

        // Every file is clean; the remaining untitled document is empty. Closing
        // therefore exercises native lifecycle without a modal save prompt.
        window.performClose(nil)
        pump(for: 0.08)
        try expect(quitProbe.terminationRequests == 0, "closing the window does not request application termination")
        try expect(!window.isVisible, "the closed reader window is no longer visible")
        reader.showWindow(nil)
        window.contentView?.layoutSubtreeIfNeeded()
        try expect(window.representedURL == nil && (try editor(in: window)).string.isEmpty, "showWindow creates a fresh editable document after close")
        try expect(quitProbe.terminationRequests == 0, "reopening the window does not restart or terminate the application")
        try expect(Data(contentsOf: markdownURL) == Data(markdown.utf8), "reading workflows never change the Markdown source")
        try expect(Data(contentsOf: jsonURL) == Data(json.utf8), "structured reading never changes the configuration source")
        try expect(Data(contentsOf: eofURL) == Data(eofText.utf8), "EOF navigation never adds a newline to the source file")
        try expect(support.path.hasPrefix(directory.path), "all recovery state uses the isolated support directory")

        print("UIWorkflowTests: \(checks) checks passed")
    }

    private static func allViews(_ root: NSView) -> [NSView] {
        [root] + root.subviews.flatMap(allViews)
    }
    private static func views(in window: NSWindow) -> [NSView] {
        window.contentView.map(allViews) ?? []
    }
    private static func fields(in window: NSWindow) -> [NSTextField] {
        views(in: window).compactMap { $0 as? NSTextField }
    }
    private static func buttons(in window: NSWindow) -> [NSButton] {
        views(in: window).compactMap { $0 as? NSButton }
    }
    private static func editor(in window: NSWindow) throws -> NSTextView {
        try require(views(in: window).compactMap { $0 as? NSTextView }.first {
            !$0.isFieldEditor && $0.isEditable && $0.accessibilityLabel() == "文本编辑区"
        }, "active source editor is attached to the window")
    }
    private static func readingView(in window: NSWindow) throws -> NSTextView {
        try require(views(in: window).compactMap { $0 as? NSTextView }.first {
            !$0.isFieldEditor && !$0.isEditable && $0.accessibilityLabel() == "阅读区"
        }, "active reading view is attached to the window")
    }
    private static func searchField(in window: NSWindow) throws -> NSSearchField {
        try require(views(in: window).compactMap { $0 as? NSSearchField }.first, "find field exists in the window")
    }
    private static func selectedText(_ view: NSTextView) -> String? {
        let source = view.string as NSString, selected = view.selectedRange()
        guard selected.location != NSNotFound, selected.location <= source.length,
              selected.length <= source.length - selected.location else { return nil }
        return source.substring(with: selected)
    }
    private static func wait(_ description: String, timeout: TimeInterval = 6, until predicate: () -> Bool) throws {
        let deadline = ProcessInfo.processInfo.systemUptime + timeout
        while !predicate() {
            guard ProcessInfo.processInfo.systemUptime < deadline else { throw failure("Timed out: \(description)") }
            pump(for: 0.02)
        }
    }
    private static func pump(for interval: TimeInterval) {
        let deadline = Date(timeIntervalSinceNow: interval)
        repeat { _ = RunLoop.current.run(mode: .default, before: deadline) } while Date() < deadline
    }
    private static func require<T>(_ value: T?, _ description: String) throws -> T {
        guard let value = value else { throw failure(description) }
        return value
    }
    private static func expect(_ condition: @autoclosure () throws -> Bool, _ description: String) throws {
        guard try condition() else { throw failure(description) }
        checks += 1
    }
    private static func failure(_ description: String) -> NSError {
        NSError(domain: "UIWorkflowTests", code: 1, userInfo: [NSLocalizedDescriptionKey: description])
    }
}
