import Foundation

enum AnnotationAnchorState: String, Codable, CaseIterable {
    case exact
    case shifted
    case uncertain
    case missing

    var label: String {
        switch self {
        case .exact: return "位置准确"
        case .shifted: return "已自动跟随正文移动"
        case .uncertain: return "位置可能已变化，请核对"
        case .missing: return "原文中未找到这段引文"
        }
    }
}

enum AnnotationColor: String, Codable, CaseIterable {
    case amber, blue, green, rose, violet
}

struct AnnotationDraft {
    var documentID: UUID
    var path: String?
    var sourceStart: Int
    var sourceEnd: Int
    var quote: String
    var contextBefore: String = ""
    var contextAfter: String = ""
    var note: String? = nil
    var tags: [String] = []
    var color: AnnotationColor = .amber
}

struct DocumentAnnotation: Codable, Equatable {
    let id: UUID
    var documentID: UUID
    var path: String?
    var sourceStart: Int
    var sourceEnd: Int
    var quote: String
    var contextBefore: String
    var contextAfter: String
    var note: String?
    var tags: [String]
    var color: AnnotationColor
    let createdAt: Date
    var updatedAt: Date
    var anchorState: AnnotationAnchorState
}

struct AnnotationAnchorRepair {
    let id: UUID
    let sourceStart: Int
    let sourceEnd: Int
    let anchorState: AnnotationAnchorState
}

struct AnnotationSaveResult {
    let savedAt: Date
    let errors: [String]
}

enum AnnotationStoreError: LocalizedError {
    case invalid(String)
    case tooMany
    case tooLarge

    var errorDescription: String? {
        switch self {
        case .invalid(let message): return message
        case .tooMany: return "批注数量超过 5,000 条上限。"
        case .tooLarge: return "批注数据超过 64 MiB 上限。"
        }
    }
}

/// Local-only annotation storage. It never writes to the document itself and
/// keeps malformed data isolated so a damaged sidecar cannot block the reader.
final class AnnotationStore {
    static let maximumItems = 5_000
    static let maximumBytes = 64 * 1024 * 1024
    static let maximumQuoteLength = 4_000
    static let maximumNoteLength = 4_000
    static let maximumContextLength = 160
    static let maximumTags = 10
    static let maximumTagLength = 32

    private let root: URL
    private let fileURL: URL
    private(set) var items: [DocumentAnnotation] = []
    private(set) var lastWarning: String?

    init(root: URL) {
        self.root = root.standardizedFileURL
        fileURL = self.root.appendingPathComponent("Annotations.json")
    }

    @discardableResult
    func load() -> [String] {
        lastWarning = nil
        guard FileManager.default.fileExists(atPath: fileURL.path) else {
            items = []
            return []
        }
        do {
            let attributes = try FileManager.default.attributesOfItem(atPath: fileURL.path)
            guard (attributes[.size] as? NSNumber)?.intValue ?? 0 <= Self.maximumBytes else {
                throw AnnotationStoreError.tooLarge
            }
            let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
            let decoded = try decoder.decode([DocumentAnnotation].self, from: Data(contentsOf: fileURL))
            try Self.validate(decoded)
            items = decoded
            return []
        } catch {
            let warning = "批注数据无法读取，已隔离：\(error.localizedDescription)"
            lastWarning = warning
            items = []
            do {
                try FileManager.default.createDirectory(at: quarantineURL.deletingLastPathComponent(), withIntermediateDirectories: true)
                let target = quarantineURL
                    .deletingLastPathComponent()
                    .appendingPathComponent("annotations-\(Int(Date().timeIntervalSince1970))-(UUID().uuidString).json")
                try FileManager.default.moveItem(at: fileURL, to: target)
            } catch {
                lastWarning = warning + "（隔离失败：\(error.localizedDescription)）"
            }
            return [lastWarning!]
        }
    }

    func forDocument(documentID: UUID, path: String? = nil) -> [DocumentAnnotation] {
        let normalized = path.map(Self.canonicalPath)
        return items.filter { item in
            guard item.documentID == documentID else { return false }
            if let normalized { return item.path.map(Self.canonicalPath) == normalized }
            return true
        }.sorted { $0.sourceStart == $1.sourceStart ? $0.createdAt < $1.createdAt : $0.sourceStart < $1.sourceStart }
    }

    @discardableResult
    func add(_ draft: AnnotationDraft, now: Date = Date()) throws -> DocumentAnnotation {
        guard items.count < Self.maximumItems else { throw AnnotationStoreError.tooMany }
        let normalized = try Self.normalized(draft)
        let item = DocumentAnnotation(id: UUID(), documentID: normalized.documentID,
                                      path: normalized.path, sourceStart: normalized.sourceStart,
                                      sourceEnd: normalized.sourceEnd, quote: normalized.quote,
                                      contextBefore: normalized.contextBefore, contextAfter: normalized.contextAfter,
                                      note: normalized.note, tags: normalized.tags, color: normalized.color,
                                      createdAt: now, updatedAt: now, anchorState: .exact)
        items.append(item)
        return item
    }

