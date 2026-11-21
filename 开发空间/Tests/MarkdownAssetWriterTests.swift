import AppKit
import Foundation

@main
struct MarkdownAssetWriterTests {
    private static var checks = 0
    static func main() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-assets-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let document = root.appendingPathComponent("读写.md")
        try Data("# 图片\n".utf8).write(to: document)
        let image = NSImage(size: NSSize(width: 12, height: 8))
        image.lockFocus(); NSColor.systemBlue.setFill(); NSRect(origin: .zero, size: image.size).fill(); image.unlockFocus()
        let first = try MarkdownAssetWriter.write(image: image, suggestedName: "截图 1", documentURL: document)
        expect(first.fileURL.pathExtension == "png", "图片统一写为 PNG")
        expect(FileManager.default.fileExists(atPath: first.fileURL.path), "图片写入文档旁 assets 目录")
        expect(first.relativePath.hasPrefix("读写.assets/"), "生成文档相对图片路径")
        let second = try MarkdownAssetWriter.write(image: image, suggestedName: "截图 1", documentURL: document)
        expect(second.fileURL != first.fileURL, "重复图片不覆盖旧附件")
        let encoded = first.relativePath.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? first.relativePath
        expect(first.markdownLink.contains(encoded), "生成可直接插入的 Markdown 图片链接")
        let original = try Data(contentsOf: first.fileURL)
        _ = try MarkdownAssetWriter.write(sourceURL: first.fileURL, documentURL: document)
        expect(try! Data(contentsOf: first.fileURL) == original, "本地图片导入不覆盖已有附件")
        let linkedAssets = root.appendingPathComponent("linked.assets")
        try FileManager.default.createSymbolicLink(at: linkedAssets, withDestinationURL: root)
        do { _ = try MarkdownAssetWriter.write(image: image, documentURL: root.appendingPathComponent("linked.md")); fatalError("必须拒绝附件目录符号链接") } catch { checks += 1 }
        let oversized = root.appendingPathComponent("oversized.png")
        FileManager.default.createFile(atPath: oversized.path, contents: Data())
        let largeFile = try FileHandle(forWritingTo: oversized); try largeFile.truncate(atOffset: UInt64(MarkdownAssetWriter.maxInputBytes + 1)); try largeFile.close()
        do { _ = try MarkdownAssetWriter.write(sourceURL: oversized, documentURL: document); fatalError("必须读前拒绝大图片") } catch { checks += 1 }
        let special = try MarkdownAssetWriter.write(image: image, suggestedName: "a]b", documentURL: root.appendingPathComponent("notes(1).md"))
        expect(special.markdownLink.contains("notes%281%29.assets") && special.markdownLink.contains("a\\]b"), "文件名括号和说明方括号不会破坏Markdown")
        print("MarkdownAssetWriterTests: \(checks) checks passed")
    }
    private static func expect(_ value: @autoclosure () -> Bool, _ message: String) { guard value() else { fatalError(message) }; checks += 1 }
}
