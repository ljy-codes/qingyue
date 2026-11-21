import Foundation

struct LineChange: Equatable {
    let line: Int // one-based line in the current document
    let saved: Bool // false = orange unsaved, true = green saved this session
    var deletion: Bool = false
}

enum LineChanges {
    static let maximumTextLength = 2_000_000
    static let maximumLines = 100_000
    /// The two immutable baselines let a save turn an orange mark green without
    /// losing its place after subsequent insertion/deletion of preceding lines.
    static func marks(original: String, saved: String, current: String) -> [LineChange] {
        guard original.utf16.count <= maximumTextLength, saved.utf16.count <= maximumTextLength,
              current.utf16.count <= maximumTextLength,
              lineCountWithinBudget(original), lineCountWithinBudget(saved), lineCountWithinBudget(current) else { return [] }
        let first = lines(original), baseline = lines(saved), now = lines(current)
        let unsaved = diff(baseline, now)
        var result: [Int: LineChange] = [:]
        if !exact(original, saved) {
            let stored = diff(first, baseline)
            for change in stored.changes {
                guard change.line > 0, change.line <= unsaved.mapping.count else { continue }
                let mapped = unsaved.mapping[change.line - 1]
                if mapped >= 0 { result[mapped + 1] = LineChange(line: mapped + 1, saved: true, deletion: change.deletion) }
            }
        }
        for change in unsaved.changes { result[change.line] = change }
        return result.values.sorted { $0.line < $1.line }
    }
    static func compare(baseline: String, current: String, savedBaseline: String? = nil) -> [LineChange] {
        marks(original: baseline, saved: savedBaseline ?? baseline, current: current)
    }
    private static func lines(_ text: String) -> [String] { text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n").components(separatedBy: "\n") }
    private static func lineCountWithinBudget(_ text: String) -> Bool {
        var count = 1, previous: UInt8 = 0
        for unit in text.utf8 {
            if unit == 13 || (unit == 10 && previous != 13) { count += 1; if count > maximumLines { return false } }
            previous = unit
        }
        return true
    }
    private static func exact(_ a: String, _ b: String) -> Bool { a.utf8.elementsEqual(b.utf8) }
    private struct Difference { let mapping: [Int]; let changes: [LineChange] }

    private static func diff(_ before: [String], _ after: [String]) -> Difference {
        var mapping = Array(repeating: -1, count: before.count)
        match(0, before.count, 0, after.count, depth: 0)
        var changes: [LineChange] = [], oldStart = 0, newStart = 0
        // Bound output work for pathological huge edits. The visible editor may
        // show a conservative band for wholesale rewrites rather than an N*M diff.
        let maximumMarks = 200_000
        for old in 0...before.count {
            if old < before.count && mapping[old] < 0 { continue }
            let next = old == before.count ? after.count : mapping[old]
            if next > newStart {
                for row in newStart..<next where changes.count < maximumMarks { changes.append(LineChange(line: row + 1, saved: false)) }
            } else if old > oldStart {
                if changes.count < maximumMarks { changes.append(LineChange(line: min(newStart + 1, after.count), saved: false, deletion: true)) }
            }
            oldStart = old + 1; newStart = next + 1
        }
        return Difference(mapping: mapping, changes: changes)

        func match(_ initialA: Int, _ lastA: Int, _ initialB: Int, _ lastB: Int, depth: Int) {
            var a = initialA, aEnd = lastA, b = initialB, bEnd = lastB
            while a < aEnd && b < bEnd && exact(before[a], after[b]) { mapping[a] = b; a += 1; b += 1 }
            while a < aEnd && b < bEnd && exact(before[aEnd - 1], after[bEnd - 1]) { aEnd -= 1; bEnd -= 1; mapping[aEnd] = bEnd }
            guard a < aEnd && b < bEnd, depth < 64 else { return }
            if myers(a, aEnd, b, bEnd) { return }
            // Unique-line patience anchors preserve unchanged islands when the
            // bounded Myers edit budget is exhausted. No quadratic line table.
            let left = unique(before, start: a, end: aEnd), right = unique(after, start: b, end: bEnd)
            let pairs = left.compactMap { key, row -> (Int, Int)? in
                guard row >= 0, let other = right[key], other >= 0, exact(before[row], after[other]) else { return nil }
                return (row, other)
            }.sorted { $0.0 < $1.0 }
            guard !pairs.isEmpty else { return }
            var tails: [Int] = [], previous = Array(repeating: -1, count: pairs.count)
            for i in pairs.indices {
                var low = 0, high = tails.count
                while low < high { let middle = (low + high) / 2; if pairs[tails[middle]].1 < pairs[i].1 { low = middle + 1 } else { high = middle } }
                if low > 0 { previous[i] = tails[low - 1] }
                if low == tails.count { tails.append(i) } else { tails[low] = i }
            }
            var anchor = tails.last ?? -1, anchors: [Int] = []
            while anchor >= 0 { anchors.append(anchor); anchor = previous[anchor] }
            for index in anchors.reversed() {
                let pair = pairs[index]
                match(a, pair.0, b, pair.1, depth: depth + 1); mapping[pair.0] = pair.1
                a = pair.0 + 1; b = pair.1 + 1
            }
            match(a, aEnd, b, bEnd, depth: depth + 1)
        }

        func myers(_ a: Int, _ aEnd: Int, _ b: Int, _ bEnd: Int) -> Bool {
            let n = aEnd - a, m = bEnd - b, limit = min(n + m, 128), origin = limit + 1
            guard abs(n - m) <= limit else { return false }
            var frontier = Array(repeating: 0, count: 2 * limit + 3), trace: [[Int]] = []
            for depth in 0...limit {
                for diagonal in stride(from: -depth, through: depth, by: 2) {
                    let at = origin + diagonal
                    var x = diagonal == -depth || (diagonal != depth && frontier[at - 1] < frontier[at + 1]) ? frontier[at + 1] : frontier[at - 1] + 1
                    var y = x - diagonal
                    while x < n && y < m && exact(before[a + x], after[b + y]) { x += 1; y += 1 }
                    frontier[at] = x
                    if x < n || y < m { continue }
                    if depth > 0 {
                        for d in stride(from: depth, through: 1, by: -1) {
                            let prior = trace[d - 1], k = x - y
                            let previousK = k == -d || (k != d && prior[origin + k - 1] < prior[origin + k + 1]) ? k + 1 : k - 1
                            let previousX = prior[origin + previousK], previousY = previousX - previousK
                            while x > previousX && y > previousY { x -= 1; y -= 1; mapping[a + x] = b + y }
                            x = previousX; y = previousY
                        }
                    }
                    while x > 0 && y > 0 { x -= 1; y -= 1; mapping[a + x] = b + y }
                    return true
                }
                trace.append(frontier)
            }
            return false
        }
    }

    private static func unique(_ text: [String], start: Int, end: Int) -> [String: Int] {
        var result: [String: Int] = [:]
        for i in start..<end { if result[text[i]] == nil { result[text[i]] = i } else { result[text[i]] = -1 } }
        return result
    }
}
