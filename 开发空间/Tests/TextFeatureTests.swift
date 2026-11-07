import AppKit
import Foundation

@main
struct TextFeatureTests {
    static var checks = 0

    static func main() throws {
        let source = #"{"n":900719925474099312345678901234567890,"decimal":-0.0001200,"e":1.2300E+009,"s":"\u4F60\u597D 😀","a":[true,null,{},[]]}"#
        let formatted = try TextFeatures.formatJSON(source)
        expect(formatted.contains("900719925474099312345678901234567890"), "Large integer spelling must survive")
        expect(formatted.contains("-0.0001200"), "Trailing decimal zeroes must survive")
        expect(formatted.contains("1.2300E+009"), "Exponent spelling must survive")
        expect(formatted.contains(#""\u4F60\u597D 😀""#), "Unicode escapes and emoji must survive")
        expect(TextFeatures.diagnostic(formatted, kind: .json) == nil, "Formatted JSON must validate")
        try expect(try TextFeatures.formatJSON(formatted) == formatted, "Formatting must be idempotent")
        try expect(try TextFeatures.formatJSON(" { } ") == "{}", "Empty object formatting")
        try expect(try TextFeatures.formatJSON("[ ]") == "[]", "Empty array formatting")
        try expect(try TextFeatures.formatJSON("1e1000000") == "1e1000000", "Valid JSON exponent must not need Double conversion")
        try expect(try TextFeatures.formatJSON(#"{"same":1,"same":2}"#).components(separatedBy: "\"same\"").count == 3, "Formatting preserves duplicate keys")

        let invalid = ["", "01", "-", "1.", "1e+", "NaN", "true false", "[1,]", #"{"a":1,}"#, #"{"a" 1}"#, "[1 2]", "{a:1}", #""\q""#, #""\u123Z""#, "\"hello\nworld\"", #"{"a":/*x*/1}"#, "/**/1"]
        for input in invalid {
            expect(TextFeatures.diagnostic(input, kind: .json) != nil, "Strict JSON must reject: \(input)")
            do {
                _ = try TextFeatures.formatJSON(input)
                fatalError("Formatter accepted invalid JSON: \(input)")
            } catch { checks += 1 }
        }
        expect(TextFeatures.diagnostic("\n{\n  \"a\":\n}", kind: .json)?.contains("第 4 行") == true, "Diagnostic line number")
        let jsonc = "// keep comments\n{\"path\":\"https://example.test/#value\", /*comment*/ \"x\":[1,2,],}"
        expect(TextFeatures.diagnostic(jsonc, kind: .jsonc) == nil, "JSONC supports comments and trailing commas")
        expect(TextFeatures.diagnostic("/* unfinished", kind: .jsonc) != nil, "Unfinished JSONC block comment")
        expect(TextFeatures.diagnostic(String(repeating: "[", count: 270) + "0" + String(repeating: "]", count: 270), kind: .json) != nil, "Depth limit")
        expect(TextFeatures.diagnostic("root:\n\tchild: 1\n", kind: .yaml)?.contains("第 2 行") == true, "YAML tab indentation")
        expect(TextFeatures.diagnostic("root:\n  value: \"a\tb\"\n", kind: .yaml) == nil, "Tab within YAML scalar is not indentation")

        expect(TextKind.detect(url: URL(fileURLWithPath: "/tmp/config.json"), text: "") == .json, "JSON extension")
        expect(TextKind.detect(url: URL(fileURLWithPath: "/tmp/.env.local"), text: "A=1") == .ini, "Dotenv detection")
        expect(TextKind.detect(url: URL(fileURLWithPath: "/tmp/app.config"), text: "<?xml version=\"1.0\"?><x />") == .xml, "Config XML detection")
        expect(TextKind.detect(url: URL(fileURLWithPath: "/tmp/app.config"), text: "[main]\nx=1") == .ini, "Config INI detection")
        expect(TextKind.detect(url: URL(fileURLWithPath: "/tmp/data.txt"), text: "{hi}") == .plain, "Explicit TXT remains plain")
        expect(TextKind.detect(url: URL(fileURLWithPath: "/tmp/app.config"), text: "server:\n  port: 8080") == .yaml, "Config YAML detects a nested mapping without a scalar on the first line")

        let markdown = "---\ntitle: demo\n---\n# 第一章 😀\n\n```md\n# not a heading\n```\n\n小节\n----\n\n## 结束 ##\n"
        let headings = TextFeatures.headings(markdown)
        expect(headings.map(\.title) == ["第一章 😀", "小节", "结束"], "Heading extraction ignores code and frontmatter")
        expect((markdown as NSString).substring(with: headings[0].range) == "# 第一章 😀", "Heading ranges are UTF16 safe")

        let palette = EditorPalette(background: .black, foreground: .white, muted: .gray, accent: .systemBlue, string: .systemGreen, number: .systemOrange, comment: .systemGray, key: .systemPurple)
        let font = NSFont.monospacedSystemFont(ofSize: 14, weight: .regular)
        let input = "{\"😀\": \"https://example.test/#a\", // \"comment\"\n\"n\": 123}"
        let storage = NSTextStorage(string: input)
        TextFeatures.highlight(storage, kind: .jsonc, palette: palette, font: font)
        expect(storage.string == input, "Highlighting never changes source")
        let commentRange = (input as NSString).range(of: "// \"comment\"")
        let commentColor = storage.attribute(.foregroundColor, at: commentRange.location + 4, effectiveRange: nil) as? NSColor
        expect(commentColor == palette.comment, "Quoted text inside comment stays comment color")
        let stringRange = (input as NSString).range(of: "https://example.test/#a")
        let stringColor = storage.attribute(.foregroundColor, at: stringRange.location + 8, effectiveRange: nil) as? NSColor
        expect(stringColor == palette.string, "URL slash/hash inside string stays string color")
        TextFeatures.highlight(storage, kind: .plain, palette: palette, font: font)
        expect(storage.attribute(.foregroundColor, at: stringRange.location, effectiveRange: nil) as? NSColor == palette.foreground, "Switching to plain clears syntax colors")

        let preview = TextFeatures.markdownPreview("# 标题\n\n- **重点** 与 `code`\n\n![猫](https://example.test/a.png)\n[网站](https://example.test)\n[危险](javascript:alert)\n<script>alert(1)</script>\n```js\n# literal heading\n```\n", palette: palette, fontSize: 16)
        expect(preview.string.contains("标题") && !preview.string.contains("# 标题"), "Native heading rendering")
        expect(preview.string.contains("• 重点 与 code"), "List and inline rendering")
        expect(preview.string.contains("〔图片：猫〕"), "Images become text, never remote downloads")
        expect(preview.string.contains("<script>alert(1)</script>"), "HTML is literal text")
        expect(preview.string.contains("# literal heading"), "Code fence content stays literal")
        var links: [String] = []
        var hasAttachment = false
        preview.enumerateAttributes(in: NSRange(location: 0, length: preview.length)) { attrs, _, _ in
            if let url = attrs[.link] as? URL { links.append(url.absoluteString) }
            if attrs[.attachment] != nil { hasAttachment = true }
        }
        expect(links == ["https://example.test"], "Only allowed link schemes are interactive")
        expect(!hasAttachment, "Preview has no remote attachments")

        let tasks = TextFeatures.markdownPreview("- [ ] 待办 😀\n- [x] 完成\n1. [X] 有序任务\n- [not] 普通列表\n`- [x] inline code`\n```md\n- [x] fenced code\n```\n", palette: palette, fontSize: 16)
        expect(tasks.string.contains("☐ 待办 😀") && tasks.string.contains("☑ 完成") && tasks.string.contains("☑ 有序任务"), "Task lists show checked and unchecked states")
        expect(tasks.string.contains("• [not] 普通列表"), "Non-task brackets stay ordinary list text")
        expect(tasks.string.contains("- [x] inline code") && tasks.string.contains("- [x] fenced code"), "Task syntax is literal inside inline and fenced code")

        let styled = TextFeatures.markdownPreview("~~停用 😀~~ 和 `~~literal~~`\nfile_name_here\n\\*literal\\*\n", palette: palette, fontSize: 16)
        let strikeRange = (styled.string as NSString).range(of: "停用 😀")
        expect(styled.attribute(.strikethroughStyle, at: strikeRange.location, effectiveRange: nil) as? Int == NSUnderlineStyle.single.rawValue, "Strikethrough survives Chinese and Emoji")
        let literalStrike = (styled.string as NSString).range(of: "~~literal~~")
        expect(literalStrike.location != NSNotFound && styled.attribute(.strikethroughStyle, at: literalStrike.location, effectiveRange: nil) == nil, "Code does not inherit Markdown strike styling")
        expect(styled.string.contains("file_name_here"), "Underscores inside identifiers are literal")
        expect(styled.string.contains("*literal*"), "Escaped punctuation displays literally")

        let blockSource = "标题\n--\n\n***\n\n- 项目\n---\n\n1. 项目\n___\n\n```md\n---\n# literal\n```\n"
        let blocks = TextFeatures.markdownPreview(blockSource, palette: palette, fontSize: 16)
        expect(TextFeatures.headings(blockSource).map(\.title) == ["标题"], "Lists and horizontal rules do not become Setext headings")
        expect(blocks.string.contains("# literal") && blocks.string.contains("---"), "Fenced horizontal rules remain source")
        var rules = Set<ObjectIdentifier>()
        blocks.enumerateAttribute(.paragraphStyle, in: NSRange(location: 0, length: blocks.length)) { value, _, _ in
            if let style = value as? NSParagraphStyle {
                for block in style.textBlocks where !(block is NSTextTableBlock) { rules.insert(ObjectIdentifier(block)) }
            }
        }
        expect(rules.count == 3, "Three thematic breaks render with native border blocks")
        let metadata = TextFeatures.markdownPreview("---\ntitle: **metadata**\n...\n# Visible\n", palette: palette, fontSize: 16)
        expect(metadata.string.contains("title: **metadata**"), "Frontmatter is preserved as literal metadata")
        expect(TextFeatures.headings("---\n# metadata\n...\n# Visible\n").map(\.title) == ["Visible"], "Frontmatter terminated by ellipsis stays out of the outline")

        let tableSource = "| 名称 | 状态 |\n| :--- | ---: |\n| 中文😀 | **正常** |\n| A\\|B | ~~停用~~ |\n"
        let renderedTable = TextFeatures.markdownPreview(tableSource, palette: palette, fontSize: 16)
        let cells = tableBlocks(renderedTable)
        expect(cells.count == 6, "Three rows by two columns use native table cells")
        expect(cells["0,0"]?.table.numberOfColumns == 2, "Native table has the declared column count")
        expect(cells["0,0"]?.table === cells["2,1"]?.table, "All cells belong to the same native table")
        expect(renderedTable.string.contains("A|B") && !renderedTable.string.contains("---:"), "Escaped pipes stay inside the cell and separators are hidden")
        let codePipe = TextFeatures.markdownPreview("A | B\n--- | ---\n`a\\|b` | value\n", palette: palette, fontSize: 16)
        expect(codePipe.string.contains("a|b") && !codePipe.string.contains("a\\|b"), "Table pipe escapes are removed inside code spans as well")
        let alignedRange = (renderedTable.string as NSString).range(of: "状态")
        expect((renderedTable.attribute(.paragraphStyle, at: alignedRange.location, effectiveRange: nil) as? NSParagraphStyle)?.alignment == .right, "Delimiter colons control cell alignment")
        let struckCell = (renderedTable.string as NSString).range(of: "停用")
        expect(renderedTable.attribute(.strikethroughStyle, at: struckCell.location, effectiveRange: nil) as? Int == NSUnderlineStyle.single.rawValue, "Inline styles work inside native cells")

        let tableStorage = NSTextStorage(attributedString: renderedTable)
        let tableLayout = NSLayoutManager(); let container = NSTextContainer(size: NSSize(width: 520, height: 100_000))
        tableStorage.addLayoutManager(tableLayout); tableLayout.addTextContainer(container)
        tableLayout.ensureLayout(for: container)
        let used = tableLayout.usedRect(for: container)
        expect(used.width.isFinite && used.height.isFinite && used.width > 300 && used.height > 30, "Native table lays out at a finite useful size")
        let firstHeader = (renderedTable.string as NSString).range(of: "名称")
        let secondHeader = (renderedTable.string as NSString).range(of: "状态")
        let firstRect = tableLayout.boundingRect(forGlyphRange: tableLayout.glyphRange(forCharacterRange: firstHeader, actualCharacterRange: nil), in: container)
        let secondRect = tableLayout.boundingRect(forGlyphRange: tableLayout.glyphRange(forCharacterRange: secondHeader, actualCharacterRange: nil), in: container)
        expect(secondRect.minX > firstRect.maxX, "Native cells occupy separate columns rather than a vertical text list")

        let ragged = TextFeatures.markdownPreview("A | B\n--- | ---\nonly |\nextra | data | retained\n", palette: palette, fontSize: 16)
        expect(tableBlocks(ragged).count == 6 && ragged.string.contains("data | retained"), "Missing cells are padded and excess content remains visible")
        let malformed = TextFeatures.markdownPreview("A | B\n::--- | ---\n", palette: palette, fontSize: 16)
        expect(tableBlocks(malformed).isEmpty && malformed.string.contains("::---"), "Malformed delimiter rows stay source text")
        let indented = TextFeatures.markdownPreview("    A | B\n    --- | ---\n", palette: palette, fontSize: 16)
        expect(tableBlocks(indented).isEmpty, "Indented code is not promoted to a table")
        let blockStart = TextFeatures.markdownPreview("# Heading | value\n--- | ---\n", palette: palette, fontSize: 16)
        expect(tableBlocks(blockStart).isEmpty && blockStart.string.contains("Heading | value"), "ATX headings take precedence over tables")
        let interrupted = TextFeatures.markdownPreview("A | B\n--- | ---\n```text | info\n| literal | code |\n```\n", palette: palette, fontSize: 16)
        expect(tableBlocks(interrupted).count == 2 && interrupted.string.contains("| literal | code |"), "A fenced code block ends a preceding table")
        let fencedTable = TextFeatures.markdownPreview("```md\n" + tableSource + "```\n", palette: palette, fontSize: 16)
        expect(tableBlocks(fencedTable).isEmpty && fencedTable.string.contains("| :--- | ---: |"), "Fenced table source remains literal")
        let largeTable = "A | B\n--- | ---\n" + (0..<600).map { "row\($0) | value\n" }.joined()
        let bounded = TextFeatures.markdownPreview(largeTable, palette: palette, fontSize: 16)
        expect(tableBlocks(bounded).count <= 1024 && bounded.string.contains("row599 | value"), "Very large tables cap native cells and retain the remaining source")
        let threeColumns = TextFeatures.markdownPreview("A | B | C\n--- | --- | ---\n" + String(repeating: "x | y | z\n", count: 400), palette: palette, fontSize: 16)
        expect(tableBlocks(threeColumns).count <= 1024, "Native cell budget also holds for column counts that do not divide the limit")

        print("TextFeatureTests: \(checks) checks passed")
    }

    private static func expect(_ condition: @autoclosure () throws -> Bool, _ message: String) rethrows {
        guard try condition() else { fatalError(message) }
        checks += 1
    }

    private static func tableBlocks(_ text: NSAttributedString) -> [String: NSTextTableBlock] {
        var result: [String: NSTextTableBlock] = [:]
        text.enumerateAttribute(.paragraphStyle, in: NSRange(location: 0, length: text.length)) { value, _, _ in
            if let style = value as? NSParagraphStyle {
                for block in style.textBlocks {
                    if let cell = block as? NSTextTableBlock { result["\(cell.startingRow),\(cell.startingColumn)"] = cell }
                }
            }
        }
        return result
    }
}
