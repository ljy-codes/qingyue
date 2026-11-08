import Foundation
import Darwin

struct WorkspaceEntry: Codable {
    var id: UUID
    var path: String?
    var isReading: Bool
    var sourceAnchor: Int
    var selectionLocation: Int
    var selectionLength: Int
    var automaticKind: Bool
    var kindRaw: String
    var recoverDraft: Bool
}

struct WorkspaceSession: Codable {
    var version: Int = 1
    var entries: [WorkspaceEntry]
    var activeID: UUID?
}

struct WorkspaceSaveResult {
    let savedAt: Date
    let errors: [String]
}

struct WorkspaceLoadedEntry {
    let entry: WorkspaceEntry
    let file: TextFile
}

struct WorkspaceLoadResult {
    let entries: [WorkspaceLoadedEntry]
    let activeID: UUID?
    let warnings: [String]
}

/// Session metadata and unsaved text have separate lifetimes. All encoding and
/// filesystem work is serialized; callers capture immutable snapshots first.
final class WorkspaceStore {
    static let maximumDocuments = 200
    static let maximumDraftBytes = 150 * 1024 * 1024
    private static let maximumSessionBytes = 2 * 1024 * 1024
    private let root: URL
    private let queue = DispatchQueue(label: "qingyue.workspace", qos: .utility)
    private var legacyWasLoaded = false
    private var sessionURL: URL { root.appendingPathComponent("Session.json") }
    private var draftDirectory: URL { root.appendingPathComponent("Drafts", isDirectory: true) }
    private var quarantineDirectory: URL { root.appendingPathComponent("Quarantine", isDirectory: true) }
    private var legacyURL: URL { root.appendingPathComponent("Recovery.json") }

    init(root: URL) { self.root = root.standardizedFileURL }

    func save(session: WorkspaceSession, drafts: [TextFile.RecoverySnapshot],
              completion: @escaping (WorkspaceSaveResult) -> Void) {
        queue.async {
            let result = self.performSave(session: session, drafts: drafts)
            DispatchQueue.main.async { completion(result) }
        }
    }

    /// Used only at an explicit quit/close boundary. It waits for prior writes
    /// without waiting for main-thread callbacks, so it cannot deadlock the UI.
    func saveAndWait(session: WorkspaceSession, drafts: [TextFile.RecoverySnapshot]) -> WorkspaceSaveResult {
        queue.sync { performSave(session: session, drafts: drafts) }
    }

    func load(completion: @escaping (WorkspaceLoadResult) -> Void) {
        queue.async {
            let result = self.performLoad()
            DispatchQueue.main.async { completion(result) }
        }
    }

    func clearDrafts(completion: @escaping (Error?) -> Void) {
        queue.async {
            var failure: Error?
            do {
                try self.ensureDirectories()
                for url in try self.draftFiles() { try FileManager.default.removeItem(at: url) }
                // An old-format draft must not reappear on the next launch after
                // the user explicitly clears all recoverable drafts.
                if FileManager.default.fileExists(atPath: self.legacyURL.path) {
                    try FileManager.default.removeItem(at: self.legacyURL)
                }
                for url in try FileManager.default.contentsOfDirectory(at: self.quarantineDirectory, includingPropertiesForKeys: nil) {
                    if ["draft-", "legacy-", "duplicate-", "capacity-"].contains(where: { url.lastPathComponent.hasPrefix($0) }) {
                        try FileManager.default.removeItem(at: url)
                    }
                }
                self.legacyWasLoaded = false
            } catch { failure = error }
            DispatchQueue.main.async { completion(failure) }
        }
    }

    func flush() { queue.sync {} }

