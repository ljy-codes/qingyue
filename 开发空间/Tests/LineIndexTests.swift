import AppKit
import Foundation

@main
struct LineIndexTests {
    static var checks = 0

    static func main() {
        _ = NSApplication.shared
        let editor = EditorTextView(frame: NSRect(x: 0, y: 0, width: 600, height: 300))
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 600, height: 300))
        scroll.documentView = editor
        let ruler = LineNumberRuler(editor: editor, scroll: scroll)
        var text = ""
        func reset(_ value: String) {
            text = value; editor.string = value; ruler.updateLines(value)
            expect(ruler.starts == scan(value), "Initial full index")
        }
        func edit(_ range: NSRange, _ replacement: String) {
            ruler.applyEdit(range: range, replacement: replacement)
            text = (text as NSString).replacingCharacters(in: range, with: replacement)
            editor.string = text
            expect(ruler.starts == scan(text), "Incremental index differs for range \(range), replacement \(replacement.debugDescription), text \(text.debugDescription)")
            expect(ruler.starts.first == 0, "First line must always begin at zero")
            expect(zip(ruler.starts, ruler.starts.dropFirst()).allSatisfy { $0 < $1 }, "Line starts must be strictly increasing")
        }

        reset("")
        edit(NSRange(location: 0, length: 0), "\n")
        expect(ruler.starts == [0, 1], "Leading and trailing empty lines")
        edit(NSRange(location: 0, length: 1), "")
        expect(ruler.starts == [0], "Removing only newline")
        edit(NSRange(location: 0, length: 0), "😀\n中文\n")
        expect(ruler.starts == [0, 3, 6], "Emoji uses two UTF16 units")
        edit(NSRange(location: 3, length: 0), "\n")
        edit(NSRange(location: 2, length: 2), "")
        edit(NSRange(location: (text as NSString).length, length: 0), "尾\n\n")
        edit(NSRange(location: 0, length: (text as NSString).length), "")

        reset("alpha\nbeta\ngamma\n")
        edit(NSRange(location: 6, length: 0), "X")
        edit(NSRange(location: 5, length: 1), "\n\n")
        edit(NSRange(location: 0, length: 7), "\n")
        edit(NSRange(location: (text as NSString).length - 1, length: 1), "")

        reset(String(repeating: "行😀\n", count: 20_000))
        edit(NSRange(location: 0, length: 0), "前缀")
        edit(NSRange(location: 12_002, length: 4), "替换\n一行\n")
        edit(NSRange(location: (text as NSString).length, length: 0), "\n末尾😀")

        var random = Generator(state: 0x230711)
        let replacements = ["", "a", "中文", "😀", "\n", "\n\n", "A\nB", "👨‍👩‍👧‍👦\n", "e\u{301}", "\t值\n"]
        reset("第一行😀\n第二行\n\n末尾")
        for iteration in 0..<2_000 {
            // Choose genuine String boundaries, then convert to AppKit's UTF16 range.
            // This covers variable-width emoji/combining graphemes without inventing
            // invalid ranges through half a surrogate pair.
            let boundaries = Array(text.indices) + [text.endIndex]
            let a = random.next(boundaries.count)
            let b = random.next(boundaries.count)
            let lower = min(a, b), upper = max(a, b)
            let range = NSRange(boundaries[lower]..<boundaries[upper], in: text)
            edit(range, replacements[random.next(replacements.count)])
            if iteration % 100 == 0 { edit(NSRange(location: (text as NSString).length, length: 0), "\n终点😀\n") }
        }

        let untouched = ruler.starts
        ruler.applyEdit(range: NSRange(location: NSNotFound, length: 0), replacement: "x")
        expect(ruler.starts == untouched, "Invalid range does not corrupt index")

        editor.allowsUndo = true
        func prepareEditor(_ value: String, selection: NSRange) {
            editor.breakUndoCoalescing()
            editor.string = value
            editor.tabUndo.removeAllActions()
            editor.setSelectedRange(selection)
        }
        func performEditorCommand(_ action: () -> Void) {
            editor.tabUndo.beginUndoGrouping()
            action()
            editor.tabUndo.endUndoGrouping()
            editor.breakUndoCoalescing()
        }

        prepareEditor("x", selection: NSRange(location: 1, length: 0))
        performEditorCommand { editor.insertTab(nil) }
        expect(editor.string == "x  ", "Tab inserts two spaces")
        expect(editor.selectedRange() == NSRange(location: 3, length: 0), "Tab advances the insertion point")
        editor.tabUndo.undo()
        expect(editor.string == "x", "Tab insertion is undoable")
        editor.tabUndo.redo()
        expect(editor.string == "x  ", "Tab insertion can be redone")

        prepareEditor("abc", selection: NSRange(location: 1, length: 1))
        performEditorCommand { editor.insertTab(nil) }
        expect(editor.string == "  abc", "Tab indents the full selected line without replacing selected text")
        expect(editor.selectedRange() == NSRange(location: 3, length: 1), "Single-line indentation preserves the selected character")
        editor.tabUndo.undo()
        expect(editor.string == "abc", "Selected-line indentation is one undo operation")
        editor.tabUndo.redo()
        expect(editor.string == "  abc", "Selected-line indentation can be redone")

        let multiline = "标题😀\n内容\n末行\n"
        let selectedLines = ("标题😀\n内容\n" as NSString).length
        prepareEditor(multiline, selection: NSRange(location: 0, length: selectedLines))
        performEditorCommand { editor.insertTab(nil) }
        expect(editor.string == "  标题😀\n  内容\n末行\n", "Tab indents complete selected lines including Chinese/Emoji")
        expect(editor.selectedRange() == NSRange(location: 0, length: selectedLines + 4), "Multi-line selection includes inserted line prefixes")
        expect(!editor.string.contains("  末行"), "A selection ending at the next line start excludes that line")
        editor.tabUndo.undo()
        expect(editor.string == multiline, "Multi-line indentation undoes atomically")

        prepareEditor("abc\ndef\n", selection: NSRange(location: 1, length: 4))
        performEditorCommand { editor.insertTab(nil) }
        expect(editor.string == "  abc\n  def\n", "Partial multi-line selection indents both affected lines")
        expect((editor.string as NSString).substring(with: editor.selectedRange()) == "bc\n  d", "Partial selection keeps its original endpoints after indentation")

        let indented = "  alpha\n\tbeta\n gamma\n"
        prepareEditor(indented, selection: NSRange(location: 0, length: (indented as NSString).length))
        performEditorCommand { editor.insertBacktab(nil) }
        expect(editor.string == "alpha\nbeta\ngamma\n", "ShiftTab removes up to two spaces or one leading Tab per line")
        expect(editor.selectedRange() == NSRange(location: 0, length: (editor.string as NSString).length), "ShiftTab adjusts a whole-document selection")
        editor.tabUndo.undo()
        expect(editor.string == indented, "ShiftTab is one undo operation")
        editor.tabUndo.redo()
        expect(editor.string == "alpha\nbeta\ngamma\n", "ShiftTab can be redone")

        prepareEditor("    value", selection: NSRange(location: 3, length: 0))
        performEditorCommand { editor.insertBacktab(nil) }
        expect(editor.string == "  value", "ShiftTab with an insertion point unindents the current line")
        expect(editor.selectedRange() == NSRange(location: 1, length: 0), "ShiftTab preserves caret position relative to remaining text")
        editor.tabUndo.undo()
        expect(editor.string == "    value", "Caret-line unindent is undoable")

        prepareEditor("abc", selection: NSRange(location: 1, length: 0))
        performEditorCommand { editor.insertBacktab(nil) }
        expect(editor.string == "abc", "ShiftTab on an unindented line is a no-op")
        // An explicit empty test undo group may still be recorded by AppKit.
        // Verify its observable effect instead of relying on private grouping state.
        if editor.tabUndo.canUndo { editor.tabUndo.undo() }
        expect(editor.string == "abc", "Undo after no-op ShiftTab does not alter text")

        let yamlLine = "  key: value"
        prepareEditor(yamlLine, selection: NSRange(location: (yamlLine as NSString).length, length: 0))
        performEditorCommand { editor.insertNewline(nil) }
        expect(editor.string == "  key: value\n  ", "Newline inherits the current line's spaces")
        expect(editor.selectedRange() == NSRange(location: (editor.string as NSString).length, length: 0), "Newline places the caret after inherited indentation")
        editor.tabUndo.undo()
        expect(editor.string == yamlLine, "Indented newline is one undo operation")
        editor.tabUndo.redo()
        expect(editor.string == "  key: value\n  ", "Indented newline can be redone")

        let tabbedLine = "\t  条目😀"
        prepareEditor(tabbedLine, selection: NSRange(location: (tabbedLine as NSString).length, length: 0))
        performEditorCommand { editor.insertNewline(nil) }
        expect(editor.string == "\t  条目😀\n\t  ", "Newline inherits a mixed Tab/space prefix exactly")
        editor.tabUndo.undo()
        expect(editor.string == tabbedLine, "Unicode indented newline undoes without changing the source")

        prepareEditor("    abcd", selection: NSRange(location: 6, length: 2))
        performEditorCommand { editor.insertNewline(nil) }
        expect(editor.string == "    ab\n    ", "Newline replaces selected text and preserves leading indentation")
        editor.tabUndo.undo()
        expect(editor.string == "    abcd", "Replacing a selection with newline is undoable")

        prepareEditor("    key", selection: NSRange(location: 2, length: 0))
        performEditorCommand { editor.insertNewline(nil) }
        expect(editor.string == "  \n    key", "Newline inside indentation only copies the prefix before the caret")
        prepareEditor("abc", selection: NSRange(location: 1, length: 0))
        performEditorCommand { editor.insertNewline(nil) }
        expect(editor.string == "a\nbc", "Newline in an unindented line adds no whitespace")

        prepareEditor("abc", selection: NSRange(location: 0, length: 3))
        editor.isEditable = false
        performEditorCommand { editor.insertTab(nil); editor.insertBacktab(nil); editor.insertNewline(nil) }
        expect(editor.string == "abc", "Editing commands leave a read-only text view unchanged")
        if editor.tabUndo.canUndo { editor.tabUndo.undo() }
        expect(editor.string == "abc", "Undo after read-only commands leaves text unchanged")
        editor.isEditable = true

        prepareEditor("", selection: NSRange(location: 0, length: 0))
        performEditorCommand { editor.insertNewline(nil) }
        expect(editor.string == "\n", "Newline works in an empty document")
        editor.tabUndo.undo()
        expect(editor.string.isEmpty, "Newline in an empty document is undoable")
        print("LineIndexTests: \(checks) checks passed")
    }

    static func scan(_ value: String) -> [Int] {
        [0] + value.utf16.enumerated().compactMap { $0.element == 10 ? $0.offset + 1 : nil }
    }
    static func expect(_ condition: @autoclosure () -> Bool, _ message: String) {
        guard condition() else { fatalError(message) }
        checks += 1
    }
    struct Generator {
        var state: UInt64
        mutating func next(_ limit: Int) -> Int {
            state = state &* 6364136223846793005 &+ 1442695040888963407
            return Int((state >> 32) % UInt64(limit))
        }
    }
}
