import Foundation

struct SearchOptions {
    var caseSensitive: Bool = false
    var regularExpression: Bool = false
    var wholeWord: Bool = false
}

struct SearchMatch {
    /// Locations and lengths use UTF-16, as required by NSTextView and NSString.
    let range: NSRange
    /// Already expanded for this match. Literal searches never interpret $ or \.
    let replacement: String
}

enum TextSearchError: LocalizedError {
    case invalidRegularExpression(String)
    case textTooLarge
    case queryTooLong
    case replacementTooLong
    case tooManyMatches
    case resultTooLarge
    case timedOut
    case engineFailure

    var errorDescription: String? {
        switch self {
        case .invalidRegularExpression(let detail): return "正则表达式无效：\(detail)"
        case .textTooLarge: return "当前查找最多处理 50,000,000 个文本单元，请缩小文件后再试。"
        case .queryTooLong: return "查找表达式过长，请控制在 4,096 个文本单元内。"
        case .replacementTooLong: return "替换内容过长，请控制在 1,000,000 个文本单元内。"
        case .tooManyMatches: return "匹配超过 100,000 处，请缩小查找范围或使用更具体的条件。"
        case .resultTooLarge: return "替换后的内容过大，本次没有替换；请分批处理。"
        case .timedOut: return "查找耗时过长，请简化正则表达式或缩小文件后再试。"
        case .engineFailure: return "无法完成这次查找，请简化正则表达式后再试。"
        }
    }
}

enum TextSearch {
    static let maximumTextLength = 50_000_000
    static let maximumMatchCount = 100_000
    static let maximumResultLength = 10_000_000
    private static let maximumQueryLength = 4_096
    private static let maximumReplacementLength = 1_000_000
    private static let defaultTimeLimit: TimeInterval = 0.35

    /// Kept separate from the text allocation so tests can verify the limit
    /// without building a 50-million-unit string.
    static func validateTextLength(_ length: Int) throws {
        guard length <= maximumTextLength else { throw TextSearchError.textTooLarge }
    }

    private static func timeLimit(for length: Int) -> TimeInterval {
        if length > 20_000_000 { return 4.0 }
        if length > 5_000_000 { return 2.0 }
        return defaultTimeLimit
    }

    /// Whole words contain Unicode letters, numbers, combining marks and `_`.
    /// Chinese letters are therefore one continuous word until punctuation/space;
    /// searching for `中` as a whole word will not match inside `中文`.
    /// Regex follows Foundation/ICU semantics. Use (?m) for per-line ^ / $ anchors.
    static func matches(in text: String, query: String, replacement: String = "", options: SearchOptions) throws -> [SearchMatch] {
        guard !query.isEmpty else { return [] }
        let textLength = text.utf16.count
        try validateTextLength(textLength)
        guard query.utf16.count <= maximumQueryLength else { throw TextSearchError.queryTooLong }
        guard replacement.utf16.count <= maximumReplacementLength else { throw TextSearchError.replacementTooLong }

        if !options.regularExpression {
            return try literalMatches(in: text, query: query, replacement: replacement, options: options)
        }

        var pattern = query
        if options.wholeWord {
            // Noncapturing group preserves the user's capture-group numbering.
            pattern = #"(?<![\p{L}\p{N}\p{M}_])(?:"# + pattern + #")(?![\p{L}\p{N}\p{M}_])"#
        }
        let expression: NSRegularExpression
        do {
            expression = try NSRegularExpression(pattern: pattern, options: options.caseSensitive ? [] : [.caseInsensitive])
        } catch {
            throw TextSearchError.invalidRegularExpression(error.localizedDescription)
        }

        var results: [SearchMatch] = []
        var failure: TextSearchError?
        var outputLength = textLength
        let deadline = ProcessInfo.processInfo.systemUptime + Self.timeLimit(for: textLength)
        // Progress callbacks let ICU interrupt pathological backtracking as well as
        // ordinary long searches. Enumerating also avoids allocating unbounded matches.
        expression.enumerateMatches(in: text, options: [.reportProgress], range: NSRange(location: 0, length: textLength)) { match, flags, stop in
            if flags.contains(.internalError) {
                failure = .engineFailure; stop.pointee = true; return
            }
            if (flags.contains(.progress) || results.count & 255 == 0), ProcessInfo.processInfo.systemUptime > deadline {
                failure = .timedOut; stop.pointee = true; return
            }
            guard let match = match else { return }
            guard results.count < maximumMatchCount else {
                failure = .tooManyMatches; stop.pointee = true; return
            }
            let expanded = options.regularExpression
                ? expression.replacementString(for: match, in: text, offset: 0, template: replacement)
                : replacement
            outputLength += expanded.utf16.count - match.range.length
            guard outputLength <= maximumResultLength else {
                failure = .resultTooLarge; stop.pointee = true; return
            }
            results.append(SearchMatch(range: match.range, replacement: expanded))
        }
        if let failure = failure { throw failure }
        return results
    }