    private func performSave(session: WorkspaceSession, drafts: [TextFile.RecoverySnapshot]) -> WorkspaceSaveResult {
        var errors: [String] = []
        do {
            try Self.validateSession(session)
            guard drafts.count <= Self.maximumDocuments else { throw storeError("恢复草稿数量超出限制，原有记录已保留。") }
            let metadata = try JSONEncoder().encode(session)
            guard metadata.count <= Self.maximumSessionBytes else { throw storeError("会话信息过大，原有记录已保留。") }
            try ensureDirectories()
            let allowedIDs = Set(session.entries.filter { $0.recoverDraft }.map { $0.id })
            let liveIDs = Set(drafts.map { $0.id })
            guard liveIDs.count == drafts.count, liveIDs.isSubset(of: allowedIDs) else {
                throw storeError("草稿标识与可恢复标签不一致，原有会话和草稿已保留。")
            }
            let snapshots = drafts
            let existing = try draftFiles()
            var totalBytes = 0
            for url in existing {
                if let id = UUID(uuidString: url.deletingPathExtension().lastPathComponent), liveIDs.contains(id) {
                    do { totalBytes += try fileSize(url) }
                    catch { errors.append("草稿容量无法读取：\(error.localizedDescription)") }
                }
            }
            for snapshot in snapshots {
                let target = draftURL(snapshot.id)
                do {
                    _ = try TextFile.restore(snapshot: snapshot)
                    let data = try JSONEncoder().encode(snapshot)
                    let oldSize = FileManager.default.fileExists(atPath: target.path) ? try fileSize(target) : 0
                    guard data.count <= Self.maximumDraftBytes,
                          totalBytes - oldSize <= Self.maximumDraftBytes - data.count else {
                        throw storeError("草稿总量超过 150 MiB，已保留这份文档上一次成功的草稿。")
                    }
                    try atomicPrivateWrite(data, to: target)
                    totalBytes = totalBytes - oldSize + data.count
                } catch {
                    errors.append("\(snapshot.path.map { URL(fileURLWithPath: $0).lastPathComponent } ?? "未命名")：\(error.localizedDescription)")
                }
            }
            // Commit metadata only after draft attempts. A crash between these
            // stages leaves discoverable orphan drafts rather than losing text.
            try atomicPrivateWrite(metadata, to: sessionURL)
            for url in existing {
                guard let id = UUID(uuidString: url.deletingPathExtension().lastPathComponent), !liveIDs.contains(id) else { continue }
                do { try FileManager.default.removeItem(at: url) }
                catch { errors.append("旧草稿清理失败：\(error.localizedDescription)") }
            }
            if legacyWasLoaded && errors.isEmpty && FileManager.default.fileExists(atPath: legacyURL.path) {
                do { try quarantine(legacyURL, prefix: "legacy") ; legacyWasLoaded = false }
                catch { errors.append("旧格式草稿归档失败：\(error.localizedDescription)") }
            }
        } catch { errors.append(error.localizedDescription) }
        return WorkspaceSaveResult(savedAt: Date(), errors: errors)
    }

