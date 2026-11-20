import Foundation

enum MarkdownTableAlignment: Equatable {
    case left, center, right
    var marker: String {
        switch self { case .left: return ":---"; case .center: return ":---:"; case .right: return "---:" }
    }
}

struct MarkdownTableModel: Equatable {
    struct Cell: Equatable { var text: String; var alignment: MarkdownTableAlignment }
    var rows: [[Cell]]
    var range: NSRange
    var lineRanges: [NSRange]
    var rowLineIndexes: [Int]
    var columnCount: Int { rows.first?.count ?? 0 }
}

enum MarkdownTableAction: Equatable {
    case tab(backward: Bool), insertRow, deleteRow, insertColumn, deleteColumn
    case align(MarkdownTableAlignment)
}

/// Table commands use UTF-16 throughout, change only the necessary source, and
/// return the final selection along with a single undoable replacement.
enum MarkdownTableEditing {
    private static let maximumColumns = 32, maximumCells = 1024, maximumCharacters = 100_000
    private struct Line { let start: Int; let end: Int; let next: Int }
    private struct Row { let line: Line; let cells: [NSRange]; let indent: String }
    private struct Table {
        let rows: [Row]
        var start: Int { rows[0].line.start }
        var end: Int { rows[rows.count - 1].line.end }
        var columns: Int { rows[0].cells.count }
        var cells: Int { (rows.count - 1) * columns }
    }

    static func table(in text: String, at offset: Int) -> MarkdownTableModel? {
        let source = text as NSString
        guard valid(NSRange(location: offset, length: 0), text), !inCode(source, offset, 0),
              let table = find(source, offset) else { return nil }
        let alignments = table.rows[1].cells.map { alignment(source.substring(with: content(source, $0))) }
        let indexes = table.rows.indices.filter { $0 != 1 }
        return MarkdownTableModel(rows: indexes.map { index in
            table.rows[index].cells.enumerated().map { column, range in
                MarkdownTableModel.Cell(text: source.substring(with: content(source, range)), alignment: alignments[column])
            }
        }, range: NSRange(location: table.start, length: table.end - table.start),
           lineRanges: table.rows.map { NSRange(location: $0.line.start, length: $0.line.next - $0.line.start) },
           rowLineIndexes: Array(indexes))
    }

