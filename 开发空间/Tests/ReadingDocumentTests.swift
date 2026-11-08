import AppKit
import Foundation

@main
struct ReadingDocumentTests {
    static var checks = 0
    static let palette = EditorPalette(background: .black, foreground: .white, muted: .gray, accent: .systemBlue,
                                       string: .systemGreen, number: .systemOrange, comment: .systemGray, key: .systemPurple)

    static func main() {
        let markdown = "# 第一章 😀\n\n正文 **重点文字**，查看[网站](https://example.test/hidden-url)。\n\n## 表格\n\n| 名称 | 值 |\n| --- | --- |\n| 中文😀 | **正常** |\n\n```json\n{\"😀\":123}\n```\n\n结尾。"
        let reading = build(markdown, .markdown)
        expect(reading.attributedText.string.contains("正文 重点文字") && !reading.attributedText.string.contains("**重点文字**"), "Reading search uses visible text rather than Markdown punctuation")
        expect(!reading.attributedText.string.contains("hidden-url"), "Hidden link addresses are not searchable visible text")
        expect(reading.headings.map(\.title) == ["第一章 😀", "表格"], "Outline shares rendered Markdown headings")
        for node in reading.headings {
            expect(valid(node.sourceRange, in: markdown), "Source heading range valid")
            expect(valid(node.displayRange, in: reading.attributedText.string), "Displayed heading range valid")
            expect((reading.attributedText.string as NSString).substring(with: node.displayRange).contains(node.title), "Heading jumps to its rendered heading")
        }
        expect(!reading.segments.isEmpty, "Markdown has block mappings")
        var previousSource = 0, previousDisplay = 0
        for segment in reading.segments {
            expect(valid(segment.sourceRange, in: markdown) && valid(segment.displayRange, in: reading.attributedText.string), "Every mapping has valid Unicode ranges")
            expect(segment.sourceRange.location >= previousSource && segment.displayRange.location >= previousDisplay, "Mappings remain ordered")
            previousSource = NSMaxRange(segment.sourceRange); previousDisplay = NSMaxRange(segment.displayRange)
            let range = reading.sourceRange(forDisplayOffset: segment.displayRange.location)
            expect(range == segment.sourceRange, "Displayed block returns its original block")
        }
        expect(previousDisplay == reading.attributedText.length, "Every displayed character belongs to a mapped block")
        let sourceBody = (markdown as NSString).range(of: "**重点文字**")
        let bodyDisplay = reading.displayRange(forSourceOffset: sourceBody.location)
        expect((reading.attributedText.string as NSString).substring(with: bodyDisplay).contains("重点文字"), "Source anchor lands in the same rendered paragraph")
        let sourceTable = (markdown as NSString).range(of: "| 中文😀 |")
        let tableDisplay = reading.displayRange(forSourceOffset: sourceTable.location)
        expect((reading.attributedText.string as NSString).substring(with: tableDisplay).contains("名称\n值\n中文😀\n正常"), "Every source row maps to the containing native table")
        expect(reading.sourceRange(forDisplayOffset: tableDisplay.location).location == (markdown as NSString).range(of: "| 名称 |").location, "Table navigation is explicit block mapping")
        expect(reading.displayRange(forSourceOffset: (markdown as NSString).length) == NSRange(location: reading.attributedText.length, length: 0), "Source end maps to display end")
        expect(reading.sourceRange(forDisplayOffset: reading.attributedText.length) == NSRange(location: (markdown as NSString).length, length: 0), "Display end maps to source end")
        expect(reading.displayRange(forSourceOffset: Int.max) == reading.displayRange(forSourceOffset: (markdown as NSString).length), "Unbounded source offsets clamp safely")
        expect(reading.sourceRange(forDisplayOffset: Int.min) == reading.sourceRange(forDisplayOffset: 0), "Negative display offsets clamp safely")
        for offset in 0...(markdown as NSString).length {
            expect(valid(reading.displayRange(forSourceOffset: offset), in: reading.attributedText.string), "All source offsets produce safe display ranges")
        }
        for offset in 0...reading.attributedText.length {
            expect(valid(reading.sourceRange(forDisplayOffset: offset), in: markdown), "All display offsets produce safe source ranges")
        }

        let alternate = ReadingDocument.build(text: markdown, kind: .markdown, palette: palette, fontSize: 24,
                                             readingFontName: "Songti SC", lineSpacing: 11)
        expect(alternate.attributedText.string == reading.attributedText.string, "Font and spacing changes preserve visible text")
        expect(alternate.segments.map { $0.sourceRange } == reading.segments.map { $0.sourceRange }
               && alternate.segments.map { $0.displayRange } == reading.segments.map { $0.displayRange }, "Layout preferences never alter semantic mappings")
        let styledRange = (alternate.attributedText.string as NSString).range(of: "重点文字")
        expect((alternate.attributedText.attribute(.font, at: styledRange.location, effectiveRange: nil) as? NSFont).map { NSFontManager.shared.traits(of: $0).contains(.boldFontMask) } == true, "Custom body font preserves emphasis")

        let setext = build("小节😀\r\n====\r\n\r\n正文\r\n", .markdown)
        expect(setext.headings.count == 1 && setext.attributedText.string.hasPrefix("小节😀\n"), "Setext headings and CRLF source stay mapped")
        expect(build("# **重点** 与 [链接](https://example.test)\n", .markdown).headings.first?.title == "重点 与 链接", "Outline title uses visible heading words rather than Markdown punctuation")
        let beforeFence = build("```txt\n第一行😀\n```\n", .markdown)
        expect(beforeFence.displayRange(forSourceOffset: 0).location == 0, "Hidden opening fence maps to next visible block")
        expect(beforeFence.attributedText.string.contains("第一行😀"), "Code content survives mapping metadata")
        let metadata = build("---\n# hidden metadata\n---\n# 标题\n", .markdown)
        expect(metadata.headings.map(\.title) == ["标题"], "Metadata is not added to reading outline")
        let onlyFence = build("```", .markdown)
        expect(onlyFence.attributedText.length == 0 && onlyFence.displayRange(forSourceOffset: 1) == NSRange(location: 0, length: 0), "A hidden-only document has safe empty mapping")

        let literal = "序言\n第一章 开始😀\n内容。\n第十二节 细节\nChapter 3: End\n不是章节\n"
        let plain = build(literal, .plain)
        expect(plain.attributedText.string == literal, "Plain reading never changes original text")
        expect(plain.headings.map(\.title) == ["第一章 开始😀", "第十二节 细节", "Chapter 3: End"], "TXT recognizes Chinese chapters and English chapter headings")
        for offset in 0...(literal as NSString).length {
            let a = plain.displayRange(forSourceOffset: offset)
            let b = plain.sourceRange(forDisplayOffset: offset)
            expect(a == b && a.length == 0 && valid(a, in: literal), "Literal positions remain exact and never split surrogate pairs")
        }
        let emoji = build("😀x", .plain)
        expect(emoji.displayRange(forSourceOffset: 1).location == 0 && emoji.displayRange(forSourceOffset: 2).location == 2, "Identity point inside Emoji rounds to a scalar boundary")
        expect(build(String(repeating: "第一章 标题\n", count: 600), .plain).headings.count == 500, "TXT outline has bounded entries")

        let json = #"{"😀":900719925474099312345,"value":"quoted \"fake\": not a key","nested":{"child":true}}"#
        let jsonReading = build(json, .json)
        expect(jsonReading.attributedText.string == json, "JSON reading is a non-mutating source presentation")
        expect(jsonReading.headings.map(\.title) == ["😀", "value", "nested", "child"], "JSON key outline ignores escaped quotes in values")
        for heading in jsonReading.headings {
            expect(heading.displayRange == heading.sourceRange && valid(heading.sourceRange, in: json), "Configuration outline is an exact source range")
        }
        expect((jsonReading.attributedText.attribute(.font, at: 0, effectiveRange: nil) as? NSFont).map { NSFontManager.shared.traits(of: $0).contains(.fixedPitchFontMask) } == true, "Structured text stays monospaced")
        let jsonc = "// \"fake\":1\n{\"real\":2, /* \"fake2\":3 */ \"tail\":true}"
        expect(build(jsonc, .jsonc).headings.map(\.title) == ["real", "tail"], "JSONC key outline skips comments")
        let yaml = "server:\n  port: 8080 # 注释\npath: '😀'\n"
        expect(build(yaml, .yaml).attributedText.string == yaml, "YAML retains exact indentation and comments")
        expect(build(yaml, .yaml).headings.map(\.title) == ["server:"], "YAML offers only root block key navigation")
        expect(build("[main]\na=1\n[next] ; comment\nb=2\n", .ini).headings.count == 2, "INI section outline")

        let huge = "# 标题\n" + String(repeating: "正文😀\n", count: 50_000)
        let downgraded = build(huge, .markdown)
        expect(downgraded.attributedText.string == huge && downgraded.headings.isEmpty, "Large Markdown safely falls back to literal text")
        expect(downgraded.displayRange(forSourceOffset: 20) == NSRange(location: 20, length: 0), "Large source retains identity positions")
        let empty = build("", .markdown)
        expect(empty.attributedText.length == 0 && empty.segments.isEmpty && empty.headings.isEmpty, "Empty input has no fabricated blocks")
        expect(empty.sourceRange(forDisplayOffset: Int.max) == NSRange(location: 0, length: 0), "Empty source remains safe at any point")
        let nonfinite = ReadingDocument.build(text: "safe", kind: .plain, palette: palette, fontSize: .nan, readingFontName: "invalid-font", lineSpacing: .infinity)
        expect(nonfinite.attributedText.string == "safe", "Invalid preferences fall back safely")
        print("ReadingDocumentTests: \(checks) checks passed")
    }

    static func build(_ text: String, _ kind: TextKind) -> ReadingDocument {
        ReadingDocument.build(text: text, kind: kind, palette: palette, fontSize: 16, readingFontName: "PingFang SC", lineSpacing: 6)
    }
    static func valid(_ range: NSRange, in text: String) -> Bool {
        let source = text as NSString
        guard range.location != NSNotFound, range.location >= 0, range.location <= source.length,
              range.length >= 0, range.length <= source.length - range.location else { return false }
        func boundary(_ index: Int) -> Bool {
            if index == 0 || index == source.length { return true }
            return !((0xD800...0xDBFF).contains(source.character(at: index - 1)) && (0xDC00...0xDFFF).contains(source.character(at: index)))
        }
        return boundary(range.location) && boundary(NSMaxRange(range))
    }
    static func expect(_ value: @autoclosure () -> Bool, _ message: String) {
        guard value() else { fatalError(message) }; checks += 1
    }
}
