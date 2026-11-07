import Foundation

@main
struct TextSearchTests {
    private static var checks = 0

    static func main() throws {
        let insensitive = SearchOptions()
        let sensitive = SearchOptions(caseSensitive: true)
        let whole = SearchOptions(wholeWord: true)
        let regex = SearchOptions(regularExpression: true)
        let regexSensitive = SearchOptions(caseSensitive: true, regularExpression: true)

        let text = "Apple apple APPLE pineapple apple_2"
        try expect(TextSearch.matches(in: text, query: "apple", options: insensitive).count == 5, "Literal search defaults to case insensitive")
        try expect(TextSearch.matches(in: text, query: "apple", options: sensitive).count == 3, "Case sensitive literal matching")
        try expect(TextSearch.matches(in: text, query: "apple", options: whole).count == 3, "Whole word excludes suffixes and underscores")
        try expect(TextSearch.matches(in: "a.b [x] $1 \\ \n", query: "a.b", options: insensitive).map(\.range) == [NSRange(location: 0, length: 3)], "Regex punctuation remains literal")
        let dollarMatches = try TextSearch.matches(in: "a+a+a", query: "a", replacement: #"$1\value"#, options: insensitive)
        expect(TextSearch.replacingAll(in: "a+a+a", matches: dollarMatches) == #"$1\value+$1\value+$1\value"#, "Literal replacement keeps dollar and backslash")
        try expect(TextSearch.matches(in: "abc", query: "", options: insensitive).isEmpty, "Empty query has no matches")
        try expect(TextSearch.matches(in: "", query: "x", options: insensitive).isEmpty, "Empty text literal search")

        let unicode = "😀中文😀 e\u{301} café 𐐀value 中文 中。"
        let emojis = try TextSearch.matches(in: unicode, query: "😀", replacement: "🦊", options: insensitive)
        expect(emojis.map(\.range) == [NSRange(location: 0, length: 2), NSRange(location: 4, length: 2)], "Emoji ranges are UTF16")
        expect(TextSearch.replacingAll(in: unicode, matches: emojis) == "🦊中文🦊 e\u{301} café 𐐀value 中文 中。", "Unicode replacements preserve adjacent content")
        try expect(TextSearch.matches(in: unicode, query: "中", options: whole).count == 1, "Chinese is continuous Unicode word")
        try expect(TextSearch.matches(in: unicode, query: "e", options: whole).isEmpty, "Combining marks extend whole word")
        try expect(TextSearch.matches(in: unicode, query: "value", options: whole).isEmpty, "Supplementary Unicode letter extends whole word")
        try expect(TextSearch.matches(in: "é e\u{301}", query: "é", options: sensitive).count == 1, "Literal code points are not silently normalized")

        let rows = "name=Alice\r\nname=Bob\r\n"
        let captured = try TextSearch.matches(in: rows, query: #"(?m)^(name)=([A-Za-z]+)"#, replacement: "$2:$1", options: regexSensitive)
        expect(captured.count == 2, "Regex captures across CRLF lines")
        expect(TextSearch.replacingAll(in: rows, matches: captured) == "Alice:name\r\nBob:name\r\n", "Captures preserve CRLF and final newline")
        let wholeCapture = try TextSearch.matches(in: "abc abcdef", query: "(a)(bc)", replacement: "$2-$1", options: SearchOptions(regularExpression: true, wholeWord: true))
        expect(TextSearch.replacingAll(in: "abc abcdef", matches: wholeCapture) == "bc-a abcdef", "Whole word wrapper preserves capture numbers")
        let optional = try TextSearch.matches(in: "a ab", query: "a(b)?", replacement: "[$1]", options: regex)
        expect(TextSearch.replacingAll(in: "a ab", matches: optional) == "[] [b]", "Unmatched optional capture expands to empty")
        let literalDollar = try TextSearch.matches(in: "a", query: "a", replacement: #"\$1"#, options: regex)
        expect(TextSearch.replacingAll(in: "a", matches: literalDollar) == "$1", "Regex can escape literal dollar")
        let zero = try TextSearch.matches(in: "aba", query: "(?=a)", replacement: "X", options: regex)
        expect(zero.map(\.range) == [NSRange(location: 0, length: 0), NSRange(location: 2, length: 0)], "Zero-width lookahead finds distinct positions")
        expect(TextSearch.replacingAll(in: "aba", matches: zero) == "XabXa", "Zero-length replacement neither drops nor duplicates text")
        let empty = try TextSearch.matches(in: "", query: #"\A\z"#, replacement: "empty", options: regex)
        expect(TextSearch.replacingAll(in: "", matches: empty) == "empty", "Zero match in empty source")
        let final = try TextSearch.matches(in: "😀\n", query: #"\z"#, replacement: "end", options: regex)
        expect(final.first?.range == NSRange(location: 3, length: 0), "EOF zero-length range is UTF16")
        expect(TextSearch.replacingAll(in: "😀\n", matches: final) == "😀\nend", "EOF match preserves existing final newline")
        let scalarMatches = try TextSearch.matches(in: "😀中", query: ".", replacement: "x", options: regex)
        expect(scalarMatches.map(\.range) == [NSRange(location: 0, length: 2), NSRange(location: 2, length: 1)], "Regex scalar matches never split emoji surrogate pair")

        do { _ = try TextSearch.matches(in: "abc", query: "[", options: regex); fatalError("Expected invalid regex") }
        catch let error as TextSearchError { expect(error.localizedDescription.contains("正则表达式无效"), "Regex error is readable") }
        try expect(TextSearch.matches(in: "[", query: "[", options: insensitive).count == 1, "Invalid regex syntax is valid literal query")
        do { _ = try TextSearch.matches(in: String(repeating: "x", count: TextSearch.maximumTextLength + 1), query: "x", options: insensitive); fatalError("Expected text limit") }
        catch TextSearchError.textTooLarge { checks += 1 }
        do { _ = try TextSearch.matches(in: "x", query: String(repeating: "x", count: 4_097), options: insensitive); fatalError("Expected query limit") }
        catch TextSearchError.queryTooLong { checks += 1 }
        do { _ = try TextSearch.matches(in: "x", query: "x", replacement: String(repeating: "x", count: 1_000_001), options: insensitive); fatalError("Expected replacement limit") }
        catch TextSearchError.replacementTooLong { checks += 1 }
        do { _ = try TextSearch.matches(in: String(repeating: "a", count: TextSearch.maximumMatchCount + 1), query: "a", options: insensitive); fatalError("Expected match limit") }
        catch TextSearchError.tooManyMatches { checks += 1 }
        do { _ = try TextSearch.matches(in: String(repeating: "a", count: 12), query: "a", replacement: String(repeating: "z", count: 1_000_000), options: insensitive); fatalError("Expected result limit") }
        catch TextSearchError.resultTooLarge { checks += 1 }
        let expensive = String(repeating: "a", count: 100_000) + "!"
        let began = ProcessInfo.processInfo.systemUptime
        do { _ = try TextSearch.matches(in: expensive, query: "(a+)+$", options: regex); fatalError("Expected regex time limit") }
        catch TextSearchError.timedOut { expect(ProcessInfo.processInfo.systemUptime - began < 5, "Backtracking is interrupted in bounded time") }

        expect(TextSearch.replacingAll(in: "abc", matches: [SearchMatch(range: NSRange(location: NSNotFound, length: 1), replacement: "x")]) == "abc", "Invalid location is a safe no-op")
        expect(TextSearch.replacingAll(in: "abc", matches: [SearchMatch(range: NSRange(location: 2, length: Int.max), replacement: "x")]) == "abc", "Overflowing range is rejected without addition")
        expect(TextSearch.replacingAll(in: "abc", matches: [SearchMatch(range: NSRange(location: 0, length: 2), replacement: "x"), SearchMatch(range: NSRange(location: 1, length: 1), replacement: "y")]) == "abc", "Overlapping snapshots are rejected atomically")
        expect(TextSearch.replacingAll(in: "abc", matches: [SearchMatch(range: NSRange(location: 2, length: 1), replacement: "x"), SearchMatch(range: NSRange(location: 0, length: 1), replacement: "y")]) == "abc", "Unsorted snapshots are rejected atomically")
        expect(TextSearch.replacingAll(in: "😀x", matches: [SearchMatch(range: NSRange(location: 0, length: 1), replacement: "y")]) == "😀x", "Range cannot replace half an emoji surrogate pair")
        expect(TextSearch.replacingAll(in: "😀x", matches: [SearchMatch(range: NSRange(location: 1, length: 0), replacement: "y")]) == "😀x", "Zero-width insertion cannot split an emoji surrogate pair")

        // Generated literal cases compare against Foundation's replacement result,
        // independently exercising offsets after variable-width Unicode replacements.
        var random = LCG(state: 0xC0FFEE)
        let units = ["a", "b", "中", "😀", "\r\n", "\n", "e\u{301}", ".", "$", "_"]
        let replacements = ["", "长替换🦊", "$1", "\n", "x"]
        for _ in 0..<500 {
            var generated = ""
            for _ in 0..<Int(random.next() % 100) { generated += units[Int(random.next() % UInt64(units.count))] }
            let query = units[Int(random.next() % UInt64(units.count))]
            let replacement = replacements[Int(random.next() % UInt64(replacements.count))]
            let matches = try TextSearch.matches(in: generated, query: query, replacement: replacement, options: sensitive)
            let expected = generated.replacingOccurrences(of: query, with: replacement, options: .literal)
            expect(TextSearch.replacingAll(in: generated, matches: matches).utf8.elementsEqual(expected.utf8), "Generated literal replacement preserves exact Unicode/line endings")
            expect(matches.allSatisfy { (generated as NSString).substring(with: $0.range).utf8.elementsEqual(query.utf8) }, "Each generated range matches the original literal")
        }
        print("TextSearchTests: \(checks) checks passed")
    }

    private static func expect(_ condition: @autoclosure () throws -> Bool, _ message: String) rethrows {
        guard try condition() else { fatalError(message) }
        checks += 1
    }
    private struct LCG {
        var state: UInt64
        mutating func next() -> UInt64 { state = state &* 6364136223846793005 &+ 1442695040888963407; return state }
    }
}