    static func edit(text: String, selection: NSRange, action: MarkdownTableAction) -> TextEditResult? {
        let source = text as NSString
        guard valid(selection, text), !inCode(source, selection.location, selection.length),
              let table = find(source, selection.location),
              let rowIndex = table.rows.firstIndex(where: { selection.location >= $0.line.start && selection.location <= $0.line.end }) else { return nil }
        let row = table.rows[rowIndex]
        let column = row.cells.firstIndex(where: { selection.location <= NSMaxRange($0) }) ?? table.columns - 1
        let cell = row.cells[column]
        guard selection.length == 0 || (selection.location >= cell.location && NSMaxRange(selection) <= NSMaxRange(cell)) else { return nil }
        switch action {
        case .tab(let backward):
            guard rowIndex != 1 else { return nil }
            let nextColumn = column + (backward ? -1 : 1)
            if row.cells.indices.contains(nextColumn) { return select(source, origin: selection.location, cell: row.cells[nextColumn]) }
            if backward {
                let previous = rowIndex == 2 ? 0 : rowIndex - 1
                return previous >= 0 ? select(source, origin: selection.location, cell: table.rows[previous].cells[table.columns - 1]) : nil
            }
            let next = rowIndex == 0 ? 2 : rowIndex + 1
            return next < table.rows.count ? select(source, origin: selection.location, cell: table.rows[next].cells[0]) : addRow(source, table, after: table.rows.count - 1, column: 0)
        case .insertRow:
            guard rowIndex != 1 else { return nil }
            return addRow(source, table, after: rowIndex == 0 ? 1 : rowIndex, column: column)
        case .deleteRow:
            guard rowIndex >= 2 else { return nil }
            let line = row.line
            var begin = line.start
            let end = line.next
            let targetRow: Int
            if rowIndex + 1 < table.rows.count { targetRow = rowIndex + 1 }
            else {
                targetRow = rowIndex > 2 ? rowIndex - 1 : 0
                if line.next == line.end { begin = table.rows[rowIndex - 1].line.end }
            }
            let target = content(source, table.rows[targetRow].cells[column])
            let location = target.location >= end ? target.location - (end - begin) : target.location
            return TextEditResult(range: NSRange(location: begin, length: end - begin), replacement: "", selection: NSRange(location: location, length: target.length))
        case .insertColumn, .deleteColumn:
            let insert = action == .insertColumn
            let columns = table.columns + (insert ? 1 : -1)
            guard columns >= 1, columns <= maximumColumns, (table.rows.count - 1) * columns <= maximumCells else { return nil }
            var replacement = "", targetStart = 0
            let targetRow = rowIndex == 1 ? 0 : rowIndex
            for (index, row) in table.rows.enumerated() {
                if index == targetRow { targetStart = replacement.utf16.count }
                replacement += source.substring(with: NSRange(location: row.line.start, length: row.cells[0].location - row.line.start))
                var cells = row.cells.map { source.substring(with: $0) }
                if insert { cells.insert(index == 1 ? " --- " : "  ", at: column + 1) }
                else { cells.remove(at: column) }
                replacement += cells.joined(separator: "|")
                let last = row.cells[row.cells.count - 1]
                replacement += source.substring(with: NSRange(location: NSMaxRange(last), length: row.line.end - NSMaxRange(last)))
                if index + 1 < table.rows.count { replacement += source.substring(with: NSRange(location: row.line.end, length: row.line.next - row.line.end)) }
                guard replacement.utf16.count <= maximumCharacters else { return nil }
            }
            let result = replacement as NSString
            guard let changed = parseRow(result, line(result, targetStart)) else { return nil }
            let targetColumn = insert ? column + 1 : min(column, columns - 1)
            let target = content(result, changed.cells[targetColumn])
            return minimal(source, range: NSRange(location: table.start, length: table.end - table.start), value: replacement,
                           selection: NSRange(location: table.start + target.location, length: target.length))
        case .align(let style):
            let delimiter = content(source, table.rows[1].cells[column])
            let original = source.substring(with: delimiter)
            let dashes = original.trimmingCharacters(in: CharacterSet(charactersIn: ":"))
            let value: String
            switch style { case .left: value = ":" + dashes; case .center: value = ":" + dashes + ":"; case .right: value = dashes + ":" }
            guard table.end - table.start + value.utf16.count - delimiter.length <= maximumCharacters else { return nil }
            func map(_ position: Int) -> Int {
                if position < delimiter.location { return position }
                if position >= NSMaxRange(delimiter) { return position + value.utf16.count - delimiter.length }
                return delimiter.location + min(position - delimiter.location, value.utf16.count)
            }
            let mapped = NSRange(location: map(selection.location), length: max(0, map(NSMaxRange(selection)) - map(selection.location)))
            return minimal(source, range: delimiter, value: value, selection: mapped)
        }
    }

