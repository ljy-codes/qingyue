import Foundation

struct WorkspaceSearchOptions {
    var search = SearchOptions()
    var fileNameContains: String = ""
    var contextLines = 0
    var maximumResults = 10_000
    var maximumFileBytes = 10 * 1024 * 1024
    var maximumTotalBytes = 128 * 1024 * 1024
}

struct WorkspaceSearchResult: Equatable {
    let file: LocalWorkspaceFile
    let line: Int
    let column: Int
    let preview: String
    /// UTF-16 range in the editor's normalized LF source, including for UTF-16
    /// and GB18030 files. Opening a result therefore uses the same coordinates.
    let range: NSRange
}

final class WorkspaceReplacementPreview {
    let file: LocalWorkspaceFile
    let matchCount: Int
    let originalText: String
    let replacementText: String
    fileprivate let document: TextFile
    fileprivate var applied = false
    fileprivate init(file: LocalWorkspaceFile, document: TextFile, matchCount: Int, replacementText: String) {
        self.file = file; self.document = document; self.matchCount = matchCount
        self.originalText = document.text; self.replacementText = replacementText
    }
}

enum GlobalSearchEngine {
    static func search(files: [LocalWorkspaceFile], query: String, options: WorkspaceSearchOptions = WorkspaceSearchOptions(), shouldCancel: () -> Bool = { false }) throws -> [WorkspaceSearchResult] {
        guard !query.isEmpty, options.maximumResults > 0, options.maximumFileBytes > 0, options.maximumTotalBytes > 0 else { return [] }
        // Validate even when no readable files exist; malformed patterns must not
        // look like a successful search with no matches.
        _ = try TextSearch.matches(in: "", query: query, options: options.search)
        let limit = min(10_000, options.maximumResults)
        let byteLimit = min(TextFile.maximumSize, options.maximumFileBytes)
        var results: [WorkspaceSearchResult] = []
        var scannedBytes = 0
        for file in files {
            guard !shouldCancel() else { return results }
            if !options.fileNameContains.isEmpty && !file.relativePath.localizedCaseInsensitiveContains(options.fileNameContains) { continue }
            let url = URL(fileURLWithPath: file.path)
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
                  attributes[.type] as? FileAttributeType == .typeRegular,
                  let size = (attributes[.size] as? NSNumber)?.intValue,
                  size >= 0, size <= byteLimit, size <= options.maximumTotalBytes - scannedBytes else { continue }
            scannedBytes += size
            // Reuse strict decoding, BOM handling, binary rejection and newline
            // normalization instead of silently omitting every non-UTF-8 file.
            guard let document = try? TextFile.open(url), document.text.utf16.count <= TextSearch.maximumTextLength else { continue }
            let text = document.text
            let source = text as NSString
            let matches = try TextSearch.matches(in: text, query: query, options: options.search)
            guard !matches.isEmpty else { continue }
            var starts = [0]
            for (index, unit) in text.utf16.enumerated() where unit == 10 { starts.append(index + 1) }
            let context = min(10, max(0, options.contextLines))
            for match in matches {
                guard results.count < limit else { return results }
                let lineIndex = lineIndex(for: match.range.location, starts: starts)
                let first = max(0, lineIndex - context)
                let last = min(starts.count - 1, lineIndex + context)
                let end = last + 1 < starts.count ? starts[last + 1] : source.length
                let previewRange = safeRange(location: starts[first], end: min(end, starts[first] + 1024), source: source)
                let preview = source.substring(with: previewRange).trimmingCharacters(in: .whitespacesAndNewlines)
                results.append(WorkspaceSearchResult(file: file, line: lineIndex + 1,
                    column: match.range.location - starts[lineIndex] + 1,
                    preview: preview + (NSMaxRange(previewRange) < end ? "…" : ""), range: match.range))
            }
        }
        return results
    }

    /// Every preview retains the exact file baseline that was searched. Applying
    /// a preview runs the same digest, encoding, line-ending and atomic-save
    /// checks as an ordinary document save; it cannot bless an external edit.
    static func previewReplacements(files: [LocalWorkspaceFile], query: String, replacement: String,
                                    options: WorkspaceSearchOptions = WorkspaceSearchOptions(),
                                    shouldCancel: () -> Bool = { false }) throws -> [WorkspaceReplacementPreview] {
        guard !query.isEmpty, options.maximumFileBytes > 0, options.maximumTotalBytes > 0, options.maximumResults > 0 else { return [] }
        _ = try TextSearch.matches(in: "", query: query, replacement: replacement, options: options.search)
        var result: [WorkspaceReplacementPreview] = [], consumed = 0, matchesLeft = min(10_000, options.maximumResults)
        for file in files {
            guard !shouldCancel(), matchesLeft > 0 else { return result }
            if !options.fileNameContains.isEmpty && !file.relativePath.localizedCaseInsensitiveContains(options.fileNameContains) { continue }
            let url = URL(fileURLWithPath: file.path)
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
                  attributes[.type] as? FileAttributeType == .typeRegular,
                  let bytes = (attributes[.size] as? NSNumber)?.intValue,
                  bytes >= 0, bytes <= min(TextFile.maximumSize, options.maximumFileBytes), bytes <= options.maximumTotalBytes - consumed else { continue }
            consumed += bytes
            guard let document = try? TextFile.open(url), document.text.utf16.count <= TextSearch.maximumTextLength else { continue }
            let matches = try TextSearch.matches(in: document.text, query: query, replacement: replacement, options: options.search)
            guard !matches.isEmpty else { continue }
            guard matches.count <= matchesLeft else { throw TextSearchError.tooManyMatches }
            matchesLeft -= matches.count
            let changed = TextSearch.replacingAll(in: document.text, matches: matches)
            if !changed.utf8.elementsEqual(document.text.utf8) { result.append(WorkspaceReplacementPreview(file: file, document: document, matchCount: matches.count, replacementText: changed)) }
        }
        return result
    }

    static func apply(_ preview: WorkspaceReplacementPreview) throws {
        guard !preview.applied else { throw TextFileError.externalChange }
        guard try !preview.document.diskChanged() else { throw TextFileError.externalChange }
        preview.document.text = preview.replacementText
        try preview.document.save()
        preview.applied = true
    }

    private static func lineIndex(for offset: Int, starts: [Int]) -> Int {
        var low = 0, high = starts.count
        while low < high { let middle = (low + high) / 2; if starts[middle] <= offset { low = middle + 1 } else { high = middle } }
        return max(0, low - 1)
    }
    private static func safeRange(location: Int, end: Int, source: NSString) -> NSRange {
        var end = end
        if end > location, end < source.length,
           (0xD800...0xDBFF).contains(source.character(at: end - 1)), (0xDC00...0xDFFF).contains(source.character(at: end)) { end -= 1 }
        return NSRange(location: location, length: max(0, end - location))
    }
}