    @discardableResult
    func update(id: UUID, note: String?, tags: [String], color: AnnotationColor, now: Date = Date()) throws -> Bool {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return false }
        let normalizedNote = try Self.normalizedNote(note)
        let normalizedTags = try Self.normalizedTags(tags)
        items[index].note = normalizedNote
        items[index].tags = normalizedTags
        items[index].color = color
        items[index].updatedAt = now
        return true
    }

    @discardableResult
    func remove(id: UUID) -> Bool {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return false }
        items.remove(at: index)
        return true
    }

    @discardableResult
    func migratePath(documentID: UUID, oldPath: String?, newPath: String) -> Int {
        let old = oldPath.map(Self.canonicalPath)
        let target = Self.canonicalPath(newPath)
        var count = 0
        for index in items.indices where items[index].documentID == documentID {
            guard old == nil || items[index].path.map(Self.canonicalPath) == old else { continue }
            items[index].path = newPath
            items[index].updatedAt = Date()
            count += 1
        }
        _ = target // Keep canonicalization explicit while retaining the user's path spelling.
        return count
    }

    @discardableResult
    func applyAnchorRepairs(_ repairs: [AnnotationAnchorRepair], repairedAt: Date = Date()) -> Int {
        var count = 0
        for repair in repairs {
            guard let index = items.firstIndex(where: { $0.id == repair.id }) else { continue }
            let start = max(0, repair.sourceStart), end = max(start, repair.sourceEnd)
            if items[index].sourceStart != start || items[index].sourceEnd != end || items[index].anchorState != repair.anchorState {
                items[index].sourceStart = start; items[index].sourceEnd = end
                items[index].anchorState = repair.anchorState; items[index].updatedAt = repairedAt
                count += 1
            }
        }
        return count
    }

    func save() -> AnnotationSaveResult {
        let savedAt = Date()
        do {
            try Self.validate(items)
            let data = try JSONEncoder.annotated.encode(items)
            guard data.count <= Self.maximumBytes else { throw AnnotationStoreError.tooLarge }
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            let temporary = root.appendingPathComponent(".Annotations-\(UUID().uuidString).tmp")
            try data.write(to: temporary, options: [.atomic])
            if FileManager.default.fileExists(atPath: fileURL.path) {
                _ = try FileManager.default.replaceItemAt(fileURL, withItemAt: temporary)
            } else {
                try FileManager.default.moveItem(at: temporary, to: fileURL)
            }
            return AnnotationSaveResult(savedAt: savedAt, errors: [])
        } catch {
            return AnnotationSaveResult(savedAt: savedAt, errors: [error.localizedDescription])
        }
    }

    private var quarantineURL: URL { root.appendingPathComponent("Quarantine", isDirectory: true).appendingPathComponent("Annotations.json") }

    private static func validate(_ values: [DocumentAnnotation]) throws {
        guard values.count <= maximumItems else { throw AnnotationStoreError.tooMany }
        for value in values {
            guard value.sourceStart >= 0, value.sourceEnd >= value.sourceStart else { throw AnnotationStoreError.invalid("批注位置无效。") }
            guard value.quote.utf16.count <= maximumQuoteLength else { throw AnnotationStoreError.invalid("批注引文过长。") }
            guard value.contextBefore.utf16.count <= maximumContextLength, value.contextAfter.utf16.count <= maximumContextLength else { throw AnnotationStoreError.invalid("批注上下文过长。") }
            _ = try normalizedNote(value.note); _ = try normalizedTags(value.tags)
        }
    }

    private static func normalized(_ draft: AnnotationDraft) throws -> AnnotationDraft {
        guard draft.sourceStart >= 0, draft.sourceEnd >= draft.sourceStart else { throw AnnotationStoreError.invalid("批注位置无效。") }
        guard draft.quote.utf16.count <= maximumQuoteLength else { throw AnnotationStoreError.invalid("批注引文过长。") }
        guard draft.contextBefore.utf16.count <= maximumContextLength, draft.contextAfter.utf16.count <= maximumContextLength else { throw AnnotationStoreError.invalid("批注上下文过长。") }
        var result = draft; result.note = try normalizedNote(draft.note); result.tags = try normalizedTags(draft.tags)
        return result
    }

    private static func normalizedNote(_ note: String?) throws -> String? {
        guard let note else { return nil }
        guard note.utf16.count <= maximumNoteLength else { throw AnnotationStoreError.invalid("批注备注过长。") }
        let value = note.trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }

    private static func normalizedTags(_ tags: [String]) throws -> [String] {
        guard tags.count <= maximumTags else { throw AnnotationStoreError.invalid("批注标签不能超过 10 个。") }
        var result: [String] = []
        for tag in tags {
            let value = tag.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !value.isEmpty, value.utf16.count <= maximumTagLength else { throw AnnotationStoreError.invalid("批注标签不能为空且不能超过 32 个字符。") }
            if !result.contains(value) { result.append(value) }
        }
        return result
    }

    private static func canonicalPath(_ path: String) -> String { URL(fileURLWithPath: path).standardizedFileURL.path }
}

private extension JSONEncoder {
    static var annotated: JSONEncoder {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]; encoder.dateEncodingStrategy = .iso8601; return encoder
    }
}
