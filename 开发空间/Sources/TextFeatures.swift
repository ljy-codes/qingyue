import AppKit
import Foundation

enum TextKind: String, CaseIterable {
    case plain, json, jsonc, yaml, ini, markdown, xml
    case javascript, typescript, python, java, csharp, sql, html, css, go, rust, shell, powershell, toml, csv

    var label: String {
        switch self {
        case .plain: return "纯文本"
        case .json: return "JSON"
        case .jsonc: return "JSONC"
        case .yaml: return "YAML"
        case .ini: return "INI / 配置"
        case .markdown: return "Markdown"
        case .xml: return "XML"
        case .javascript: return "JavaScript"
        case .typescript: return "TypeScript"
        case .python: return "Python"
        case .java: return "Java"
        case .csharp: return "C#"
        case .sql: return "SQL"
        case .html: return "HTML"
        case .css: return "CSS"
        case .go: return "Go"
        case .rust: return "Rust"
        case .shell: return "Shell"
        case .powershell: return "PowerShell"
        case .toml: return "TOML"
        case .csv: return "CSV"
        }
    }

    static func detect(url: URL?, text: String) -> TextKind {
        let ext = url?.pathExtension.lowercased() ?? ""
        let name = url?.lastPathComponent.lowercased() ?? ""
        switch ext {
        case "txt", "log": return .plain
        case "csv", "tsv": return .csv
        case "json": return .json
        case "jsonc": return .jsonc
        case "yml", "yaml": return .yaml
        case "md", "markdown", "mdown", "mkd": return .markdown
        case "ini", "conf", "cfg", "properties", "env": return .ini
        case "toml": return .toml
        case "xml", "plist", "svg": return .xml
        case "html", "htm": return .html
        case "js", "mjs", "cjs", "jsx": return .javascript
        case "ts", "tsx": return .typescript
        case "py", "pyw": return .python
        case "java": return .java
        case "cs": return .csharp
        case "sql": return .sql
        case "css", "scss", "less": return .css
        case "go": return .go
        case "rs": return .rust
        case "sh", "bash", "zsh", "fish": return .shell
        case "ps1", "psm1", "psd1": return .powershell
        default: break
        }
        if name == ".env" || name.hasPrefix(".env.") { return .ini }
        let prefix = text.prefix(4096).trimmingCharacters(in: .whitespacesAndNewlines)
        if prefix.hasPrefix("<?xml") || prefix.hasPrefix("<") { return .xml }
        if prefix.hasPrefix("{") || prefix.hasPrefix("[") {
            if prefix.hasPrefix("[") && matches(#"^\[[^\]\r\n]+\][ \t]*(?:\r?\n|$)"#, prefix) { return .ini }
            return .json
        }
        if prefix.hasPrefix("//") || prefix.hasPrefix("/*") { return .jsonc }
        if ext == "config" {
            if matches(#"(?m)^[ \t]*[^#;\s][^=\r\n]*="#, prefix) { return .ini }
            if matches(#"(?m)^[A-Za-z_][A-Za-z0-9_-]*:[ \t]*(?:\r?\n|\S)"#, prefix) { return .yaml }
        }
        return .plain
    }

    private static func matches(_ pattern: String, _ text: String) -> Bool {
        guard let re = try? NSRegularExpression(pattern: pattern) else { return false }
        return re.firstMatch(in: text, range: NSRange(location: 0, length: (text as NSString).length)) != nil
    }
}

struct EditorPalette {
    let background: NSColor
    let foreground: NSColor
    let muted: NSColor
    let accent: NSColor
    let string: NSColor
    let number: NSColor
    let comment: NSColor
    let key: NSColor
}

enum TextFeatureError: LocalizedError {
    case invalidJSON(String)
    var errorDescription: String? {
        switch self { case .invalidJSON(let message): return message }
    }
}

enum TextFeatures {
    /// Called by the UI after a debounce, only for reasonably sized documents.
    /// Coloring changes attributes only; source characters and the undo history are untouched.
    static func highlight(_ storage: NSTextStorage, kind: TextKind, palette: EditorPalette, font: NSFont) {
        let text = storage.string
        let all = NSRange(location: 0, length: storage.length)
        guard all.length > 0 else { return }
        storage.beginEditing()
        defer { storage.endEditing() }
        storage.addAttributes([.foregroundColor: palette.foreground, .font: font], range: all)
        guard kind != .plain else { return }
        func paint(_ range: NSRange, _ color: NSColor, _ specialFont: NSFont? = nil) {
            guard range.location != NSNotFound, range.location >= 0, NSMaxRange(range) <= all.length else { return }
            storage.addAttribute(.foregroundColor, value: color, range: range)
            if let specialFont = specialFont { storage.addAttribute(.font, value: specialFont, range: range) }
        }
        func regex(_ pattern: String, color: NSColor, capture: Int = 0, specialFont: NSFont? = nil) {
            guard let re = try? NSRegularExpression(pattern: pattern) else { return }
            for match in re.matches(in: text, range: all) {
                if capture < match.numberOfRanges { paint(match.range(at: capture), color, specialFont) }
            }
        }
        switch kind {
        case .json, .jsonc, .yaml, .ini:
            regex(#"(?<![\w.])-?\b(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\b"#, color: palette.number)
            regex(#"\b(?:true|false|null|True|False|None|yes|no|on|off)\b"#, color: palette.accent)
            if kind == .yaml {
                regex(#"(?m)^[ \t]*(?:-[ \t]+)?([^\s\[\]{},#'\"][^:\r\n]*?)[ \t]*:(?=[ \t]|$)"#, color: palette.key, capture: 1)
            }
            if kind == .ini {
                regex(#"(?m)^[ \t]*([^#;\s][^=\r\n]*?)[ \t]*="#, color: palette.key, capture: 1)
                regex(#"(?m)^[ \t]*\[[^\r\n]*\]"#, color: palette.accent)
            }
            for token in lexicalColors(text, kind: kind) {
                switch token.style {
                case .string: paint(token.range, palette.string)
                case .key: paint(token.range, palette.key)
                case .comment: paint(token.range, palette.comment)
                }
            }
        case .markdown:
            let bold = NSFontManager.shared.convert(font, toHaveTrait: .boldFontMask)
            regex(#"(?m)^ {0,3}#{1,6}[ \t]+.*$"#, color: palette.accent, specialFont: bold)
            regex(#"\*\*[^*\r\n]+\*\*|__[^_\r\n]+__"#, color: palette.foreground, specialFont: bold)
            regex(#"!?\[[^\]\r\n]+\]\([^\)\r\n]+\)"#, color: palette.key)
            regex(#"(?m)^[ \t]*>.*$"#, color: palette.muted)
            regex(#"`[^`\r\n]+`"#, color: palette.string)
            let ns = text as NSString
            var fence: Character?
            var start = 0
            var fenceLength = 0
            for line in lines(text) {
                let trimmed = line.text.trimmingCharacters(in: .whitespaces)
                let marker = fenceMarker(trimmed)
                if fence == nil, let marker = marker {
                    fence = marker.character; fenceLength = marker.count; start = line.range.location
                } else if let marker = marker, marker.character == fence, marker.count >= fenceLength,
                          trimmed.dropFirst(marker.count).trimmingCharacters(in: .whitespaces).isEmpty {
                    paint(NSRange(location: start, length: NSMaxRange(line.range) - start), palette.string)
                    fence = nil
                }
            }
            if fence != nil { paint(NSRange(location: start, length: ns.length - start), palette.string) }
        case .xml:
            regex(#"</?[A-Za-z_:][A-Za-z0-9_:.-]*|/?>"#, color: palette.accent)
            regex(#"\b[A-Za-z_:][A-Za-z0-9_:.-]*(?=[ \t]*=)"#, color: palette.key)
            regex(#"\"[^\"]*\"|'[^']*'"#, color: palette.string)
            regex(#"(?s)<!--.*?(?:-->|$)"#, color: palette.comment)
        default: break
        }
    }

    static func diagnostic(_ text: String, kind: TextKind) -> String? {
        switch kind {
        case .json, .jsonc:
            do {
                let parser = try JSONSourceParser(text, allowComments: kind == .jsonc, allowTrailingComma: kind == .jsonc)
                _ = try parser.run(render: false)
                return nil
            } catch { return error.localizedDescription }
        case .yaml:
            for (index, line) in lines(text).enumerated() {
                let indent = line.text.prefix { $0 == " " || $0 == "\t" }
                if indent.contains("\t") { return "第 \(index + 1) 行：YAML 缩进含 Tab，请使用空格（基础检查）" }
            }
            return nil
        default: return nil
        }
    }

    /// Parses source tokens and emits the original value spellings. It never converts
    /// JSON numbers through Double/NSNumber, so large integers and exponent lexemes survive.
    static func formatJSON(_ text: String) throws -> String {
        try JSONSourceParser(text, allowComments: false, allowTrailingComma: false).run(render: true)
    }

    static func headings(_ text: String) -> [(title: String, range: NSRange)] {
        let sourceLines = lines(text)
        var result: [(String, NSRange)] = []
        var fence: Character?
        var fenceLength = 0
        let metadataEnd = frontmatterEnd(sourceLines)
        var index = 0
        while index < sourceLines.count {
            let line = sourceLines[index]
            let trimmed = line.text.trimmingCharacters(in: .whitespaces)
            defer { index += 1 }
            if let end = metadataEnd, index <= end { continue }
            if let marker = blockFenceMarker(line.text) {
                if fence == nil { fence = marker.character; fenceLength = marker.count }
                else if marker.character == fence && marker.count >= fenceLength && trimmed.dropFirst(marker.count).trimmingCharacters(in: .whitespaces).isEmpty { fence = nil }
                continue
            }
            if fence != nil { continue }
            if let title = headingTitle(line.text) {
                result.append((title.title, line.range))
            } else if setextLevel(at: index, in: sourceLines) != nil {
                result.append((trimmed, line.range)); index += 1
            }
        }
        return result
    }

    /// A deliberately small native renderer: no HTML evaluation, scripts, attachments,
    /// image downloads or WebView. Basic GFM tables use TextKit's native table blocks.
    static func markdownPreview(_ text: String, palette: EditorPalette, fontSize: CGFloat) -> NSAttributedString {
        let output = NSMutableAttributedString()
        let baseFont = NSFont.systemFont(ofSize: fontSize)
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineSpacing = 5
        paragraph.paragraphSpacing = 7
        let base: [NSAttributedString.Key: Any] = [.font: baseFont, .foregroundColor: palette.foreground, .paragraphStyle: paragraph]
        let codeFont = NSFont.monospacedSystemFont(ofSize: max(11, fontSize - 1), weight: .regular)
        let sourceLines = lines(text)
        var fence: Character?
        var fenceLength = 0
        let metadataEnd = frontmatterEnd(sourceLines)
        var index = 0
        while index < sourceLines.count {
            let sourceStart = sourceLines[index].range.location
            let displayStart = output.length
            var blockKind = "paragraph"
            // The renderer deliberately records block ranges rather than
            // pretending that removed Markdown punctuation maps character-wise.
            // `index` points to the first unconsumed line when this scope exits,
            // including early `continue` paths for fences, tables and metadata.
            defer {
                if output.length > displayStart {
                    let sourceEnd = index < sourceLines.count ? sourceLines[index].range.location : (text as NSString).length
                    let sourceRange = NSRange(location: sourceStart, length: max(0, sourceEnd - sourceStart))
                    output.addAttribute(NSAttributedString.Key("qingyue.blockKind"), value: blockKind, range: NSRange(location: displayStart, length: output.length - displayStart))
                    output.addAttribute(NSAttributedString.Key("qingyue.sourceRange"), value: NSValue(range: sourceRange),
                                        range: NSRange(location: displayStart, length: output.length - displayStart))
                }
            }
            let line = sourceLines[index]
            let trimmed = line.text.trimmingCharacters(in: .whitespaces)
            if let end = metadataEnd, index <= end {
                var attrs = base; attrs[.font] = codeFont; attrs[.foregroundColor] = palette.muted
                output.append(NSAttributedString(string: line.text + "\n", attributes: attrs)); index += 1; continue
            }
            if let marker = blockFenceMarker(line.text) {
                if fence == nil {
                    fence = marker.character; fenceLength = marker.count
                    index += 1; continue
                }
                if marker.character == fence && marker.count >= fenceLength && trimmed.dropFirst(marker.count).trimmingCharacters(in: .whitespaces).isEmpty {
                    fence = nil
                    output.append(NSAttributedString(string: "\n", attributes: base))
                    index += 1; continue
                }
            }
            if fence != nil {
                blockKind = "code"
                var attrs = base
                attrs[.font] = codeFont
                attrs[.foregroundColor] = palette.string
                attrs[.backgroundColor] = palette.muted.withAlphaComponent(0.09)
                output.append(NSAttributedString(string: line.text + "\n", attributes: attrs))
                index += 1; continue
            }
            if let table = table(at: index, in: sourceLines) {
                blockKind = "table"
                appendTable(table.rows, alignments: table.alignments, to: output, base: base, palette: palette, codeFont: codeFont, fontSize: fontSize)
                output.append(NSAttributedString(string: "\n", attributes: base))
                index = table.end; continue
            } else if let heading = headingTitle(line.text) {
                blockKind = "heading"
                var attrs = base
                attrs[.font] = NSFont.systemFont(ofSize: fontSize + CGFloat(max(1, 5 - heading.level)) * 2, weight: .semibold)
                attrs[.foregroundColor] = palette.accent
                output.append(inline(heading.title, attributes: attrs, palette: palette, codeFont: codeFont))
            } else if let level = setextLevel(at: index, in: sourceLines) {
                blockKind = "heading"
                var attrs = base
                attrs[.font] = NSFont.systemFont(ofSize: fontSize + (level == 1 ? 8 : 6), weight: .semibold)
                attrs[.foregroundColor] = palette.accent
                output.append(inline(trimmed, attributes: attrs, palette: palette, codeFont: codeFont))
                index += 1
            } else if isThematicBreak(line.text) {
                let block = NSTextBlock()
                block.setContentWidth(100, type: .percentageValueType)
                block.setWidth(0.75, type: .absoluteValueType, for: .border, edge: .minY)
                block.setBorderColor(palette.muted.withAlphaComponent(0.45), for: .minY)
                let style = paragraph.mutableCopy() as! NSMutableParagraphStyle
                style.textBlocks = [block]; style.paragraphSpacing = 12
                var attrs = base; attrs[.font] = NSFont.systemFont(ofSize: 5); attrs[.paragraphStyle] = style
                output.append(NSAttributedString(string: "\u{200B}\n", attributes: attrs)); index += 1; continue
            } else if trimmed.hasPrefix(">") {
                blockKind = "quote"
                var attrs = base
                attrs[.foregroundColor] = palette.muted
                let style = paragraph.mutableCopy() as! NSMutableParagraphStyle
                style.headIndent = 15; style.firstLineHeadIndent = 15
                attrs[.paragraphStyle] = style
                output.append(inline("▎ " + trimmed.dropFirst().trimmingCharacters(in: .whitespaces), attributes: attrs, palette: palette, codeFont: codeFont))
            } else {
                var content = line.text
                if let re = taskPattern,
                   let match = re.firstMatch(in: content, range: NSRange(location: 0, length: (content as NSString).length)) {
                    let ns = content as NSString
                    let indent = ns.substring(with: match.range(at: 1))
                    let checked = ns.substring(with: match.range(at: 2)).lowercased() == "x"
                    content = indent + (checked ? "☑ " : "☐ ") + ns.substring(from: NSMaxRange(match.range))
                } else if let re = bulletPattern,
                   let match = re.firstMatch(in: content, range: NSRange(location: 0, length: (content as NSString).length)) {
                    let indent = (content as NSString).substring(with: match.range(at: 1))
                    content = indent + "• " + (content as NSString).substring(from: NSMaxRange(match.range))
                }
                output.append(inline(content, attributes: base, palette: palette, codeFont: codeFont))
            }
            output.append(NSAttributedString(string: "\n", attributes: base))
            index += 1
        }
        return output
    }

    private static let taskPattern = try? NSRegularExpression(pattern: #"^([ \t]*)(?:[-+*]|[0-9]{1,9}[.)])[ \t]+\[([ xX])\](?:[ \t]+|$)"#)
    private static let bulletPattern = try? NSRegularExpression(pattern: #"^([ \t]*)[-+*][ \t]+"#)
    private static let orderedListPattern = try? NSRegularExpression(pattern: #"^[ \t]*[0-9]{1,9}[.)][ \t]+"#)

    private struct MarkdownTable {
        let rows: [[String]]
        let alignments: [NSTextAlignment]
        let end: Int
    }

    /// Split before inline rendering. A backslash-escaped pipe belongs to its cell.
    private static func tableCells(_ line: String) -> [String]? {
        let text = line.trimmingCharacters(in: .whitespaces)
        let units = Array(text.utf16)
        var cells: [String] = [], start = 0, index = 0, hasPipe = false, lastPipe = -1
        var codeTicks = 0
        while index < units.count {
            if units[index] == 92 { index += min(2, units.count - index); continue }
            if units[index] == 96 {
                var end = index
                while end < units.count && units[end] == 96 { end += 1 }
                let count = end - index
                if codeTicks == 0 {
                    // Treat as a code span only if a matching closing run exists.
                    var probe = end
                    while probe < units.count {
                        if units[probe] == 96 {
                            var next = probe
                            while next < units.count && units[next] == 96 { next += 1 }
                            if next - probe == count { codeTicks = count; break }
                            probe = next
                        } else { probe += 1 }
                    }
                } else if count == codeTicks { codeTicks = 0 }
                index = end; continue
            }
            if units[index] == 124 && codeTicks == 0 {
                cells.append(String(decoding: units[start..<index], as: UTF16.self).trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "\\|", with: "|"))
                start = index + 1; hasPipe = true; lastPipe = index
            }
            index += 1
        }
        guard hasPipe else { return nil }
        cells.append(String(decoding: units[start..<units.count], as: UTF16.self).trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "\\|", with: "|"))
        if units.first == 124 { cells.removeFirst() }
        if lastPipe == units.count - 1 { cells.removeLast() }
        return cells.isEmpty ? nil : cells
    }

    private static func table(at index: Int, in lines: [SourceLine]) -> MarkdownTable? {
        guard index + 1 < lines.count,
              isTableRowCandidate(lines[index].text),
              lines[index + 1].text.prefix(while: { $0 == " " }).count <= 3, !lines[index + 1].text.hasPrefix("\t"),
              let header = tableCells(lines[index].text), header.count <= 32,
              let separators = tableCells(lines[index + 1].text), header.count == separators.count else { return nil }
        var alignments: [NSTextAlignment] = []
        for cell in separators {
            let dashes = cell.dropFirst(cell.hasPrefix(":") ? 1 : 0).dropLast(cell.hasSuffix(":") ? 1 : 0)
            guard dashes.count >= 1, dashes.allSatisfy({ $0 == "-" }) else { return nil }
            alignments.append(cell.hasSuffix(":") ? (cell.hasPrefix(":") ? .center : .right) : .left)
        }
        var rows = [header], end = index + 2
        // Limit native block allocations. Remaining rows stay visible as source text.
        while end < lines.count, (rows.count + 1) * header.count <= 1024,
              isTableRowCandidate(lines[end].text), let cells = tableCells(lines[end].text) {
            var row = cells
            if row.count < header.count { row += Array(repeating: "", count: header.count - row.count) }
            if row.count > header.count { row = Array(row.prefix(header.count - 1)) + [row.dropFirst(header.count - 1).joined(separator: " | ")] }
            rows.append(row); end += 1
        }
        return MarkdownTable(rows: rows, alignments: alignments, end: end)
    }

    private static func isTableRowCandidate(_ line: String) -> Bool {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        let range = NSRange(location: 0, length: (line as NSString).length)
        return !trimmed.isEmpty && line.prefix(while: { $0 == " " }).count <= 3 && !line.hasPrefix("\t")
            && headingTitle(line) == nil && blockFenceMarker(line) == nil && !trimmed.hasPrefix(">") && !isThematicBreak(line)
            && bulletPattern?.firstMatch(in: line, range: range) == nil && orderedListPattern?.firstMatch(in: line, range: range) == nil
    }

    private static func appendTable(_ rows: [[String]], alignments: [NSTextAlignment], to output: NSMutableAttributedString, base: [NSAttributedString.Key: Any], palette: EditorPalette, codeFont: NSFont, fontSize: CGFloat) {
        let table = NSTextTable(); table.numberOfColumns = alignments.count
        table.layoutAlgorithm = .fixedLayoutAlgorithm; table.collapsesBorders = true
        table.setContentWidth(100, type: .percentageValueType)
        // Give descriptive columns more room while bounding skew. Equal-width
        // allocation makes paths dominate narrow columns in real README tables.
        let weights = (0..<alignments.count).map { column -> CGFloat in
            let longest = rows.map { column < $0.count ? $0[column].count : 0 }.max() ?? 1
            return sqrt(CGFloat(min(100, max(4, longest))))
        }
        let totalWeight = max(1, weights.reduce(0, +))
        for (rowIndex, row) in rows.enumerated() {
            for (column, cell) in row.enumerated() {
                let block = NSTextTableBlock(table: table, startingRow: rowIndex, rowSpan: 1, startingColumn: column, columnSpan: 1)
                block.setContentWidth(100 * weights[column] / totalWeight, type: .percentageValueType)
                block.setWidth(6, type: .absoluteValueType, for: .padding)
                block.setWidth(0.5, type: .absoluteValueType, for: .border)
                block.setBorderColor(palette.muted.withAlphaComponent(0.35))
                block.verticalAlignment = .topAlignment
                if rowIndex == 0 { block.backgroundColor = palette.accent.withAlphaComponent(0.10) }
                else if rowIndex % 2 == 0 { block.backgroundColor = palette.muted.withAlphaComponent(0.04) }
                let style = NSMutableParagraphStyle(); style.lineSpacing = 3
                style.textBlocks = [block]; style.alignment = alignments[column]
                var attrs = base; attrs[.paragraphStyle] = style
                if rowIndex == 0 { attrs[.font] = NSFont.systemFont(ofSize: fontSize, weight: .semibold) }
                let rendered = NSMutableAttributedString(attributedString: inline(cell, attributes: attrs, palette: palette, codeFont: codeFont))
                rendered.append(NSAttributedString(string: "\n", attributes: attrs)); output.append(rendered)
            }
        }
    }

    private static func frontmatterEnd(_ lines: [SourceLine]) -> Int? {
        guard lines.first?.text == "---", lines.count > 1 else { return nil }
        return (1..<lines.count).first { lines[$0].text == "---" || lines[$0].text == "..." }
    }

    private static func setextLevel(at index: Int, in lines: [SourceLine]) -> Int? {
        guard index + 1 < lines.count else { return nil }
        let line = lines[index].text, trimmed = line.trimmingCharacters(in: .whitespaces)
        let underline = lines[index + 1].text
        guard !trimmed.isEmpty, !isThematicBreak(line), headingTitle(line) == nil,
              blockFenceMarker(line) == nil, !trimmed.hasPrefix(">"),
              bulletPattern?.firstMatch(in: line, range: NSRange(location: 0, length: (line as NSString).length)) == nil,
              orderedListPattern?.firstMatch(in: line, range: NSRange(location: 0, length: (line as NSString).length)) == nil,
              line.prefix(while: { $0 == " " }).count <= 3,
              underline.prefix(while: { $0 == " " }).count <= 3,
              !underline.hasPrefix("\t") else { return nil }
        let marker = underline.trimmingCharacters(in: .whitespaces)
        guard let first = marker.first, first == "-" || first == "=", marker.allSatisfy({ $0 == first }) else { return nil }
        return first == "=" ? 1 : 2
    }

    private static func isThematicBreak(_ line: String) -> Bool {
        guard line.prefix(while: { $0 == " " }).count <= 3, !line.hasPrefix("\t") else { return false }
        let markers = line.filter { $0 != " " && $0 != "\t" }
        guard markers.count >= 3, let first = markers.first, first == "-" || first == "*" || first == "_" else { return false }
        return markers.allSatisfy { $0 == first }
    }

    private static func blockFenceMarker(_ line: String) -> (character: Character, count: Int)? {
        guard line.prefix(while: { $0 == " " }).count <= 3, !line.hasPrefix("\t") else { return nil }
        return fenceMarker(line.trimmingCharacters(in: .whitespaces))
    }

    private static let inlinePattern = ##"!\[([^\]]*)\]\([^)]*\)|\[([^\]]+)\]\(([^)\s]+)(?:[ \t]+\"[^\"]*\")?\)|`([^`]+)`|\*\*([^*]+)\*\*|(?<![\p{L}\p{N}])__([^_]+)__(?![\p{L}\p{N}])|~~([^~]+)~~|\*([^*\n]+)\*|(?<![\p{L}\p{N}])_([^_\n]+)_(?![\p{L}\p{N}])|\\([!\"#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])"##
    private static let inlineRegex = try? NSRegularExpression(pattern: inlinePattern)

    private static func inline(_ source: String, attributes: [NSAttributedString.Key: Any], palette: EditorPalette, codeFont: NSFont) -> NSAttributedString {
        let output = NSMutableAttributedString()
        guard let regex = inlineRegex else { return NSAttributedString(string: source, attributes: attributes) }
        let ns = source as NSString
        var position = 0
        for match in regex.matches(in: source, range: NSRange(location: 0, length: ns.length)) {
            if match.range.location > position { output.append(NSAttributedString(string: ns.substring(with: NSRange(location: position, length: match.range.location - position)), attributes: attributes)) }
            func value(_ index: Int) -> String? {
                let range = match.range(at: index)
                return range.location == NSNotFound ? nil : ns.substring(with: range)
            }
            var attrs = attributes
            var content = ns.substring(with: match.range)
            if let escaped = value(10) {
                content = escaped
            } else if let alt = value(1) {
                content = alt.isEmpty ? "〔图片〕" : "〔图片：\(alt)〕"
                attrs[.foregroundColor] = palette.muted
                let complete = ns.substring(with: match.range)
                if let start = complete.range(of: "](") {
                    var reference = String(complete[start.upperBound...].dropLast()).trimmingCharacters(in: .whitespaces)
                    if reference.hasPrefix("<"), let end = reference.firstIndex(of: ">") { reference = String(reference[reference.index(after: reference.startIndex)..<end]) }
                    else if let title = reference.range(of: #"[ \t]+["'].*["']$"#, options: .regularExpression) { reference = String(reference[..<title.lowerBound]) }
                    attrs[NSAttributedString.Key("qingyue.imageReference")] = reference
                    attrs[NSAttributedString.Key("qingyue.imageAlt")] = alt
                    attrs[NSAttributedString.Key("qingyue.imageID")] = match.range.location
                }
            } else if let label = value(2), let address = value(3) {
                content = label
                attrs[.foregroundColor] = palette.accent
                if let url = URL(string: address), let scheme = url.scheme?.lowercased(), ["https", "http", "mailto"].contains(scheme) { attrs[.link] = url }
            } else if let code = value(4) {
                content = code
                attrs[.font] = codeFont
                attrs[.foregroundColor] = palette.string
                attrs[.backgroundColor] = palette.muted.withAlphaComponent(0.09)
            } else if let bold = value(5) ?? value(6) {
                content = bold
                if let font = attrs[.font] as? NSFont { attrs[.font] = NSFontManager.shared.convert(font, toHaveTrait: .boldFontMask) }
            } else if let strike = value(7) {
                content = strike
                attrs[.strikethroughStyle] = NSUnderlineStyle.single.rawValue
            } else if let italic = value(8) ?? value(9) {
                content = italic
                if let font = attrs[.font] as? NSFont { attrs[.font] = NSFontManager.shared.convert(font, toHaveTrait: .italicFontMask) }
            }
            output.append(NSAttributedString(string: content, attributes: attrs))
            position = NSMaxRange(match.range)
        }
        if position < ns.length { output.append(NSAttributedString(string: ns.substring(from: position), attributes: attributes)) }
        return output
    }

    private struct SourceLine { let text: String; let range: NSRange }
    private static func lines(_ text: String) -> [SourceLine] {
        var result: [SourceLine] = []
        text.enumerateSubstrings(in: text.startIndex..<text.endIndex, options: .byLines) { substring, range, _, _ in
            result.append(SourceLine(text: substring ?? "", range: NSRange(range, in: text)))
        }
        return result
    }

    private static func fenceMarker(_ line: String) -> (character: Character, count: Int)? {
        guard let first = line.first, first == "`" || first == "~" else { return nil }
        let count = line.prefix { $0 == first }.count
        return count >= 3 ? (first, count) : nil
    }

    private static func headingTitle(_ line: String) -> (title: String, level: Int)? {
        let spaces = line.prefix { $0 == " " }.count
        guard spaces <= 3 else { return nil }
        let content = line.dropFirst(spaces)
        let level = content.prefix { $0 == "#" }.count
        guard level >= 1, level <= 6, content.dropFirst(level).first == " " || content.dropFirst(level).first == "\t" else { return nil }
        var title = content.dropFirst(level).trimmingCharacters(in: .whitespaces)
        if let re = try? NSRegularExpression(pattern: #"[ \t]+#+[ \t]*$"#), let match = re.firstMatch(in: title, range: NSRange(location: 0, length: (title as NSString).length)) {
            title = (title as NSString).substring(to: match.range.location)
        }
        return (title, level)
    }

    private enum LexicalStyle { case string, key, comment }
    private struct ColoredToken { let range: NSRange; let style: LexicalStyle }
    private static func lexicalColors(_ text: String, kind: TextKind) -> [ColoredToken] {
        let units = Array(text.utf16)
        var output: [ColoredToken] = []
        var index = 0
        while index < units.count {
            let current = units[index]
            let next: UInt16 = index + 1 < units.count ? units[index + 1] : 0
            let start = index
            if (kind == .yaml || kind == .ini) && ((current == 35 && (index == 0 || units[index - 1] <= 32)) || (kind == .ini && current == 59 && (index == 0 || units[index - 1] <= 32))) {
                while index < units.count && units[index] != 10 && units[index] != 13 { index += 1 }
                output.append(ColoredToken(range: NSRange(location: start, length: index - start), style: .comment))
            } else if kind == .jsonc && current == 47 && (next == 47 || next == 42) {
                index += 2
                if next == 47 {
                    while index < units.count && units[index] != 10 && units[index] != 13 { index += 1 }
                } else {
                    while index < units.count {
                        if units[index] == 42 && index + 1 < units.count && units[index + 1] == 47 { index += 2; break }
                        index += 1
                    }
                }
                output.append(ColoredToken(range: NSRange(location: start, length: index - start), style: .comment))
            } else if current == 34 || (current == 39 && (kind == .yaml || kind == .ini)) {
                let quote = current
                index += 1
                while index < units.count {
                    if units[index] == 10 || units[index] == 13 { break }
                    if units[index] == 92 && quote == 34 { index = min(units.count, index + 2); continue }
                    if units[index] == quote {
                        index += 1
                        if kind == .yaml && quote == 39 && index < units.count && units[index] == 39 { index += 1; continue }
                        break
                    }
                    index += 1
                }
                var after = index
                while after < units.count && (units[after] == 32 || units[after] == 9) { after += 1 }
                let isKey = after < units.count && (units[after] == 58 || (kind == .ini && units[after] == 61))
                output.append(ColoredToken(range: NSRange(location: start, length: index - start), style: isKey ? .key : .string))
            } else { index += 1 }
        }
        return output
    }
}

/// A strict source parser shared by diagnostics and formatting. Tokens keep their
/// exact original spelling; comments are accepted only by the JSONC diagnostic path.
private final class JSONSourceParser {
    private enum Kind { case string, number, literal, symbol }
    private struct Token { let kind: Kind; let text: String; let offset: Int }
    private let source: [UInt16]
    private let allowTrailingComma: Bool
    private var tokens: [Token] = []
    private var position = 0
    private var output: [String] = []
    private var rendering = false

    init(_ text: String, allowComments: Bool, allowTrailingComma: Bool) throws {
        source = Array(text.utf16)
        self.allowTrailingComma = allowTrailingComma
        try tokenize(allowComments: allowComments)
    }

    func run(render: Bool) throws -> String {
        rendering = render
        position = 0
        output.removeAll(keepingCapacity: true)
        guard !tokens.isEmpty else { throw failure("JSON 内容为空", at: 0) }
        try value(depth: 0)
        guard position == tokens.count else { throw failure("值之后还有多余内容", at: tokens[position].offset) }
        return output.joined()
    }

    private func tokenize(allowComments: Bool) throws {
        var index = 0
        while index < source.count {
            let unit = source[index]
            if unit == 32 || unit == 9 || unit == 10 || unit == 13 { index += 1; continue }
            let start = index
            if [UInt16(123), 125, 91, 93, 58, 44].contains(unit) {
                index += 1
                tokens.append(Token(kind: .symbol, text: spelling(start, index), offset: start))
            } else if unit == 34 {
                index += 1
                var closed = false
                while index < source.count {
                    if source[index] == 34 { index += 1; closed = true; break }
                    if source[index] < 32 { throw failure("字符串含未转义的控制字符", at: index) }
                    if source[index] == 92 {
                        index += 1
                        guard index < source.count else { throw failure("字符串转义未完成", at: index) }
                        if source[index] == 117 {
                            for _ in 0..<4 {
                                index += 1
                                guard index < source.count, isHex(source[index]) else { throw failure("Unicode 转义需要 4 位十六进制数字", at: index) }
                            }
                        } else if ![UInt16(34), 92, 47, 98, 102, 110, 114, 116].contains(source[index]) {
                            throw failure("无效的字符串转义", at: index)
                        }
                    }
                    index += 1
                }
                guard closed else { throw failure("字符串缺少结束引号", at: start) }
                tokens.append(Token(kind: .string, text: spelling(start, index), offset: start))
            } else if unit == 45 || isDigit(unit) {
                if unit == 45 { index += 1 }
                guard index < source.count, isDigit(source[index]) else { throw failure("负号后需要数字", at: index) }
                if source[index] == 48 {
                    index += 1
                    if index < source.count && isDigit(source[index]) { throw failure("JSON 数字不能带前导零", at: index) }
                } else {
                    while index < source.count && isDigit(source[index]) { index += 1 }
                }
                if index < source.count && source[index] == 46 {
                    index += 1
                    guard index < source.count, isDigit(source[index]) else { throw failure("小数点后需要数字", at: index) }
                    while index < source.count && isDigit(source[index]) { index += 1 }
                }
                if index < source.count && (source[index] == 101 || source[index] == 69) {
                    index += 1
                    if index < source.count && (source[index] == 43 || source[index] == 45) { index += 1 }
                    guard index < source.count, isDigit(source[index]) else { throw failure("指数部分需要数字", at: index) }
                    while index < source.count && isDigit(source[index]) { index += 1 }
                }
                tokens.append(Token(kind: .number, text: spelling(start, index), offset: start))
            } else if unit == 116 || unit == 102 || unit == 110 {
                let literal = unit == 116 ? "true" : unit == 102 ? "false" : "null"
                let units = Array(literal.utf16)
                guard index + units.count <= source.count, Array(source[index..<(index + units.count)]) == units else { throw failure("无效的 JSON 值", at: index) }
                index += units.count
                tokens.append(Token(kind: .literal, text: literal, offset: start))
            } else if unit == 47 && allowComments && index + 1 < source.count && (source[index + 1] == 47 || source[index + 1] == 42) {
                let block = source[index + 1] == 42
                index += 2
                if block {
                    var closed = false
                    while index + 1 < source.count {
                        if source[index] == 42 && source[index + 1] == 47 { index += 2; closed = true; break }
                        index += 1
                    }
                    guard closed else { throw failure("块注释缺少 */", at: start) }
                } else {
                    while index < source.count && source[index] != 10 && source[index] != 13 { index += 1 }
                }
            } else {
                throw failure(unit == 47 ? "标准 JSON 不支持注释；可切换为 JSONC 查看" : "出现无效的 JSON 字符", at: index)
            }
        }
    }

    private func value(depth: Int) throws {
        guard depth <= 256 else { throw failure("嵌套超过 256 层，已停止检查", at: currentOffset) }
        guard position < tokens.count else { throw failure("需要一个 JSON 值", at: source.count) }
        let token = tokens[position]
        if token.kind != .symbol { emit(token.text); position += 1; return }
        if token.text == "{" { try object(depth: depth) }
        else if token.text == "[" { try array(depth: depth) }
        else { throw failure("这里需要一个 JSON 值", at: token.offset) }
    }

    private func object(depth: Int) throws {
        position += 1
        emit("{")
        if consume("}") { emit("}"); return }
        emit("\n")
        while true {
            guard position < tokens.count, tokens[position].kind == .string else { throw failure("对象的键需要双引号字符串", at: currentOffset) }
            indent(depth + 1)
            emit(tokens[position].text); position += 1
            try require(":", message: "对象的键后缺少冒号")
            emit(": ")
            try value(depth: depth + 1)
            if consume("}") { emit("\n"); indent(depth); emit("}"); return }
            try require(",", message: "对象成员之间缺少逗号或结束大括号")
            if allowTrailingComma && consume("}") { emit("\n"); indent(depth); emit("}"); return }
            emit(",\n")
        }
    }

    private func array(depth: Int) throws {
        position += 1
        emit("[")
        if consume("]") { emit("]"); return }
        emit("\n")
        while true {
            indent(depth + 1)
            try value(depth: depth + 1)
            if consume("]") { emit("\n"); indent(depth); emit("]"); return }
            try require(",", message: "数组元素之间缺少逗号或结束方括号")
            if allowTrailingComma && consume("]") { emit("\n"); indent(depth); emit("]"); return }
            emit(",\n")
        }
    }

    private func consume(_ symbol: String) -> Bool {
        guard position < tokens.count, tokens[position].kind == .symbol, tokens[position].text == symbol else { return false }
        position += 1; return true
    }
    private func require(_ symbol: String, message: String) throws {
        guard consume(symbol) else { throw failure(message, at: currentOffset) }
    }
    private var currentOffset: Int { position < tokens.count ? tokens[position].offset : source.count }
    private func emit(_ text: String) { if rendering { output.append(text) } }
    private func indent(_ depth: Int) { if rendering { output.append(String(repeating: "  ", count: depth)) } }
    private func spelling(_ start: Int, _ end: Int) -> String { String(decoding: source[start..<end], as: UTF16.self) }
    private func isDigit(_ unit: UInt16) -> Bool { unit >= 48 && unit <= 57 }
    private func isHex(_ unit: UInt16) -> Bool { isDigit(unit) || unit >= 65 && unit <= 70 || unit >= 97 && unit <= 102 }
    private func failure(_ message: String, at offset: Int) -> TextFeatureError {
        var line = 1, column = 1
        for unit in source.prefix(max(0, min(offset, source.count))) {
            if unit == 10 { line += 1; column = 1 } else { column += 1 }
        }
        return .invalidJSON("第 \(line) 行，第 \(column) 列：\(message)")
    }
}
