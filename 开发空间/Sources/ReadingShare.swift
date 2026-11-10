import AppKit
import Foundation

enum ReadingShareError: LocalizedError {
    case tooLarge
    case conversion
    var errorDescription: String? {
        switch self { case .tooLarge: return "图文内容超过 20 MiB，请分段复制或导出。"; case .conversion: return "无法生成图文剪贴板，请尝试复制纯文本。" }
    }
}

/// Serializes only the supplied attributed range. Callers choose the selection,
/// full document, code block or table; this layer never expands that range.
enum ReadingShare {
    static let maximumHTMLBytes = 20 * 1024 * 1024

    static func clipboard(_ input: NSAttributedString) throws -> [NSPasteboard.PasteboardType: Data] {
        let text = sharingText(input)
        let html = try exportHTML(text, title: "轻阅图文")
        let range = NSRange(location: 0, length: text.length)
        var result: [NSPasteboard.PasteboardType: Data] = [.html: html, .string: Data(plainText(text).utf8)]
        // RTFD carries native attachments for Mac applications. HTML carries
        // embedded PNGs for web-aware targets. Recipients choose their format.
        if let rtfd = try? text.data(from: range, documentAttributes: [.documentType: NSAttributedString.DocumentType.rtfd]), rtfd.count <= maximumHTMLBytes {
            result[.rtfd] = rtfd
        }
        if let rtf = try? text.data(from: range, documentAttributes: [.documentType: NSAttributedString.DocumentType.rtf]), rtf.count <= maximumHTMLBytes {
            result[.rtf] = rtf
        }
        return result
    }

    static func plainText(_ input: NSAttributedString) -> String {
        let text = sharingText(input)
        let output = NSMutableString(string: text.string)
        var replacements: [(NSRange, String)] = []
        text.enumerateAttribute(.attachment, in: NSRange(location: 0, length: text.length)) { value, range, _ in
            guard value != nil else { return }
            let alt = (value as? ReadingImageAttachment)?.alternativeText
                ?? text.attribute(ReadingMediaLoader.altAttribute, at: range.location, effectiveRange: nil) as? String
                ?? "图形"
            replacements.append((range, alt.isEmpty ? "〔图片〕" : "〔图片：\(alt)〕"))
        }
        for (range, replacement) in replacements.reversed() { output.replaceCharacters(in: range, with: replacement) }
        return output as String
    }

    static func exportHTML(_ input: NSAttributedString, title: String = "轻阅文档") throws -> Data {
        let text = sharingText(input)
        guard text.length <= 2_000_000 else { throw ReadingShareError.tooLarge }
        var body = ""
        var bodyBytes = 0
        let ns = text.string as NSString
        var offset = 0
        var activeTable: NSTextTable?
        var activeRow = -1
        func add(_ value: String) throws {
            bodyBytes += value.utf8.count
            body += value
            if bodyBytes > maximumHTMLBytes { throw ReadingShareError.tooLarge }
        }
        func closeTable() throws {
            if activeTable != nil { try add("</tr></tbody></table>"); activeTable = nil; activeRow = -1 }
        }
        while offset < ns.length {
            let paragraph = ns.paragraphRange(for: NSRange(location: offset, length: 0))
            let range = NSRange(location: offset, length: NSMaxRange(paragraph) - offset)
            let style = text.attribute(.paragraphStyle, at: offset, effectiveRange: nil) as? NSParagraphStyle
            let block = style?.textBlocks.compactMap { $0 as? NSTextTableBlock }.first
            var contentRange = range
            // Paragraph terminators are represented by the surrounding element.
            while contentRange.length > 0 && [10, 13].contains(ns.character(at: NSMaxRange(contentRange) - 1)) { contentRange.length -= 1 }
            let inner = try inlineHTML(text, range: contentRange)
            if let block = block {
                if activeTable !== block.table {
                    try closeTable(); try add("<table><tbody><tr>"); activeTable = block.table; activeRow = block.startingRow
                } else if activeRow != block.startingRow { try add("</tr><tr>"); activeRow = block.startingRow }
                let align: String
                switch style?.alignment { case .center?: align = "center"; case .right?: align = "right"; default: align = "left" }
                try add("<td style=\"text-align:\(align)\">\(inner)</td>")
            } else {
                try closeTable()
                let kind = text.attribute(NSAttributedString.Key("qingyue.blockKind"), at: offset, effectiveRange: nil) as? String ?? "paragraph"
                if kind == "code" { try add("<pre><code>\(inner)</code></pre>") }
                else if kind == "quote" { try add("<blockquote>\(inner)</blockquote>") }
                else if kind == "heading" { try add("<h2>\(inner)</h2>") }
                else { try add("<p>\(inner.isEmpty ? "<br>" : inner)</p>") }
            }
            offset = NSMaxRange(range)
        }
        try closeTable()
        let html = """
        <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>\(escape(title))</title><style>body{max-width:1000px;margin:32px auto;padding:0 24px;font-family:system-ui,sans-serif;line-height:1.7;color:#243044;background:#fff}p{white-space:pre-wrap}table{border-collapse:collapse;width:100%;margin:16px 0}td{border:1px solid #aab3bf;padding:8px;vertical-align:top;overflow-wrap:anywhere}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#f1f3f6;padding:12px}blockquote{border-left:3px solid #789;padding-left:16px}img{max-width:100%;height:auto}span{overflow-wrap:anywhere}</style></head><body>\(body)</body></html>
        """
        let data = Data(html.utf8)
        guard data.count <= maximumHTMLBytes else { throw ReadingShareError.tooLarge }
        return data
    }

