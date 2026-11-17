import Foundation

enum MarkdownTableAlignment: Equatable {
    case left, center, right

    var marker: String {
        switch self {
        case .left: return "---"
        case .center: return ":---:"
        case .right: return "---:"
        }
    }
}

struct MarkdownTableModel: Equatable {
    struct Cell: Equatable {
        var text: String
        var alignment: MarkdownTableAlignment
    }

    var rows: [[Cell]]
    var range: NSRange
    var lineRanges: [NSRange]
    var rowLineIndexes: [Int]

    var columnCount: Int { rows.map(\.count).max() ?? 0 }
}

enum MarkdownTableAction: Equatable {
    case tab(backward: Bool)
    case insertRow
    case deleteRow
    case insertColumn
    case deleteColumn
    case align(MarkdownTableAlignment)
}

/// Conservative Markdown table editing. It only activates when a complete
/// header/separator pair is present, and leaves code spans and escaped pipes in
/// cell text intact.
enum MarkdownTableEditing {
    static func table(in text: String, at offset: Int) -> MarkdownTableModel? {
        let source = text as NSString
        guard source.length > 0, offset >= 0, offset <= source.length else { return nil }
        let point = min(offset, max(0, source.length - (offset == source.length ? 1 : 0)))
        let allLines = lineRanges(in: source)
        guard let currentIndex = allLines.firstIndex(where: { NSLocationInRange(point, $0) || ($0.length == 0 && $0.location == point) }) else { return nil }
        var start = currentIndex
        var end = currentIndex
        while start > 0 && isTableLine(source.substring(with: allLines[start - 1])) { start -= 1 }
        while end + 1 < allLines.count && isTableLine(source.substring(with: allLines[end + 1])) { end += 1 }
        guard end - start >= 1 else { return nil }
        let separator = splitLine(source.substring(with: allLines[start + 1]))
        guard !separator.isEmpty, separator.allSatisfy(isSeparatorCell) else { return nil }
        let contentLines = Array(allLines[start...end])
        var rows: [[MarkdownTableModel.Cell]] = []
        var rowIndexes: [Int] = []
        for (index, line) in contentLines.enumerated() where index != 1 {
            let cells = splitLine(source.substring(with: line))
            guard !cells.isEmpty else { continue }
            let alignments = separator.map(alignment)
            rows.append(cells.enumerated().map { column, value in
                MarkdownTableModel.Cell(text: value, alignment: alignments.indices.contains(column) ? alignments[column] : .left)
            })
            rowIndexes.append(index)
        }
        guard rows.count >= 1, let first = contentLines.first, let last = contentLines.last else { return nil }
        return MarkdownTableModel(rows: rows,
                                  range: NSRange(location: first.location, length: NSMaxRange(last) - first.location),
                                  lineRanges: contentLines,
                                  rowLineIndexes: rowIndexes)
    }

    static func edit(text: String, selection: NSRange, action: MarkdownTableAction) -> TextEditResult? {
        guard let model = table(in: text, at: selection.location), model.columnCount > 0 else { return nil }
        let source = text as NSString
        let currentLine = source.lineRange(for: NSRange(location: min(selection.location, source.length), length: 0))
        let sourceLineIndex = model.lineRanges.firstIndex(where: { $0.location == currentLine.location }) ?? 0
        let logicalRow = model.rowLineIndexes.firstIndex { model.lineRanges[$0].location == currentLine.location } ?? rowIndex(model, sourceLineIndex)
        let row = max(0, min(model.rows.count - 1, logicalRow))
        let column = columnIndex(source.substring(with: currentLine), selection.location - currentLine.location)

        switch action {
        case .tab(let backward):
            return tabEdit(text: text, selection: selection, model: model, row: row, column: column, backward: backward)
        case .insertRow:
            var rows = model.rows
            let insertAt = min(rows.count, row + 1)
            rows.insert(emptyRow(count: model.columnCount, alignments: model.rows[0]), at: insertAt)
            return replace(model: model, rows: rows, target: (insertAt, min(column, model.columnCount - 1)), original: text)
        case .deleteRow:
            guard model.rows.count > 1 else { return nil }
            var rows = model.rows; rows.remove(at: min(row, rows.count - 1))
            return replace(model: model, rows: rows, target: (min(row, rows.count - 1), min(column, model.columnCount - 1)), original: text)
        case .insertColumn:
            var rows = model.rows
            let insertAt = min(model.columnCount, column + 1)
            for index in rows.indices { rows[index].insert(MarkdownTableModel.Cell(text: "", alignment: .left), at: min(insertAt, rows[index].count)) }
            return replace(model: model, rows: rows, target: (row, insertAt), original: text)
        case .deleteColumn:
            guard model.columnCount > 1 else { return nil }
            var rows = model.rows
            let removeAt = min(column, model.columnCount - 1)
            for index in rows.indices where removeAt < rows[index].count { rows[index].remove(at: removeAt) }
            return replace(model: model, rows: rows, target: (row, min(removeAt, model.columnCount - 2)), original: text)
        case .align(let alignment):
            var rows = model.rows
            let alignColumn = min(column, model.columnCount - 1)
            for index in rows.indices where alignColumn < rows[index].count { rows[index][alignColumn].alignment = alignment }
            return replace(model: model, rows: rows, target: (row, alignColumn), original: text)
        }
    }

