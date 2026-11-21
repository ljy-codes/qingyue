import Foundation

@main
struct MarkdownTableEditingTests {
    private static var checks = 0
    private static let fixture = "| A | B |\n| --- | :---: |\n| 中 | 😀 |"
    static func main() throws {
        // Literal Windows Core scenarios, applied through the Mac UTF-16 API.
        let next = try edit(fixture, 2, .tab(backward: false))
        expect(apply(fixture, next) == fixture && next.range == NSRange(location: 2, length: 0) && next.replacement.isEmpty, "Tab only navigates")
        expect(next.selection == NSRange(location: 6, length: 1), "Tab selects complete next-cell content")
        let across = try edit(fixture, 6, .tab(backward: false), length: 1)
        expect(across.selection == NSRange(location: 28, length: 1), "Tab skips delimiter row")
        let previous = try edit(fixture, 32, .tab(backward: true), length: 2)
        expect(previous.selection == NSRange(location: 28, length: 1), "Shift-Tab selects previous cell")
        try expect(try edit(fixture, 28, .tab(backward: true)).selection == NSRange(location: 6, length: 1), "Shift-Tab crosses to header")
        expect(MarkdownTableEditing.edit(text: fixture, selection: NSRange(location: 2, length: 0), action: .tab(backward: true)) == nil, "first cell Shift-Tab falls back")
        let appended = try edit(fixture, 32, .tab(backward: false), length: 2)
        expect(apply(fixture, appended) == fixture + "\n|  |  |", "last-cell Tab appends only one empty row")
        expect(appended.range == NSRange(location: 36, length: 0) && appended.selection == NSRange(location: 39, length: 0), "new row caret uses the data row, not delimiter")
        try output(fixture, 2, .insertRow, "| A | B |\n| --- | :---: |\n|  |  |\n| 中 | 😀 |")
        try output(fixture + "\n| 尾 | 末 |", 28, .insertRow, fixture + "\n|  |  |\n| 尾 | 末 |")
        try output(fixture, 28, .deleteRow, "| A | B |\n| --- | :---: |")
        try output(fixture + "\n| 尾 | 末 |", 28, .deleteRow, "| A | B |\n| --- | :---: |\n| 尾 | 末 |")
        for (position, action) in [(2, MarkdownTableAction.deleteRow), (12, .deleteRow), (12, .insertRow), (12, .tab(backward: false))] {
            expect(MarkdownTableEditing.edit(text: fixture, selection: NSRange(location: position, length: 0), action: action) == nil, "header/delimiter cannot be removed or treated as a data row")
        }
        try output(fixture, 28, .insertColumn, "| A |  | B |\n| --- | --- | :---: |\n| 中 |  | 😀 |")
        try output(fixture, 28, .deleteColumn, "| B |\n| :---: |\n| 😀 |")
        try output(fixture, 32, .deleteColumn, "| A |\n| --- |\n| 中 |")
        try output(fixture, 28, .align(.left), "| A | B |\n| :--- | :---: |\n| 中 | 😀 |")
        try output(fixture, 28, .align(.center), "| A | B |\n| :---: | :---: |\n| 中 | 😀 |")
        let aligned = try edit(fixture, 32, .align(.right), length: 2)
        expect(apply(fixture, aligned) == "| A | B |\n| --- | ---: |\n| 中 | 😀 |" && aligned.selection == NSRange(location: 31, length: 2), "alignment preserves the emoji selection")
        let unchanged = try edit(fixture, 32, .align(.center), length: 2)
        expect(apply(fixture, unchanged) == fixture && unchanged.replacement.isEmpty && unchanged.range.length == 0 && unchanged.selection == NSRange(location: 32, length: 2), "existing alignment does not rewrite source")
        let escaped = "| a\\|b | `c|d` |\n| --- | --- |\n| 中文 | ``e`|f`` |"
        expect(MarkdownTableEditing.table(in: escaped, at: 2)?.columnCount == 2, "escaped and multi-backtick pipes remain one cell")
        let escapedNext = try edit(escaped, 2, .tab(backward: false))
        expect(escapedNext.selection == NSRange(location: 9, length: 5), "code cell selection uses UTF-16")
        try output(escaped, 2, .deleteColumn, "| `c|d` |\n| --- |\n| ``e`|f`` |")
        let preserved = try edit(escaped, 2, .insertColumn)
        expect(apply(escaped, preserved) == "| a\\|b |  | `c|d` |\n| --- | --- | --- |\n| 中文 |  | ``e`|f`` |", "column editing never adds another escape to existing source")
        try output("| a\\\\| B |\n| --- | --- |\n| 中 | 尾 |", 2, .deleteColumn, "| B |\n| --- |\n| 尾 |")
        try output("前文\n\n  |A| B  |\n  |---| :---: |\n  |中| 😀 |\n\n后文", 7, .insertColumn,
                   "前文\n\n  |A|  | B  |\n  |---| --- | :---: |\n  |中|  | 😀 |\n\n后文")
        try output("A | B\n--- | :---:\n中 | 😀", 0, .insertColumn, "A |  | B\n--- | --- | :---:\n中 |  | 😀")
        let crlf = fixture.replacingOccurrences(of: "\n", with: "\r\n")
        try output(crlf, 30, .insertRow, crlf + "\r\n|  |  |")
        try output(crlf + "\r\n", 30, .deleteRow, "| A | B |\r\n| --- | :---: |\r\n")
        try output(crlf, 30, .insertColumn, "| A |  | B |\r\n| --- | --- | :---: |\r\n| 中 |  | 😀 |")
        for fence in ["```markdown", "~~~~", "  ```"] {
            let fenced = fence + "\n" + fixture + "\n" + (fence.contains("~") ? "~~~~" : "```")
            let position = (fenced as NSString).range(of: "中").location
            for action in [MarkdownTableAction.tab(backward: false), .insertRow, .align(.left)] {
                expect(MarkdownTableEditing.edit(text: fenced, selection: NSRange(location: position, length: 0), action: action) == nil, "table-looking fenced source stays native code")
            }
        }
        for indent in ["    ", "\t", " \t"] {
            let text = fixture.components(separatedBy: "\n").map { indent + $0 }.joined(separator: "\n")
            expect(MarkdownTableEditing.edit(text: text, selection: NSRange(location: (text as NSString).range(of: "中").location, length: 0), action: .insertRow) == nil, "indented code is excluded")
        }
        for text in ["正文 | 普通", "| A | B |\n| --- |\n| 中 | 尾 |", "| A | B |\n| --- | --- |\n| 中 |"] {
            expect(MarkdownTableEditing.edit(text: text, selection: NSRange(location: 2, length: 0), action: .insertColumn) == nil, "incomplete tables and paragraphs are not changed")
        }
        for range in [NSRange(location: 2, length: 5), NSRange(location: 33, length: 0), NSRange(location: 32, length: 1), NSRange(location: -1, length: 0), NSRange(location: 1, length: Int.max)] {
            expect(MarkdownTableEditing.edit(text: fixture, selection: range, action: .insertRow) == nil, "invalid ranges and surrogate halves are refused")
        }
        let composed = "| 中文 | e\u{0301}😀 |\n| --- | --- |"
        try expect(try edit(composed, 2, .tab(backward: false)).selection == NSRange(location: 7, length: 4), "composed and astral cell content remains complete")
        expect(MarkdownTableEditing.markdownTable(fromTSV: "A\tB\n1\t2") == "| A | B |\n| --- | --- |\n| 1 | 2 |\n", "literal TSV conversion")
        expect(MarkdownTableEditing.markdownTable(fromTSV: "\"A\tB\"\t\"He said \"\"hi\"\"\"\r\n中\t😀\r\n") == "| A    B | He said \"hi\" |\n| --- | --- |\n| 中 | 😀 |\n", "quoted tabs, escaped quotes, CRLF and trailing terminator")
        expect(MarkdownTableEditing.markdownTable(fromTSV: "A\tB\nx\t") == "| A | B |\n| --- | --- |\n| x |  |\n", "TSV keeps final empty cell")
        expect(MarkdownTableEditing.markdownTable(fromTSV: "<b>\t![x](file:///x)\n&lt;b&gt;\t`a|b`") == "| `<b>` | \\!\\[x\\](file:///x) |\n| --- | --- |\n| `&lt;b&gt;` | \\`a\\|b\\` |\n", "TSV content remains literal instead of activating Markdown or HTML")
        for text in ["", "\"unclosed\tB", "\"A\"x\tB", "A\"B\tC", "A\tB\nC", "\"甲\n乙\"\tB", "<b>`x`</b>\tB", "A\t\0"] {
            expect(MarkdownTableEditing.markdownTable(fromTSV: text) == nil, "invalid TSV is refused")
        }
        let wide = "| " + Array(repeating: "A", count: 32).joined(separator: " | ") + " |\n| " + Array(repeating: "---", count: 32).joined(separator: " | ") + " |"
        expect(MarkdownTableEditing.edit(text: wide, selection: NSRange(location: 2, length: 0), action: .insertColumn) == nil, "32-column table cannot grow")
        expect(MarkdownTableEditing.markdownTable(fromTSV: Array(repeating: "A", count: 33).joined(separator: "\t")) == nil, "TSV column budget")
        let full = "| A |\n| --- |" + String(repeating: "\n| x |", count: 1023)
        expect(MarkdownTableEditing.edit(text: full, selection: NSRange(location: (full as NSString).length - 2, length: 0), action: .insertRow) == nil, "1024 data-cell budget")
        let outside = String(repeating: "x", count: 110_000) + "\n\n" + fixture + "\n\n尾"
        let small = try edit(outside, 110_030, .insertRow)
        expect(small.range.length == 0 && small.replacement == "\n|  |  |", "small command stays local in a large document")
        print("MarkdownTableEditingTests: \(checks) checks passed")
    }
    private static func edit(_ text: String, _ position: Int, _ action: MarkdownTableAction, length: Int = 0) throws -> TextEditResult {
        guard let edit = MarkdownTableEditing.edit(text: text, selection: NSRange(location: position, length: length), action: action) else { throw NSError(domain: "MarkdownTableEditingTests", code: 1, userInfo: [NSLocalizedDescriptionKey: "Command unexpectedly refused: \(action) at \(position)"]) }
        let result = apply(text, edit) as NSString
        expect(edit.selection.location >= 0 && NSMaxRange(edit.selection) <= result.length, "selection is inside changed text")
        for offset in [edit.range.location, NSMaxRange(edit.range)] { expect(validBoundary(text as NSString, offset), "replacement does not split UTF-16 or CRLF") }
        for offset in [edit.selection.location, NSMaxRange(edit.selection)] { expect(validBoundary(result, offset), "selection does not split UTF-16 or CRLF") }
        return edit
    }
    private static func apply(_ text: String, _ edit: TextEditResult) -> String { (text as NSString).replacingCharacters(in: edit.range, with: edit.replacement) }
    private static func output(_ text: String, _ position: Int, _ action: MarkdownTableAction, _ expected: String) throws {
        let change = try edit(text, position, action)
        expect(apply(text, change) == expected, "\(action) result: \(apply(text, change).debugDescription)")
    }
    private static func validBoundary(_ text: NSString, _ position: Int) -> Bool {
        guard position > 0 && position < text.length else { return position >= 0 && position <= text.length }
        let before = text.character(at: position - 1), after = text.character(at: position)
        return !((0xD800...0xDBFF).contains(before) && (0xDC00...0xDFFF).contains(after)) && !(before == 13 && after == 10)
    }
    private static func expect(_ value: @autoclosure () throws -> Bool, _ message: String) rethrows { guard try value() else { fatalError(message) }; checks += 1 }
}
