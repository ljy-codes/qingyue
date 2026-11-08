import Foundation
import Darwin

@main
struct WorkspaceStoreTests {
    static var checks = 0
    static var groups = 0

    static func main() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-workspace-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        func fixture(_ name: String) -> URL { root.appendingPathComponent(name) }

        try group("Session preserves order, active tab and view state while rereading saved disk files") {
            let a = fixture("a.txt"), b = fixture("b.md")
            try Data("first".utf8).write(to: a); try Data("# second".utf8).write(to: b)
            let first = try TextFile.open(a), second = try TextFile.open(b)
            var e1 = entry(first); e1.sourceAnchor = 3; e1.selectionLocation = 2
            var e2 = entry(second); e2.isReading = true; e2.automaticKind = false; e2.kindRaw = "markdown"
            let store = WorkspaceStore(root: fixture("session-order"))
            let saved = store.saveAndWait(session: WorkspaceSession(entries: [e2, e1], activeID: first.id), drafts: [])
            expect(saved.errors.isEmpty, "session write succeeds")
            try Data("changed outside".utf8).write(to: a)
            let loaded = try load(store)
            expect(loaded.entries.map { $0.entry.id } == [second.id, first.id], "exact tab order retained")
            expect(loaded.activeID == first.id, "active identity retained")
            expect(loaded.entries[0].entry.isReading && !loaded.entries[0].entry.automaticKind, "view and explicit kind retained")
            expect(loaded.entries[1].entry.sourceAnchor == 3 && loaded.entries[1].entry.selectionLocation == 2, "source position retained")
            expect(loaded.entries[1].file.text == "changed outside", "clean session reads disk instead of stale text")
            expect(!loaded.entries[1].file.isDirty, "reopened saved text is clean")
        }
        try group("Immutable snapshots restore unsaved text, baseline and external conflicts") {
            let p = fixture("dirty.txt"); try Data("baseline\r\n".utf8).write(to: p)
            let file = try TextFile.open(p); file.text = "草稿 😀\n"
            let snapshot = file.recoverySnapshot(); file.text = "newer changes not in this snapshot"
            let store = WorkspaceStore(root: fixture("dirty"))
            expect(store.saveAndWait(session: WorkspaceSession(entries: [entry(file)], activeID: file.id), drafts: [snapshot]).errors.isEmpty, "snapshot write succeeds")
            try Data("external\r\n".utf8).write(to: p)
            let loaded = try load(store)
            expect(loaded.entries.count == 1 && loaded.entries[0].file.text == "草稿 😀\n", "captured value is immutable")
            let restored = loaded.entries[0].file
            expect(restored.isDirty && restored.lineEnding == .crlf, "dirty and format baseline survive")
            try expect(try restored.diskChanged(), "disk change remains detectable after restore")
            do { try restored.save(); fatalError("Expected external conflict") }
            catch TextFileError.externalChange { checks += 1 }
            try expect(try Data(contentsOf: p) == Data("external\r\n".utf8), "recovery never writes original file")
        }
        try group("Untitled and empty deletion drafts survive separately from session metadata") {
            let p = fixture("empty-deletion.txt"); try Data("original".utf8).write(to: p)
            let deletion = try TextFile.open(p); deletion.text = ""
            let untitled = TextFile(text: "未命名内容"), empty = TextFile()
            let store = WorkspaceStore(root: fixture("empty"))
            let docs = [deletion, untitled, empty]
            expect(store.saveAndWait(session: WorkspaceSession(entries: docs.map(entry), activeID: untitled.id), drafts: docs.map { $0.recoverySnapshot() }).errors.isEmpty, "all draft forms save")
            let loaded = try load(store)
            expect(loaded.entries.map { $0.file.text } == ["", "未命名内容", ""], "empty and untitled content restored")
            expect(loaded.entries.allSatisfy { $0.file.isDirty }, "drafts remain unsaved")
            expect(loaded.activeID == untitled.id, "untitled active tab survives")
            let metadata = try String(contentsOf: fixture("empty/Session.json"), encoding: .utf8)
            expect(!metadata.contains("未命名内容"), "session never embeds text")
        }
        try group("Clean stale draft cannot overwrite a saved document's newer disk state") {
            let p = fixture("clean.txt"); try Data("old".utf8).write(to: p)
            let file = try TextFile.open(p), store = WorkspaceStore(root: fixture("clean"))
            expect(store.saveAndWait(session: WorkspaceSession(entries: [entry(file)], activeID: file.id), drafts: [file.recoverySnapshot()]).errors.isEmpty, "clean snapshot stored for regression")
            try Data("fresh".utf8).write(to: p)
            let loaded = try load(store)
            expect(loaded.entries[0].file.text == "fresh", "clean snapshot is not authority")
        }
        try group("Recovery exclusion and disabling remove old drafts without touching original files") {
            let p = fixture("exclude.txt"); try Data("saved".utf8).write(to: p)
            let file = try TextFile.open(p); file.text = "secret draft"
            let store = WorkspaceStore(root: fixture("exclusion")); let session = WorkspaceSession(entries: [entry(file)], activeID: file.id)
            _ = store.saveAndWait(session: session, drafts: [file.recoverySnapshot()])
            var excluded = entry(file); excluded.recoverDraft = false
            expect(store.saveAndWait(session: WorkspaceSession(entries: [excluded], activeID: file.id), drafts: []).errors.isEmpty, "explicit exclusion saves")
            let loaded = try load(store)
            expect(loaded.entries[0].file.text == "saved", "excluded draft is not recovered")
            try expect(try contents(fixture("exclusion/Drafts")).isEmpty, "excluded draft removed")
            try expect(try Data(contentsOf: p) == Data("saved".utf8), "original untouched")
        }
        try group("Closing a tab removes only its obsolete draft") {
            let first = TextFile(text: "a"), second = TextFile(text: "b")
            let store = WorkspaceStore(root: fixture("close"))
            _ = store.saveAndWait(session: WorkspaceSession(entries: [entry(first), entry(second)], activeID: first.id), drafts: [first.recoverySnapshot(), second.recoverySnapshot()])
            _ = store.saveAndWait(session: WorkspaceSession(entries: [entry(second)], activeID: second.id), drafts: [second.recoverySnapshot()])
            let loaded = try load(store)
            expect(loaded.entries.count == 1 && loaded.entries[0].file.text == "b", "closed draft not orphan recovered")
            try expect(try contents(fixture("close/Drafts")).count == 1, "remaining draft retained")
        }
        try group("One damaged draft is quarantined while other drafts and clean paths load") {
            let first = TextFile(text: "damaged"), second = TextFile(text: "keep me")
            let store = WorkspaceStore(root: fixture("corrupt"))
            _ = store.saveAndWait(session: WorkspaceSession(entries: [entry(first), entry(second)], activeID: second.id), drafts: [first.recoverySnapshot(), second.recoverySnapshot()])
            try Data("{truncated".utf8).write(to: fixture("corrupt/Drafts/\(first.id.uuidString).json"))
            let loaded = try load(store)
            expect(loaded.entries.count == 1 && loaded.entries[0].file.text == "keep me", "unaffected draft restored")
            expect(!loaded.warnings.isEmpty, "damage is visible")
            try expect(try contents(fixture("corrupt/Quarantine")).count == 1, "damaged bytes retained in quarantine")
            _ = store.saveAndWait(session: WorkspaceSession(entries: [entry(second)], activeID: second.id), drafts: [second.recoverySnapshot()])
            try expect(try contents(fixture("corrupt/Quarantine")).count == 1, "future save never deletes quarantined bytes")
        }
        try group("Damaged session can recover orphan drafts and preserves invalid metadata") {
            let file = TextFile(text: "orphan survives"), store = WorkspaceStore(root: fixture("orphan"))
            _ = store.saveAndWait(session: WorkspaceSession(entries: [entry(file)], activeID: file.id), drafts: [file.recoverySnapshot()])
            try Data("not json".utf8).write(to: fixture("orphan/Session.json"))
            let loaded = try load(store)
            expect(loaded.entries.count == 1 && loaded.entries[0].file.text == "orphan survives", "orphan recovered")
            let isolated = try contents(fixture("orphan/Quarantine"))
            expect(!loaded.warnings.isEmpty && isolated.count == 1, "broken metadata retained")
        }
        try group("Missing paths and duplicate file references do not abort valid entries") {
            let p = fixture("duplicate.txt"); try Data("same file".utf8).write(to: p)
            let a = try TextFile.open(p), b = try TextFile.open(p), missing = TextFile(text: "", url: fixture("missing.txt"))
            let store = WorkspaceStore(root: fixture("duplicate"))
            _ = store.saveAndWait(session: WorkspaceSession(entries: [entry(a), entry(missing), entry(b)], activeID: missing.id), drafts: [])
            let loaded = try load(store)
            expect(loaded.entries.count == 1 && loaded.entries[0].entry.id == a.id, "duplicate canonical path loaded once")
            expect(loaded.warnings.count == 2, "missing and duplicate warnings independent")
            expect(loaded.activeID == a.id, "missing active tab falls back safely")
        }
        try group("Individual snapshot failure preserves its old draft while updating other documents") {
            let a = TextFile(text: "prior a"), b = TextFile(text: "prior b"), store = WorkspaceStore(root: fixture("isolation"))
            let session = WorkspaceSession(entries: [entry(a), entry(b)], activeID: a.id)
            _ = store.saveAndWait(session: session, drafts: [a.recoverySnapshot(), b.recoverySnapshot()])
            a.text = String(repeating: "z", count: TextFile.maximumSize + 1); b.text = "new b"
            let result = store.saveAndWait(session: session, drafts: [a.recoverySnapshot(), b.recoverySnapshot()])
            expect(result.errors.count == 1, "only failing document reported")
            let loaded = try load(store)
            expect(loaded.entries.map { $0.file.text } == ["prior a", "new b"], "old valid a and fresh b retained")
        }
        try group("Invalid session never replaces previously usable state") {
            let file = TextFile(text: "valid"), store = WorkspaceStore(root: fixture("invalid"))
            let good = WorkspaceSession(entries: [entry(file)], activeID: file.id)
            _ = store.saveAndWait(session: good, drafts: [file.recoverySnapshot()])
            let invalid = WorkspaceSession(version: 99, entries: [], activeID: nil)
            expect(!store.saveAndWait(session: invalid, drafts: []).errors.isEmpty, "unknown version rejected")
            var invalidEntry = entry(file); invalidEntry.sourceAnchor = -1
            expect(!store.saveAndWait(session: WorkspaceSession(entries: [invalidEntry], activeID: nil), drafts: []).errors.isEmpty, "negative positions rejected")
            let loaded = try load(store)
            expect(loaded.entries[0].file.text == "valid", "last valid state survives")
        }
        try group("Storage is private and clearDrafts leaves session and originals intact") {
            let file = TextFile(text: "private"), store = WorkspaceStore(root: fixture("permissions"))
            _ = store.saveAndWait(session: WorkspaceSession(entries: [entry(file)], activeID: file.id), drafts: [file.recoverySnapshot()])
            for path in ["permissions", "permissions/Drafts", "permissions/Quarantine"] {
                try expect(try permissions(fixture(path)) == 0o700, "private directory \(path)")
            }
            for path in ["permissions/Session.json", "permissions/Drafts/\(file.id.uuidString).json"] {
                try expect(try permissions(fixture(path)) == 0o600, "private file \(path)")
            }
            var completed = false, failure: Error?
            store.clearDrafts { failure = $0; completed = true; expect(Thread.isMainThread, "clear callback on main") }
            try wait { completed }
            expect(failure == nil && FileManager.default.fileExists(atPath: fixture("permissions/Session.json").path), "clear leaves metadata")
            try expect(try contents(fixture("permissions/Drafts")).isEmpty, "clear removes all drafts")
        }
        try group("Queued writes, clearing and subsequent snapshots remain serialized") {
            let file = TextFile(text: "first"), store = WorkspaceStore(root: fixture("queue"))
            let session = WorkspaceSession(entries: [entry(file)], activeID: file.id)
            var callbacks: [String] = []
            store.save(session: session, drafts: [file.recoverySnapshot()]) { result in
                expect(result.errors.isEmpty && Thread.isMainThread, "save callback on main")
                callbacks.append("first")
            }
            store.clearDrafts { error in expect(error == nil, "queue clear success"); callbacks.append("clear") }
            file.text = "latest"
            store.save(session: session, drafts: [file.recoverySnapshot()]) { _ in callbacks.append("latest") }
            store.flush() // must never wait for its main-thread callbacks
            try wait { callbacks.count == 3 }
            expect(callbacks == ["first", "clear", "latest"], "callbacks preserve submission order")
            let loaded = try load(store)
            expect(loaded.entries[0].file.text == "latest", "latest queued write wins after clear")
        }
        try group("Legacy recovery migrates without deleting the only recoverable source early") {
            let oldRoot = fixture("legacy"), oldURL = oldRoot.appendingPathComponent("Recovery.json")
            let file = TextFile(text: "legacy draft")
            try TextFile.writeRecovery([file], to: oldURL)
            let store = WorkspaceStore(root: oldRoot), loaded = try load(store)
            expect(loaded.entries.count == 1 && loaded.entries[0].file.text == "legacy draft", "legacy draft loaded")
            expect(FileManager.default.fileExists(atPath: oldURL.path), "legacy file retained until new commit")
            let restored = loaded.entries[0].file
            let result = store.saveAndWait(session: WorkspaceSession(entries: [entry(restored)], activeID: restored.id), drafts: [restored.recoverySnapshot()])
            expect(result.errors.isEmpty && !FileManager.default.fileExists(atPath: oldURL.path), "legacy archived only after successful new save")
            try expect(try contents(oldRoot.appendingPathComponent("Quarantine")).count == 1, "legacy bytes retained in archive")
            try expect((try load(WorkspaceStore(root: oldRoot))).entries[0].file.text == "legacy draft", "new format is independently recoverable")
        }
        try group("An intentionally empty saved session never resurrects legacy drafts") {
            let oldRoot = fixture("legacy-empty"), store = WorkspaceStore(root: oldRoot)
            let file = TextFile(text: "old dismissed")
            try TextFile.writeRecovery([file], to: oldRoot.appendingPathComponent("Recovery.json"))
            _ = store.saveAndWait(session: WorkspaceSession(entries: [], activeID: nil), drafts: [])
            try expect((try load(store)).entries.isEmpty, "empty new session blocks obsolete legacy import")
        }
        try group("Total draft budget fails one update without discarding other valid drafts") {
            let a = TextFile(text: "old a"), b = TextFile(text: "old b"), store = WorkspaceStore(root: fixture("total-budget"))
            let session = WorkspaceSession(entries: [entry(a), entry(b)], activeID: a.id)
            _ = store.saveAndWait(session: session, drafts: [a.recoverySnapshot(), b.recoverySnapshot()])
            // A sparse file simulates old on-disk occupancy without allocating a
            // 150 MiB String merely to test the aggregate accounting branch.
            let aURL = fixture("total-budget/Drafts/\(a.id.uuidString).json")
            let handle = try FileHandle(forWritingTo: aURL)
            try handle.truncate(atOffset: UInt64(WorkspaceStore.maximumDraftBytes - 64)); try handle.close()
            a.text = "updated a"; b.text = "updated b"
            let result = store.saveAndWait(session: session, drafts: [b.recoverySnapshot(), a.recoverySnapshot()])
            expect(result.errors.count == 1 && result.errors[0].contains("150 MiB"), "aggregate overflow is visible and isolated")
            let loaded = try load(store)
            expect(loaded.entries.map { $0.file.text } == ["updated a", "old b"], "failed b retains its usable old snapshot while a succeeds")
        }
        try group("Identity mismatch cannot clear a previously valid draft") {
            let a = TextFile(text: "keep"), b = TextFile(text: "unrelated"), store = WorkspaceStore(root: fixture("mismatch"))
            let session = WorkspaceSession(entries: [entry(a)], activeID: a.id)
            _ = store.saveAndWait(session: session, drafts: [a.recoverySnapshot()])
            expect(!store.saveAndWait(session: session, drafts: [b.recoverySnapshot()]).errors.isEmpty, "unassociated identity is a structural error")
            let loaded = try load(store)
            expect(loaded.entries.count == 1 && loaded.entries[0].file.text == "keep", "identity error preserves prior session and text")
        }
        try group("Clear all drafts also removes quarantined text but keeps metadata evidence") {
            let file = TextFile(text: "private damaged text"), store = WorkspaceStore(root: fixture("clear-isolated"))
            _ = store.saveAndWait(session: WorkspaceSession(entries: [entry(file)], activeID: file.id), drafts: [file.recoverySnapshot()])
            try Data("bad text snapshot".utf8).write(to: fixture("clear-isolated/Drafts/\(file.id.uuidString).json"))
            _ = try load(store)
            let q = fixture("clear-isolated/Quarantine")
            try Data("metadata only".utf8).write(to: q.appendingPathComponent("session-evidence.json"))
            var finished = false, failure: Error?
            store.clearDrafts { failure = $0; finished = true }
            try wait { finished }
            let left = try contents(q)
            expect(failure == nil && left.map { $0.lastPathComponent } == ["session-evidence.json"], "quarantined plaintext is included in clear-all scope")
            expect(FileManager.default.fileExists(atPath: fixture("clear-isolated/Session.json").path), "current session remains")
        }
        try group("Orphan dirty text supersedes a clean same-path session after an interrupted commit") {
            let path = fixture("crash-path.txt"); try Data("DISK".utf8).write(to: path)
            let clean = try TextFile.open(path), newer = try TextFile.open(path)
            newer.text = "NEWER UNSAVED"
            let storeRoot = fixture("crash-orphan"), store = WorkspaceStore(root: storeRoot)
            var oldEntry = entry(clean)
            oldEntry.isReading = true; oldEntry.sourceAnchor = 2; oldEntry.selectionLocation = 1
            _ = store.saveAndWait(session: WorkspaceSession(entries: [oldEntry], activeID: clean.id), drafts: [])
            // Simulate the exact crash window: the new draft has committed but
            // Session.json still identifies the previous clean tab.
            let orphanURL = storeRoot.appendingPathComponent("Drafts/\(newer.id.uuidString).json")
            try JSONEncoder().encode(newer.recoverySnapshot()).write(to: orphanURL)
            let loaded = try load(store)
            expect(loaded.entries.count == 1 && loaded.entries[0].file.text == "NEWER UNSAVED", "newer unsaved text stays visible")
            expect(loaded.entries[0].entry.id == newer.id && loaded.activeID == newer.id, "entry and active identity map to recovered draft")
            expect(loaded.entries[0].entry.isReading && loaded.entries[0].entry.sourceAnchor == 2 && loaded.entries[0].entry.selectionLocation == 1, "old session view metadata and position are retained")
            expect(loaded.entries[0].file.isDirty && loaded.entries[0].file.url == path, "the real dirty buffer retains disk conflict baseline")
            try expect(try Data(contentsOf: path) == Data("DISK".utf8), "load does not modify disk")
        }
        try group("Two dirty same-path buffers both recover and the extra copy cannot silently overwrite") {
            let path = fixture("two-dirty.txt"); try Data("baseline".utf8).write(to: path)
            let first = try TextFile.open(path), second = try TextFile.open(path)
            first.text = "FIRST UNSAVED"; second.text = "SECOND UNSAVED"
            let storeRoot = fixture("two-dirty"), store = WorkspaceStore(root: storeRoot)
            let session = WorkspaceSession(entries: [entry(first), entry(second)], activeID: second.id)
            _ = store.saveAndWait(session: session, drafts: [first.recoverySnapshot(), second.recoverySnapshot()])
            let loaded = try load(store)
            expect(loaded.entries.count == 2 && loaded.entries.map { $0.file.text } == ["FIRST UNSAVED", "SECOND UNSAVED"], "neither dirty version is hidden")
            expect(loaded.entries[0].file.url == path && loaded.entries[1].file.url == nil, "second version is a detached recovery copy")
            expect(loaded.entries[1].file.isDirty && loaded.entries[1].entry.path == nil, "detached copy explicitly needs saving")
            expect(loaded.entries[1].entry.id == loaded.entries[1].file.id && loaded.activeID == loaded.entries[1].entry.id, "detached copy has a stable matching identity for the next write")
            do { try loaded.entries[1].file.save(); fatalError("Expected Save As requirement") }
            catch TextFileError.missingDestination { checks += 1 }
            do { try loaded.entries[1].file.save(to: path); fatalError("Expected explicit overwrite requirement") }
            catch TextFileError.externalChange { checks += 1 }
            let nextSession = WorkspaceSession(entries: loaded.entries.map { value in
                var metadata = value.entry; metadata.id = value.file.id; return metadata
            }, activeID: loaded.activeID)
            expect(store.saveAndWait(session: nextSession, drafts: loaded.entries.map { $0.file.recoverySnapshot() }).errors.isEmpty, "both recovered copies can persist independently")
            let reloaded = try load(store)
            expect(reloaded.entries.map { $0.file.text } == ["FIRST UNSAVED", "SECOND UNSAVED"], "next launch preserves both versions")
        }
        try group("A damaged large draft does not consume the successful recovery budget") {
            let storeRoot = fixture("bad-budget"), store = WorkspaceStore(root: storeRoot)
            let good = TextFile(text: String(repeating: "g", count: 2 * 1024 * 1024))
            var badEntry = entry(TextFile())
            badEntry.id = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!
            _ = store.saveAndWait(session: WorkspaceSession(entries: [badEntry, entry(good)], activeID: good.id), drafts: [good.recoverySnapshot()])
            let badURL = storeRoot.appendingPathComponent("Drafts/\(badEntry.id.uuidString).json")
            FileManager.default.createFile(atPath: badURL.path, contents: Data())
            let handle = try FileHandle(forWritingTo: badURL)
            try handle.truncate(atOffset: UInt64(WorkspaceStore.maximumDraftBytes - 1024 * 1024)); try handle.close()
            let loaded = try load(store)
            expect(loaded.entries.count == 1 && loaded.entries[0].file.text.utf8.count == 2 * 1024 * 1024, "valid later draft restores after invalid large record")
            expect(loaded.activeID == good.id && !loaded.warnings.isEmpty, "bad draft reported independently from active valid recovery")
            try expect(try contents(storeRoot.appendingPathComponent("Quarantine")).count == 1, "only damaged record is isolated")
        }
        print("WorkspaceStoreTests: \(groups) groups, \(checks) checks passed")
    }

    private static func entry(_ file: TextFile) -> WorkspaceEntry {
        WorkspaceEntry(id: file.id, path: file.url?.path, isReading: false, sourceAnchor: 0,
                       selectionLocation: 0, selectionLength: 0, automaticKind: true, kindRaw: "plain", recoverDraft: true)
    }
    private static func load(_ store: WorkspaceStore) throws -> WorkspaceLoadResult {
        var result: WorkspaceLoadResult?
        store.load { value in expect(Thread.isMainThread, "load callback on main"); result = value }
        try wait { result != nil }
        return result!
    }
    private static func wait(_ predicate: () -> Bool) throws {
        let deadline = Date(timeIntervalSinceNow: 12)
        while !predicate() {
            if Date() >= deadline { throw NSError(domain: "WorkspaceStoreTests", code: 1, userInfo: [NSLocalizedDescriptionKey: "Async operation timed out"]) }
            _ = RunLoop.current.run(mode: .default, before: Date(timeIntervalSinceNow: 0.01))
        }
    }
    private static func contents(_ directory: URL) throws -> [URL] { try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) }
    private static func permissions(_ url: URL) throws -> Int { (try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? NSNumber)?.intValue ?? -1 }
    private static func group(_ title: String, _ work: () throws -> Void) rethrows { try work(); groups += 1; print("PASS \(title)") }
    private static func expect(_ condition: @autoclosure () throws -> Bool, _ title: String) rethrows {
        guard try condition() else { fatalError(title) }; checks += 1
    }
}