    static func markdownTable(fromTSV text: String) -> String? {
        let lines = text.replacingOccurrences(of: "\r\n", with: "\n").split(separator: "\n", omittingEmptySubsequences: false)
        guard lines.count > 0, lines.allSatisfy({ !$0.contains("\n") }) else { return nil }
        let rows = lines.map { $0.split(separator: "\t", omittingEmptySubsequences: false).map(String.init) }
        let columns = rows.map(\.count).max() ?? 0
        guard columns > 0 else { return nil }
        let normalized = rows.map { row in row + Array(repeating: "", count: max(0, columns - row.count)) }
        var output = normalized.map { "| " + $0.map(escapeCell).joined(separator: " | ") + " |" }
        output.insert("| " + Array(repeating: "---", count: columns).joined(separator: " | ") + " |", at: 1)
        return output.joined(separator: "\n") + "\n"
    }

    private static func tabEdit(text: String, selection: NSRange, model: MarkdownTableModel, row: Int, column: Int, backward: Bool) -> TextEditResult? {
        var targetRow = row, targetColumn = column + (backward ? -1 : 1)
        var rows = model.rows
        if targetColumn < 0 { targetRow -= 1; targetColumn = model.columnCount - 1 }
        if targetColumn >= model.columnCount { targetRow += 1; targetColumn = 0 }
        if targetRow < 0 { return TextEditResult(range: NSRange(location: selection.location, length: 0), replacement: "", selection: NSRange(location: selection.location, length: 0)) }
        if targetRow >= rows.count {
            rows.append(emptyRow(count: model.columnCount, alignments: model.rows[0]))
            return replace(model: model, rows: rows, target: (rows.count - 1, 0), original: text)
        }
        let target = cellLocation(text: text, model: model, row: targetRow, column: targetColumn)
        return TextEditResult(range: NSRange(location: selection.location, length: 0), replacement: "", selection: NSRange(location: target, length: 0))
    }

    private static func replace(model: MarkdownTableModel, rows: [[MarkdownTableModel.Cell]], target: (Int, Int), original: String) -> TextEditResult {
        let old = original as NSString
        let hadTrailingNewline = model.range.location + model.range.length >= old.length || old.substring(with: model.range).hasSuffix("\n")
        let rendered = render(rows: rows, trailingNewline: hadTrailingNewline)
        let targetOffset = cellOffset(in: rendered, row: target.0, column: target.1)
        return TextEditResult(range: model.range, replacement: rendered,
                              selection: NSRange(location: model.range.location + targetOffset, length: 0))
    }

    private static func render(rows: [[MarkdownTableModel.Cell]], trailingNewline: Bool) -> String {
        let count = rows.map(\.count).max() ?? 1
        let normalized = rows.map { row in row + Array(repeating: MarkdownTableModel.Cell(text: "", alignment: .left), count: max(0, count - row.count)) }
        var lines = normalized.map { row in "| " + row.map { escapeCell($0.text) }.joined(separator: " | ") + " |" }
        let separator = normalized.first?.map { $0.alignment.marker } ?? Array(repeating: "---", count: count)
        lines.insert("| " + separator.joined(separator: " | ") + " |", at: 1)
        return lines.joined(separator: "\n") + (trailingNewline ? "\n" : "")
    }

