import Foundation

struct TextEditResult {
    let range: NSRange
    let replacement: String
    /// Selection in the complete document after applying this replacement.
    let selection: NSRange
}

enum TextEditing {
    private struct PrefixEdit {
        let location: Int
        let removed: Int
        let inserted: Int
    }

    /// Empty selections insert spaces. Nonempty selections indent their complete
    /// lines; a selection ending at the next line's start does not include that line.
    static func indent(text: String, selection: NSRange, remove: Bool, spaces: Int = 2) -> TextEditResult? {
        let source = text as NSString
        guard valid(selection, in: text, length: source.length), spaces > 0 else { return nil }
        if !remove && selection.length == 0 {
            let whitespace = String(repeating: " ", count: spaces)
            return TextEditResult(range: selection, replacement: whitespace,
                                  selection: NSRange(location: selection.location + spaces, length: 0))
        }

        let finalLocation = selection.length == 0 ? selection.location : NSMaxRange(selection) - 1
        let first = source.lineRange(for: NSRange(location: selection.location, length: 0))
        let last = source.lineRange(for: NSRange(location: finalLocation, length: 0))
        let block = NSRange(location: first.location, length: NSMaxRange(last) - first.location)
        let indentation = remove ? "" : String(repeating: " ", count: spaces)
        var replacement = ""
        var edits: [PrefixEdit] = []
        var cursor = block.location

        repeat {
            let line = source.lineRange(for: NSRange(location: cursor, length: 0))
            var removed = 0
            if remove && line.length > 0 {
                if source.character(at: cursor) == 9 {
                    removed = 1
                } else {
                    while removed < min(spaces, line.length), source.character(at: cursor + removed) == 32 {
                        removed += 1
                    }
                }
            }
            let inserted = remove ? 0 : spaces
            if removed > 0 || inserted > 0 {
                edits.append(PrefixEdit(location: cursor, removed: removed, inserted: inserted))
            }
            replacement += indentation
            replacement += source.substring(with: NSRange(location: cursor + removed, length: line.length - removed))
            cursor = NSMaxRange(line)
        } while cursor < NSMaxRange(block)

        guard !edits.isEmpty else { return nil }
        let start = mapped(selection.location, through: edits, includeInsertionAtPosition: false)
        let end = mapped(NSMaxRange(selection), through: edits, includeInsertionAtPosition: false)
        return TextEditResult(range: block, replacement: replacement,
                              selection: NSRange(location: start, length: max(0, end - start)))
    }

    /// Inserts the editor's internal LF plus the current line's leading whitespace.
    /// If the cursor is inside that whitespace, only the prefix before it is copied.
    static func newline(text: String, selection: NSRange) -> TextEditResult? {
        let source = text as NSString
        guard valid(selection, in: text, length: source.length) else { return nil }
        let line = source.lineRange(for: NSRange(location: selection.location, length: 0))
        var end = line.location
        while end < selection.location {
            let unit = source.character(at: end)
            guard let scalar = UnicodeScalar(unit), CharacterSet.whitespaces.contains(scalar) else { break }
            end += 1
        }
        let indentation = source.substring(with: NSRange(location: line.location, length: end - line.location))
        let replacement = "\n" + indentation
        return TextEditResult(range: selection, replacement: replacement,
                              selection: NSRange(location: selection.location + (replacement as NSString).length, length: 0))
    }

    private static func valid(_ range: NSRange, in text: String, length: Int) -> Bool {
        guard range.location >= 0, range.length >= 0, range.location <= length,
              range.length <= length - range.location else { return false }
        // Avoid splitting a surrogate pair or invalid Unicode string boundary.
        return Range(range, in: text) != nil
    }

    private static func mapped(_ position: Int, through edits: [PrefixEdit], includeInsertionAtPosition: Bool) -> Int {
        var delta = 0
        for edit in edits {
            if position < edit.location { break }
            if edit.removed == 0 {
                if position == edit.location && !includeInsertionAtPosition { break }
                delta += edit.inserted
            } else if position < edit.location + edit.removed {
                return edit.location + delta
            } else {
                delta += edit.inserted - edit.removed
            }
        }
        return position + delta
    }
}
