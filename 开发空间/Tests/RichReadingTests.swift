import AppKit
import Foundation

@main
struct RichReadingTests {
    static var checks = 0
    static let palette = EditorPalette(background: .black, foreground: .white, muted: .gray, accent: .systemBlue, string: .systemGreen, number: .systemOrange, comment: .systemGray, key: .systemPurple)
    static func main() throws {
        _ = NSApplication.shared
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-rich-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let png = makePNG(), imageURL = root.appendingPathComponent("中文 图片.png"), docURL = root.appendingPathComponent("示例.md")
        try png.write(to: imageURL)
        let md = "# 图文 😀\n\n先看 ![中文图片](中文%20图片.png) 再继续。\n\n| 代码 | 说明 |\n| :- | -: |\n| `a|b` | **重点** |\n\n```swift\nlet x = \"😀\"\n```\n\n末尾内容。"
        let reading = build(md)
        expect(reading.attributedText.string.contains("a|b\n重点"), "Code pipes remain in one table cell")
        let table = tableBlocks(reading.attributedText)
        expect(table.count == 4, "Short delimiters and code pipes form a real two-column table")
        expect(table["0,0"]?.table === table["1,1"]?.table, "Cells share their native table")
        let source = md as NSString
        let marker = source.range(of: "![中文图片]")
        let originalBlock = reading.displayRange(forSourceOffset: marker.location)
        expect(reading.attributedText.attribute(ReadingMediaLoader.referenceAttribute, at: originalBlock.location + 3, effectiveRange: nil) != nil, "Image references are declared without loading")
        var loaded: NSAttributedString!, notices: [String] = []
        _ = ReadingMediaLoader.load(document: reading.attributedText, baseURL: docURL, maxWidth: 1) { loaded = $0; notices = $1 }
        try wait { loaded != nil }
        expect(notices.isEmpty, "Local percent-encoded Unicode image loads")
        let attachments = imageAttachments(loaded)
        expect(attachments.count == 1 && attachments[0].pngData.starts(with: [137,80,78,71]), "Loaded image is an embedded PNG attachment")
        expect(attachments[0].bounds.width <= 80 && attachments[0].bounds.height > 0, "Attachment display dimensions are bounded")
        let rebuilt = reading.replacingPresentation(loaded)
        let imageRange = rebuilt.displayRange(forSourceOffset: marker.location)
        expect((loaded.string as NSString).substring(with: imageRange).contains("\u{FFFC}"), "Source image maps to its new attachment block")
        expect(rebuilt.headings.first?.title == "图文 😀", "Attachment insertion preserves headings")
        for point in 0...loaded.length { expect(rebuilt.sourceRange(forDisplayOffset: point).location <= source.length, "All attachment document offsets safely map to source") }
        ReadingMediaLoader.resizeAttachments(in: loaded, maxWidth: 200)
        expect(attachments[0].bounds.width == 12, "Small images retain natural decoded size")
        let html = String(data: try ReadingShare.exportHTML(loaded, title: "<测试>"), encoding: .utf8)!
        expect(html.contains("<table>") && html.components(separatedBy: "<td ").count == 5, "HTML preserves true table cells")
        expect(html.contains("text-align:right") && html.contains("<strong>重点</strong>"), "HTML preserves alignment and emphasis")
        expect(html.contains("data:image/png;base64,") && !html.contains("src=\"file:"), "Export is self-contained and never refers to local paths")
        expect(html.contains("&lt;测试&gt;"), "HTML title is escaped")
        expect(html.contains("末尾内容"), "Trailing content survives rich export")
        expect(ReadingShare.plainText(loaded).contains("〔图片：中文图片〕"), "Plain text uses attachment alternate text")
        let tiny = loaded.attributedSubstring(from: NSRange(location: 0, length: 2))
        let tinyHTML = String(data: try ReadingShare.exportHTML(tiny), encoding: .utf8)!
        expect(!tinyHTML.contains("data:image") && !tinyHTML.contains("末尾内容"), "Selection export never widens to the full document")
        let clipboard = try ReadingShare.clipboard(loaded)
        expect(clipboard[.html] != nil && clipboard[.string] != nil && clipboard[.rtfd] != nil, "Clipboard offers HTML, plain text and native attachments")
        let board = NSPasteboard.withUniqueName()
        board.clearContents(); board.declareTypes(Array(clipboard.keys), owner: nil)
        for (type, data) in clipboard { board.setData(data, forType: type) }
        if board.data(forType: .rtfd) != nil {
            expect(board.string(forType: .string)?.contains("末尾内容") == true, "A dedicated pasteboard receives rich data")
        } else {
            // Some CLI sandboxes cannot contact the pasteboard server. The actual
            // encoded RTFD/HTML/string payloads are still validated above.
            print("Dedicated pasteboard service unavailable in this CLI environment")
        }
        board.releaseGlobally()
        let absolute = "![absolute](<\(imageURL.path)>)"
        let absoluteResult = try load(build(absolute).attributedText, base: nil)
        expect(imageAttachments(absoluteResult.0).count == 1, "Absolute image path containing spaces loads")
        let fileResult = try load(build("![file](\(imageURL.absoluteString))").attributedText, base: nil)
        expect(imageAttachments(fileResult.0).count == 1, "File URL loads")
        let dataResult = try load(build("![内嵌](data:image/png;base64,\(png.base64EncodedString()))").attributedText, base: nil)
        expect(imageAttachments(dataResult.0).count == 1, "Embedded PNG loads without filesystem access")
        for ref in ["https://example.test/a.png", "smb://server/a.png", "//server/a.png", "file://server/a.png", "%2F%2Fserver/a.png", "missing.png", "data:image/svg+xml;base64,PHN2Zz4="] {
            let result = try load(build("![failed](\(ref))").attributedText, base: docURL)
            expect(imageAttachments(result.0).isEmpty && !result.1.isEmpty, "Unsupported, missing or remote image remains a diagnosed placeholder")
        }
        let unsaved = try load(build("![relative](中文%20图片.png)").attributedText, base: nil)
        expect(!unsaved.1.isEmpty && imageAttachments(unsaved.0).isEmpty, "Unsaved relative images require a base path")
        let oversizedURL = root.appendingPathComponent("large.png")
        try Data(repeating: 0, count: ReadingMediaLoader.maximumFileBytes + 1).write(to: oversizedURL)
        let oversized = try load(build("![large](large.png)").attributedText, base: docURL)
        expect(imageAttachments(oversized.0).isEmpty && !oversized.1.isEmpty, "Compressed file byte limit is enforced before decoding")
        let adjacent = try load(build("![same](中文%20图片.png)![same](中文%20图片.png)").attributedText, base: docURL)
        expect(imageAttachments(adjacent.0).count == 2, "Identical adjacent image references never merge into one attachment")
        let repeated = build(String(repeating: "![small](中文%20图片.png)\n", count: 34))
        let limited = try load(repeated.attributedText, base: docURL)
        expect(imageAttachments(limited.0).count == 32 && !limited.1.isEmpty, "Per-document image count is bounded")
        var cancelledResult: NSAttributedString?
        let cancelled = ReadingMediaLoader.load(document: reading.attributedText, baseURL: docURL, maxWidth: 200) { cancelledResult = $0; _ = $1 }
        cancelled.cancel(); try wait { cancelledResult != nil }
        expect(imageAttachments(cancelledResult!).isEmpty, "Cancelled work cannot install decoded attachments")
        let diagram = NSTextAttachment(); diagram.image = NSImage(data: png)
        let diagramText = NSMutableAttributedString(attachment: diagram)
        diagramText.addAttribute(NSAttributedString.Key("qingyue.blockKind"), value: "diagram", range: NSRange(location: 0, length: 1))
        diagramText.append(NSAttributedString(string: "sequenceDiagram\nA->>B: hello"))
        let diagramHTML = String(data: try ReadingShare.exportHTML(diagramText), encoding: .utf8)!
        expect(diagramHTML.contains("sequenceDiagram") && !diagramHTML.contains("data:image"), "Diagram sharing preserves source without raster export")
        let unsafe = NSAttributedString(string: "<script>alert(1)</script> & 😀", attributes: [.font: NSFont.systemFont(ofSize: 14)])
        let escaped = String(data: try ReadingShare.exportHTML(unsafe), encoding: .utf8)!
        expect(escaped.contains("&lt;script&gt;") && !escaped.contains("<script>"), "Export escapes untrusted source text")
        do { _ = try ReadingShare.exportHTML(NSAttributedString(string: String(repeating: "a", count: 2_000_001))); fatalError("Expected export bound") }
        catch ReadingShareError.tooLarge { checks += 1 }
        expect(TextKind.detect(url: URL(fileURLWithPath: "/tmp/a.ts"), text: "") == .typescript && TextKind.detect(url: URL(fileURLWithPath: "/tmp/a.csv"), text: "") == .csv, "Additional language types are recognized")
        print("RichReadingTests: \(checks) checks passed")
    }
    static func build(_ text: String) -> ReadingDocument { ReadingDocument.build(text: text, kind: .markdown, palette: palette, fontSize: 16, readingFontName: "system", lineSpacing: 5) }
    static func load(_ text: NSAttributedString, base: URL?) throws -> (NSAttributedString, [String]) {
        var result: (NSAttributedString, [String])?
        _ = ReadingMediaLoader.load(document: text, baseURL: base, maxWidth: 600) { result = ($0, $1) }
        try wait { result != nil }; return result!
    }
    static func makePNG() -> Data {
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 12, pixelsHigh: 8, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        for y in 0..<8 { for x in 0..<12 { bitmap.setColor(NSColor(deviceRed: 0.1, green: 0.4, blue: 0.8, alpha: 1), atX: x, y: y) } }
        return bitmap.representation(using: .png, properties: [:])!
    }
    static func imageAttachments(_ text: NSAttributedString) -> [ReadingImageAttachment] {
        var result: [ReadingImageAttachment] = []
        text.enumerateAttribute(.attachment, in: NSRange(location: 0, length: text.length)) { value, _, _ in if let image = value as? ReadingImageAttachment { result.append(image) } }
        return result
    }
    static func tableBlocks(_ text: NSAttributedString) -> [String: NSTextTableBlock] {
        var result: [String: NSTextTableBlock] = [:]
        text.enumerateAttribute(.paragraphStyle, in: NSRange(location: 0, length: text.length)) { value, _, _ in
            if let style = value as? NSParagraphStyle { for case let block as NSTextTableBlock in style.textBlocks { result["\(block.startingRow),\(block.startingColumn)"] = block } }
        }
        return result
    }
    static func wait(_ ready: () -> Bool) throws {
        let deadline = Date(timeIntervalSinceNow: 12)
        while !ready() { if Date() > deadline { throw NSError(domain: "RichReadingTests", code: 1) }; _ = RunLoop.current.run(mode: .default, before: Date(timeIntervalSinceNow: 0.02)) }
    }
    static func expect(_ value: @autoclosure () -> Bool, _ message: String) { guard value() else { fatalError(message) }; checks += 1 }
}
