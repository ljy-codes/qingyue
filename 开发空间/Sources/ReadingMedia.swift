import AppKit
import Foundation
import ImageIO

final class ReadingMediaTask {
    private let lock = NSLock()
    private var cancelled = false
    private var finished = false
    func cancel() { lock.lock(); cancelled = true; finished = true; lock.unlock() }
    var isFinished: Bool { lock.lock(); defer { lock.unlock() }; return finished }
    fileprivate func finish() { lock.lock(); finished = true; lock.unlock() }
    var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelled }
}

/// Compressed input is retained for the explicit image viewer. The reading view
/// and clipboard use the bounded decoded PNG, so neither triggers full decoding.
final class ReadingImageAttachment: NSTextAttachment {
    let pngData: Data
    let originalData: Data
    let pixelSize: NSSize
    let reference: String
    let alternativeText: String
    init(pngData: Data, originalData: Data, pixelSize: NSSize, reference: String, alt: String, width: CGFloat) {
        self.pngData = pngData; self.originalData = originalData; self.pixelSize = pixelSize
        self.reference = reference; self.alternativeText = alt
        super.init(data: pngData, ofType: "public.png")
        image = NSImage(data: pngData)
        resize(maxWidth: width)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }
    func resize(maxWidth: CGFloat) {
        let w = max(1, min(pixelSize.width, max(80, maxWidth)))
        bounds = NSRect(x: 0, y: -3, width: w, height: max(1, pixelSize.height * w / max(1, pixelSize.width)))
    }
}

enum ReadingMediaLoader {
    static let referenceAttribute = NSAttributedString.Key("qingyue.imageReference")
    static let altAttribute = NSAttributedString.Key("qingyue.imageAlt")
    static let maximumImages = 32
    static let maximumFileBytes = 8 * 1024 * 1024
    static let maximumTotalBytes = 32 * 1024 * 1024
    static let maximumImagePixels = 16_000_000
    static let maximumTotalPixels = 48_000_000
    private static let queue = DispatchQueue(label: "qingyue.reading-images", qos: .userInitiated)

