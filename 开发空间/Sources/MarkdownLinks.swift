import Foundation

struct MarkdownLinkReference: Equatable {
    let label: String
    let destination: String
    let title: String?
}

struct MarkdownLinkOccurrence: Equatable {
    let range: NSRange
    let label: String
    let destination: String
    let title: String?
    let isImage: Bool
    let isAutomatic: Bool
}

/// Small, safe GFM link scanner. It resolves reference links without
/// evaluating HTML, scripts, or remote resources. Callers can use the
/// occurrences to render links while preserving the original source range.
enum MarkdownLinks {
    static let allowedSchemes: Set<String> = ["http", "https", "mailto"]

    static func references(in text: String) -> [String: MarkdownLinkReference] {
        Dictionary(referenceDefinitions(in: text).map { (normalizeLabel($0.reference.label), $0.reference) }, uniquingKeysWith: { first, _ in first })
    }

    static func referenceDefinitions(in text: String) -> [(range: NSRange, reference: MarkdownLinkReference)] {
        let ns = text as NSString
        let pattern = #"(?m)^[ \t]{0,3}\[([^\]\r\n]+)\]:[ \t]*(?:<([^>\r\n]+)>|([^ \t\r\n]+))(?:[ \t]+(?:[\"']([^\"']+)[\"']|\(([^)]+)\)))?[ \t]*(?:\r?\n|$)"#
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return [] }
        var result: [(NSRange, MarkdownLinkReference)] = []
        let ignored = codeRanges(in: text)
        for match in regex.matches(in: text, range: NSRange(location: 0, length: ns.length)) where !ignored.contains(where: { NSIntersectionRange($0, match.range).length > 0 }) {
            let label = ns.substring(with: match.range(at: 1)).trimmingCharacters(in: .whitespacesAndNewlines)
            let destination = match.range(at: 2).location != NSNotFound ? ns.substring(with: match.range(at: 2)) : ns.substring(with: match.range(at: 3))
            guard let safe = normalize(destination) else { continue }
            let titleRange = match.range(at: 4).location != NSNotFound ? match.range(at: 4) : match.range(at: 5)
            let title = titleRange.location == NSNotFound ? nil : ns.substring(with: titleRange)
            result.append((match.range, MarkdownLinkReference(label: label, destination: safe, title: title)))
        }
        return result
    }

    static func occurrences(in text: String, references supplied: [String: MarkdownLinkReference]? = nil) -> [MarkdownLinkOccurrence] {
        let source = text as NSString
        let refs = supplied ?? references(in: text)
        let ignored = ignoredRanges(in: text)
        var result: [MarkdownLinkOccurrence] = []
        let inline = #"!?\[([^\]\r\n]+)\]\(\s*(?:<([^>\r\n]+)>|([^\s)]+))(?:\s+(?:[\"']([^\"']+)[\"']|\(([^)]+)\)))?\s*\)"#
        if let regex = try? NSRegularExpression(pattern: inline) {
            for match in regex.matches(in: text, range: NSRange(location: 0, length: source.length)) where !ignored.contains(where: { NSIntersectionRange($0, match.range).length > 0 }) {
                guard !isEscaped(match.range.location, in: source) else { continue }
                let destinationValue = match.range(at: 2).location != NSNotFound ? source.substring(with: match.range(at: 2)) : source.substring(with: match.range(at: 3))
                guard let destination = normalize(destinationValue) else { continue }
                let titleRange = match.range(at: 4).location != NSNotFound ? match.range(at: 4) : match.range(at: 5)
                let title = titleRange.location == NSNotFound ? nil : source.substring(with: titleRange)
                let label = source.substring(with: match.range(at: 1))
                result.append(MarkdownLinkOccurrence(range: match.range, label: label, destination: destination, title: title, isImage: source.substring(with: match.range).hasPrefix("!"), isAutomatic: false))
            }
        }
        let reference = #"!?\[([^\]\r\n]+)\](?:\[([^\]\r\n]*)\])"#
        if let regex = try? NSRegularExpression(pattern: reference) {
            for match in regex.matches(in: text, range: NSRange(location: 0, length: source.length)) where !ignored.contains(where: { NSIntersectionRange($0, match.range).length > 0 }) {
                guard !isEscaped(match.range.location, in: source), !result.contains(where: { NSIntersectionRange($0.range, match.range).length > 0 }) else { continue }
                let label = source.substring(with: match.range(at: 1)); let key = match.range(at: 2).length == 0 ? label : source.substring(with: match.range(at: 2))
                guard let reference = refs[normalizeLabel(key)] else { continue }
                result.append(MarkdownLinkOccurrence(range: match.range, label: label, destination: reference.destination, title: reference.title, isImage: source.substring(with: match.range).hasPrefix("!"), isAutomatic: false))
            }
        }
        let shortcut = #"!?\[([^\]\r\n]+)\](?![\[(])"#
        if let regex = try? NSRegularExpression(pattern: shortcut) {
            for match in regex.matches(in: text, range: NSRange(location: 0, length: source.length)) where !ignored.contains(where: { NSIntersectionRange($0, match.range).length > 0 }) {
                guard !isEscaped(match.range.location, in: source), !result.contains(where: { NSIntersectionRange($0.range, match.range).length > 0 }) else { continue }
                let label = source.substring(with: match.range(at: 1))
                guard let reference = refs[normalizeLabel(label)] else { continue }
                result.append(MarkdownLinkOccurrence(range: match.range, label: label, destination: reference.destination, title: reference.title, isImage: source.substring(with: match.range).hasPrefix("!"), isAutomatic: false))
            }
        }
        let automatic = #"(?<![\w])(?:<((?:https?://|mailto:)[^>\s]+)>|((?:https?://|mailto:)[^\s<>()]+)|((?:www\.)[\w.-]+\.[A-Za-z]{2,}(?:/[^\s<>()]*)?))"#
        if let regex = try? NSRegularExpression(pattern: automatic) {
            for match in regex.matches(in: text, range: NSRange(location: 0, length: source.length)) where !ignored.contains(where: { NSIntersectionRange($0, match.range).length > 0 }) {
                let rawMatch = match.range(at: 1).location != NSNotFound ? source.substring(with: match.range(at: 1)) : match.range(at: 2).location != NSNotFound ? source.substring(with: match.range(at: 2)) : source.substring(with: match.range(at: 3))
                let raw = match.range(at: 1).location != NSNotFound ? rawMatch : rawMatch.trimmingCharacters(in: CharacterSet(charactersIn: ".,;:!?"))
                let range = match.range(at: 1).location != NSNotFound ? match.range : NSRange(location: match.range.location, length: raw.utf16.count)
                guard !raw.isEmpty else { continue }
                let destination = raw.hasPrefix("www.") ? "https://" + raw : raw
                guard let safe = normalize(destination), !result.contains(where: { NSIntersectionRange($0.range, match.range).length > 0 }) else { continue }
                result.append(MarkdownLinkOccurrence(range: range, label: raw, destination: safe, title: nil, isImage: false, isAutomatic: true))
            }
        }
        return result.sorted { $0.range.location < $1.range.location }
    }

    private static func normalizeLabel(_ value: String) -> String {
        value.split(whereSeparator: { $0.isWhitespace }).joined(separator: " ").lowercased()
    }
    private static func normalize(_ value: String) -> String? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        if let url = URL(string: trimmed), let scheme = url.scheme?.lowercased() { return allowedSchemes.contains(scheme) ? trimmed : nil }
        return trimmed.hasPrefix("/") || trimmed.hasPrefix(".") || !trimmed.contains(":") ? trimmed : nil
    }
    private static func isEscaped(_ location: Int, in source: NSString) -> Bool {
        var before = location, slashes = 0
        while before > 0 && source.character(at: before - 1) == 92 { slashes += 1; before -= 1 }
        return slashes % 2 == 1
    }
    private static func ignoredRanges(in text: String) -> [NSRange] {
        let source = text as NSString
        var result = codeRanges(in: text)
        if let definitions = try? NSRegularExpression(pattern: #"(?m)^[ \t]{0,3}\[[^\]\r\n]+\]:[^\r\n]*(?:\r?\n|$)"#) {
            result.append(contentsOf: definitions.matches(in: text, range: NSRange(location: 0, length: source.length)).map(\.range))
        }
        return result
    }
    private static func codeRanges(in text: String) -> [NSRange] {
        let ns = text as NSString; var result: [NSRange] = []; var fence: (Character, Int, Int)?
        for line in text.enumeratedLinesWithRanges() {
            let raw = ns.substring(with: line.range)
            let indent = raw.prefix { $0 == " " }.count
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if indent <= 3, !raw.hasPrefix("\t"), let marker = trimmed.first, marker == "`" || marker == "~" {
                let count = trimmed.prefix(while: { $0 == marker }).count
                if count >= 3 {
                    if fence == nil { fence = (marker, count, line.range.location) }
                    else if fence!.0 == marker && count >= fence!.1 && trimmed.dropFirst(count).trimmingCharacters(in: .whitespaces).isEmpty {
                        result.append(NSRange(location: fence!.2, length: NSMaxRange(line.range) - fence!.2)); fence = nil
                    }
                    continue
                }
            }
            if fence == nil && (indent >= 4 || raw.hasPrefix("\t")) { result.append(line.range) }
        }
        if let open = fence { result.append(NSRange(location: open.2, length: ns.length - open.2)) }
        let blocks = result.sorted { $0.location < $1.location }
        func blockEnd(at location: Int) -> Int? {
            var low = 0, high = blocks.count
            while low < high {
                let middle = (low + high) / 2
                if blocks[middle].location <= location { low = middle + 1 } else { high = middle }
            }
            guard low > 0, NSLocationInRange(location, blocks[low - 1]) else { return nil }
            return NSMaxRange(blocks[low - 1])
        }
        var index = 0
        while index < ns.length {
            if let end = blockEnd(at: index) { index = end; continue }
            guard ns.character(at: index) == 96 && !isEscaped(index, in: ns) else { index += 1; continue }
            let start = index; while index < ns.length && ns.character(at: index) == 96 { index += 1 }
            let count = index - start
            var probe = index, closing: Int?
            while probe < ns.length {
                if let end = blockEnd(at: probe) { probe = end; continue }
                if ns.character(at: probe) == 96 {
                    let candidate = probe
                    while probe < ns.length && ns.character(at: probe) == 96 { probe += 1 }
                    if probe - candidate == count { closing = probe; break }
                } else { probe += 1 }
            }
            if let closing { result.append(NSRange(location: start, length: closing - start)); index = closing }
        }
        return result
    }

}

private extension String {
    func enumeratedLinesWithRanges() -> [(text: String, range: NSRange)] {
        var result: [(String, NSRange)] = []; let ns = self as NSString; var cursor = 0
        while cursor < ns.length { let range = ns.lineRange(for: NSRange(location: cursor, length: 0)); result.append((ns.substring(with: range), range)); cursor = NSMaxRange(range) }
        return result
    }
}
