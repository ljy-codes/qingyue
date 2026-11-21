import Foundation
#if canImport(FoundationXML)
import FoundationXML
#endif

enum TextFormattingError: LocalizedError {
    case invalid(String)
    var errorDescription: String? { if case .invalid(let message) = self { return message }; return nil }
}

/// Format display text without converting JSON values through floating point or
/// dictionaries. The caller presents a preview for generic whitespace cleanup.
enum TextFormatting {
    static let maximumLength = 500_000
    static func format(_ text: String, kind: String) throws -> String {
        guard text.utf16.count <= maximumLength else { throw TextFormattingError.invalid("格式化最多处理 500,000 个文本单元，请分段整理。") }
        let source = normalizeNewlines(text)
        switch kind.lowercased() {
        case "json": return try TextFeatures.formatJSON(source)
        case "jsonc": return try formatJSONC(source)
        case "plain", "txt": return formatPlain(source)
        case "markdown", "md": return formatMarkdown(source)
        case "xml": return try XMLSafeFormatter.format(source)
        default: return normalizeWhitespace(source)
        }
    }

    static func normalizeWhitespace(_ text: String) -> String {
        normalizeNewlines(text).components(separatedBy: "\n").map(trimTrailingWhitespace).joined(separator: "\n")
    }

    private static func normalizeNewlines(_ text: String) -> String { text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n") }
    private static func trimTrailingWhitespace(_ text: String) -> String {
        var result = text
        while result.last == " " || result.last == "\t" { result.removeLast() }
        return result
    }
    private static func formatPlain(_ text: String) -> String {
        var output: [String] = [], previousBlank = false
        for raw in text.components(separatedBy: "\n") {
            let line = trimTrailingWhitespace(raw)
            if line.isEmpty && previousBlank { continue }
            previousBlank = line.isEmpty
            var columns = 0, position = line.startIndex
            while position < line.endIndex && (line[position] == " " || line[position] == "\t") {
                columns += line[position] == "\t" ? 4 - columns % 4 : 1
                position = line.index(after: position)
            }
            output.append(String(repeating: " ", count: columns) + line[position...])
        }
        return output.joined(separator: "\n")
    }

    private static func formatMarkdown(_ text: String) -> String {
        let lines = text.components(separatedBy: "\n")
        // HTML block rules and container-sensitive whitespace need a full parser.
        // Returning the original is preferable to silently changing their content.
        if lines.contains(where: { $0.trimmingCharacters(in: .whitespaces).hasPrefix("<") }) { return text }
        var protected = Array(repeating: false, count: lines.count)
        if lines.first?.trimmingCharacters(in: .whitespacesAndNewlines).replacingOccurrences(of: "\u{FEFF}", with: "") == "---" {
            let end = lines.indices.dropFirst().first(where: { ["---", "..."].contains(lines[$0].trimmingCharacters(in: .whitespaces)) }) ?? (lines.count - 1)
            for i in 0...end { protected[i] = true }
        }
        var fence: (Character, Int)?
        for i in lines.indices where !protected[i] {
            let line = lines[i], trimmed = line.trimmingCharacters(in: .whitespaces)
            let marker = fenceMarker(line)
            if let active = fence {
                protected[i] = true
                if let m = marker, m.0 == active.0, m.1 >= active.1, m.2 { fence = nil }
                continue
            }
            if let marker = marker { fence = (marker.0, marker.1); protected[i] = true; continue }
            let indent = line.prefix { $0 == " " || $0 == "\t" }
            if indent.count >= 4 || indent.contains("\t") || trimmed.hasPrefix(">") || line.contains("|") || line.contains("](") || line.contains("]:") { protected[i] = true }
            if !trimmed.isEmpty && (trimmed.allSatisfy { $0 == "=" } || trimmed.allSatisfy { $0 == "-" }) {
                protected[i] = true; if i > 0 { protected[i - 1] = true }
            }
        }
        let list = try! NSRegularExpression(pattern: #"^(?:[-+*]|[0-9]{1,9}[.)])([ ]{1,4})(\S.*)$"#)
        for i in lines.indices {
            let all = NSRange(location: 0, length: (lines[i] as NSString).length)
            guard list.firstMatch(in: lines[i], range: all) != nil else { continue }
            var next = i + 1
            while next < lines.count && lines[next].trimmingCharacters(in: .whitespaces).isEmpty { next += 1 }
            if next < lines.count && (lines[next].first == " " || lines[next].first == "\t") { protected[i] = true }
        }
        var output: [String] = [], index = 0
        while index < lines.count {
            if lines[index].isEmpty {
                var end = index + 1
                while end < lines.count && lines[end].isEmpty { end += 1 }
                let safe = index > 0 && end < lines.count && !protected[index - 1] && !protected[end]
                    && !protected[index..<end].contains(true)
                    && list.firstMatch(in: lines[index - 1], range: NSRange(location: 0, length: (lines[index - 1] as NSString).length)) == nil
                output.append(contentsOf: Array(repeating: "", count: safe ? 1 : end - index)); index = end; continue
            }
            var line = lines[index]
            if !protected[index] {
                let heading = try! NSRegularExpression(pattern: #"^( {0,3}#{1,6})[ \t]+(\S.*)$"#)
                let all = NSRange(location: 0, length: (line as NSString).length)
                if let match = heading.firstMatch(in: line, range: all) {
                    let ns = line as NSString
                    line = ns.substring(with: match.range(at: 1)) + " " + ns.substring(with: match.range(at: 2))
                } else if let match = list.firstMatch(in: line, range: all) {
                    let ns = line as NSString, spacing = match.range(at: 1)
                    line = ns.substring(to: spacing.location) + " " + ns.substring(with: match.range(at: 2))
                }
            }
            // Keep trailing spaces: two spaces are a Markdown hard break.
            output.append(line); index += 1
        }
        return output.joined(separator: "\n")
    }

    private static func fenceMarker(_ line: String) -> (Character, Int, Bool)? {
        let prefixCharacters = CharacterSet(charactersIn: " \t>-+*.)0123456789")
        let body = line.unicodeScalars.drop { prefixCharacters.contains($0) }
        guard let first = body.first, first == "`" || first == "~" else { return nil }
        let length = body.prefix { $0 == first }.count
        guard length >= 3 else { return nil }
        return (Character(String(first)), length, body.dropFirst(length).allSatisfy { CharacterSet.whitespaces.contains($0) })
    }

    private static func formatJSONC(_ text: String) throws -> String {
        let parser = try JSONTokenParser(text, allowComments: true, maximumLength: maximumLength, maximumNodes: 100_000)
        _ = try parser.parse()
        var output = "", depth = 0, atStart = true, previous = "", outputLength = 0
        func newline() { if !atStart { output += "\n"; outputLength += 1 }; atStart = true }
        func write(_ value: String) throws {
            if atStart { output += String(repeating: " ", count: depth * 2); outputLength += depth * 2; atStart = false }
            output += value
            outputLength += value.utf16.count
            guard outputLength <= 4_000_000 else { throw TextFormattingError.invalid("格式化输出过大，请分段处理。") }
        }
        for token in parser.tokens {
            switch token.kind {
            case .comment:
                newline(); try write(token.raw); newline()
            case .symbol:
                switch token.raw {
                case "{", "[":
                    if previous == "{" || previous == "[" { newline() }
                    try write(token.raw); depth += 1
                case "}", "]":
                    depth -= 1
                    if previous != "{" && previous != "[" { newline() }
                    try write(token.raw)
                case ",": try write(","); newline()
                case ":": try write(": ")
                default: break
                }
            default:
                if previous == "{" || previous == "[" { newline() }
                try write(token.raw)
            }
            previous = token.kind == .comment ? "/" : token.raw
        }
        return output
    }
}

/// Tokens retain their original spelling, including integer precision, exponent
/// spelling, duplicate keys, string escapes and comment contents.
struct JSONLexeme {
    enum Kind { case symbol, string, value, comment }
    let kind: Kind
    let raw: String
    let range: NSRange
}

final class JSONStructureNode: NSObject {
    enum Kind { case object, array, string, number, bool, null }
    let key: String
    let kind: Kind
    let rawValue: String
    let sourceRange: NSRange
    let children: [JSONStructureNode]
    var isContainer: Bool { kind == .object || kind == .array }
    var summary: String {
        switch kind {
        case .object: return "{ \(children.count) 个字段 }"
        case .array: return "[ \(children.count) 项 ]"
        default: return rawValue
        }
    }
    init(key: String, kind: Kind, rawValue: String, sourceRange: NSRange, children: [JSONStructureNode] = []) {
        self.key = key; self.kind = kind; self.rawValue = rawValue; self.sourceRange = sourceRange; self.children = children
    }
    static func parse(_ text: String, allowComments: Bool) throws -> JSONStructureNode {
        try JSONTokenParser(text, allowComments: allowComments, maximumLength: 2_000_000, maximumNodes: 10_000).parse()
    }
}

/// A bounded recursive-descent validator. It never stores JSON as a dictionary
/// and never parses numbers as Double/NSNumber.
final class JSONTokenParser {
    let tokens: [JSONLexeme]
    private let semantic: [JSONLexeme]
    private let allowComments: Bool
    private let maximumNodes: Int
    private var position = 0, nodes = 0

    init(_ text: String, allowComments: Bool, maximumLength: Int, maximumNodes: Int) throws {
        guard text.utf16.count <= maximumLength else { throw TextFormattingError.invalid("JSON 结构或格式化文本超过处理上限。") }
        self.allowComments = allowComments; self.maximumNodes = maximumNodes
        let units = Array(text.utf16), ns = text as NSString
        var output: [JSONLexeme] = [], index = 0
        func fail(_ detail: String) -> TextFormattingError { .invalid("JSON 第 \(ns.substring(to: min(index, ns.length)).filter { $0 == "\n" }.count + 1) 行：" + detail) }
        func append(_ kind: JSONLexeme.Kind, _ start: Int) { output.append(JSONLexeme(kind: kind, raw: ns.substring(with: NSRange(location: start, length: index - start)), range: NSRange(location: start, length: index - start))) }
        while index < units.count {
            let c = units[index]
            if [9, 10, 13, 32].contains(c) { index += 1; continue }
            let start = index
            if [123, 125, 91, 93, 44, 58].contains(c) { index += 1; append(.symbol, start); continue }
            if c == 34 {
                index += 1; var closed = false
                while index < units.count {
                    let value = units[index]; index += 1
                    if value == 34 { closed = true; break }
                    if value < 32 { throw fail("字符串含未转义控制字符。") }
                    if value == 92 {
                        guard index < units.count else { throw fail("字符串转义不完整。") }
                        let escape = units[index]; index += 1
                        if escape == 117 {
                            guard index + 4 <= units.count, units[index..<(index + 4)].allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }) else { throw fail("Unicode 转义无效。") }
                            index += 4
                        } else if ![34, 92, 47, 98, 102, 110, 114, 116].contains(escape) { throw fail("字符串转义无效。") }
                    }
                }
                guard closed else { throw fail("字符串未闭合。") }; append(.string, start); continue
            }
            if c == 47 {
                guard allowComments, index + 1 < units.count else { throw fail("标准 JSON 不接受注释。") }
                if units[index + 1] == 47 {
                    index += 2; while index < units.count && units[index] != 10 && units[index] != 13 { index += 1 }
                } else if units[index + 1] == 42 {
                    index += 2
                    while index + 1 < units.count && !(units[index] == 42 && units[index + 1] == 47) { index += 1 }
                    guard index + 1 < units.count else { throw fail("块注释未闭合。") }; index += 2
                } else { throw fail("注释无效。") }
                append(.comment, start); continue
            }
            while index < units.count && ![9, 10, 13, 32, 123, 125, 91, 93, 44, 58, 47].contains(units[index]) { index += 1 }
            guard index > start else { throw fail("无法识别的字符。") }
            let value = ns.substring(with: NSRange(location: start, length: index - start))
            let number = value.range(of: #"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$"#, options: .regularExpression) != nil
            guard number || ["true", "false", "null"].contains(value) else { throw fail("值无效：\(String(value.prefix(40)))") }
            append(.value, start)
        }
        tokens = output; semantic = output.filter { $0.kind != .comment }
    }

    func parse() throws -> JSONStructureNode {
        position = 0; nodes = 0
        let node = try value(key: "根节点", depth: 0)
        guard position == semantic.count else { throw error("根值之后有多余内容。") }
        return node
    }
    private func error(_ detail: String) -> TextFormattingError { .invalid("JSON 结构：" + detail) }
    private func consume(_ spelling: String) -> Bool {
        if position < semantic.count && semantic[position].raw == spelling { position += 1; return true }
        return false
    }
    private func value(key: String, depth: Int) throws -> JSONStructureNode {
        guard depth <= 128, nodes < maximumNodes else { throw error("嵌套超过 128 层或节点超过 \(maximumNodes) 个。") }
        guard position < semantic.count else { throw error("缺少值。") }
        nodes += 1
        let token = semantic[position]; position += 1
        if token.raw == "{" || token.raw == "[" {
            let object = token.raw == "{", close = object ? "}" : "]"
            var children: [JSONStructureNode] = []
            if !consume(close) {
                while true {
                    let childKey: String
                    if object {
                        guard position < semantic.count, semantic[position].kind == .string else { throw error("对象字段名需要双引号。") }
                        let keyToken = semantic[position]; position += 1
                        childKey = Self.decodeString(keyToken.raw)
                        guard consume(":") else { throw error("字段名后缺少冒号。") }
                    } else { childKey = "[\(children.count)]" }
                    children.append(try value(key: childKey, depth: depth + 1))
                    if consume(close) { break }
                    guard consume(",") else { throw error("值之间缺少逗号或容器没有闭合。") }
                    if consume(close) {
                        guard allowComments else { throw error("标准 JSON 不接受尾逗号。") }
                        break
                    }
                }
            }
            let end = semantic[position - 1].range
            return JSONStructureNode(key: key, kind: object ? .object : .array, rawValue: token.raw, sourceRange: NSRange(location: token.range.location, length: NSMaxRange(end) - token.range.location), children: children)
        }
        let kind: JSONStructureNode.Kind
        if token.kind == .string { kind = .string }
        else if token.kind != .value { throw error("需要一个 JSON 值。") }
        else if token.raw == "null" { kind = .null }
        else if token.raw == "true" || token.raw == "false" { kind = .bool }
        else { kind = .number }
        return JSONStructureNode(key: key, kind: kind, rawValue: token.raw, sourceRange: token.range)
    }
    private static func decodeString(_ token: String) -> String {
        if let data = ("[" + token + "]").data(using: .utf8), let result = try? JSONSerialization.jsonObject(with: data) as? [String], let value = result.first { return value }
        return String(token.dropFirst().dropLast())
    }
}

private enum XMLSafeFormatter {
    private indirect enum Node {
        case element(open: String, close: String, children: [Node], preserve: Bool)
        case text(String)
        case raw(String)
        var original: String {
            switch self { case .element(let open, let close, let children, _): return open + children.map { $0.original }.joined() + close
            case .text(let text), .raw(let text): return text }
        }
    }
    private final class ParserDelegate: NSObject, XMLParserDelegate {
        var failure: Error?
        func parser(_ parser: XMLParser, parseErrorOccurred parseError: Error) { failure = parseError }
        func parser(_ parser: XMLParser, resolveExternalEntityName name: String, systemID: String?) -> Data? { Data() }
    }
    private struct Token { let raw: String; let type: Int } // 0 text, 1 open, 2 close, 3 raw/self-closing

    static func format(_ source: String) throws -> String {
        guard source.range(of: #"<!\s*(?:DOCTYPE|ENTITY)\b"#, options: [.regularExpression, .caseInsensitive]) == nil else { throw TextFormattingError.invalid("XML 格式化不接受 DTD 或实体声明。") }
        let delegate = ParserDelegate(), parser = XMLParser(data: Data(source.utf8))
        parser.shouldResolveExternalEntities = false; parser.externalEntityResolvingPolicy = .never; parser.delegate = delegate
        guard parser.parse() else { throw TextFormattingError.invalid("XML 语法无效：\(delegate.failure?.localizedDescription ?? parser.parserError?.localizedDescription ?? "无法解析")") }
        let tokens = try tokenize(source)
        var position = 0
        func node(_ depth: Int) throws -> Node {
            guard depth <= 128, position < tokens.count else { throw TextFormattingError.invalid("XML 嵌套超过 128 层。") }
            let token = tokens[position]; position += 1
            if token.type == 0 { return .text(token.raw) }
            if token.type != 1 { return .raw(token.raw) }
            var children: [Node] = []
            while position < tokens.count && tokens[position].type != 2 { children.append(try node(depth + 1)) }
            guard position < tokens.count else { throw TextFormattingError.invalid("XML 标签没有闭合。") }
            let close = tokens[position].raw; position += 1
            let preserve = token.raw.range(of: #"\bxml:space\s*=\s*["']preserve["']"#, options: .regularExpression) != nil
            return .element(open: token.raw, close: close, children: children, preserve: preserve)
        }
        func render(_ value: Node, _ depth: Int) -> String {
            guard case .element(let open, let close, let children, let preserve) = value else { return value.original }
            if preserve { return value.original }
            for child in children {
                if case .text(let text) = child, !text.isEmpty && (!text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !text.contains("\n")) { return value.original }
                if case .raw(let raw) = child, raw.hasPrefix("<![CDATA[") { return value.original }
            }
            let markup = children.filter { if case .text = $0 { return false }; return true }
            guard !markup.isEmpty else { return value.original }
            return open + markup.map { "\n" + String(repeating: " ", count: (depth + 1) * 2) + render($0, depth + 1) }.joined()
                + "\n" + String(repeating: " ", count: depth * 2) + close
        }
        var roots: [Node] = []
        while position < tokens.count { roots.append(try node(0)) }
        let output = roots.map { render($0, 0) }.joined()
        guard output.utf16.count <= 4_000_000 else { throw TextFormattingError.invalid("XML 格式化输出过大。") }
        return output
    }
    private static func tokenize(_ text: String) throws -> [Token] {
        let ns = text as NSString, units = Array(text.utf16)
        var output: [Token] = [], i = 0
        while i < units.count {
            let start = i
            if units[i] != 60 {
                while i < units.count && units[i] != 60 { i += 1 }
                output.append(Token(raw: ns.substring(with: NSRange(location: start, length: i - start)), type: 0)); continue
            }
            let suffix = ns.substring(with: NSRange(location: i, length: min(9, ns.length - i)))
            let marker: String? = suffix.hasPrefix("<!--") ? "-->" : (suffix.hasPrefix("<![CDATA[") ? "]]>" : (suffix.hasPrefix("<?") ? "?>" : nil))
            if let marker = marker {
                let end = ns.range(of: marker, range: NSRange(location: i + 2, length: ns.length - i - 2))
                guard end.location != NSNotFound else { throw TextFormattingError.invalid("XML 标记未闭合。") }
                i = NSMaxRange(end); output.append(Token(raw: ns.substring(with: NSRange(location: start, length: i - start)), type: 3)); continue
            }
            var quote: UInt16 = 0
            i += 1
            while i < units.count {
                let c = units[i]; i += 1
                if quote != 0 { if c == quote { quote = 0 }; continue }
                if c == 34 || c == 39 { quote = c; continue }
                if c == 62 { break }
            }
            let raw = ns.substring(with: NSRange(location: start, length: i - start))
            let type = raw.hasPrefix("</") ? 2 : (raw.hasSuffix("/>") || raw.hasPrefix("<!") ? 3 : 1)
            output.append(Token(raw: raw, type: type))
        }
        return output
    }
}