    private static func emptyRow(count: Int, alignments: [MarkdownTableModel.Cell]) -> [MarkdownTableModel.Cell] {
        (0..<count).map { index in MarkdownTableModel.Cell(text: "", alignment: alignments.indices.contains(index) ? alignments[index].alignment : .left) }
    }

    private static func rowIndex(_ model: MarkdownTableModel, _ lineIndex: Int) -> Int {
        if lineIndex <= 0 { return 0 }
        return max(0, min(model.rows.count - 1, lineIndex - 1))
    }

    private static func cellLocation(text: String, model: MarkdownTableModel, row: Int, column: Int) -> Int {
        guard model.rowLineIndexes.indices.contains(row) else { return model.range.location + model.range.length }
        let source = text as NSString
        let line = source.lineRange(for: NSRange(location: model.range.location + model.lineRanges[model.rowLineIndexes[row]].location - model.lineRanges[0].location, length: 0))
        let lineText = source.substring(with: line)
        let starts = cellStarts(lineText)
        return line.location + (starts.indices.contains(column) ? starts[column] : max(0, line.length - 1))
    }

    private static func cellOffset(in text: String, row: Int, column: Int) -> Int {
        let source = text as NSString; let lines = lineRanges(in: source)
        guard lines.indices.contains(row), let start = cellStarts(source.substring(with: lines[row])).dropFirst(column).first else { return source.length }
        return lines[row].location + start
    }

    private static func columnIndex(_ line: String, _ offset: Int) -> Int {
        let starts = cellStarts(line); return max(0, starts.lastIndex(where: { $0 <= max(0, offset) }) ?? 0)
    }

    private static func cellStarts(_ line: String) -> [Int] {
        let cells = splitLineWithOffsets(line)
        return cells.map { $0.offset }
    }

    private static func lineRanges(in source: NSString) -> [NSRange] {
        var result: [NSRange] = []; var cursor = 0
        while cursor < source.length { let line = source.lineRange(for: NSRange(location: cursor, length: 0)); result.append(line); cursor = NSMaxRange(line) }
        if source.length == 0 || source.character(at: source.length - 1) == 10 { result.append(NSRange(location: source.length, length: 0)) }
        return result
    }

    private static func isTableLine(_ line: String) -> Bool { line.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("|") || line.trimmingCharacters(in: .whitespacesAndNewlines).contains("|") }
    private static func isSeparatorCell(_ cell: String) -> Bool { let t = cell.trimmingCharacters(in: .whitespaces); return t.replacingOccurrences(of: ":", with: "").count >= 3 && t.allSatisfy { $0 == "-" || $0 == ":" || $0 == " " } }
    private static func alignment(_ cell: String) -> MarkdownTableAlignment { let t = cell.trimmingCharacters(in: .whitespaces); return t.hasPrefix(":") && t.hasSuffix(":") ? .center : t.hasSuffix(":") ? .right : .left }
    private static func escapeCell(_ value: String) -> String { value.replacingOccurrences(of: "|", with: "\\|").replacingOccurrences(of: "\n", with: " ") }

    private static func splitLine(_ line: String) -> [String] { splitLineWithOffsets(line).map(\.text) }
    private static func splitLineWithOffsets(_ line: String) -> [(text: String, offset: Int)] {
        var value = line.trimmingCharacters(in: .whitespacesAndNewlines); var base = line.firstIndex(of: value.first ?? "|").map { line.distance(from: line.startIndex, to: $0) } ?? 0
        if value.hasPrefix("|") { value.removeFirst(); base += 1 }
        if value.hasSuffix("|") { value.removeLast() }
        var cells: [(String, Int)] = []; var start = 0; var escaped = false; var code = false
        for (index, scalar) in value.unicodeScalars.enumerated() {
            if scalar == "\\" && !escaped { escaped = true; continue }
            if scalar == "`" && !escaped { code.toggle() }
            if scalar == "|" && !escaped && !code { cells.append((value[value.index(value.startIndex, offsetBy: start)..<value.index(value.startIndex, offsetBy: index)].trimmingCharacters(in: .whitespaces), base + start)); start = index + 1 }
            escaped = false
        }
        cells.append((value[value.index(value.startIndex, offsetBy: start)...].trimmingCharacters(in: .whitespacesAndNewlines), base + start))
        return cells
    }
}
