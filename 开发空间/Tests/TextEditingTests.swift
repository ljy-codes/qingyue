import Foundation

@main
struct TextEditingTests {
    private static var checks = 0

    static func main() throws {
        try checkIndent("", NSRange(location: 0, length: 0), remove: false,
                        output: "  ", selection: NSRange(location: 2, length: 0))
        try checkIndent("甲😀乙", NSRange(location: 3, length: 0), remove: false,
                        output: "甲😀  乙", selection: NSRange(location: 5, length: 0))
        try checkIndent("a\nb\nc", NSRange(location: 0, length: 4), remove: false,
                        output: "  a\n  b\nc", selection: NSRange(location: 0, length: 8))
        try checkIndent("a\nb\nc", NSRange(location: 0, length: 3), remove: false,
                        output: "  a\n  b\nc", selection: NSRange(location: 0, length: 7))
        try checkIndent("a\nb\nc", NSRange(location: 1, length: 1), remove: false,
                        output: "  a\nb\nc", selection: NSRange(location: 3, length: 1))
        try checkIndent("abc\ndef", NSRange(location: 1, length: 1), remove: false,
                        output: "  abc\ndef", selection: NSRange(location: 3, length: 1))
        try checkIndent("甲😀\n乙\n", NSRange(location: 0, length: 6), remove: false,
                        output: "  甲😀\n  乙\n", selection: NSRange(location: 0, length: 10))
        try checkIndent("last\n", NSRange(location: 5, length: 0), remove: false,
                        output: "last\n  ", selection: NSRange(location: 7, length: 0))
        try checkIndent("a\r\nb\r\nc", NSRange(location: 0, length: 6), remove: false,
                        output: "  a\r\n  b\r\nc", selection: NSRange(location: 0, length: 10))
        try checkIndent("x\ny", NSRange(location: 0, length: 3), remove: false, spaces: 4,
                        output: "    x\n    y", selection: NSRange(location: 0, length: 11))

        try checkIndent("  one\n    two\n\tthree", NSRange(location: 0, length: 20), remove: true,
                        output: "one\n  two\nthree", selection: NSRange(location: 0, length: 15))
        try checkIndent("  text", NSRange(location: 1, length: 0), remove: true,
                        output: "text", selection: NSRange(location: 0, length: 0))
        try checkIndent("    text", NSRange(location: 4, length: 0), remove: true,
                        output: "  text", selection: NSRange(location: 2, length: 0))
        try checkIndent(" text", NSRange(location: 0, length: 2), remove: true,
                        output: "text", selection: NSRange(location: 0, length: 1))
        try checkIndent("\t text", NSRange(location: 2, length: 0), remove: true,
                        output: " text", selection: NSRange(location: 1, length: 0))
        try checkIndent(" \ttext", NSRange(location: 3, length: 0), remove: true,
                        output: "\ttext", selection: NSRange(location: 2, length: 0))
        try checkIndent("  甲\n  😀\n  乙", NSRange(location: 0, length: 9), remove: true,
                        output: "甲\n😀\n  乙", selection: NSRange(location: 0, length: 5))
        expect(TextEditing.indent(text: "text", selection: NSRange(location: 2, length: 0), remove: true) == nil,
               "outdent without leading whitespace is a no-op")
        expect(TextEditing.indent(text: "", selection: NSRange(location: 0, length: 0), remove: true) == nil,
               "outdent empty text is a no-op")

        try checkNewline("", NSRange(location: 0, length: 0), output: "\n", caret: 1)
        try checkNewline("  甲😀", NSRange(location: 5, length: 0), output: "  甲😀\n  ", caret: 8)
        try checkNewline("\t  value", NSRange(location: 8, length: 0), output: "\t  value\n\t  ", caret: 12)
        try checkNewline("    value", NSRange(location: 2, length: 0), output: "  \n    value", caret: 5)
        try checkNewline("    value", NSRange(location: 0, length: 0), output: "\n    value", caret: 1)
        try checkNewline("  first\n    second", NSRange(location: 2, length: 12), output: "  \n  cond", caret: 5)
        try checkNewline("  \n", NSRange(location: 3, length: 0), output: "  \n\n", caret: 4)
        try checkNewline("\u{3000}正文", NSRange(location: 3, length: 0), output: "\u{3000}正文\n\u{3000}", caret: 5)
        try checkNewline("  text\r\n\tend", NSRange(location: 12, length: 0), output: "  text\r\n\tend\n\t", caret: 14)

        let invalidRanges = [NSRange(location: -1, length: 0), NSRange(location: 0, length: -1),
                             NSRange(location: NSNotFound, length: 0), NSRange(location: 0, length: Int.max),
                             NSRange(location: 5, length: 0), NSRange(location: 2, length: 0),
                             NSRange(location: 1, length: 1)]
        for range in invalidRanges {
            expect(TextEditing.indent(text: "甲😀乙", selection: range, remove: false) == nil, "invalid indent range \(range)")
            expect(TextEditing.newline(text: "甲😀乙", selection: range) == nil, "invalid newline range \(range)")
        }
        expect(TextEditing.indent(text: "x", selection: NSRange(location: 0, length: 0), remove: false, spaces: 0) == nil,
               "zero spaces is a no-op")
        expect(TextEditing.indent(text: "x", selection: NSRange(location: 0, length: 0), remove: false, spaces: -1) == nil,
               "negative indentation is rejected")

        try randomizedIndentRoundtrip()
        print("TextEditingTests: \(checks) checks passed")
    }

