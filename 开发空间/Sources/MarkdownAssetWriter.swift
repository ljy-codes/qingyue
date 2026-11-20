import AppKit
import Foundation
import ImageIO
import Darwin

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
        let info = try sourceURL.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey, .volumeIsLocalKey])
        guard sourceURL.isFileURL, info.isRegularFile == true, info.volumeIsLocal == true,
              let size = info.fileSize, size <= maxInputBytes else { throw MarkdownAssetWriterError.tooLarge }
        let handle = try FileHandle(forReadingFrom: sourceURL); defer { try? handle.close() }
        let values = try handle.read(upToCount: maxInputBytes + 1) ?? Data()
        guard values.count <= maxInputBytes else { throw MarkdownAssetWriterError.tooLarge }
        guard let source = CGImageSourceCreateWithData(values as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int, let height = properties[kCGImagePropertyPixelHeight] as? Int,
              width > 0, height > 0, width <= maxPixels / height else { throw MarkdownAssetWriterError.tooLarge }
        guard let cg = CGImageSourceCreateImageAtIndex(source, 0, nil) else { throw CocoaError(.fileReadCorruptFile) }
        return try write(cg: cg, suggestedName: sourceURL.deletingPathExtension().lastPathComponent, documentURL: documentURL)
    }

    static func write(image: NSImage, suggestedName: String = "image", documentURL: URL) throws -> MarkdownAssetWriteResult {
        guard image.isValid, image.size.width > 0, image.size.height > 0,
              image.size.width * image.size.height <= CGFloat(maxPixels) else { throw MarkdownAssetWriterError.tooLarge }
        for rep in image.representations where rep.pixelsWide > 0 && rep.pixelsHigh > 0 {
            guard rep.pixelsWide <= maxPixels / rep.pixelsHigh else { throw MarkdownAssetWriterError.tooLarge }
        }
        guard let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { throw CocoaError(.fileReadCorruptFile) }
        return try write(cg: cg, suggestedName: suggestedName, documentURL: documentURL)
    }

    private static func write(cg: CGImage, suggestedName: String, documentURL: URL) throws -> MarkdownAssetWriteResult {
        guard cg.height > 0, cg.width <= maxPixels / cg.height else { throw MarkdownAssetWriterError.tooLarge }
        let bitmap = NSBitmapImageRep(cgImage: cg)
        guard let png = bitmap.representation(using: .png, properties: [:]), png.count <= maxInputBytes else { throw MarkdownAssetWriterError.tooLarge }
        let directory = documentURL.deletingLastPathComponent().standardizedFileURL
        let assets = directory.appendingPathComponent(documentURL.deletingPathExtension().lastPathComponent + ".assets", isDirectory: true)
        if let attrs = try? FileManager.default.attributesOfItem(atPath: assets.path), attrs[.type] as? FileAttributeType != .typeDirectory { throw CocoaError(.fileWriteInvalidFileName) }
        try FileManager.default.createDirectory(at: assets, withIntermediateDirectories: true)
        let safe = sanitize(suggestedName)
        let target = assets.appendingPathComponent(safe + "-" + UUID().uuidString + ".png")
        let fd = Darwin.open(target.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0o600)
        guard fd >= 0 else { throw CocoaError(.fileWriteFileExists) }
        let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        do { try handle.write(contentsOf: png); try handle.close() }
        catch { try? handle.close(); try? FileManager.default.removeItem(at: target); throw error }
        let relative = assets.lastPathComponent + "/" + target.lastPathComponent
        var allowed = CharacterSet.urlPathAllowed
        allowed.remove(charactersIn: "()#?[]")
        let encoded = relative.addingPercentEncoding(withAllowedCharacters: allowed) ?? relative
        let alt = suggestedName.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "]", with: "\\]").replacingOccurrences(of: "[", with: "\\[").replacingOccurrences(of: "\n", with: " ")
        return MarkdownAssetWriteResult(fileURL: target, relativePath: relative, markdownLink: "![\(alt.isEmpty ? "图片" : alt)](\(encoded))")
    }

    private static func sanitize(_ value: String) -> String {
        let base = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let filtered = base.replacingOccurrences(of: "[^A-Za-z0-9_一-龥-]", with: "-", options: .regularExpression)
        return String((filtered.isEmpty ? "image" : filtered).prefix(80))
    }
}