    private func performLoad() -> WorkspaceLoadResult {
        var warnings: [String] = []
        var session = WorkspaceSession(entries: [], activeID: nil)
        let fm = FileManager.default
        do { try ensureDirectories() }
        catch { return WorkspaceLoadResult(entries: [], activeID: nil, warnings: [error.localizedDescription]) }
        let hadSessionFile = fm.fileExists(atPath: sessionURL.path)
        if hadSessionFile {
            do {
                guard try fileSize(sessionURL) <= Self.maximumSessionBytes else { throw storeError("会话文件超过容量限制。") }
                session = try JSONDecoder().decode(WorkspaceSession.self, from: Data(contentsOf: sessionURL))
                try Self.validateSession(session)
            } catch {
                warnings.append("会话信息无法读取，已尝试保留到隔离目录：\(error.localizedDescription)")
                do { try quarantine(sessionURL, prefix: "session") }
                catch { warnings.append("会话隔离失败：\(error.localizedDescription)") }
                session = WorkspaceSession(entries: [], activeID: nil)
            }
        }
        let allDraftURLs: [URL]
        do { allDraftURLs = try draftFiles().sorted { $0.lastPathComponent < $1.lastPathComponent } }
        catch { return WorkspaceLoadResult(entries: [], activeID: nil, warnings: warnings + [error.localizedDescription]) }
        var loaded: [WorkspaceLoadedEntry] = []
        var pathIndexes: [String: Int] = [:]
        var remappedIDs: [UUID: UUID] = [:]
        var handledIDs = Set<UUID>()
        var loadedDraftBytes = 0
        func append(_ file: TextFile, entry: WorkspaceEntry, draft: URL?) {
            let pathKey = file.url.map(Self.canonicalPath)
            if let key = pathKey, let index = pathIndexes[key] {
                let existing = loaded[index]
                if file.isDirty && !existing.file.isDirty {
                    // A new draft can reach disk before its new session record.
                    // It is authoritative over the clean disk reread, while the
                    // old session's order and viewing metadata remain useful.
                    var replacement = existing.entry
                    replacement.id = entry.id; replacement.path = file.url?.path; replacement.recoverDraft = true
                    loaded[index] = WorkspaceLoadedEntry(entry: replacement, file: file)
                    remappedIDs[existing.entry.id] = entry.id
                    warnings.append("已找回更新的未保存草稿：\(file.title)")
                    return
                }
                if file.isDirty && existing.file.isDirty {
                    // Two independent dirty buffers must both remain visible.
                    // Detach the second as an unsaved copy, avoiding two buffers
                    // that could each silently write back to the same path.
                    guard loaded.count < Self.maximumDocuments else {
                        warnings.append("同文件的额外草稿超过恢复上限，已尝试隔离保留。")
                        if let url = draft {
                            do { try quarantine(url, prefix: "capacity") }
                            catch { warnings.append("超限草稿隔离失败：\(error.localizedDescription)") }
                        }
                        return
                    }
                    let detached = TextFile(text: file.text)
                    detached.codec = file.codec; detached.lineEnding = file.lineEnding
                    var detachedEntry = entry
                    detachedEntry.id = detached.id; detachedEntry.path = nil; detachedEntry.recoverDraft = true
                    loaded.append(WorkspaceLoadedEntry(entry: detachedEntry, file: detached))
                    remappedIDs[entry.id] = detached.id
                    warnings.append("\(file.title) 存在另一份未保存内容，已作为独立恢复副本打开；请另存为。")
                    return
                }
                remappedIDs[entry.id] = existing.entry.id
                warnings.append("重复文件已跳过：\(file.title)")
                return
            }
            guard loaded.count < Self.maximumDocuments else {
                warnings.append("超过恢复文档上限的草稿已尝试隔离。")
                if let url = draft {
                    do { try quarantine(url, prefix: "capacity") }
                    catch { warnings.append("超限草稿隔离失败：\(error.localizedDescription)") }
                }
                return
            }
            if let key = pathKey { pathIndexes[key] = loaded.count }
            loaded.append(WorkspaceLoadedEntry(entry: entry, file: file))
        }
        func readDraft(_ url: URL) -> TextFile? {
            do {
                let size = try fileSize(url)
                guard size <= Self.maximumDraftBytes, loadedDraftBytes <= Self.maximumDraftBytes - size else {
                    throw storeError("恢复草稿超过本次加载容量。")
                }
                let snapshot = try JSONDecoder().decode(TextFile.RecoverySnapshot.self, from: Data(contentsOf: url))
                guard snapshot.id == UUID(uuidString: url.deletingPathExtension().lastPathComponent) else { throw TextFileError.recoveryInvalid }
                let restored = try TextFile.restore(snapshot: snapshot)
                loadedDraftBytes += size
                return restored
            } catch {
                warnings.append("一份草稿无法恢复，已尝试隔离：\(error.localizedDescription)")
                do { try quarantine(url, prefix: "draft") }
                catch { warnings.append("草稿隔离失败：\(error.localizedDescription)") }
                return nil
            }
        }
        for entry in session.entries {
            handledIDs.insert(entry.id)
            let url = draftURL(entry.id)
            if entry.recoverDraft, fm.fileExists(atPath: url.path), let file = readDraft(url) {
                if file.isDirty || file.url == nil { append(file, entry: entry, draft: url); continue }
                // A stale clean snapshot is never authoritative over the disk.
            }
            if let path = entry.path {
                do { append(try TextFile.open(URL(fileURLWithPath: path)), entry: entry, draft: nil) }
                catch { warnings.append("未能重新打开 \(URL(fileURLWithPath: path).lastPathComponent)：\(error.localizedDescription)") }
            }
        }
        for url in allDraftURLs {
            guard let id = UUID(uuidString: url.deletingPathExtension().lastPathComponent), !handledIDs.contains(id), fm.fileExists(atPath: url.path) else { continue }
            handledIDs.insert(id)
            guard let file = readDraft(url) else { continue }
            let entry = WorkspaceEntry(id: id, path: file.url?.path, isReading: false, sourceAnchor: 0,
                                       selectionLocation: 0, selectionLength: 0, automaticKind: true,
                                       kindRaw: "plain", recoverDraft: true)
            if !file.isDirty, let path = file.url {
                do { append(try TextFile.open(path), entry: entry, draft: url) }
                catch { warnings.append("未能打开草稿对应文件：\(error.localizedDescription)") }
            } else { append(file, entry: entry, draft: url) }
        }
        if !hadSessionFile, allDraftURLs.isEmpty, fm.fileExists(atPath: legacyURL.path) {
            do {
                let files = try TextFile.recovery(from: legacyURL)
                for file in files.prefix(Self.maximumDocuments) {
                    let entry = WorkspaceEntry(id: file.id, path: file.url?.path, isReading: false, sourceAnchor: 0,
                                               selectionLocation: 0, selectionLength: 0, automaticKind: true,
                                               kindRaw: "plain", recoverDraft: true)
                    append(file, entry: entry, draft: nil)
                }
                legacyWasLoaded = !files.isEmpty
                if legacyWasLoaded { warnings.append("已读取旧版本未保存草稿，将在新记录成功写入后归档旧文件。") }
            } catch {
                warnings.append("旧版本恢复草稿无法读取，已尝试隔离：\(error.localizedDescription)")
                do { try quarantine(legacyURL, prefix: "legacy-invalid") }
                catch { warnings.append("旧草稿隔离失败：\(error.localizedDescription)") }
            }
        }
        var requestedActive = session.activeID
        var visitedActive = Set<UUID>()
        while let id = requestedActive, visitedActive.insert(id).inserted, let remapped = remappedIDs[id], remapped != id {
            requestedActive = remapped
        }
        let active = requestedActive.flatMap { id in loaded.contains(where: { $0.entry.id == id }) ? id : nil } ?? loaded.first?.entry.id
        return WorkspaceLoadResult(entries: loaded, activeID: active, warnings: warnings)
    }