    private static func checkIndent(_ text: String, _ range: NSRange, remove: Bool, spaces: Int = 2,
                                    output: String, selection: NSRange) throws {
        guard let result = TextEditing.indent(text: text, selection: range, remove: remove, spaces: spaces) else {
            throw failure("Missing indentation result: \(text.debugDescription), \(range)")
        }
        expect(apply(result, to: text).utf8.elementsEqual(output.utf8), "indent output: \(apply(result, to: text).debugDescription) expected \(output.debugDescription)")
        expect(result.selection == selection, "indent selection \(result.selection), expected \(selection)")
        expect(Range(result.selection, in: output) != nil, "selection stays at valid Unicode boundaries")
    }

    private static func checkNewline(_ text: String, _ range: NSRange, output: String, caret: Int) throws {
        guard let result = TextEditing.newline(text: text, selection: range) else { throw failure("Missing newline result") }
        expect(result.range == range, "newline replaces exactly the requested selection")
        expect(apply(result, to: text).utf8.elementsEqual(output.utf8), "newline output: \(apply(result, to: text).debugDescription) expected \(output.debugDescription)")
        expect(result.selection == NSRange(location: caret, length: 0), "newline caret \(result.selection), expected \(caret)")
    }

    private static func randomizedIndentRoundtrip() throws {
        var state: UInt64 = 0x312AFA
        func random(_ count: Int) -> Int {
            state = state &* 6364136223846793005 &+ 1442695040888963407
            return Int((state >> 32) % UInt64(count))
        }
        for _ in 0..<500 {
            let count = random(12) + 1
            let newline = ["\n", "\r\n", "\r"][random(3)]
            let lines = (0..<count).map { _ in String(repeating: " ", count: random(7)) + ["甲😀乙", "e\u{301}", "key: value", ""][random(4)] }
            let text = lines.joined(separator: newline)
            let full = NSRange(location: 0, length: (text as NSString).length)
            if full.length == 0 { continue }
            let width = random(6) + 1
            guard let indented = TextEditing.indent(text: text, selection: full, remove: false, spaces: width) else { throw failure("random indent missing") }
            let changed = apply(indented, to: text)
            guard let outdented = TextEditing.indent(text: changed, selection: indented.selection, remove: true, spaces: width) else { throw failure("random outdent missing") }
            expect(apply(outdented, to: changed).utf8.elementsEqual(text.utf8), "full-line indent/outdent is byte exact")
            expect(Range(indented.selection, in: changed) != nil, "random selection stays Unicode safe")
        }
    }

    private static func apply(_ result: TextEditResult, to text: String) -> String {
        (text as NSString).replacingCharacters(in: result.range, with: result.replacement)
    }
    private static func failure(_ message: String) -> NSError {
        NSError(domain: "TextEditingTests", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
    }
    private static func expect(_ condition: @autoclosure () -> Bool, _ message: String) {
        guard condition() else { fatalError(message) }
        checks += 1
    }
}
