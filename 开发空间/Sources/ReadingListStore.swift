import Foundation

struct ReadingListItem: Codable, Equatable {
    let id: UUID
    var path: String
    var pinned: Bool
    var completed: Bool
    var progress: Double
    var sourceAnchor: Int
    var updatedAt: Date
}

enum ReadingListStoreError: LocalizedError {
    case invalid(String), tooMany, tooLarge
    var errorDescription: String? {
        switch self {
        case .invalid(let message): return message
        case .tooMany: return "阅读清单超过 500 条上限。"
        case .tooLarge: return "阅读清单文件超过容量上限。"
        }
    }
}

final class ReadingListStore {
    static let maximumItems = 500
    private static let maximumBytes = 2 * 1024 * 1024
    private let root: URL
    private var fileURL: URL { root.appendingPathComponent("ReadingList.json") }
    private(set) var items: [ReadingListItem] = []
    private(set) var lastWarning: String?

    init(root: URL) { self.root = root.standardizedFileURL }
    func item(for path: String) -> ReadingListItem? {
        guard let canonical = try? Self.canonicalPath(path) else { return nil }
        return items.first { $0.path == canonical }
    }
    @discardableResult func load() -> [String] {
        lastWarning = nil
        guard FileManager.default.fileExists(atPath: fileURL.path) else { items = []; return [] }
        do {
            let attributes = try FileManager.default.attributesOfItem(atPath: fileURL.path)
            guard attributes[.type] as? FileAttributeType == .typeRegular else { throw ReadingListStoreError.invalid("阅读清单不是普通文件。") }
            guard (attributes[.size] as? NSNumber)?.intValue ?? Int.max <= Self.maximumBytes else { throw ReadingListStoreError.tooLarge }
            let handle = try FileHandle(forReadingFrom: fileURL); defer { try? handle.close() }
            let data = try handle.read(upToCount: Self.maximumBytes + 1) ?? Data()
            guard data.count <= Self.maximumBytes else { throw ReadingListStoreError.tooLarge }
            let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
            let decoded = try decoder.decode([ReadingListItem].self, from: data)
            guard decoded.count <= Self.maximumItems else { throw ReadingListStoreError.tooMany }
            let validated = try decoded.map(Self.validate)
            guard Set(validated.map(\.id)).count == validated.count, Set(validated.map(\.path)).count == validated.count else { throw ReadingListStoreError.invalid("阅读清单含有重复条目。") }
            items = validated
            return []
        } catch {
            var warning = "阅读清单无法读取：\(error.localizedDescription)"
            let target = root.appendingPathComponent("ReadingList-\(UUID().uuidString).corrupt.json")
            do { try FileManager.default.moveItem(at: fileURL, to: target); warning += "（已隔离）" }
            catch { warning += "（原文件保留：\(error.localizedDescription)）" }
            lastWarning = warning; items = []
            return [warning]
        }
    }
    @discardableResult
    func upsert(path: String, pinned: Bool? = nil, completed: Bool? = nil, progress: Double? = nil, sourceAnchor: Int? = nil, now: Date = Date()) throws -> ReadingListItem {
        let canonical = try Self.canonicalPath(path)
        if let progress, !progress.isFinite { throw ReadingListStoreError.invalid("阅读进度必须是有限数值。") }
        guard sourceAnchor == nil || sourceAnchor! >= 0 else { throw ReadingListStoreError.invalid("阅读位置无效。") }
        if let index = items.firstIndex(where: { $0.path == canonical }) {
            if let pinned { items[index].pinned = pinned }
            if let completed { items[index].completed = completed }
            if let progress { items[index].progress = min(1, max(0, progress)) }
            if let sourceAnchor { items[index].sourceAnchor = sourceAnchor }
            items[index].updatedAt = now
            return items[index]
        }
        guard items.count < Self.maximumItems else { throw ReadingListStoreError.tooMany }
        let item = ReadingListItem(id: UUID(), path: canonical, pinned: pinned ?? false, completed: completed ?? false,
                                  progress: min(1, max(0, progress ?? 0)), sourceAnchor: sourceAnchor ?? 0, updatedAt: now)
        items.append(item)
        return item
    }
    @discardableResult func remove(path: String) -> Bool {
        guard let canonical = try? Self.canonicalPath(path), let index = items.firstIndex(where: { $0.path == canonical }) else { return false }
        items.remove(at: index); return true
    }
    func save() throws {
        _ = try items.map(Self.validate)
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601
        let data = try encoder.encode(items)
        guard data.count <= Self.maximumBytes else { throw ReadingListStoreError.tooLarge }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let temp = root.appendingPathComponent(".ReadingList.\(UUID().uuidString).tmp")
        defer { try? FileManager.default.removeItem(at: temp) }
        try data.write(to: temp, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: temp.path)
        if FileManager.default.fileExists(atPath: fileURL.path) {
            let attributes = try FileManager.default.attributesOfItem(atPath: fileURL.path)
            guard attributes[.type] as? FileAttributeType == .typeRegular else { throw ReadingListStoreError.invalid("阅读清单不是普通文件。") }
            _ = try FileManager.default.replaceItemAt(fileURL, withItemAt: temp, backupItemName: nil, options: .usingNewMetadataOnly)
        } else { try FileManager.default.moveItem(at: temp, to: fileURL) }
    }
    private static func validate(_ value: ReadingListItem) throws -> ReadingListItem {
        guard value.progress.isFinite, value.progress >= 0, value.progress <= 1, value.sourceAnchor >= 0 else { throw ReadingListStoreError.invalid("阅读清单条目无效。") }
        var result = value; result.path = try canonicalPath(value.path); return result
    }
    private static func canonicalPath(_ value: String) throws -> String {
        guard value.hasPrefix("/"), !value.contains("\0"), value.utf8.count <= 4096 else { throw ReadingListStoreError.invalid("阅读清单需要有效的本机绝对路径。") }
        return URL(fileURLWithPath: value).standardizedFileURL.path
    }
}
