import AppKit
import Foundation

/// Integration checks for the Windows 0.3 reading/sharing behaviors implemented
/// with Mac controls. No real application support directory or source is used.
@main
struct MacParityWorkflowTests {
    private static var checks = 0

    static func main() {
        do { try run() }
        catch {
            FileHandle.standardError.write(Data(("MacParityWorkflowTests failed: " + error.localizedDescription + "\n").utf8))
            exit(1)
        }
    }
    static func run() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.prohibited)
        let domain = Bundle.main.bundleIdentifier ?? ProcessInfo.processInfo.processName
        UserDefaults.standard.setVolatileDomain([
            "recent": [String](), "positions": [String: [Double]](), "fontSize": 14.0,
            "dark": false, "followSystemAppearance": false, "readingWidth": 680.0,
            "wrapping": true, "lineNumbers": true, "restoreSession": true,
            "recoveryEnabled": true, "excludedDraftPaths": [String]()
        ], forName: UserDefaults.argumentDomain)
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-mac-parity-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer {
            UserDefaults.standard.removePersistentDomain(forName: domain)
            UserDefaults.standard.removeVolatileDomain(forName: UserDefaults.argumentDomain)
            try? FileManager.default.removeItem(at: root)
        }
        let markdownURL = root.appendingPathComponent("阅读默认.md")
        let paragraphs = (0..<15).map { "第 \($0) 段普通文字，用来验证阅读中的稳定定位与字号变化。\n" }.joined(separator: "\n")
        let markdown = "# 第一章\n\n开头文字 needle。\n\n" + paragraphs + "\n## 中间标题 😀\n\n需要保持位置的正文 **ANCHOR**。\n\n" + paragraphs + "\n## 最后标题\n\n尾部文字。\n"
        try Data(markdown.utf8).write(to: markdownURL)
        let cleanURL = root.appendingPathComponent("外部干净.txt")
        try Data("磁盘初始\n".utf8).write(to: cleanURL)
        let jsonURL = root.appendingPathComponent("格式.json")
        let json = "{\"number\":900719925474099312345,\"中文\":\"😀\",\"same\":1,\"same\":2}"
        try Data(json.utf8).write(to: jsonURL)

        let reader = ReaderWindow(supportDirectory: root.appendingPathComponent("Support"))
        let win = try require(reader.window, "main window exists")
        reader.showWindow(nil)
        win.contentView?.layoutSubtreeIfNeeded()
        defer { win.orderOut(nil) }
        try wait("initial restore") { sourceView(win).isEditable }

        reader.openURLs([markdownURL])
        try wait("Markdown defaults to reading") { win.representedURL?.lastPathComponent == markdownURL.lastPathComponent && isVisible(readingView(win)) }
        let read = readingView(win)
        try expect(read.string.contains("第一章") && !read.string.contains("**ANCHOR**"), "opening Markdown directly shows native rendered content")
        try expect(!isVisible(sourceView(win)), "default Markdown preview occupies the document area")
        try expect(sourceView(win).string == markdown, "rendering never changes the hidden source buffer")

        // Native menu dispatch uses source offsets, then font changes preserve
        // the same displayed heading rather than repeatedly drifting upward.
        let heading = NSMenuItem(title: "中间标题", action: nil, keyEquivalent: "")
        heading.representedObject = (markdown as NSString).range(of: "## 中间标题").location
        try expect(app.sendAction(NSSelectorFromString("jumpHeading:"), to: reader, from: heading), "heading command dispatches")
        let displayedHeading = (read.string as NSString).range(of: "中间标题 😀").location
        try expect(displayedHeading != NSNotFound && read.selectedRange().location == displayedHeading, "heading source maps to rendered heading")
        for _ in 0..<5 { reader.increaseFont(nil); pump(0.02) }
        try expect(read.selectedRange().location == displayedHeading, "five font increases keep the same heading anchor")
        for _ in 0..<5 { reader.decreaseFont(nil); pump(0.02) }
        try expect(read.selectedRange().location == displayedHeading, "font decreases do not accumulate anchor drift")
        reader.returnReadingPosition(nil)
        try expect(read.selectedRange().location < displayedHeading, "return command restores the pre-navigation position")

        // Splitting is an editing action: both source and presentation remain
        // attached, and edits asynchronously update only the shown preview.
        reader.toggleSplitPreview(nil)
        win.contentView?.layoutSubtreeIfNeeded()
        let source = sourceView(win)
        try expect(isVisible(source) && isVisible(read), "split mode shows the native editor and preview together")
        try expect(source.enclosingScrollView?.frame.width ?? 0 > 100 && read.enclosingScrollView?.frame.width ?? 0 > 100, "both split panes have usable width")
        try expect(source.enclosingScrollView?.superview === read.enclosingScrollView?.superview, "split panes share the native divider container")
        source.setSelectedRange(NSRange(location: source.string.utf16.count, length: 0))
        source.insertText("\n\n### 刚刚编辑\n\nIMMEDIATE_COPY_CONTENT\n", replacementRange: source.selectedRange())
        let editedMarkdown = source.string
        // Trigger copy immediately, before the debounce has rebuilt the split
        // preview. The clipboard API may be unavailable headlessly; the source
        // and selection checks do not depend on querying the global pasteboard.
        let sourceSelection = source.selectedRange()
        reader.copyReadingFull(nil)
        try wait("copy preparation flushes latest split text") { read.string.contains("IMMEDIATE_COPY_CONTENT") && statusFields(win).contains { $0.stringValue.contains("已复制全文") } }
        try expect(source.string == editedMarkdown, "copying full presentation never alters the source")
        try expect(source.selectedRange() == sourceSelection, "full copy keeps the source selection in split mode")
        try expect(try Data(contentsOf: markdownURL) == Data(markdown.utf8), "split edits and copying do not automatically save disk source")

        reader.toggleSplitPreview(nil)
        reader.toggleReading(nil)
        try wait("return to full preview") { isVisible(read) && !isVisible(source) }
        let selection = (read.string as NSString).range(of: "IMMEDIATE_COPY_CONTENT")
        read.setSelectedRange(selection)
        read.scrollRangeToVisible(selection)
        reader.copyReadingFull(nil)
        pump(0.25)
        try expect(read.selectedRange() == selection, "full reading copy keeps the user's selected content")
        try expect(source.string == editedMarkdown, "full reading copy still leaves source unchanged")

        // New names are unique and explicit source editing remains available.
        reader.newDocument(nil)
        let firstUntitled = win.title
        reader.newDocument(nil)
        try expect(firstUntitled.contains("新文件 1") && win.title.contains("新文件 2"), "new-document names increment without colliding")

        reader.openURLs([cleanURL])
        try wait("open clean external fixture") { win.representedURL?.lastPathComponent == cleanURL.lastPathComponent }
        let editor = sourceView(win)
        pump(0.25) // Initial on-open external check finishes before next activation.
        try Data("外部新内容\n".utf8).write(to: cleanURL, options: .atomic)
        reader.becameActive()
        try wait("clean document reloads from disk") { editor.string == "外部新内容\n" }
        editor.setSelectedRange(NSRange(location: 0, length: 0))
        editor.insertText("LOCAL_EDIT_", replacementRange: editor.selectedRange())
        let dirty = editor.string
        pump(0.15)
        try Data("另一程序更新\n".utf8).write(to: cleanURL, options: .atomic)
        reader.becameActive()
        try wait("dirty external conflict appears") { statusFields(win).contains { $0.stringValue.contains("当前修改已保留") } }
        try expect(editor.string == dirty, "external update does not replace unsaved source text")
        try expect(try String(contentsOf: cleanURL, encoding: .utf8) == "另一程序更新\n", "checking external changes does not overwrite the disk")

        reader.openURLs([jsonURL])
        try wait("open JSON formatting fixture") { win.representedURL?.lastPathComponent == jsonURL.lastPathComponent }
        let jsonEditor = sourceView(win)
        reader.formatDocument(nil)
        try wait("format JSON runs asynchronously") { jsonEditor.string != json }
        let formatted = jsonEditor.string
        try expect(formatted.contains("900719925474099312345") && formatted.components(separatedBy: "\"same\"").count == 3, "formatting retains big integers and duplicate keys")
        reader.undoEdit(nil)
        try expect(jsonEditor.string == json, "formatting is a single native undo step")
        reader.redoEdit(nil)
        try expect(jsonEditor.string == formatted, "formatted result supports native redo")
        try expect(try String(contentsOf: jsonURL, encoding: .utf8) == json, "formatting does not save the original file")

        // A local inline PNG must become an attachment in the actual reading
        // view while retaining the source-range metadata for navigation.
        let png = try smallPNG()
        let imageURL = root.appendingPathComponent("内嵌图片.md")
        let imageLine = "![小红点](data:image/png;base64," + png.base64EncodedString() + ")"
        let imageMarkdown = "# 图片标题\n\n" + imageLine + "\n\n图片后的正文 IMAGE_TAIL\n"
        try Data(imageMarkdown.utf8).write(to: imageURL)
        reader.openURLs([imageURL])
        try wait("open image Markdown") { win.representedURL?.lastPathComponent == imageURL.lastPathComponent && isVisible(readingView(win)) }
        let images = readingView(win)
        try wait("inline PNG loads asynchronously") { attachmentRanges(images).count == 1 }
        let imageRange = try require(attachmentRanges(images).first, "image attachment range")
        let mapped = (images.textStorage?.attribute(ReadingDocument.sourceRangeAttribute, at: imageRange.location, effectiveRange: nil) as? NSValue)?.rangeValue
        let sourceImage = (imageMarkdown as NSString).range(of: imageLine)
        try expect(mapped.map { NSIntersectionRange($0, sourceImage).length > 0 } == true, "loaded image retains its Markdown source block")
        try expect(images.string.contains("IMAGE_TAIL"), "image replacement preserves later reading text")
        try expect(try String(contentsOf: imageURL, encoding: .utf8) == imageMarkdown, "image loading never rewrites Markdown source")

        // Cancel must be observable before the background read can install a
        // document. The operation panel is real and synchronously attached.
        let cancelledURL = root.appendingPathComponent("取消打开.txt")
        let cancelledBytes = Data("CANCELLED_FILE_MUST_NOT_APPEAR\n".utf8)
        try cancelledBytes.write(to: cancelledURL)
        let beforeCancelURL = win.representedURL
        let beforeCancelCount = views(win).compactMap { $0 as? DraggableTabButton }.count
        reader.openURLs([cancelledURL])
        let progressPanel = try require(win.childWindows?.first { $0.title.hasPrefix("打开 ") }, "opening creates an attached progress panel")
        let cancelButton = try require(progressPanel.contentView.map(allViews)?.compactMap { $0 as? NSButton }.first { $0.title == "取消" }, "opening progress offers a cancel button")
        cancelButton.performClick(nil)
        pump(0.4)
        try expect(win.representedURL == beforeCancelURL, "cancelled opening does not select a new document")
        try expect(views(win).compactMap { $0 as? DraggableTabButton }.count == beforeCancelCount, "cancelled opening does not leave a tab")
        try expect(!progressPanel.isVisible, "cancelled operation removes its progress panel")
        try expect(try Data(contentsOf: cancelledURL) == cancelledBytes, "cancelling open never changes the target file")

        // Queue several image presentations without yielding to their completion
        // callbacks. Old callbacks must not install a cancelled source revision.
        reader.toggleReading(nil)
        let imageEditor = sourceView(win)
        let oldRevision = imageMarkdown + "\nOLD_PENDING_REVISION\n"
        imageEditor.insertText(oldRevision, replacementRange: NSRange(location: 0, length: imageEditor.string.utf16.count))
        reader.toggleReading(nil) // Starts a new image load for OLD_PENDING_REVISION.
        reader.toggleReading(nil)
        let newestRevision = imageMarkdown + "\nLATEST_REVISION_MUST_WIN\n"
        imageEditor.insertText(newestRevision, replacementRange: NSRange(location: 0, length: imageEditor.string.utf16.count))
        reader.toggleSplitPreview(nil)
        reader.increaseFont(nil); reader.increaseFont(nil); reader.decreaseFont(nil)
        try wait("latest image revision completes") {
            images.string.contains("LATEST_REVISION_MUST_WIN") && !images.string.contains("OLD_PENDING_REVISION") && attachmentRanges(images).count == 1
        }
        pump(0.35)
        try expect(images.string.contains("LATEST_REVISION_MUST_WIN") && !images.string.contains("OLD_PENDING_REVISION"), "cancelled image callbacks cannot replace a newer source revision")
        try expect(imageEditor.string == newestRevision, "rapid image mode and font changes preserve the latest source buffer")
        try expect(try Data(contentsOf: imageURL) == Data(imageMarkdown.utf8), "image revision races never write pending edits to disk")

        print("MacParityWorkflowTests: \(checks) checks passed")
    }

    private static func allViews(_ root: NSView) -> [NSView] { [root] + root.subviews.flatMap(allViews) }
    private static func views(_ win: NSWindow) -> [NSView] { win.contentView.map(allViews) ?? [] }
    private static func isVisible(_ view: NSView) -> Bool { !view.isHiddenOrHasHiddenAncestor }
    private static func sourceView(_ win: NSWindow) -> NSTextView {
        views(win).compactMap { $0 as? NSTextView }.first { $0.accessibilityLabel() == "文本编辑区" }!
    }
    private static func readingView(_ win: NSWindow) -> NSTextView {
        views(win).compactMap { $0 as? NSTextView }.first { $0.accessibilityLabel() == "阅读区" }!
    }
    private static func statusFields(_ win: NSWindow) -> [NSTextField] { views(win).compactMap { $0 as? NSTextField } }
    private static func attachmentRanges(_ view: NSTextView) -> [NSRange] {
        guard let text = view.textStorage else { return [] }
        var ranges: [NSRange] = []
        text.enumerateAttribute(.attachment, in: NSRange(location: 0, length: text.length)) { value, range, _ in
            if value is ReadingImageAttachment { ranges.append(range) }
        }
        return ranges
    }
    private static func smallPNG() throws -> Data {
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 3, pixelsHigh: 2, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { throw fail("PNG bitmap") }
        for y in 0..<2 { for x in 0..<3 { rep.setColor(.red, atX: x, y: y) } }
        return try require(rep.representation(using: .png, properties: [:]), "PNG encoded")
    }
    private static func wait(_ description: String, timeout: TimeInterval = 8, until predicate: () -> Bool) throws {
        let deadline = ProcessInfo.processInfo.systemUptime + timeout
        while !predicate() {
            if ProcessInfo.processInfo.systemUptime >= deadline { throw fail("Timed out: " + description) }
            pump(0.02)
        }
    }
    private static func pump(_ interval: TimeInterval) {
        let end = Date(timeIntervalSinceNow: interval)
        repeat { _ = RunLoop.current.run(mode: .default, before: end) } while Date() < end
    }
    private static func require<T>(_ value: T?, _ message: String) throws -> T {
        guard let value = value else { throw fail(message) }; return value
    }
    private static func expect(_ value: @autoclosure () throws -> Bool, _ message: String) throws {
        guard try value() else { throw fail(message) }; checks += 1
    }
    private static func fail(_ message: String) -> NSError { NSError(domain: "MacParityWorkflowTests", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
}
