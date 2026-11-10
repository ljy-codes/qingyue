import AppKit
import Foundation

struct ReadingSegment {
    let sourceRange: NSRange
    let displayRange: NSRange
}

struct ReadingHeading {
    let title: String
    let sourceRange: NSRange
    let displayRange: NSRange
}

/// A reading view is a presentation of the source, never a second editable file.
/// Markdown mappings are intentionally block-level; literal text uses exact UTF-16
/// offsets. Pixel scroll positions can therefore be replaced by source anchors.
struct ReadingDocument {
    static let sourceRangeAttribute = NSAttributedString.Key("qingyue.sourceRange")
    static let richTextLimit = 500_000
    private static let headingLimit = 500

    let attributedText: NSAttributedString
    let segments: [ReadingSegment]
    let headings: [ReadingHeading]
    private let sourceText: NSString
    private let identityMapping: Bool

    /// Returns the rendered block containing this source point. A hidden Markdown
    /// marker maps to the next visible block. Identity presentations return a point.
    func displayRange(forSourceOffset offset: Int) -> NSRange {
        let point = Self.safeOffset(offset, in: sourceText)
        if identityMapping { return NSRange(location: point, length: 0) }
        if point == sourceText.length { return NSRange(location: attributedText.length, length: 0) }
        guard !segments.isEmpty else { return NSRange(location: 0, length: 0) }
        var low = 0, high = segments.count
        while low < high {
            let middle = (low + high) / 2
            if NSMaxRange(segments[middle].sourceRange) <= point { low = middle + 1 }
            else { high = middle }
        }
        return segments[min(low, segments.count - 1)].displayRange
    }

    /// Returns a source block for a rendered point. This must not be used as an
    /// exact replacement range: Markdown punctuation may have been removed.
    func sourceRange(forDisplayOffset offset: Int) -> NSRange {
        let point = Self.safeOffset(offset, in: attributedText.string as NSString)
        if identityMapping { return NSRange(location: point, length: 0) }
        if point == attributedText.length { return NSRange(location: sourceText.length, length: 0) }
        guard !segments.isEmpty else { return NSRange(location: 0, length: 0) }
        var low = 0, high = segments.count
        while low < high {
            let middle = (low + high) / 2
            if NSMaxRange(segments[middle].displayRange) <= point { low = middle + 1 }
            else { high = middle }
        }
        return segments[min(low, segments.count - 1)].sourceRange
    }