    /// Rectangular TSV with quoted tabs/doubled quotes; embedded newlines and
    /// malformed quoting are refused. A final record terminator adds no blank row.
    static func markdownTable(fromTSV text: String) -> String? {
        let source = text as NSString
        guard source.length > 0, source.length <= maximumCharacters else { return nil }
        var rows: [[String]] = [], position = 0, columns = 0, count = 0
        while position < source.length {
            var row: [String] = [], more = true
            while more {
                let quoted = position < source.length && source.character(at: position) == 34
                if quoted { position += 1 }
                var value = "", closed = !quoted
                while position < source.length {
                    let unit = source.character(at: position)
                    if quoted && unit == 34 {
                        position += 1
                        if position < source.length && source.character(at: position) == 34 { value += "\""; position += 1; continue }
                        closed = true; break
                    }
                    if !quoted && [9, 10, 13].contains(unit) { break }
                    if !quoted && unit == 34 { return nil }
                    let span = source.rangeOfComposedCharacterSequence(at: position)
                    value += source.substring(with: span); position = NSMaxRange(span)
                }
                guard closed, position == source.length || [9, 10, 13].contains(source.character(at: position)), let encoded = encodeCell(value) else { return nil }
                row.append(encoded); count += 1
                guard row.count <= maximumColumns, count <= maximumCells else { return nil }
                more = position < source.length && source.character(at: position) == 9
                if more { position += 1 }
            }
            if columns == 0 { columns = row.count }
            guard row.count == columns else { return nil }
            rows.append(row)
            if position < source.length {
                let isCR = source.character(at: position) == 13; position += 1
                if isCR && position < source.length && source.character(at: position) == 10 { position += 1 }
            }
        }
        var lines = rows.map { "| " + $0.joined(separator: " | ") + " |" }
        lines.insert("| " + Array(repeating: "---", count: columns).joined(separator: " | ") + " |", at: 1)
        let result = lines.joined(separator: "\n") + "\n"
        return result.utf16.count <= maximumCharacters ? result : nil
    }

    private static func addRow(_ source: NSString, _ table: Table, after: Int, column: Int) -> TextEditResult? {
        guard table.cells + table.columns <= maximumCells else { return nil }
        let anchor = table.rows[after].line
        let separator = newline(source, anchor)
        let value = separator + table.rows[0].indent + "| " + Array(repeating: "", count: table.columns).joined(separator: " | ") + " |"
        guard table.end - table.start + value.utf16.count <= maximumCharacters else { return nil }
        let inserted = value as NSString
        guard let row = parseRow(inserted, line(inserted, separator.utf16.count)) else { return nil }
        let target = content(inserted, row.cells[column])
        return TextEditResult(range: NSRange(location: anchor.end, length: 0), replacement: value,
                              selection: NSRange(location: anchor.end + target.location, length: target.length))
    }

    private static func select(_ source: NSString, origin: Int, cell: NSRange) -> TextEditResult {
        TextEditResult(range: NSRange(location: origin, length: 0), replacement: "", selection: content(source, cell))
    }
    private static func content(_ source: NSString, _ range: NSRange) -> NSRange {
        var start = range.location, end = NSMaxRange(range)
        while start < end && [9, 32].contains(source.character(at: start)) { start += 1 }
        while end > start && [9, 32].contains(source.character(at: end - 1)) { end -= 1 }
        return start == end ? NSRange(location: range.location + (range.length > 0 ? 1 : 0), length: 0) : NSRange(location: start, length: end - start)
    }
    private static func minimal(_ source: NSString, range: NSRange, value: String, selection: NSRange) -> TextEditResult {
        let replacement = value as NSString
        var prefix = 0, suffix = 0
        while prefix < min(range.length, replacement.length) && source.character(at: range.location + prefix) == replacement.character(at: prefix) { prefix += 1 }
        while prefix > 0 && (!boundary(source, range.location + prefix) || !boundary(replacement, prefix)) { prefix -= 1 }
        while suffix < min(range.length, replacement.length) - prefix && source.character(at: NSMaxRange(range) - suffix - 1) == replacement.character(at: replacement.length - suffix - 1) { suffix += 1 }
        while suffix > 0 && (!boundary(source, NSMaxRange(range) - suffix) || !boundary(replacement, replacement.length - suffix)) { suffix -= 1 }
        return TextEditResult(range: NSRange(location: range.location + prefix, length: range.length - prefix - suffix),
                              replacement: replacement.substring(with: NSRange(location: prefix, length: replacement.length - prefix - suffix)), selection: selection)
    }
    private static func valid(_ range: NSRange, _ text: String) -> Bool {
        let source = text as NSString
        return range.location >= 0 && range.length >= 0 && range.location <= source.length && range.length <= source.length - range.location && boundary(source, range.location) && boundary(source, NSMaxRange(range))
    }
    private static func boundary(_ source: NSString, _ offset: Int) -> Bool {
        guard offset >= 0, offset <= source.length else { return false }
        if offset > 0 && offset < source.length {
            let before = source.character(at: offset - 1), after = source.character(at: offset)
            if (0xD800...0xDBFF).contains(before) && (0xDC00...0xDFFF).contains(after) { return false }
            if before == 13 && after == 10 { return false }
        }
        return true
    }

