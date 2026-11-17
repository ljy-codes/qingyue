import AppKit
import Foundation

struct MarkdownAssetWriteResult {
    let fileURL: URL
    let relativePath: String
    let markdownLink: String
}

enum MarkdownAssetWriterError: LocalizedError {
    case tooLarge

    var errorDescription: String? {
        "图片超过轻阅的大小或像素上限。"
    }
}

enum MarkdownAssetWriter {
    static let maxInputBytes = 8 * 1024 * 1024
    static let maxPixels = 16_000_000

    static func write(sourceURL: URL, documentURL: URL) throws -> MarkdownAssetWriteResult {
        let values = try Data(contentsOf: sourceURL, options: [.mappedIfSafe])
        guard values.count <= maxInputBytes else { throw MarkdownAssetWriterError.tooLarge }
        guard let image = NSImage(data: values) else { throw CocoaError(.fileReadCorruptFile) }
        return try write(image: image, suggestedName: sourceURL.deletingPathExtension().lastPathComponent, documentURL: documentURL)
    }

    static func write(image: NSImage, suggestedName: String = "image", documentURL: URL) throws -> MarkdownAssetWriteResult {
        guard image.isValid, image.size.width > 0, image.size.height > 0,
              image.size.width * image.size.height <= CGFloat(maxPixels) else { throw MarkdownAssetWriterError.tooLarge }
        guard let tiff = image.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiff),
              let png = bitmap.representation(using: .png, properties: [:]), png.count <= maxInputBytes else { throw MarkdownAssetWriterError.tooLarge }
        let directory = documentURL.deletingLastPathComponent()
        let assets = directory.appendingPathComponent(documentURL.deletingPathExtension().lastPathComponent + ".assets", isDirectory: true)
        try FileManager.default.createDirectory(at: assets, withIntermediateDirectories: true)
        let safe = sanitize(suggestedName)
        var index = 1
        var target = assets.appendingPathComponent(safe + ".png")
        while FileManager.default.fileExists(atPath: target.path) {
            index += 1; target = assets.appendingPathComponent("\(safe)-\(index).png")
        }
        try png.write(to: target, options: .atomic)
        let relative = target.path.replacingOccurrences(of: directory.path.hasSuffix("/") ? directory.path : directory.path + "/", with: "")
        let encoded = relative.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? relative
        return MarkdownAssetWriteResult(fileURL: target, relativePath: relative, markdownLink: "![\(suggestedName.isEmpty ? "图片" : suggestedName)](\(encoded))")
    }

    private static func sanitize(_ value: String) -> String {
        let base = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let filtered = base.replacingOccurrences(of: "[^A-Za-z0-9_一-龥-]", with: "-", options: .regularExpression)
        return String((filtered.isEmpty ? "image" : filtered).prefix(80))
    }
}