    static func build(text: String, kind: TextKind, palette: EditorPalette, fontSize: CGFloat,
                      readingFontName: String, lineSpacing: CGFloat) -> ReadingDocument {
        let source = text as NSString
        let size = fontSize.isFinite ? min(48, max(10, fontSize)) : 16
        let spacing = lineSpacing.isFinite ? min(40, max(0, lineSpacing)) : 6
        let rich = source.length <= richTextLimit

        if kind == .markdown && rich {
            let rendered = NSMutableAttributedString(attributedString: TextFeatures.markdownPreview(text, palette: palette, fontSize: size))
            applyReadingStyle(rendered, fontName: readingFontName, lineSpacing: spacing)
            var mapped: [ReadingSegment] = []
            rendered.enumerateAttribute(sourceRangeAttribute, in: NSRange(location: 0, length: rendered.length)) { value, display, _ in
                guard let value = value as? NSValue else { return }
                let raw = value.rangeValue
                guard raw.location != NSNotFound, raw.location >= 0, raw.location <= source.length,
                      raw.length >= 0, raw.length <= source.length - raw.location else { return }
                mapped.append(ReadingSegment(sourceRange: raw, displayRange: display))
            }
            let first = ReadingDocument(attributedText: rendered.copy() as! NSAttributedString, segments: mapped,
                                        headings: [], sourceText: source, identityMapping: false)
            let headings = TextFeatures.headings(text).prefix(headingLimit).map {
                let display = first.displayRange(forSourceOffset: $0.range.location)
                let visibleTitle = (rendered.string as NSString).substring(with: display).trimmingCharacters(in: .whitespacesAndNewlines)
                return ReadingHeading(title: visibleTitle.isEmpty ? $0.title : String(visibleTitle.prefix(160)), sourceRange: $0.range, displayRange: display)
            }
            return ReadingDocument(attributedText: first.attributedText, segments: mapped, headings: headings,
                                   sourceText: source, identityMapping: false)
        }

        let structured = kind != .plain && kind != .markdown
        let font = structured ? NSFont.monospacedSystemFont(ofSize: size, weight: .regular)
            : bodyFont(readingFontName, size: size)
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineSpacing = structured ? min(spacing, 4) : spacing
        paragraph.paragraphSpacing = structured ? 0 : 4
        let rendered = NSTextStorage(string: text, attributes: [.font: font, .foregroundColor: palette.foreground, .paragraphStyle: paragraph])
        if rich && structured { TextFeatures.highlight(rendered, kind: kind, palette: palette, font: font) }
        let all = NSRange(location: 0, length: source.length)
        let mapped = source.length == 0 ? [] : [ReadingSegment(sourceRange: all, displayRange: all)]
        if source.length > 0 { rendered.addAttribute(sourceRangeAttribute, value: NSValue(range: all), range: all) }
        let nodes = rich ? literalHeadings(text, kind: kind) : []
        return ReadingDocument(attributedText: NSAttributedString(attributedString: rendered), segments: mapped,
                               headings: nodes.map { ReadingHeading(title: $0.title, sourceRange: $0.range, displayRange: $0.range) },
                               sourceText: source, identityMapping: true)
    }

    /// Rebuild block positions after attachments or native diagrams replace
    /// placeholders. Source ranges remain authoritative; source text never changes.
    func replacingPresentation(_ attributed: NSAttributedString) -> ReadingDocument {
        if identityMapping {
            return ReadingDocument(attributedText: attributed, segments: segments, headings: headings, sourceText: sourceText, identityMapping: true)
        }
        var mapped: [ReadingSegment] = []
        attributed.enumerateAttribute(Self.sourceRangeAttribute, in: NSRange(location: 0, length: attributed.length)) { value, display, _ in
            guard let raw = (value as? NSValue)?.rangeValue, raw.location != NSNotFound,
                  raw.location >= 0, raw.location <= sourceText.length, raw.length >= 0,
                  raw.length <= sourceText.length - raw.location else { return }
            mapped.append(ReadingSegment(sourceRange: raw, displayRange: display))
        }
        let base = ReadingDocument(attributedText: attributed, segments: mapped, headings: [], sourceText: sourceText, identityMapping: false)
        let nodes = headings.map { ReadingHeading(title: $0.title, sourceRange: $0.sourceRange,
                                                 displayRange: base.displayRange(forSourceOffset: $0.sourceRange.location)) }
        return ReadingDocument(attributedText: attributed, segments: mapped, headings: nodes, sourceText: sourceText, identityMapping: false)
    }

    private static func safeOffset(_ offset: Int, in text: NSString) -> Int {
        let clamped = min(text.length, max(0, offset))
        guard clamped > 0, clamped < text.length else { return clamped }
        let before = text.character(at: clamped - 1), after = text.character(at: clamped)
        return (0xD800...0xDBFF).contains(before) && (0xDC00...0xDFFF).contains(after) ? clamped - 1 : clamped
    }

    private static func bodyFont(_ name: String, size: CGFloat) -> NSFont {
        NSFont(name: name, size: size) ?? NSFont.systemFont(ofSize: size)
    }

