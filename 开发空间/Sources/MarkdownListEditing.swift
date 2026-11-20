import Foundation

struct MarkdownListItem: Equatable {
    let lineRange: NSRange
    let indent: String
    let marker: String
    let ordered: Bool
    let task: Bool
    let contentRange: NSRange
    let content: String
    let level: Int
}

/// Source-preserving list commands. UTF-16 ranges match NSTextView, and each
/// command returns one replacement without rewriting the line ending or suffix.
enum MarkdownListEditing {
    private struct Line {
        let start: Int, end: Int, next: Int
        let text: String
        let code: Bool, opensFence: Bool
    }
    private struct Marker {
        let line: Int, indentUnits: Int, indentColumns: Int, contentStart: Int, contentIndent: Int, level: Int
        let marker: String, number: Int, ordered: Bool, taskOffset: Int?
    }
    private struct Context {
        let lines: [Line]
        let markers: [Marker]
        let owners: [Int?]
        let indentedCode: [Bool]
    }
    private static let markerExpression = try! NSRegularExpression(pattern: #"^([ \t]*)([-+*]|[0-9]{1,9}[.)])([ \t]+)(\[[ xX]\][ \t]+)?"#)
    private static let fenceExpression = try! NSRegularExpression(pattern: #"^[ \t]*(`{3,}|~{3,})(.*)$"#)
    private static let breaksList = try! NSRegularExpression(pattern: #"^ {0,3}(?:#{1,6}(?:[ \t]+|$)|>|(?:[-*_][ \t]*){3,}$|\[[^\]]+\]:)"#)

    static func item(in text: String, at offset: Int) -> MarkdownListItem? {
        guard valid(NSRange(location: offset, length: 0), text) else { return nil }
        let context = context(text)
        guard let marker = context.markers.last(where: { context.lines[$0.line].start <= offset && offset <= context.lines[$0.line].end }) else { return nil }
        let line = context.lines[marker.line], source = text as NSString
        return MarkdownListItem(lineRange: NSRange(location: line.start, length: line.next - line.start),
                                indent: source.substring(with: NSRange(location: line.start, length: marker.indentUnits)),
                                marker: marker.ordered ? String(marker.number) : marker.marker, ordered: marker.ordered, task: marker.taskOffset != nil,
                                contentRange: NSRange(location: marker.contentStart, length: line.end - marker.contentStart),
                                content: source.substring(with: NSRange(location: marker.contentStart, length: line.end - marker.contentStart)), level: marker.level)
    }

    static func edit(text: String, selection: NSRange, action: MarkdownListAction) -> TextEditResult? {
        guard valid(selection, text) else { return nil }
        let source = text as NSString, context = context(text)
        let index = context.lines.lastIndex { $0.start <= selection.location } ?? 0
        let line = context.lines[index]
        let markerIndex = context.markers.firstIndex { $0.line == index }
        guard !line.code, !context.indentedCode[index] else { return nil }
        let selectedEnd = selection.length == 0 ? selection.location : NSMaxRange(selection) - 1
        if action == .newline {
            guard !context.lines.enumerated().contains(where: { index, line in
                line.start <= selectedEnd && line.next > selection.location && (line.code || context.indentedCode[index])
            }) else { return nil }
        }
        switch action {
        case .newline:
            let newline = preferredNewline(source, line)
            guard let markerIndex else {
                guard let ownerIndex = context.owners[index], !line.text.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
                let owner = context.markers[ownerIndex]
                guard indentation(line.text) >= owner.contentIndent else { return nil }
                let insertion = newline + nextPrefix(source, context.lines[owner.line], owner)
                return TextEditResult(range: selection, replacement: insertion, selection: NSRange(location: selection.location + insertion.utf16.count, length: 0))
            }
            let marker = context.markers[markerIndex]
            guard selection.location >= marker.contentStart else { return nil }
            let content = source.substring(with: NSRange(location: marker.contentStart, length: line.end - marker.contentStart))
            if selection.length == 0 && content.trimmingCharacters(in: .whitespaces).isEmpty {
                if marker.indentColumns > 0 { return changeIndent(source, selection, context, markerIndex, indent: false) }
                return TextEditResult(range: NSRange(location: line.start, length: line.end - line.start), replacement: "", selection: NSRange(location: line.start, length: 0))
            }
            let insertion = newline + nextPrefix(source, line, marker)
            return TextEditResult(range: selection, replacement: insertion, selection: NSRange(location: selection.location + insertion.utf16.count, length: 0))
        case .indent, .outdent:
            guard let markerIndex else { return nil }
            return changeIndent(source, selection, context, markerIndex, indent: action == .indent)
        case .toggleTask:
            guard let markerIndex, let offset = context.markers[markerIndex].taskOffset else { return nil }
            return TextEditResult(range: NSRange(location: offset, length: 1), replacement: source.character(at: offset) == 32 ? "x" : " ", selection: selection)
        }
    }

    private static func changeIndent(_ source: NSString, _ selection: NSRange, _ context: Context, _ rootIndex: Int, indent: Bool) -> TextEditResult? {
        let root = context.markers[rootIndex], first = root.line
        let previous = context.markers.prefix(rootIndex)
        let parent = previous.last { $0.indentColumns < root.indentColumns }
        let sibling = previous.last { $0.indentColumns == root.indentColumns && (parent == nil || $0.line > parent!.line) }
        if indent && sibling == nil { return nil }
        if indent, let sibling, context.lines.indices.contains(where: { index in
            index > sibling.line && index < root.line && !context.lines[index].text.trimmingCharacters(in: .whitespaces).isEmpty && indentation(context.lines[index].text) <= root.indentColumns && !context.markers.contains(where: { $0.line == index })
        }) { return nil }
        let change = max(0, indent ? sibling!.contentIndent - sibling!.indentColumns : root.indentColumns - (parent?.indentColumns ?? 0))
        guard change > 0 else { return nil }
        var last = first
        while last + 1 < context.lines.count && context.lines[last + 1].start < NSMaxRange(selection) { last += 1 }
        let depth = context.markers.filter { $0.line >= first && $0.line <= last }.map(\.indentColumns).min() ?? root.indentColumns
        var next = last + 1
        while next < context.lines.count {
            if context.lines[next].text.trimmingCharacters(in: .whitespaces).isEmpty { next += 1; continue }
            guard let owner = context.owners[next], context.markers[owner].line >= first, context.markers[owner].indentColumns >= depth,
                  !context.markers.contains(where: { $0.line == next && $0.indentColumns <= depth }) else { break }
            last = next; next += 1
        }
        var edits: [(range: NSRange, value: String)] = []
        for index in first...last {
            let line = context.lines[index], raw = line.text as NSString
            if line.text.trimmingCharacters(in: .whitespaces).isEmpty { continue }
            if indent {
                var leading = 0
                while leading < raw.length && [9, 32].contains(raw.character(at: leading)) { leading += 1 }
                let whitespace = raw.substring(to: leading)
                if whitespace.contains("\t") { edits.append((NSRange(location: line.start, length: leading), String(repeating: " ", count: indentation(line.text) + change))) }
                else { edits.append((NSRange(location: line.start, length: 0), String(repeating: " ", count: change))) }
            } else {
                var removed = 0, columns = 0
                while removed < raw.length && columns < change && [9, 32].contains(raw.character(at: removed)) {
                    columns += raw.character(at: removed) == 9 ? 4 - columns % 4 : 1; removed += 1
                }
                if removed > 0 { edits.append((NSRange(location: line.start, length: removed), String(repeating: " ", count: max(0, columns - change)))) }
            }
        }
        guard let firstEdit = edits.first, let lastEdit = edits.last else { return nil }
        let range = NSRange(location: firstEdit.range.location, length: NSMaxRange(lastEdit.range) - firstEdit.range.location)
        let value = NSMutableString(string: source.substring(with: range))
        for edit in edits.reversed() { value.replaceCharacters(in: NSRange(location: edit.range.location - range.location, length: edit.range.length), with: edit.value) }
        func map(_ position: Int) -> Int {
            var delta = 0
            for edit in edits {
                if position < edit.range.location { break }
                if position < NSMaxRange(edit.range) { return edit.range.location + delta + edit.value.utf16.count }
                delta += edit.value.utf16.count - edit.range.length
            }
            return position + delta
        }
        let start = map(selection.location), end = map(NSMaxRange(selection))
        return TextEditResult(range: range, replacement: value as String, selection: NSRange(location: start, length: max(0, end - start)))
    }

    private static func context(_ text: String) -> Context {
        let source = text as NSString
        var lines: [Line] = [], fence: String?, start = 0
        while start <= source.length {
            var end = start
            while end < source.length && ![10, 13].contains(source.character(at: end)) { end += 1 }
            var next = end
            if next < source.length { let cr = source.character(at: next) == 13; next += 1; if cr && next < source.length && source.character(at: next) == 10 { next += 1 } }
            let text = source.substring(with: NSRange(location: start, length: end - start)), raw = text as NSString
            let match = fenceExpression.firstMatch(in: text, range: NSRange(location: 0, length: raw.length))
            let code = fence != nil || match != nil
            let token = match.map { raw.substring(with: $0.range(at: 1)) }
            let rest = match.map { raw.substring(with: $0.range(at: 2)) } ?? ""
            let opens = fence == nil && token != nil && (token!.first != "`" || !rest.contains("`"))
            if opens { fence = token }
            else if let current = fence, let token, token.first == current.first, token.count >= current.count, rest.trimmingCharacters(in: .whitespaces).isEmpty { fence = nil }
            lines.append(Line(start: start, end: end, next: next, text: text, code: code, opensFence: opens))
            if next == source.length && end == source.length { break }
            start = next
        }
        var markers: [Marker] = [], depths: [Int] = [], listContext = false, afterBlank = false
        for (index, line) in lines.enumerated() {
            let raw = line.text as NSString, indent = indentation(line.text)
            if line.text.trimmingCharacters(in: .whitespaces).isEmpty { afterBlank = true; continue }
            if line.code || breaksList.firstMatch(in: line.text, range: NSRange(location: 0, length: raw.length)) != nil {
                if indent == 0 { listContext = false; depths.removeAll() }; afterBlank = false; continue
            }
            guard let match = markerExpression.firstMatch(in: line.text, range: NSRange(location: 0, length: raw.length)), indent < 4 || listContext else {
                if afterBlank && indent == 0 { listContext = false; depths.removeAll() }; afterBlank = false; continue
            }
            afterBlank = false
            while let last = depths.last, last > indent { depths.removeLast() }
            if depths.last == nil || depths.last! < indent { depths.append(indent) }
            let token = raw.substring(with: match.range(at: 2)), ordered = token.first?.isNumber == true
            let task = match.range(at: 4).location != NSNotFound
            let contentIndent = columns(raw.substring(to: task ? match.range(at: 4).location : match.range.length))
            markers.append(Marker(line: index, indentUnits: match.range(at: 1).length, indentColumns: indent, contentStart: line.start + match.range.length,
                                  contentIndent: contentIndent, level: depths.count - 1, marker: token,
                                  number: ordered ? Int(token.dropLast()) ?? 1 : 1, ordered: ordered,
                                  taskOffset: task ? line.start + match.range(at: 4).location + 1 : nil))
            listContext = true
        }
        var owners: [Int?] = Array(repeating: nil, count: lines.count), indentedCode = Array(repeating: false, count: lines.count)
        let byLine = Dictionary(uniqueKeysWithValues: markers.enumerated().map { ($0.element.line, $0.offset) })
        var parents: [Int] = [], paragraphOpen = false, wasCode = false, codeOwner: Int?
        for (index, line) in lines.enumerated() {
            let indent = indentation(line.text)
            if line.code {
                if !wasCode || line.opensFence { codeOwner = parents.last { markers[$0].contentIndent <= indent } }
                owners[index] = codeOwner
                if codeOwner == nil { parents.removeAll() }
                wasCode = true; paragraphOpen = false; continue
            }
            wasCode = false
            if line.text.trimmingCharacters(in: .whitespaces).isEmpty { paragraphOpen = false; continue }
            if let markerIndex = byLine[index] {
                let marker = markers[markerIndex]
                while let last = parents.last, markers[last].indentColumns >= marker.indentColumns { parents.removeLast() }
                parents.append(markerIndex); owners[index] = markerIndex
                paragraphOpen = !source.substring(with: NSRange(location: marker.contentStart, length: line.end - marker.contentStart)).trimmingCharacters(in: .whitespaces).isEmpty
            } else {
                var owner = parents.last { markers[$0].contentIndent <= indent }
                let table = line.text.contains("|") && index + 1 < lines.count && lines[index + 1].text.contains("-") && lines[index + 1].text.allSatisfy { "|-: \t".contains($0) }
                let startsBlock = table || breaksList.firstMatch(in: line.text, range: NSRange(location: 0, length: (line.text as NSString).length)) != nil
                if owner == nil && paragraphOpen && !startsBlock { owner = parents.last }
                indentedCode[index] = !paragraphOpen && indent >= (owner.map { markers[$0].contentIndent } ?? 0) + 4
                owners[index] = owner
                if let owner { while let last = parents.last, markers[last].line > markers[owner].line { parents.removeLast() } }
                else { parents.removeAll() }
                paragraphOpen = !startsBlock && !indentedCode[index]
            }
        }
        return Context(lines: lines, markers: markers, owners: owners, indentedCode: indentedCode)
    }
    private static func nextPrefix(_ source: NSString, _ line: Line, _ marker: Marker) -> String {
        let indent = source.substring(with: NSRange(location: line.start, length: marker.indentUnits))
        let token = marker.ordered ? "\(min(999_999_999, marker.number + 1))" + String(marker.marker.suffix(1)) : marker.marker
        return indent + token + " " + (marker.taskOffset != nil ? "[ ] " : "")
    }
    private static func indentation(_ text: String) -> Int { columns(String(text.prefix { $0 == " " || $0 == "\t" })) }
    private static func columns(_ text: String) -> Int { text.reduce(0) { $0 + ($1 == "\t" ? 4 - $0 % 4 : 1) } }
    private static func preferredNewline(_ source: NSString, _ line: Line) -> String {
        if line.next > line.end { return source.substring(with: NSRange(location: line.end, length: line.next - line.end)) }
        let value = source as String
        return value.contains("\r\n") ? "\r\n" : value.contains("\r") ? "\r" : "\n"
    }
    private static func valid(_ range: NSRange, _ text: String) -> Bool {
        let source = text as NSString
        guard range.location >= 0, range.length >= 0, range.location <= source.length, range.length <= source.length - range.location else { return false }
        for offset in [range.location, NSMaxRange(range)] where offset > 0 && offset < source.length {
            let before = source.character(at: offset - 1), after = source.character(at: offset)
            if (0xD800...0xDBFF).contains(before) && (0xDC00...0xDFFF).contains(after) || before == 13 && after == 10 { return false }
        }
        return true
    }
}