    /// Fast literal path. Foundation's literal range search avoids compiling a
    /// regex for every keystroke and keeps multi-megabyte logs responsive.
    private static func literalMatches(in text: String, query: String, replacement: String, options: SearchOptions) throws -> [SearchMatch] {
        let textBytes = Array(text.utf8)
        let queryBytes = Array(query.utf8)
        if textBytes.allSatisfy({ $0 < 0x80 }) && queryBytes.allSatisfy({ $0 < 0x80 }) {
            return try asciiLiteralMatches(textBytes: textBytes, queryBytes: queryBytes,
                                           replacement: replacement, options: options)
        }
        let source = text as NSString
        var compareOptions: NSString.CompareOptions = [.literal]
        if !options.caseSensitive { compareOptions.insert(.caseInsensitive) }
        let deadline = ProcessInfo.processInfo.systemUptime + Self.timeLimit(for: source.length)
        var results: [SearchMatch] = []
        var searchRange = NSRange(location: 0, length: source.length)
        var outputLength = source.length
        var iterations = 0
        while searchRange.length > 0 {
            if iterations & 255 == 0, ProcessInfo.processInfo.systemUptime > deadline { throw TextSearchError.timedOut }
            iterations += 1
            let found = source.range(of: query, options: compareOptions, range: searchRange)
            if found.location == NSNotFound { break }
            let wordStart = found.location == 0 || !isWordCharacter(in: source, at: found.location - 1)
            let end = NSMaxRange(found)
            let wordEnd = end == source.length || !isWordCharacter(in: source, at: end)
            if !options.wholeWord || (wordStart && wordEnd) {
                guard results.count < maximumMatchCount else { throw TextSearchError.tooManyMatches }
                outputLength += replacement.utf16.count - found.length
                guard outputLength <= maximumResultLength else { throw TextSearchError.resultTooLarge }
                results.append(SearchMatch(range: found, replacement: replacement))
            }
            let next = found.location + max(1, found.length)
            guard next <= source.length else { break }
            searchRange = NSRange(location: next, length: source.length - next)
        }
        return results
    }

    /// ASCII logs are the common large-file case. A byte scan avoids ICU's
    /// case-insensitive literal machinery and keeps multi-megabyte searches
    /// comfortably inside the time budget.
    private static func asciiLiteralMatches(textBytes: [UInt8], queryBytes: [UInt8],
                                            replacement: String, options: SearchOptions) throws -> [SearchMatch] {
        let needle = options.caseSensitive ? queryBytes : queryBytes.map(asciiLower)
        guard !needle.isEmpty else { return [] }
        let deadline = ProcessInfo.processInfo.systemUptime + Self.timeLimit(for: textBytes.count)
        var results: [SearchMatch] = []
        var outputLength = textBytes.count
        var index = 0
        while index <= textBytes.count - needle.count {
            if index & 4095 == 0, ProcessInfo.processInfo.systemUptime > deadline { throw TextSearchError.timedOut }
            var offset = 0
            while offset < needle.count {
                let byte = options.caseSensitive ? textBytes[index + offset] : asciiLower(textBytes[index + offset])
                if byte != needle[offset] { break }
                offset += 1
            }
            if offset == needle.count {
                let end = index + needle.count
                let wordStart = index == 0 || !isASCIIWord(textBytes[index - 1])
                let wordEnd = end == textBytes.count || !isASCIIWord(textBytes[end])
                if !options.wholeWord || (wordStart && wordEnd) {
                    guard results.count < maximumMatchCount else { throw TextSearchError.tooManyMatches }
                    outputLength += replacement.utf16.count - needle.count
                    guard outputLength <= maximumResultLength else { throw TextSearchError.resultTooLarge }
                    results.append(SearchMatch(range: NSRange(location: index, length: needle.count), replacement: replacement))
                }
                index += max(1, needle.count)
            } else {
                index += 1
            }
        }
        return results
    }

    private static func asciiLower(_ byte: UInt8) -> UInt8 {
        (byte >= 0x41 && byte <= 0x5A) ? byte + 0x20 : byte
    }

    private static func isASCIIWord(_ byte: UInt8) -> Bool {
        (byte >= 0x30 && byte <= 0x39) || (byte >= 0x41 && byte <= 0x5A)
            || (byte >= 0x61 && byte <= 0x7A) || byte == 0x5F
    }

    private static func isWordCharacter(in source: NSString, at index: Int) -> Bool {
        guard index >= 0, index < source.length else { return false }
        let range = source.rangeOfComposedCharacterSequence(at: index)
        return source.substring(with: range).unicodeScalars.contains { scalar in
            CharacterSet.alphanumerics.contains(scalar)
                || CharacterSet.nonBaseCharacters.contains(scalar)
                || scalar == "_"
        }
    }

    /// Replaces a snapshot of sorted, nonoverlapping search matches in one pass.
    /// Invalid ranges return the original source unchanged, rather than
    /// partially applying a replacement or triggering an NSString range exception.
    static func replacingAll(in text: String, matches: [SearchMatch]) -> String {
        guard !matches.isEmpty, matches.count <= maximumMatchCount else { return text }
        let source = text as NSString
        func scalarBoundary(_ index: Int) -> Bool {
            guard index > 0, index < source.length else { return true }
            let previous = source.character(at: index - 1)
            let next = source.character(at: index)
            return !((0xD800...0xDBFF).contains(previous) && (0xDC00...0xDFFF).contains(next))
        }
        var cursor = 0
        var resultLength = source.length
        for match in matches {
            let range = match.range
            guard range.location != NSNotFound, range.location >= cursor,
                  range.location <= source.length, range.length >= 0,
                  range.length <= source.length - range.location,
                  scalarBoundary(range.location), scalarBoundary(range.location + range.length) else { return text }
            resultLength += match.replacement.utf16.count - range.length
            guard resultLength <= maximumResultLength else { return text }
            cursor = range.location + range.length
        }
        let result = NSMutableString(capacity: resultLength)
        cursor = 0
        for match in matches {
            if match.range.location > cursor {
                result.append(source.substring(with: NSRange(location: cursor, length: match.range.location - cursor)))
            }
            result.append(match.replacement)
            cursor = match.range.location + match.range.length
        }
        if cursor < source.length { result.append(source.substring(from: cursor)) }
        return result as String
    }
}