    private static func applyReadingStyle(_ text: NSMutableAttributedString, fontName: String, lineSpacing: CGFloat) {
        let all = NSRange(location: 0, length: text.length)
        // Inspect one attribute at a time so the source-map attribute does not
        // force an identical font/paragraph conversion for every short line.
        text.enumerateAttribute(.font, in: all) { value, range, _ in
            if let old = value as? NSFont,
               !NSFontManager.shared.traits(of: old).contains(.fixedPitchFontMask) {
                let font = bodyFont(fontName, size: old.pointSize)
                let traits = NSFontManager.shared.traits(of: old).intersection([.boldFontMask, .italicFontMask])
                text.addAttribute(.font, value: NSFontManager.shared.convert(font, toHaveTrait: traits), range: range)
            }
        }
        text.enumerateAttribute(.paragraphStyle, in: all) { value, range, _ in
            if let style = value as? NSParagraphStyle,
               let changed = style.mutableCopy() as? NSMutableParagraphStyle {
                changed.lineSpacing = lineSpacing
                text.addAttribute(.paragraphStyle, value: changed, range: range)
            }
        }
    }

    private static func literalHeadings(_ text: String, kind: TextKind) -> [(title: String, range: NSRange)] {
        if kind == .json || kind == .jsonc { return jsonKeys(text, comments: kind == .jsonc) }
        let pattern: String
        switch kind {
        case .plain:
            pattern = #"^\s*(?:第[〇零一二三四五六七八九十百千万两0-9]+[章节回卷部篇](?:\s*.*)?|(?:Chapter|Part)\s+[0-9IVXLCDM]+(?:\b.*)?)\s*$"#
        case .ini: pattern = #"^[ \t]*\[[^\]\r\n]+\][ \t]*(?:[#;].*)?$"#
        case .yaml: pattern = #"^[A-Za-z_\p{L}][^:#\r\n]*:[ \t]*(?:#.*)?$"#
        default: return []
        }
        guard let expression = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else { return [] }
        var output: [(String, NSRange)] = []
        text.enumerateSubstrings(in: text.startIndex..<text.endIndex, options: .byLines) { substring, range, _, stop in
            let line = substring ?? "", ns = line as NSString
            if expression.firstMatch(in: line, range: NSRange(location: 0, length: ns.length)) != nil {
                output.append((String(line.trimmingCharacters(in: .whitespaces).prefix(160)), NSRange(range, in: text)))
                if output.count >= headingLimit { stop = true }
            }
        }
        return output
    }

    /// A lexical key outline, not a second JSON parser. Strings and JSONC comments
    /// are skipped correctly, and the source is never reformatted or converted.
    private static func jsonKeys(_ text: String, comments: Bool) -> [(title: String, range: NSRange)] {
        let units = Array(text.utf16), source = text as NSString
        var result: [(String, NSRange)] = [], index = 0
        while index < units.count && result.count < headingLimit {
            if comments, units[index] == 47, index + 1 < units.count {
                if units[index + 1] == 47 { index += 2; while index < units.count && units[index] != 10 && units[index] != 13 { index += 1 }; continue }
                if units[index + 1] == 42 {
                    index += 2
                    while index < units.count {
                        if units[index] == 42 && index + 1 < units.count && units[index + 1] == 47 { index += 2; break }
                        index += 1
                    }
                    continue
                }
            }
            guard units[index] == 34 else { index += 1; continue }
            let start = index; index += 1
            var closed = false
            while index < units.count {
                if units[index] == 92 { index = min(units.count, index + 2); continue }
                if units[index] == 34 { index += 1; closed = true; break }
                index += 1
            }
            guard closed else { break }
            var after = index
            while after < units.count && [9, 10, 13, 32].contains(units[after]) { after += 1 }
            if after < units.count && units[after] == 58 {
                let token = source.substring(with: NSRange(location: start, length: index - start))
                let decoded = (try? JSONSerialization.jsonObject(with: Data(("[" + token + "]").utf8))) as? [String]
                let title = decoded?.first ?? String(token.dropFirst().dropLast())
                result.append((String(title.prefix(160)), NSRange(location: start, length: index - start)))
            }
        }
        return result
    }
}
