import Foundation

@main
struct MarkdownListEditingTests {
    private static var checks = 0
    static func main() throws {
        let first = MarkdownListEditing.item(in: "- 第一项\n", at: 2)
        expect(first?.marker == "-" && first?.ordered == false && first?.content == "第一项", "recognizes unordered source")
        try output("- 中文", 4, .newline, "- 中文\n- ", caret: 7)
        try output("9. 中文", 5, .newline, "9. 中文\n10. ", caret: 10)
        try output("9) 中文", 5, .newline, "9) 中文\n10) ", caret: 10)
        try output("- [x] 完成", 8, .newline, "- [x] 完成\n- [ ] ", caret: 15)
        try output("- 中😀文\n- 后续", 5, .newline, "- 中😀\n- 文\n- 后续", caret: 8)
        try output("- 中文\n- 后续", 4, .newline, "- 中文\n- \n- 后续", caret: 7)
        try output("- a\r\n- b", 3, .newline, "- a\r\n- \r\n- b", caret: 7)
        try output("- 父\n  - ", 8, .newline, "- 父\n- ", caret: 6)
        try output("- ", 2, .newline, "", caret: 0)
        try output("- \n下一行", 2, .newline, "\n下一行", caret: 0)
        try output("- [ ] \r\n下一行", 6, .newline, "\r\n下一行", caret: 0)
        try output("- \nabc", 2, .newline, "- \n- ", caret: 5, length: 4)
        try output("- \r\nabc", 2, .newline, "- \r\n- ", caret: 6, length: 5)
        try output("- abcXYZ\n尾", 5, .newline, "- abc\n- Z\n尾", caret: 8, length: 2)
        try output("- 一\n- 二\n  - 子\n- 三", 6, .indent, "- 一\n  - 二\n    - 子\n- 三", caret: 8)
        try output("- 一\n  - 二\n    - 子\n- 三", 8, .outdent, "- 一\n- 二\n  - 子\n- 三", caret: 6)
        try output("- 父\n    - 子\n        - 孙", 10, .outdent, "- 父\n- 子\n    - 孙", caret: 6)
        try output("10. 父\n11. 子", 11, .indent, "10. 父\n    11. 子", caret: 15)
        try output("- 父\n  续段", 8, .newline, "- 父\n  续段\n- ", caret: 11)
        try output("- [ ] task\n\n  continuation", 25, .newline, "- [ ] task\n\n  continuatio\n- [ ] n", caret: 32)
        let continuation = "- [ ] task\n\n  continuation"
        let continuationEdit = try required(MarkdownListEditing.edit(text: continuation, selection: NSRange(location: continuation.utf16.count, length: 0), action: .newline))
        expect(apply(continuation, continuationEdit) == continuation + "\n- [ ] ", "task continuation retains task marker")
        let parent = "- 一\n- 二\n  ```\n  code\n  ```\n  - 子"
        try output(parent, 7, .indent, "- 一\n  - 二\n    ```\n    code\n    ```\n    - 子", caret: 9)
        let lazy = "- a\n- b\nlazy\n  - c"
        try output(lazy, 4, .indent, "- a\n  - b\n  lazy\n    - c", caret: 6)
        let block = "- prev\n- item\n  ~~~\n  code\n  ~~~\noutside"
        try output(block, 7, .indent, "- prev\n  - item\n    ~~~\n    code\n    ~~~\noutside", caret: 9)
        let tabbed = "- 父\n\t- 甲\n\t- 乙"
        let tabEdit = try required(MarkdownListEditing.edit(text: tabbed, selection: NSRange(location: tabbed.utf16.count, length: 0), action: .indent))
        expect(apply(tabbed, tabEdit) == "- 父\n\t- 甲\n      - 乙", "tab indentation follows four-column stops")
        try output("- [ ] 待办\n尾", 6, .toggleTask, "- [x] 待办\n尾", caret: 6)
        try output("- [X] 待办\n尾", 6, .toggleTask, "- [ ] 待办\n尾", caret: 6)
        let selected = "- parent\n  - child\n    - a\n    - b"
        let range = NSRange(location: 9, length: (selected as NSString).range(of: "\n    - b").location - 9)
        let selectedEdit = try required(MarkdownListEditing.edit(text: selected, selection: range, action: .outdent))
        expect(apply(selected, selectedEdit) == "- parent\n- child\n  - a\n  - b", "outdent carries every descendant of a selected parent")
        expect(selectedEdit.selection.length > 0 && selectedEdit.selection.location == 9, "multi-line selection remains a selection")
        for (text, position) in [("```\n- 代码\n```", 8), ("~~~~\n- code\n~~~~", 9), ("    - 缩进代码", 10), ("\t- code", 5), ("普通正文", 4), ("- - -", 5), ("* * *", 5), ("- 父\n\n正文\n    代码", 14)] {
            expect(MarkdownListEditing.edit(text: text, selection: NSRange(location: position, length: 0), action: .newline) == nil, "code and unrelated paragraphs stay native")
        }
        for action in [MarkdownListAction.indent, .outdent, .toggleTask] {
            expect(MarkdownListEditing.edit(text: "````md\n```\n- literal\n````", selection: NSRange(location: 17, length: 0), action: action) == nil, "short fence cannot close longer fence")
        }
        for range in [NSRange(location: -1, length: 0), NSRange(location: 0, length: Int.max), NSRange(location: 3, length: 1), NSRange(location: 4, length: 0)] {
            expect(MarkdownListEditing.edit(text: "- a😀", selection: range, action: .newline) == nil, "invalid UTF-16 ranges are refused")
        }
        expect(MarkdownListEditing.edit(text: "- a\r\n- b", selection: NSRange(location: 4, length: 0), action: .newline) == nil, "CRLF boundary is never split")
        expect(MarkdownListEditing.edit(text: "- first", selection: NSRange(location: 4, length: 0), action: .indent) == nil, "first item cannot become an orphan nested list")
        expect(MarkdownListEditing.item(in: "````\n```\n- literal\n````", at: 12) == nil, "code lines do not become list markers")
        expect(MarkdownListEditing.item(in: "- 父\n  - 子\n    - 孙", at: 16)?.level == 2, "nested markers expose actual list depth")
        let plainAfterFence = "- previous\n- current\n  ~~~\n  code\n  ~~~\noutside"
        let modified = try required(MarkdownListEditing.edit(text: plainAfterFence, selection: NSRange(location: 13, length: 0), action: .indent))
        expect(apply(plainAfterFence, modified).hasSuffix("\noutside"), "moving a list never consumes an external paragraph")
        print("MarkdownListEditingTests: \(checks) checks passed")
    }
    private static func output(_ text: String, _ position: Int, _ action: MarkdownListAction, _ expected: String, caret: Int, length: Int = 0) throws {
        let edit = try required(MarkdownListEditing.edit(text: text, selection: NSRange(location: position, length: length), action: action))
        let result = apply(text, edit)
        expect(result == expected, "\(action) result: \(result.debugDescription)")
        expect(edit.selection == NSRange(location: caret, length: 0), "caret stays at the source location after \(action): \(edit.selection)")
        expect(edit.selection.location <= result.utf16.count, "selection remains in document")
        for boundary in [edit.range.location, NSMaxRange(edit.range)] { expect(validBoundary(text as NSString, boundary), "one replacement keeps UTF-16 boundaries") }
    }
    private static func required(_ edit: TextEditResult?) throws -> TextEditResult {
        guard let edit else { throw NSError(domain: "MarkdownListEditingTests", code: 1, userInfo: [NSLocalizedDescriptionKey: "Command unexpectedly refused"]) }; return edit
    }
    private static func apply(_ text: String, _ edit: TextEditResult) -> String { (text as NSString).replacingCharacters(in: edit.range, with: edit.replacement) }
    private static func validBoundary(_ text: NSString, _ position: Int) -> Bool {
        guard position > 0 && position < text.length else { return position >= 0 && position <= text.length }
        let before = text.character(at: position - 1), after = text.character(at: position)
        return !((0xD800...0xDBFF).contains(before) && (0xDC00...0xDFFF).contains(after)) && !(before == 13 && after == 10)
    }
    private static func expect(_ value: @autoclosure () -> Bool, _ message: String) { guard value() else { fatalError(message) }; checks += 1 }
}