    private static func find(_ source: NSString, _ offset: Int) -> Table? {
        let current = lineAt(source, offset)
        guard let currentRow = parseRow(source, current) else { return nil }
        var header = current
        let next = current.next > current.end ? parseRow(source, line(source, current.next)) : nil
        if next == nil || !isSeparator(source, next!) {
            var row = currentRow, attempts = 0
            while !isSeparator(source, row) {
                attempts += 1
                guard row.line.start > 0, attempts <= maximumCells, offset - row.line.start <= maximumCharacters,
                      let previous = parseRow(source, lineAt(source, row.line.start - 1)) else { return nil }
                row = previous
            }
            guard row.line.start > 0 else { return nil }
            header = lineAt(source, row.line.start - 1)
        }
        guard let head = parseRow(source, header), !isSeparator(source, head), !inCode(source, header.start, 0),
              header.next > header.end, let delimiter = parseRow(source, line(source, header.next)), isSeparator(source, delimiter),
              head.cells.count == delimiter.cells.count, head.indent == delimiter.indent else { return nil }
        var rows = [head, delimiter], after = delimiter.line
        while after.next > after.end && after.next < source.length {
            let nextLine = line(source, after.next)
            guard let row = parseRow(source, nextLine) else {
                if source.substring(with: NSRange(location: nextLine.start, length: nextLine.end - nextLine.start)).contains("|") { return nil }
                break
            }
            guard row.cells.count == head.cells.count, row.indent == head.indent, !isSeparator(source, row),
                  rows.count * head.cells.count <= maximumCells, nextLine.end - header.start <= maximumCharacters else { return nil }
            rows.append(row); after = nextLine
        }
        let table = Table(rows: rows)
        return offset <= table.end && table.end - table.start <= maximumCharacters ? table : nil
    }
    private static func parseRow(_ source: NSString, _ line: Line) -> Row? {
        guard line.end - line.start <= maximumCharacters else { return nil }
        var first = line.start, end = line.end
        while first < end && source.character(at: first) == 32 { first += 1 }
        guard first - line.start <= 3, first < end, source.character(at: first) != 9 else { return nil }
        while end > first && [9, 32].contains(source.character(at: end - 1)) { end -= 1 }
        var runs: [(start: Int, end: Int)] = [], cursor = first
        while cursor < end {
            if source.character(at: cursor) != 96 { cursor += 1; continue }
            let start = cursor; cursor += 1
            while cursor < end && source.character(at: cursor) == 96 { cursor += 1 }
            runs.append((start, cursor))
        }
        var nextRuns: [Int: Int] = [:], closes: [Int: Int] = [:]
        for run in runs.reversed() { if let next = nextRuns[run.end - run.start] { closes[run.start] = next }; nextRuns[run.end - run.start] = run.end }
        var pipes: [Int] = []; cursor = first
        while cursor < end {
            let unit = source.character(at: cursor)
            if unit == 92 { cursor += min(2, end - cursor); continue }
            if unit == 96 {
                let start = cursor
                while cursor < end && source.character(at: cursor) == 96 { cursor += 1 }
                if let close = closes[start] { cursor = close }
                continue
            }
            if unit == 124 { pipes.append(cursor); if pipes.count > maximumColumns + 1 { return nil } }
            cursor += 1
        }
        guard let firstPipe = pipes.first, let lastPipe = pipes.last else { return nil }
        let leading = firstPipe == first, trailing = lastPipe == end - 1
        var start = leading ? first + 1 : first, cells: [NSRange] = []
        for pipe in pipes {
            if leading && pipe == first { continue }
            cells.append(NSRange(location: start, length: pipe - start)); start = pipe + 1
        }
        if !trailing { cells.append(NSRange(location: start, length: line.end - start)) }
        guard cells.count >= 1, cells.count <= maximumColumns else { return nil }
        return Row(line: line, cells: cells, indent: source.substring(with: NSRange(location: line.start, length: first - line.start)))
    }
    private static func isSeparator(_ source: NSString, _ row: Row) -> Bool {
        row.cells.allSatisfy { cell in
            var text = source.substring(with: content(source, cell))
            if text.hasPrefix(":") { text.removeFirst() }
            if text.hasSuffix(":") { text.removeLast() }
            return text.utf16.count >= 3 && text.allSatisfy { $0 == "-" }
        }
    }
    private static func alignment(_ value: String) -> MarkdownTableAlignment { value.hasSuffix(":") ? (value.hasPrefix(":") ? .center : .right) : .left }
    private static func lineAt(_ source: NSString, _ offset: Int) -> Line {
        var start = offset
        if start > 0 && start < source.length && source.character(at: start) == 10 && source.character(at: start - 1) == 13 { start -= 1 }
        while start > 0 && ![10, 13].contains(source.character(at: start - 1)) { start -= 1 }
        return line(source, start)
    }
    private static func line(_ source: NSString, _ start: Int) -> Line {
        var end = min(start, source.length)
        while end < source.length && ![10, 13].contains(source.character(at: end)) { end += 1 }
        var next = end
        if next < source.length { let cr = source.character(at: next) == 13; next += 1; if cr && next < source.length && source.character(at: next) == 10 { next += 1 } }
        return Line(start: min(start, source.length), end: end, next: next)
    }
    private static func newline(_ source: NSString, _ line: Line) -> String {
        if line.next > line.end { return source.substring(with: NSRange(location: line.end, length: line.next - line.end)) }
        var cursor = line.start - 1
        while cursor >= 0 {
            if source.character(at: cursor) == 10 { return cursor > 0 && source.character(at: cursor - 1) == 13 ? "\r\n" : "\n" }
            if source.character(at: cursor) == 13 { return "\r" }; cursor -= 1
        }
        return "\n"
    }
    private static func inCode(_ source: NSString, _ start: Int, _ length: Int) -> Bool {
        var fence: unichar = 0, fenceLength = 0, current = line(source, 0)
        let end = length == 0 ? start : start + length - 1
        while true {
            var first = current.start, indent = 0
            while first < current.end && [9, 32].contains(source.character(at: first)) { indent += source.character(at: first) == 9 ? 4 - indent % 4 : 1; first += 1 }
            var code = fenceLength > 0 || indent >= 4
            if indent < 4 && first < current.end && [96, 126].contains(source.character(at: first)) {
                let marker = source.character(at: first); var run = first + 1
                while run < current.end && source.character(at: run) == marker { run += 1 }
                let rest = source.substring(with: NSRange(location: run, length: current.end - run))
                if fenceLength > 0 && marker == fence && run - first >= fenceLength && rest.trimmingCharacters(in: .whitespaces).isEmpty { code = true; fenceLength = 0 }
                else if fenceLength == 0 && run - first >= 3 && (marker != 96 || !rest.contains("`")) { code = true; fence = marker; fenceLength = run - first }
            }
            if code && current.start <= end && (current.next > start || current.end == source.length && start == current.end) { return true }
            if current.next > end || current.next == current.end { return false }
            current = line(source, current.next)
        }
    }
    private static func encodeCell(_ source: String) -> String? {
        let literal = source.contains { "<>&".contains($0) }
        if literal && source.contains("`") { return nil }
        if literal && source.reversed().prefix(while: { $0 == "\\" }).count % 2 == 1 { return nil }
        var result = literal ? "`" : ""
        for scalar in source.unicodeScalars {
            if scalar == "\n" || scalar == "\r" { return nil }
            if scalar.value < 32 && scalar != "\t" { return nil }
            if scalar == "\t" { result += "    "; continue }
            if !literal && "\\|`![]*_~".unicodeScalars.contains(scalar) { result += "\\" }
            result.unicodeScalars.append(scalar)
        }
        if literal { result += "`" }
        return result.utf16.count <= maximumCharacters ? result : nil
    }
}