    @discardableResult static func load(document: NSAttributedString, baseURL: URL?, maxWidth: CGFloat,
                                        completion: @escaping (NSAttributedString, [String]) -> Void) -> ReadingMediaTask {
        let task = ReadingMediaTask()
        struct Request { let range: NSRange; let reference: String; let alt: String; let attributes: [NSAttributedString.Key: Any] }
        var requests: [Request] = []
        document.enumerateAttributes(in: NSRange(location: 0, length: document.length)) { attrs, range, _ in
            guard let ref = attrs[referenceAttribute] as? String else { return }
            // Existing attachments may be resized without re-reading disk.
            if attrs[.attachment] != nil { return }
            requests.append(Request(range: range, reference: ref, alt: attrs[altAttribute] as? String ?? "", attributes: attrs))
        }
        let input = NSAttributedString(attributedString: document)
        queue.async {
            var warnings: [String] = [], totalBytes = 0, totalPixels = 0
            struct Loaded { let request: Request; let original: Data; let png: Data; let size: NSSize }
            var results: [Loaded] = []
            let deadline = ProcessInfo.processInfo.systemUptime + 5
            for (index, request) in requests.enumerated() {
                if task.isCancelled { warnings.append("图片加载已取消。"); break }
                if ProcessInfo.processInfo.systemUptime > deadline { warnings.append("图片准备超过 5 秒，未完成的图片保留说明，可重试。"); break }
                guard index < maximumImages else { warnings.append("每篇最多加载 32 张图片，后续图片保留说明。"); break }
                do {
                    let data = try readImage(request.reference, baseURL: baseURL)
                    guard data.count <= maximumFileBytes, totalBytes <= maximumTotalBytes - data.count else { throw error("图片数据量超过限制。") }
                    totalBytes += data.count
                    guard let source = CGImageSourceCreateWithData(data as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
                          let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
                          let w = (properties[kCGImagePropertyPixelWidth] as? NSNumber)?.intValue,
                          let h = (properties[kCGImagePropertyPixelHeight] as? NSNumber)?.intValue,
                          w > 0, h > 0, w <= maximumImagePixels / h else { throw error("图片损坏或解码像素超限。") }
                    let pixels = w * h
                    guard totalPixels <= maximumTotalPixels - pixels else { throw error("本篇图片总像素超过限制。") }
                    let thumbnailOptions: [CFString: Any] = [kCGImageSourceCreateThumbnailFromImageAlways: true,
                        kCGImageSourceCreateThumbnailWithTransform: true, kCGImageSourceThumbnailMaxPixelSize: min(4096, max(w, h)),
                        kCGImageSourceShouldCacheImmediately: true]
                    guard let cg = CGImageSourceCreateThumbnailAtIndex(source, 0, thumbnailOptions as CFDictionary) else { throw error("图片无法解码。") }
                    let out = NSMutableData()
                    guard let destination = CGImageDestinationCreateWithData(out, "public.png" as CFString, 1, nil) else { throw error("无法准备图片。") }
                    CGImageDestinationAddImage(destination, cg, nil)
                    guard CGImageDestinationFinalize(destination), out.length <= maximumTotalBytes else { throw error("解码图片过大。") }
                    totalPixels += pixels
                    results.append(Loaded(request: request, original: data, png: out as Data, size: NSSize(width: cg.width, height: cg.height)))
                } catch { warnings.append("图片“\(request.alt.isEmpty ? String(request.reference.prefix(80)) : request.alt)”：\(error.localizedDescription)") }
            }
            DispatchQueue.main.async {
                task.finish()
                guard !task.isCancelled else { completion(input, ["图片加载已取消。"]); return }
                let output = NSMutableAttributedString(attributedString: input)
                for result in results.reversed() {
                    let attachment = ReadingImageAttachment(pngData: result.png, originalData: result.original,
                        pixelSize: result.size, reference: result.request.reference, alt: result.request.alt, width: imageWidth(maxWidth, attributes: result.request.attributes))
                    let replacement = NSMutableAttributedString(attachment: attachment)
                    replacement.addAttributes(result.request.attributes, range: NSRange(location: 0, length: replacement.length))
                    replacement.addAttribute(.attachment, value: attachment, range: NSRange(location: 0, length: replacement.length))
                    output.replaceCharacters(in: result.request.range, with: replacement)
                }
                completion(output, warnings)
            }
        }
        return task
    }

    static func resizeAttachments(in document: NSAttributedString, maxWidth: CGFloat) {
        document.enumerateAttribute(.attachment, in: NSRange(location: 0, length: document.length)) { value, range, _ in
            let attributes = document.attributes(at: range.location, effectiveRange: nil)
            (value as? ReadingImageAttachment)?.resize(maxWidth: imageWidth(maxWidth, attributes: attributes))
        }
    }
    private static func imageWidth(_ width: CGFloat, attributes: [NSAttributedString.Key: Any]) -> CGFloat {
        if let style = attributes[.paragraphStyle] as? NSParagraphStyle,
           let cell = style.textBlocks.compactMap({ $0 as? NSTextTableBlock }).first {
            return max(80, width / CGFloat(max(1, cell.table.numberOfColumns)) - 16)
        }
        return width
    }

    private static func readImage(_ reference: String, baseURL: URL?) throws -> Data {
        if reference.lowercased().hasPrefix("data:") {
            let prefix = "data:image/png;base64,"
            guard reference.lowercased().hasPrefix(prefix), reference.utf8.count <= (maximumFileBytes * 4 / 3 + 128),
                  let data = Data(base64Encoded: String(reference.dropFirst(prefix.count)), options: [.ignoreUnknownCharacters]),
                  data.starts(with: [137,80,78,71,13,10,26,10]) else { throw error("仅支持有界的内嵌 PNG。") }
            return data
        }
        guard !reference.hasPrefix("//"), !reference.hasPrefix("\\\\") else { throw error("不自动加载网络图片。") }
        let decoded = reference.removingPercentEncoding ?? reference
        guard !decoded.hasPrefix("//"), !decoded.hasPrefix("\\\\") else { throw error("不自动加载网络图片。") }
        let url: URL
        if reference.lowercased().hasPrefix("file:") {
            guard let file = URL(string: reference), file.isFileURL, file.host == nil || file.host == "" || file.host == "localhost" else { throw error("不自动加载网络文件图片。") }
            url = file
        } else if decoded.hasPrefix("/") { url = URL(fileURLWithPath: decoded) }
        else {
            guard URLComponents(string: reference)?.scheme == nil else { throw error("不自动加载网络图片。") }
            guard let base = baseURL, base.isFileURL else { throw error("请先保存文档，再读取相对图片。") }
            url = base.deletingLastPathComponent().appendingPathComponent(decoded)
        }
        let resolved = url.standardizedFileURL.resolvingSymlinksInPath()
        let values = try resolved.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey, .volumeIsLocalKey])
        guard values.volumeIsLocal == true else { throw error("不自动加载网络盘图片。") }
        guard values.isRegularFile == true, let bytes = values.fileSize, bytes <= maximumFileBytes else { throw error("图片不是普通文件，或超过 8 MiB。") }
        let data = try Data(contentsOf: resolved, options: .mappedIfSafe)
        guard data.count <= maximumFileBytes else { throw error("图片超过 8 MiB。") }
        return data
    }

    private static func error(_ message: String) -> NSError { NSError(domain: "ReadingMedia", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
}