    private static func validateSession(_ session: WorkspaceSession) throws {
        guard session.version == 1, session.entries.count <= maximumDocuments,
              Set(session.entries.map { $0.id }).count == session.entries.count,
              session.entries.allSatisfy({ entry in
                  (entry.path.map { $0.hasPrefix("/") && !$0.contains("\0") && $0.utf8.count <= 32768 } ?? true)
                  && entry.sourceAnchor >= 0 && entry.sourceAnchor <= 100_000_000
                  && entry.selectionLocation >= 0 && entry.selectionLocation <= 100_000_000
                  && entry.selectionLength >= 0 && entry.selectionLength <= 100_000_000
                  && entry.kindRaw.utf8.count <= 128
              }) else { throw storeError("会话信息无效或超过容量限制，原有记录已保留。") }
    }

    private func ensureDirectories() throws {
        for url in [root, draftDirectory, quarantineDirectory] {
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
            guard attributes[.type] as? FileAttributeType == .typeDirectory else { throw storeError("应用数据目录不是普通目录。") }
            guard chmod(url.path, 0o700) == 0 else { throw posixError("无法保护应用数据目录") }
        }
    }

    private func draftURL(_ id: UUID) -> URL { draftDirectory.appendingPathComponent(id.uuidString + ".json") }
    private func draftFiles() throws -> [URL] {
        try FileManager.default.contentsOfDirectory(at: draftDirectory, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "json" && UUID(uuidString: $0.deletingPathExtension().lastPathComponent) != nil }
    }
    private func fileSize(_ url: URL) throws -> Int {
        let attrs = try FileManager.default.attributesOfItem(atPath: url.path)
        guard attrs[.type] as? FileAttributeType == .typeRegular else { throw storeError("恢复记录不是普通文件。") }
        return (attrs[.size] as? NSNumber)?.intValue ?? 0
    }
    private func quarantine(_ url: URL, prefix: String) throws {
        let destination = quarantineDirectory.appendingPathComponent(prefix + "-" + UUID().uuidString + "-" + url.lastPathComponent)
        try FileManager.default.moveItem(at: url, to: destination)
    }
    private static func canonicalPath(_ url: URL) -> String {
        if let path = realpath(url.path, nil) { defer { free(path) }; return String(cString: path) }
        return url.standardizedFileURL.path
    }
    private func atomicPrivateWrite(_ data: Data, to url: URL) throws {
        let temporary = url.deletingLastPathComponent().appendingPathComponent(".workspace-\(UUID().uuidString).tmp")
        defer { try? FileManager.default.removeItem(at: temporary) }
        let descriptor = Darwin.open(temporary.path, O_WRONLY | O_CREAT | O_EXCL, 0o600)
        guard descriptor >= 0 else { throw posixError("无法创建恢复临时文件") }
        var savedError: Int32?
        data.withUnsafeBytes { buffer in
            var offset = 0
            while offset < data.count {
                let count = Darwin.write(descriptor, buffer.baseAddress!.advanced(by: offset), data.count - offset)
                if count < 0 {
                    if errno == EINTR { continue }
                    savedError = errno; break
                }
                if count == 0 { savedError = EIO; break }
                offset += count
            }
        }
        let synced = fsync(descriptor), syncError = errno
        close(descriptor)
        if let code = savedError { throw posixError("恢复记录写入失败", code: code) }
        guard synced == 0 else { throw posixError("恢复记录写入失败", code: syncError) }
        guard rename(temporary.path, url.path) == 0 else { throw posixError("恢复记录替换失败") }
    }
    private static func storeError(_ message: String) -> NSError { NSError(domain: "WorkspaceStore", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
    private func storeError(_ message: String) -> NSError { Self.storeError(message) }
    private func posixError(_ prefix: String, code: Int32 = errno) -> NSError {
        storeError(prefix + "：" + String(cString: strerror(code)))
    }
}
