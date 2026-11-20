import Foundation

struct DocumentBookmark: Codable, Equatable {
    let id: UUID
    var documentID: UUID
    var path: String?
    var sourceAnchor: Int
    var label: String
    let createdAt: Date
    var updatedAt: Date
}

enum BookmarkStoreError: LocalizedError {
    case invalid(String), tooMany, tooLarge
    var errorDescription: String? {
        switch self { case .invalid(let message): return message; case .tooMany: return "书签数量超过 500 条上限。"; case .tooLarge: return "书签数据超过 1 MiB 上限。" }
    }
}

final class BookmarkStore {
    static let maximumItems = 500
    static let maximumBytes = 1024 * 1024
    private let root: URL
    private let fileURL: URL
    private(set) var items: [DocumentBookmark] = []
    private(set) var lastWarning: String?

    init(root: URL) { self.root = root.standardizedFileURL; fileURL = self.root.appendingPathComponent("Bookmarks.json") }

    @discardableResult
    func load() -> [String] {
        lastWarning = nil
        guard FileManager.default.fileExists(atPath: fileURL.path) else { items = []; return [] }
        do {
            let attributes = try FileManager.default.attributesOfItem(atPath: fileURL.path)
            guard attributes[.type] as? FileAttributeType == .typeRegular else { throw BookmarkStoreError.invalid("书签数据不是普通文件。") }
            guard (attributes[.size] as? NSNumber)?.intValue ?? Int.max <= Self.maximumBytes else { throw BookmarkStoreError.tooLarge }
            let handle = try FileHandle(forReadingFrom: fileURL); defer { try? handle.close() }
            let data = try handle.read(upToCount: Self.maximumBytes + 1) ?? Data()
            guard data.count <= Self.maximumBytes else { throw BookmarkStoreError.tooLarge }
            let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
            let decoded = try decoder.decode([DocumentBookmark].self, from: data)
            guard decoded.count <= Self.maximumItems else { throw BookmarkStoreError.tooMany }
            guard Set(decoded.map(\.id)).count == decoded.count else { throw BookmarkStoreError.invalid("书签包含重复标识。") }
            try decoded.forEach(validate); items = decoded; return []
        } catch {
            let warning = "书签数据无法读取，已隔离：\(error.localizedDescription)"; lastWarning = warning
            let target = root.appendingPathComponent("Bookmarks-\(UUID().uuidString).corrupt.json")
            try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); try? FileManager.default.moveItem(at: fileURL, to: target)
            items = []; return [warning]
        }
    }

    func forDocument(documentID: UUID, path: String? = nil) -> [DocumentBookmark] {
        if let path, !Self.validPath(path) { return [] }
        let normalized = path.map(Self.canonicalPath)
        return items.filter { item in
            if let normalized { return item.path.map(Self.canonicalPath) == normalized }
            return item.documentID == documentID
        }.sorted { $0.sourceAnchor == $1.sourceAnchor ? $0.createdAt < $1.createdAt : $0.sourceAnchor < $1.sourceAnchor }
    }

    @discardableResult
    func add(documentID: UUID, path: String?, sourceAnchor: Int, label: String? = nil, text: String = "", now: Date = Date()) throws -> DocumentBookmark {
        guard items.count < Self.maximumItems else { throw BookmarkStoreError.tooMany }
        guard path == nil || Self.validPath(path!) else { throw BookmarkStoreError.invalid("书签需要有效的本机绝对路径。") }
        let anchor = min(max(0, sourceAnchor), text.utf16.count)
        let item = DocumentBookmark(id: UUID(), documentID: documentID, path: path.map(Self.canonicalPath), sourceAnchor: anchor,
                                    label: normalizedLabel(label, text: text, anchor: anchor), createdAt: now, updatedAt: now)
        items.append(item); return item
    }

    @discardableResult func remove(id: UUID) -> Bool { guard let index = items.firstIndex(where: { $0.id == id }) else { return false }; items.remove(at: index); return true }

    @discardableResult
    func migratePath(documentID: UUID, oldPath: String?, newPath: String, now: Date = Date()) -> Int {
        guard Self.validPath(newPath), oldPath == nil || Self.validPath(oldPath!) else { return 0 }
        let old = oldPath.map(Self.canonicalPath), target = Self.canonicalPath(newPath); var count = 0
        for index in items.indices {
            let matches = old.map { items[index].path.map(Self.canonicalPath) == $0 }
                ?? (items[index].documentID == documentID && items[index].path == nil)
            guard matches else { continue }
            items[index].documentID = documentID; items[index].path = target; items[index].updatedAt = now; count += 1
        }
        return count
    }

    @discardableResult
    func repair(documentID: UUID, path: String?, oldText: String, newText: String, now: Date = Date()) -> Int {
        guard path == nil || Self.validPath(path!) else { return 0 }
        let normalized = path.map(Self.canonicalPath); var count = 0
        for index in items.indices {
            let matches = normalized.map { items[index].path.map(Self.canonicalPath) == $0 } ?? (items[index].documentID == documentID)
            guard matches else { continue }
            items[index].sourceAnchor = TextAnchorMapper.map(oldText: oldText, newText: newText, oldAnchor: items[index].sourceAnchor); items[index].updatedAt = now; count += 1
        }
        return count
    }

    func save() throws {
        try items.forEach(validate)
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601; let data = try encoder.encode(items); guard data.count <= Self.maximumBytes else { throw BookmarkStoreError.tooLarge }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let temp = root.appendingPathComponent(".Bookmarks.\(UUID().uuidString).tmp")
        defer { try? FileManager.default.removeItem(at: temp) }
        try data.write(to: temp, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: temp.path)
        if FileManager.default.fileExists(atPath: fileURL.path) {
            let attributes = try FileManager.default.attributesOfItem(atPath: fileURL.path)
            guard attributes[.type] as? FileAttributeType == .typeRegular else { throw BookmarkStoreError.invalid("书签数据不是普通文件。") }
            _ = try FileManager.default.replaceItemAt(fileURL, withItemAt: temp, backupItemName: nil, options: .usingNewMetadataOnly)
        } else { try FileManager.default.moveItem(at: temp, to: fileURL) }
    }

    private func validate(_ item: DocumentBookmark) throws {
        guard item.sourceAnchor >= 0, item.label.utf16.count <= 160,
              item.path == nil || Self.validPath(item.path!) else { throw BookmarkStoreError.invalid("书签字段无效。") }
    }
    private func normalizedLabel(_ label: String?, text: String, anchor: Int) -> String {
        if let value = label?.trimmingCharacters(in: .whitespacesAndNewlines), !value.isEmpty { return boundedLabel(value) }
        let source = text as NSString; guard source.length > 0 else { return "位置 \(anchor)" }
        let point = min(anchor, source.length - 1), line = source.lineRange(for: NSRange(location: point, length: 0)); let value = source.substring(with: line).trimmingCharacters(in: .whitespacesAndNewlines)
        return boundedLabel(value.isEmpty ? "位置 \(anchor)" : value)
    }
    private func boundedLabel(_ value: String) -> String {
        var units = 0, result = ""
        for character in value { let length = String(character).utf16.count; guard units + length <= 160 else { break }; result.append(character); units += length }
        return result
    }
    private static func validPath(_ path: String) -> Bool { path.hasPrefix("/") && !path.contains("\0") && path.utf8.count <= 4096 }
    private static func canonicalPath(_ value: String) -> String { URL(fileURLWithPath: value).standardizedFileURL.path }
}
