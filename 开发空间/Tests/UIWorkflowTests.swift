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
            "readingWidth": 680.0, "wrapping": true, "lineNumbers": true,
            "restoreSession": true, "recoveryEnabled": true, "excludedDraftPaths": [String]()
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
        try wait("initial asynchronous workspace restore") { hasEditableSource(in: window) }

        reader.openURLs([markdownURL])
        try wait("open Markdown fixture") { window.representedURL?.lastPathComponent == markdownURL.lastPathComponent }
        try expect(views(in: window).compactMap { $0 as? NSTextView }.first { $0.accessibilityLabel() == "文本编辑区" }?.string == markdown, "opened source text is exact")
        if !isReading(in: window) { reader.toggleReading(nil) }
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
        try expect(firstReadingView.selectedRange().location <= firstMatch.location, "Esc returns to the reading location captured before finding")

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

        try testSessionAndSaveAll(in: directory, app: app)
        print("UIWorkflowTests: \(checks) checks passed")
    }

    /// Exercises the public commands against actual text views and real temporary
    /// files. No save panel, save/discard alert or real user recovery data is used.
    private static func testSessionAndSaveAll(in directory: URL, app: NSApplication) throws {
        let support = directory.appendingPathComponent("session-save-all-support", isDirectory: true)
        let firstURL = directory.appendingPathComponent("session-first.md")
        let secondURL = directory.appendingPathComponent("session-second.txt")
        let sourceLines = ["# 会话与保存", "", "原始内容", ""] +
            (0..<45).flatMap { ["第 \($0) 段，中文与 Emoji 😀。", ""] } +
            ["## 保留位置", "", "恢复后继续处理这一段。", ""] +
            (45..<65).flatMap { ["第 \($0) 段，正文。", ""] }
        let firstSource = sourceLines.joined(separator: "\n")
        let secondSource = "第二份文档 😀\n"
        let firstBytes = Data([0xEF, 0xBB, 0xBF]) + Data(firstSource.replacingOccurrences(of: "\n", with: "\r\n").utf8)
        try firstBytes.write(to: firstURL)
        try Data(secondSource.utf8).write(to: secondURL)
        let reader = ReaderWindow(supportDirectory: support)
        let window = try require(reader.window, "save-all reader window exists")
        reader.showWindow(nil)
        window.contentView?.layoutSubtreeIfNeeded()
        defer { window.orderOut(nil) }
        try wait("save-all workspace ready") { hasEditableSource(in: window) }
        reader.openURLs([firstURL])
        try wait("first save-all fixture opens") { window.representedURL?.path == firstURL.path }
        if isReading(in: window) { reader.toggleReading(nil) }
        let firstEditor = try editor(in: window)
        window.makeFirstResponder(firstEditor)
        firstEditor.breakUndoCoalescing()
        firstEditor.insertText("追加A", replacementRange: NSRange(location: firstEditor.string.utf16.count, length: 0))
        firstEditor.breakUndoCoalescing()
        try expect(window.isDocumentEdited && firstEditor.string == firstSource + "追加A", "actual editor insertion marks the first saved file dirty")

        reader.openURLs([secondURL])
        try wait("second save-all fixture opens") { window.representedURL?.path == secondURL.path }
        let secondEditor = try editor(in: window)
        window.makeFirstResponder(secondEditor)
        secondEditor.breakUndoCoalescing()
        secondEditor.insertText("追加B", replacementRange: NSRange(location: secondEditor.string.utf16.count, length: 0))
        secondEditor.breakUndoCoalescing()
        try expect(window.isDocumentEdited && secondEditor.string == secondSource + "追加B", "actual editor insertion marks the second saved file dirty")
        reader.saveAllDocuments(nil)
        try expect(window.representedURL?.path == secondURL.path, "save all restores the originally active document")
        let savedFirst = Data([0xEF, 0xBB, 0xBF]) + Data((firstSource + "追加A").replacingOccurrences(of: "\n", with: "\r\n").utf8)
        try expect(try Data(contentsOf: firstURL) == savedFirst, "save all writes the first file while preserving UTF-8 BOM and CRLF")
        try expect(try Data(contentsOf: secondURL) == Data((secondSource + "追加B").utf8), "save all writes the second file without changing its LF encoding")
        try expect(!window.isDocumentEdited, "active document is clean after save all")

        // A shared NSTextView must still route undo to the selected document's
        // own UndoManager after the Save All command temporarily visited each tab.
        reader.undoEdit(nil)
        try expect(try editor(in: window).string == secondSource, "second document undo remains independent after save all")
        reader.redoEdit(nil)
        try expect(try editor(in: window).string == secondSource + "追加B", "second document redo restores its own edit")
        reader.previousTab(nil)
        reader.undoEdit(nil)
        try expect(try editor(in: window).string == firstSource, "first document undo remains independent after save all")
        reader.redoEdit(nil)
        try expect(try editor(in: window).string == firstSource + "追加A", "first document redo restores its own edit")
        try expect(!window.isDocumentEdited, "redo back to saved content returns the first document to clean state")

        // Exercise both reorder commands while retaining the active document.
        reader.moveCurrentTabRight(nil)
        try expect(window.representedURL?.path == firstURL.path, "moving right preserves the active file")
        let numbered = NSMenuItem()
        numbered.tag = 0
        reader.selectNumberedTab(numbered)
        try expect(window.representedURL?.path == secondURL.path, "moving the first tab right changes visible tab order")
        numbered.tag = 1
        reader.selectNumberedTab(numbered)
        reader.moveCurrentTabLeft(nil)
        try expect(window.representedURL?.path == firstURL.path, "moving left preserves the active file")
        numbered.tag = 0
        reader.selectNumberedTab(numbered)
        try expect(window.representedURL?.path == firstURL.path, "moving left restores the first position")
        reader.moveCurrentTabRight(nil) // Persist the deliberately reversed order.
        reader.toggleReading(nil)
        let preview = try readingView(in: window)
        let heading = NSMenuItem(title: "保留位置", action: nil, keyEquivalent: "")
        heading.representedObject = (firstSource as NSString).range(of: "## 保留位置").location
        try expect(app.sendAction(NSSelectorFromString("jumpHeading:"), to: reader, from: heading), "session reading anchor uses the real outline action")
        try expect(preview.visibleRect.minY > 0, "session fixture has a nonzero reading position")
        try expect(reader.confirmQuit(), "clean files can quit without a modal save prompt")
        let sessionURL = support.appendingPathComponent("Session.json")
        let session = try JSONDecoder().decode(WorkspaceSession.self, from: Data(contentsOf: sessionURL))
        try expect(session.entries.compactMap(\.path) == [secondURL.path, firstURL.path], "quit persists the reordered session")
        let firstEntry = try require(session.entries.first(where: { $0.path == firstURL.path }), "session contains the first path")
        try expect(session.activeID == firstEntry.id && firstEntry.isReading, "quit persists the active document and its reading mode")
        try expect(firstEntry.sourceAnchor > 0, "quit persists a content anchor rather than only a pixel offset")
        let draftDirectory = support.appendingPathComponent("Drafts", isDirectory: true)
        let drafts = (try? FileManager.default.contentsOfDirectory(at: draftDirectory, includingPropertiesForKeys: nil)) ?? []
        try expect(drafts.filter { $0.pathExtension == "json" }.isEmpty, "clean saved files do not leave old source snapshots in recovery")
        window.orderOut(nil)

        // A clean session stores references and state, not a stale copy of source.
        // Change same-length wording so the persisted content anchor stays useful.
        let updatedFirst = (firstSource + "追加A").replacingOccurrences(of: "原始内容", with: "磁盘更新")
        let updatedBytes = Data([0xEF, 0xBB, 0xBF]) + Data(updatedFirst.replacingOccurrences(of: "\n", with: "\r\n").utf8)
        try updatedBytes.write(to: firstURL)
        let restored = ReaderWindow(supportDirectory: support)
        let restoredWindow = try require(restored.window, "restored session window exists")
        restored.showWindow(nil)
        restoredWindow.contentView?.layoutSubtreeIfNeeded()
        defer { restoredWindow.orderOut(nil) }
        try wait("ordered active reading session restores") {
            restoredWindow.representedURL?.path == firstURL.path && (try? readingView(in: restoredWindow)) != nil
        }
        let restoredPreview = try readingView(in: restoredWindow)
        try expect(restoredPreview.string.contains("磁盘更新") && !restoredPreview.string.contains("原始内容"), "saved session reloads the current disk content")
        let model = ReadingDocument.build(text: updatedFirst, kind: .markdown, palette: ReaderTheme(dark: true).palette,
                                          fontSize: 15, readingFontName: "PingFang SC", lineSpacing: 6)
        let restoredPoint = model.displayRange(forSourceOffset: firstEntry.sourceAnchor).location
        try expect(restoredPreview.selectedRange().location == restoredPoint, "restored reading position maps from the saved source anchor")
        try expect(restoredPreview.visibleRect.minY > 0, "restored reading session resumes below the first screen")
        numbered.tag = 0
        restored.selectNumberedTab(numbered)
        try expect(restoredWindow.representedURL?.path == secondURL.path, "relaunch restores the first reordered tab")
        try expect(try editor(in: restoredWindow).string == secondSource + "追加B", "other tab restores its independent source mode")
        numbered.tag = 1
        restored.selectNumberedTab(numbered)
        try expect(restoredWindow.representedURL?.path == firstURL.path && (try? readingView(in: restoredWindow)) != nil, "relaunch restores the second reordered tab and its reading mode")
        try expect(!restoredWindow.isDocumentEdited, "clean session restore does not make files dirty")

        restored.closeAllTabs(nil)
        try wait("close all hides the clean reader window") { !restoredWindow.isVisible }
        try expect(restored.confirmQuit(), "empty window can flush its cleared session without a prompt")
        let closed = try JSONDecoder().decode(WorkspaceSession.self, from: Data(contentsOf: sessionURL))
        try expect(closed.entries.isEmpty && closed.activeID == nil, "close all clears the persisted session")
        let blank = ReaderWindow(supportDirectory: support)
        let blankWindow = try require(blank.window, "blank relaunch window exists")
        blank.showWindow(nil)
        defer { blankWindow.orderOut(nil) }
        try wait("empty session relaunch becomes editable") { hasEditableSource(in: blankWindow) }
        try expect(blankWindow.representedURL == nil && (try editor(in: blankWindow)).string.isEmpty, "relaunch after close all has one blank editable document")
        try expect(try Data(contentsOf: firstURL) == updatedBytes, "session restore and close all never overwrite the externally updated first source")
        try expect(try Data(contentsOf: secondURL) == Data((secondSource + "追加B").utf8), "session restore and close all preserve the second saved source")
        // Prevent deferred writes after temporary test directories are removed.
        try expect(blank.confirmQuit(), "blank relaunch can finish persistence cleanly")
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
    private static func isReading(in window: NSWindow) -> Bool {
        views(in: window).compactMap { $0 as? NSTextView }.contains {
            !$0.isFieldEditor && !$0.isEditable && $0.accessibilityLabel() == "阅读区" && !$0.isHiddenOrHasHiddenAncestor
        }
    }
    private static func editor(in window: NSWindow) throws -> NSTextView {
        try require(views(in: window).compactMap { $0 as? NSTextView }.first {
            !$0.isFieldEditor && $0.isEditable && $0.accessibilityLabel() == "文本编辑区" && !$0.isHiddenOrHasHiddenAncestor
        }, "active source editor is attached to the window")
    }
    private static func hasEditableSource(in window: NSWindow) -> Bool {
        views(in: window).compactMap { $0 as? NSTextView }.contains {
            !$0.isFieldEditor && $0.isEditable && $0.accessibilityLabel() == "文本编辑区" && !$0.isHiddenOrHasHiddenAncestor
        }
    }
    private static func readingView(in window: NSWindow) throws -> NSTextView {
        try require(views(in: window).compactMap { $0 as? NSTextView }.first {
            !$0.isFieldEditor && !$0.isEditable && $0.accessibilityLabel() == "阅读区" && !$0.isHiddenOrHasHiddenAncestor
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