    private static func inlineHTML(_ text: NSAttributedString, range: NSRange) throws -> String {
        var result = ""
        var resultBytes = 0
        var failure: Error?
        text.enumerateAttributes(in: range) { attrs, span, stop in
            let value: String
            if let attachment = attrs[.attachment] as? NSTextAttachment {
                let png: Data?
                if let image = attachment as? ReadingImageAttachment { png = image.pngData }
                else if let image = attachment.image, let tiff = image.tiffRepresentation,
                        let bitmap = NSBitmapImageRep(data: tiff) { png = bitmap.representation(using: .png, properties: [:]) }
                else { png = nil }
                if let png = png, png.count <= maximumHTMLBytes {
                    let alt = attrs[ReadingMediaLoader.altAttribute] as? String ?? "图片"
                    value = "<img alt=\"\(escape(alt))\" src=\"data:image/png;base64,\(png.base64EncodedString())\">"
                } else { value = "〔无法复制图片〕" }
            } else {
                var rendered = escape((text.string as NSString).substring(with: span))
                var css: [String] = []
                if let font = attrs[.font] as? NSFont {
                    let traits = NSFontManager.shared.traits(of: font)
                    if traits.contains(.boldFontMask) { rendered = "<strong>\(rendered)</strong>" }
                    if traits.contains(.italicFontMask) { rendered = "<em>\(rendered)</em>" }
                    if traits.contains(.fixedPitchFontMask) { css.append("font-family:monospace") }
                    css.append("font-size:\(Int(font.pointSize.rounded()))pt")
                }
                if let strike = attrs[.strikethroughStyle] as? Int, strike != 0 { rendered = "<del>\(rendered)</del>" }
                // Avoid dark-theme near-white text becoming invisible in a light
                // destination. Semantic emphasis survives without hardcoded ink.
                if let link = attrs[.link] as? URL, let scheme = link.scheme?.lowercased(), ["https", "http", "mailto"].contains(scheme) {
                    rendered = "<a href=\"\(escape(link.absoluteString))\">\(rendered)</a>"
                }
                value = css.isEmpty ? rendered : "<span style=\"\(css.joined(separator: ";"))\">\(rendered)</span>"
            }
            resultBytes += value.utf8.count
            result += value
            if resultBytes > maximumHTMLBytes { failure = ReadingShareError.tooLarge; stop.pointee = true }
        }
        if let failure = failure { throw failure }
        return result
    }

    private static func sharingText(_ input: NSAttributedString) -> NSAttributedString {
        let output = NSMutableAttributedString(attributedString: input)
        var diagrams: [NSRange] = []
        input.enumerateAttribute(.attachment, in: NSRange(location: 0, length: input.length)) { value, range, _ in
            guard value != nil else { return }
            if input.attribute(NSAttributedString.Key("qingyue.blockKind"), at: range.location, effectiveRange: nil) as? String == "diagram" { diagrams.append(range) }
        }
        // Diagrams keep selectable source immediately below their presentation.
        // Export that source, not an undocumented raster snapshot of the diagram.
        for range in diagrams.reversed() { output.replaceCharacters(in: range, with: "") }
        if output.length > 0 {
            output.addAttribute(.foregroundColor, value: NSColor.black, range: NSRange(location: 0, length: output.length))
            output.removeAttribute(.backgroundColor, range: NSRange(location: 0, length: output.length))
        }
        return output
    }

    private static func escape(_ value: String) -> String {
        value.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&#39;")
    }
}
