import Foundation

/// Keeps a reading position useful when the file changes outside the app.
/// Offsets are UTF-16 locations, matching AppKit's NSTextView ranges.
enum TextAnchorMapper {
    static func map(oldText: String, newText: String, oldAnchor: Int) -> Int {
        let old = oldText as NSString
        let new = newText as NSString
        guard new.length > 0 else { return 0 }
        let anchor = clamp(oldAnchor, 0, old.length)
        guard old.length > 0, oldText != newText else { return min(anchor, new.length) }

        let lineStart = lastNewline(in: old, before: anchor) + 1
        let lineEnd = firstNewline(in: old, from: anchor) ?? old.length
        let lineLength = max(0, lineEnd - lineStart)
        if lineLength > 0 {
            let line = old.substring(with: NSRange(location: lineStart, length: lineLength)) as NSString
            let occurrence = closestLine(in: new, matching: line, target: anchor * new.length / max(1, old.length))
            if occurrence >= 0 {
                return clamp(occurrence + min(max(0, anchor - lineStart), line.length), 0, new.length)
            }
        }

        let prefix = commonPrefix(old, new)
        let suffix = commonSuffix(old, new, excludingPrefix: prefix)
        if anchor <= prefix { return min(anchor, new.length) }
        if anchor >= old.length - suffix {
            return clamp(new.length - (old.length - anchor), 0, new.length)
        }
        return clamp(Int((Double(anchor) * Double(new.length) / Double(old.length)).rounded()), 0, new.length)
    }

    private static func lastNewline(in text: NSString, before offset: Int) -> Int {
        guard offset > 0 else { return -1 }
        let range = text.range(of: "\n", options: .backwards, range: NSRange(location: 0, length: offset))
        return range.location == NSNotFound ? -1 : range.location
    }

    private static func firstNewline(in text: NSString, from offset: Int) -> Int? {
        guard offset < text.length else { return nil }
        let range = text.range(of: "\n", options: [], range: NSRange(location: offset, length: text.length - offset))
        return range.location == NSNotFound ? nil : range.location
    }

    private static func closestLine(in text: NSString, matching line: NSString, target: Int) -> Int {
        guard line.length > 0, line.length <= text.length else { return -1 }
        var cursor = 0
        var best = -1
        var bestDistance = Int.max
        while cursor <= text.length - line.length {
            let found = text.range(of: line as String, options: [], range: NSRange(location: cursor, length: text.length - cursor))
            guard found.location != NSNotFound else { break }
            let atStart = found.location == 0 || text.character(at: found.location - 1) == 10
            let end = NSMaxRange(found)
            let atEnd = end == text.length || text.character(at: end) == 10
            if atStart && atEnd {
                let distance = abs(found.location - target)
                if distance < bestDistance { best = found.location; bestDistance = distance }
            }
            cursor = found.location + max(1, found.length)
        }
        return best
    }

    private static func commonPrefix(_ left: NSString, _ right: NSString) -> Int {
        let length = min(left.length, right.length)
        var index = 0
        while index < length && left.character(at: index) == right.character(at: index) { index += 1 }
        return index
    }

    private static func commonSuffix(_ left: NSString, _ right: NSString, excludingPrefix prefix: Int) -> Int {
        let maximum = min(left.length, right.length) - prefix
        var count = 0
        while count < maximum,
              left.character(at: left.length - 1 - count) == right.character(at: right.length - 1 - count) {
            count += 1
        }
        return count
    }

    private static func clamp(_ value: Int, _ lower: Int, _ upper: Int) -> Int {
        min(upper, max(lower, value))
    }
}
