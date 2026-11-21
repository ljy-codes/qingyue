import AppKit
import Foundation

@main
struct FormattingParityTests {
    private static var checks = 0
    static func main() throws {
        let huge = "9007199254740993123456789012345678901234567890"
        let json = "{\"key\":\(huge),\"key\":1.200E+009,\"s\":\"中文 😀\\u4f60\",\"a\":[true,null]}"
        let formatted = try TextFormatting.format(json, kind: "json")
        expect(formatted.contains(huge) && formatted.contains("1.200E+009"), "JSON retains original number spelling")
        expect(formatted.components(separatedBy: "\"key\"").count == 3, "JSON retains duplicate keys")
        expect(TextFeatures.diagnostic(formatted, kind: .json) == nil, "Formatted JSON validates")

        let jsonc = "/*头部 😀*/{\"same\":\(huge),//行内注释\n\"same\":-0.000,\"items\":[1,/*中间*/2,],\"url\":\"https://example.invalid/a//b\",} //尾注释"
        let rendered = try TextFormatting.format(jsonc, kind: "jsonc")
        expect(TextFeatures.diagnostic(rendered, kind: .jsonc) == nil, "Formatted JSONC remains valid")
        expect(rendered.contains("/*头部 😀*/") && rendered.contains("//行内注释") && rendered.contains("/*中间*/") && rendered.contains("//尾注释"), "Every JSONC comment survives")
        expect(rendered.contains(huge) && rendered.contains("-0.000") && rendered.contains("https://example.invalid/a//b"), "JSONC leaves lexemes alone")
        try expect(try TextFormatting.format(rendered, kind: "jsonc") == rendered, "JSONC formatting is idempotent")
        for invalid in ["{x:1}", "[1,,2]", "[01]", "{\"x\":}", "{\"x\" 1}", "/* open", "{\"x\":\"\\q\"}", "{\"a\":1} true"] {
            expectThrows("Reject invalid JSONC: " + invalid) { _ = try TextFormatting.format(invalid, kind: "jsonc") }
        }
        let commentPositions = ["{/*a*/\"x\"/*b*/:/*c*/1/*d*/,/*e*/}", "[/*a*/1,//b\n2,/*c*/]", "//only\ntrue//tail"]
        for input in commentPositions {
            let result = try TextFormatting.format(input, kind: "jsonc")
            expect(TextFeatures.diagnostic(result, kind: .jsonc) == nil, "Comment relocation never swallows punctuation")
        }
        expectThrows("Standard JSON rejects JSONC") { _ = try TextFormatting.format(jsonc, kind: "json") }
        expectThrows("Input has explicit limit") { _ = try TextFormatting.format(String(repeating: "a", count: 500_001), kind: "plain") }

        try expect(try TextFormatting.format("  alpha  beta \t\r\n\t中文 😀\t\r\n\r\n\r\nlast\t", kind: "plain") == "  alpha  beta\n    中文 😀\n\nlast", "TXT trims trailing whitespace, preserves inline spaces, normalizes leading Tabs")
        try expect(try TextFormatting.format("a\n\n\nb\n", kind: "txt") == "a\n\nb\n", "TXT preserves paragraphs and final newline")
        try expect(try TextFormatting.format("a  b \t\nb\t", kind: "python") == "a  b\nb", "Generic cleanup retains inline and indentation semantics")
        let codeMD = "---\ntitle:  x  \n---\n\n#   标题\n\n```md\n#   literal  \n\n\nraw  \n```\n\n正文  \nnext\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n"
        let md = try TextFormatting.format(codeMD, kind: "markdown")
        expect(md.contains("# 标题"), "Independent Markdown heading normalized")
        expect(md.contains("title:  x  ") && md.contains("#   literal  \n\n\nraw  "), "Frontmatter and fences byte content preserved")
        expect(md.contains("正文  \nnext"), "Markdown hard line break preserved")
        expect(md.contains("| a | b |\n| --- | --- |\n| 1 | 2 |"), "Markdown table preserved")
        let continued = "-   first\n    continuation\n\n-   next\n"
        try expect(try TextFormatting.format(continued, kind: "markdown").hasPrefix("-   first\n    continuation"), "List continuation indentation not changed")
        let htmlMD = "<pre>\n#   literal\n\n\n</pre>"
        try expect(try TextFormatting.format(htmlMD, kind: "markdown") == htmlMD, "HTML blocks left unchanged")
        let quoteMD = "> ```md\n> #   literal\n> ```\n\n#   heading"
        try expect(try TextFormatting.format(quoteMD, kind: "markdown").contains("> #   literal"), "Container fence content protected")

        let xml = "<?xml version=\"1.0\"?><root><item a=\"&amp;\">中文 😀</item><empty/></root>"
        let formattedXML = try TextFormatting.format(xml, kind: "xml")
        expect(formattedXML.contains("\n  <item a=\"&amp;\">中文 😀</item>\n  <empty/>\n</root>"), "XML indents element children and retains raw attributes/content")
        for mixed in ["<p>Hello <b>bold</b> world</p>", "<p> <x/> </p>", "<r xml:space='preserve'><x/><y/></r>", "<r><![CDATA[a<b  ]]><x/></r>"] {
            try expect(try TextFormatting.format(mixed, kind: "xml") == mixed, "XML preserves mixed/space-preserve/CDATA content")
        }
        expectThrows("XML rejects external DTD") { _ = try TextFormatting.format("<!DOCTYPE x SYSTEM \"file:///etc/passwd\"><x/>", kind: "xml") }
        expectThrows("XML rejects internal entities") { _ = try TextFormatting.format("<!DOCTYPE x [<!ENTITY z \"hello\">]><x>&z;</x>", kind: "xml") }
        expectThrows("XML rejects malformed nesting") { _ = try TextFormatting.format("<x><y></x>", kind: "xml") }
        expectThrows("XML depth bounded") { _ = try TextFormatting.format(String(repeating: "<x>", count: 140) + String(repeating: "</x>", count: 140), kind: "xml") }

        let tree = try JSONStructureNode.parse("{\"same\":\(huge),\"same\":1.200E+009,\"😀\":{\"escaped\\nkey\":[\"x\",false,null]}}", allowComments: false)
        expect(tree.kind == .object && tree.children.count == 3, "JSON tree retains duplicate object fields")
        expect(tree.children[0].key == "same" && tree.children[1].key == "same", "Both duplicate field labels retained")
        expect(tree.children[0].rawValue == huge && tree.children[1].rawValue == "1.200E+009", "Tree scalar numbers are lexemes")
        expect(tree.children[2].children[0].key == "escaped\nkey", "Key decoding supports escaped strings")
        expect(tree.children[2].children[0].children.map { $0.key } == ["[0]", "[1]", "[2]"], "Arrays use stable indexed rows")
        let commentsTree = try JSONStructureNode.parse("/*c*/[1,2,]", allowComments: true)
        expect(commentsTree.children.count == 2, "JSONC structure accepts comments/trailing comma")
        expectThrows("Strict JSON tree rejects trailing comma") { _ = try JSONStructureNode.parse("[1,]", allowComments: false) }
        expectThrows("Structure node count bounded") { _ = try JSONStructureNode.parse("[" + Array(repeating: "0", count: 10_001).joined(separator: ",") + "]", allowComments: false) }
        expectThrows("Structure depth bounded") { _ = try JSONStructureNode.parse(String(repeating: "[", count: 140) + "0" + String(repeating: "]", count: 140), allowComments: false) }
        expectThrows("Structure size bounded") { _ = try JSONStructureNode.parse(String(repeating: " ", count: 2_000_001), allowComments: false) }
        let controller = try JSONStructureController(text: json, allowComments: false)
        expect(controller.rootNode.children.count == 4 && controller.window?.title == "JSON 只读结构", "Native outline controller builds without changing source")

        expect(LineChanges.marks(original: "a\nb", saved: "a\nb", current: "a\nb").isEmpty, "No change marks on clean initial text")
        expect(LineChanges.marks(original: "a\nb", saved: "a\nb", current: "a\nx") == [LineChange(line: 2, saved: false)], "Modified line orange")
        expect(LineChanges.marks(original: "a\nb", saved: "a\nx", current: "a\nx") == [LineChange(line: 2, saved: true)], "Save turns modified line green")
        expect(LineChanges.marks(original: "a\nb", saved: "a\nx", current: "z\na\nx") == [LineChange(line: 1, saved: false), LineChange(line: 3, saved: true)], "Saved changes follow later line insertion")
        expect(LineChanges.marks(original: "a\nb\nc", saved: "a\nb\nc", current: "a\nc").contains(LineChange(line: 2, saved: false, deletion: true)), "Deletion anchored to next line")
        expect(LineChanges.marks(original: "a\nb\nc", saved: "a\nb\nc", current: "a\nb").contains(LineChange(line: 2, saved: false, deletion: true)), "EOF deletion anchored to last line")
        expect(LineChanges.marks(original: "甲😀\r\n乙", saved: "甲😀\n乙", current: "甲😀\n乙").isEmpty, "Line endings are display equivalent")
        expect(!LineChanges.marks(original: "é", saved: "é", current: "e\u{301}").isEmpty, "Unicode normalization edit remains visible")
        expect(LineChanges.marks(original: "a\nb", saved: "a\nx", current: "a\ny") == [LineChange(line: 2, saved: false)], "Unsaved orange takes priority over saved green")
        let large = (0..<25_000).map { "row \($0)" }
        var changed = large; changed[12_345] = "changed"; changed.insert("inserted", at: 900)
        let began = ProcessInfo.processInfo.systemUptime
        let marks = LineChanges.marks(original: large.joined(separator: "\n"), saved: large.joined(separator: "\n"), current: changed.joined(separator: "\n"))
        expect(marks.contains(LineChange(line: 901, saved: false)) && marks.contains(LineChange(line: 12_347, saved: false)), "Large local changes retain precise lines")
        expect(ProcessInfo.processInfo.systemUptime - began < 6, "Large line diff has bounded work")
        expect(LineChanges.marks(original: "", saved: "", current: String(repeating: "x", count: 2_000_001)).isEmpty, "Very large text suspends line decoration without allocating a diff")
        expect(LineChanges.marks(original: "", saved: "", current: String(repeating: "\n", count: 100_001)).isEmpty, "Pathological line count suspends decoration")
        for size in 1...50 {
            let base = (0..<size).map { "中文😀 \($0)" }
            var insertion = base; insertion.insert("new", at: size / 2)
            let changes = LineChanges.marks(original: base.joined(separator: "\n"), saved: base.joined(separator: "\n"), current: insertion.joined(separator: "\n"))
            expect(changes == [LineChange(line: size / 2 + 1, saved: false)], "Insertion diff remains precise for \(size) lines")
        }
        // Unrelated repeated lines exhaust the bounded Myers budget. The fallback
        // must stay safe and conservative, not allocate a quadratic matrix.
        let unrelated = LineChanges.marks(original: Array(repeating: "old", count: 1000).joined(separator: "\n"), saved: Array(repeating: "old", count: 1000).joined(separator: "\n"), current: Array(repeating: "new", count: 1000).joined(separator: "\n"))
        expect(unrelated.count == 1000 && unrelated.first?.line == 1 && unrelated.last?.line == 1000, "Wholesale rewrites use a bounded conservative band")
        for count in 2...40 {
            let original = (0..<count).map { "line \($0)" }
            var updated = original
            updated.remove(at: count / 2)
            updated.insert("inserted", at: 0)
            let result = LineChanges.marks(original: original.joined(separator: "\n"), saved: original.joined(separator: "\n"), current: updated.joined(separator: "\n"))
            expect(result.allSatisfy { $0.line >= 1 && $0.line <= updated.count }, "Diff line indices remain in range after insertion and deletion")
            expect(result.contains { $0.line == 1 && !$0.saved }, "Inserted leading line remains marked after another deletion")
        }
        print("FormattingParityTests: \(checks) checks passed")
    }
    private static func expect(_ condition: @autoclosure () throws -> Bool, _ detail: String) rethrows {
        guard try condition() else { fatalError(detail) }; checks += 1
    }
    private static func expectThrows(_ detail: String, _ action: () throws -> Void) {
        do { try action(); fatalError(detail) } catch { checks += 1 }
    }
}
