import Foundation

enum AnnotationAnchorResolver {
    static func repair(_ item: DocumentAnnotation, in text: String) -> AnnotationAnchorRepair {
        let source = text as NSString, quote = item.quote as NSString
        guard quote.length > 0 else { return result(item, item.sourceStart, item.sourceEnd, .missing) }
        if item.sourceStart >= 0, item.sourceEnd <= source.length, item.sourceEnd - item.sourceStart == quote.length,
           source.substring(with: NSRange(location: item.sourceStart, length: quote.length)) == item.quote {
            return result(item, item.sourceStart, item.sourceEnd, .exact)
        }
        var cursor = 0, candidates: [(NSRange, Int)] = []
        while cursor <= source.length - quote.length && candidates.count < 500 {
            let found = source.range(of: item.quote, range: NSRange(location: cursor, length: source.length - cursor))
            if found.location == NSNotFound { break }
            let before = source.substring(with: NSRange(location: max(0, found.location - item.contextBefore.utf16.count), length: min(found.location, item.contextBefore.utf16.count)))
            let after = source.substring(with: NSRange(location: NSMaxRange(found), length: min(source.length - NSMaxRange(found), item.contextAfter.utf16.count)))
            let score = (!item.contextBefore.isEmpty && before == item.contextBefore ? 1 : 0) + (!item.contextAfter.isEmpty && after == item.contextAfter ? 1 : 0)
            candidates.append((found, score)); cursor = NSMaxRange(found)
        }
        guard let best = candidates.sorted(by: { $0.1 == $1.1 ? abs($0.0.location - item.sourceStart) < abs($1.0.location - item.sourceStart) : $0.1 > $1.1 }).first else {
            return result(item, min(item.sourceStart, source.length), min(item.sourceEnd, source.length), .missing)
        }
        let unambiguous = candidates.count == 1 || candidates.filter { $0.1 == best.1 }.count == 1
        return result(item, best.0.location, NSMaxRange(best.0), unambiguous ? .shifted : .uncertain)
    }
    private static func result(_ item: DocumentAnnotation, _ start: Int, _ end: Int, _ state: AnnotationAnchorState) -> AnnotationAnchorRepair {
        AnnotationAnchorRepair(id: item.id, sourceStart: max(0, start), sourceEnd: max(start, end), anchorState: state)
    }
}
