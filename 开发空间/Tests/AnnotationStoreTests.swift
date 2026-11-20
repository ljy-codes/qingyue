import Foundation

@main
struct AnnotationStoreTests {
    private static var checks = 0
    static func main() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-annotations-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let documentID = UUID()
        let store = AnnotationStore(root: root)
        let created = try store.add(AnnotationDraft(documentID: documentID, path: "/tmp/notes.md", sourceStart: 8, sourceEnd: 16,
                                                    quote: "重要引文", contextBefore: "前文", contextAfter: "后文",
                                                    note: "我的备注", tags: ["重点", "重点"], color: .blue), now: Date(timeIntervalSince1970: 10))
        expect(created.anchorState == .exact && created.tags == ["重点"], "new annotation normalizes tags and starts exact")
        expect(store.forDocument(documentID: documentID).count == 1, "document query returns annotation")
        let updated = try store.update(id: created.id, note: "更新", tags: ["a", "b"], color: .rose, now: Date(timeIntervalSince1970: 20))
        expect(updated, "update changes note and tags")
        expect(store.items[0].color == .rose && store.items[0].note == "更新", "updated annotation fields are retained")
        expect(store.migratePath(documentID: documentID, oldPath: "/tmp/notes.md", newPath: "/tmp/renamed.md") == 1, "path migration follows rename")
        expect(store.items[0].path == "/tmp/renamed.md", "migrated path is updated")
        expect(store.applyAnchorRepairs([AnnotationAnchorRepair(id: created.id, sourceStart: 20, sourceEnd: 28, anchorState: .shifted)], repairedAt: Date(timeIntervalSince1970: 30)) == 1, "anchor repair updates positions")
        expect(store.items[0].sourceStart == 20 && store.items[0].anchorState == .shifted, "repaired anchor is persisted in memory")
        expect(store.save().errors.isEmpty, "store saves atomically")

        let reloaded = AnnotationStore(root: root)
        let loadWarnings = reloaded.load()
        expect(loadWarnings.isEmpty && reloaded.items == store.items, "saved annotations reload exactly")
        expect(reloaded.remove(id: created.id) && reloaded.items.isEmpty, "remove deletes only the requested annotation")
        expect(storeRejectsLongValues(), "oversized quote is rejected before mutation")

        let briefInput = ReadingBriefInput(title: "我的 <笔记>", sourcePath: "/tmp/renamed.md", sourceText: "# 一\n正文\n## 二\n摘录\n",
                                            progressPercent: 120, generatedAt: Date(timeIntervalSince1970: 0),
                                            headings: [ReadingBriefHeading(title: "一", sourceStart: 0), ReadingBriefHeading(title: "二", sourceStart: 8)],
                                            annotations: store.items)
        let markdown = try ReadingBriefExporter.buildMarkdown(briefInput)
        expect(markdown.contains("# 阅读简报：我的 &lt;笔记&gt;"), "markdown title escapes user punctuation")
        expect(markdown.contains("阅读进度：100%") && markdown.contains("## 二"), "markdown includes bounded progress and chapter")
        let html = try ReadingBriefExporter.buildHTML(briefInput)
        expect(html.contains("Content-Security-Policy") && html.contains("&lt;笔记&gt;"), "html brief includes CSP and escaped title")
        expect(html.contains("已自动跟随正文移动") && html.contains("&lt;"), "html includes anchor state and escaped user text")

        let damagedRoot = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-annotation-damaged-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: damagedRoot, withIntermediateDirectories: true)
        try Data("not-json".utf8).write(to: damagedRoot.appendingPathComponent("Annotations.json"))
        let damaged = AnnotationStore(root: damagedRoot)
        let warnings = damaged.load()
        expect(warnings.count == 1 && damaged.items.isEmpty, "damaged annotation sidecar is isolated without blocking startup")
        expect(FileManager.default.fileExists(atPath: damagedRoot.appendingPathComponent("Quarantine").path), "damaged sidecar leaves quarantine evidence")
        try? FileManager.default.removeItem(at: damagedRoot)
        try reopenedDocumentChecks(root: root)
        print("AnnotationStoreTests: \(checks) checks passed")
    }

    private static func reopenedDocumentChecks(root: URL) throws {
        let support = root.appendingPathComponent("Identity")
        let originalID = UUID(), reopenedID = UUID()
        let store = AnnotationStore(root: support)
        let path = root.appendingPathComponent("durable.md").path
        let item = try store.add(AnnotationDraft(documentID: originalID, path: path, sourceStart: 0, sourceEnd: 2, quote: "引文"))
        expect(store.save().errors.isEmpty, "first annotation save works in a new support directory")
        let loaded = AnnotationStore(root: support); _ = loaded.load()
        expect(loaded.forDocument(documentID: reopenedID, path: path).map(\.id) == [item.id], "reopened file finds annotations by durable path with a new document ID")
        expect(loaded.forDocument(documentID: reopenedID, path: path + "-other").isEmpty, "same new ID cannot match a different saved path")
        let renamed = root.appendingPathComponent("durable-renamed.md").path
        expect(loaded.migratePath(documentID: reopenedID, oldPath: path, newPath: renamed) == 1, "rename after reopening migrates persisted annotations")
        expect(loaded.items[0].documentID == reopenedID, "migration rebinds annotation to current document identity")
        expect(loaded.save().errors.isEmpty, "existing annotation file can be replaced")
        do { _ = try store.add(AnnotationDraft(documentID: originalID, path: "", sourceStart: 0, sourceEnd: 1, quote: "x")); fatalError("空路径应拒绝") } catch { checks += 1 }
        do { _ = try store.add(AnnotationDraft(documentID: originalID, path: "relative.md", sourceStart: 0, sourceEnd: 1, quote: "x")); fatalError("相对路径应拒绝") } catch { checks += 1 }
        var first = item; first.sourceStart = 0; first.sourceEnd = 1
        var second = item
        // Different IDs with identical heading labels must keep separate sections.
        second = DocumentAnnotation(id: UUID(), documentID: originalID, path: path, sourceStart: 8, sourceEnd: 9, quote: "第二段", contextBefore: "", contextAfter: "", note: nil, tags: [], color: .blue, createdAt: Date(), updatedAt: Date(), anchorState: .exact)
        let input = ReadingBriefInput(title: "标题", sourcePath: "/tmp/a`b.md", sourceText: "# 同名\n甲\n# 同名\n乙", progressPercent: 0, generatedAt: Date(timeIntervalSince1970: 0), headings: [ReadingBriefHeading(title: "同名", sourceStart: 0), ReadingBriefHeading(title: "同名", sourceStart: 8)], annotations: [first, second])
        let markdown = try ReadingBriefExporter.buildMarkdown(input)
        expect(markdown.components(separatedBy: "## 同名").count == 3, "duplicate heading names do not merge separate brief sections")
        expect(markdown.contains("`` /tmp/a`b.md ``"), "Markdown code spans use a delimiter longer than user backticks")
        expect(markdown.contains("第 3 行"), "brief line numbers use UTF-16 source offsets")
        let protected = root.appendingPathComponent("untouched.json"); try Data("keep".utf8).write(to: protected)
        let unsafeRoot = root.appendingPathComponent("UnsafeAnnotation")
        try FileManager.default.createDirectory(at: unsafeRoot, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: unsafeRoot.appendingPathComponent("Annotations.json"), withDestinationURL: protected)
        let unsafe = AnnotationStore(root: unsafeRoot)
        let warnings = unsafe.load()
        expect(!warnings.isEmpty, "annotation sidecar links are isolated")
        let data = try Data(contentsOf: protected)
        expect(data == Data("keep".utf8), "quarantine leaves a symlink target untouched")
    }

    private static func storeRejectsLongValues() -> Bool {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("qingyue-annotation-reject-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let store = AnnotationStore(root: root)
        do {
            _ = try store.add(AnnotationDraft(documentID: UUID(), path: nil, sourceStart: 0, sourceEnd: 1,
                                               quote: String(repeating: "x", count: AnnotationStore.maximumQuoteLength + 1)))
            return false
        } catch { return store.items.isEmpty }
    }

    private static func expect(_ condition: @autoclosure () -> Bool, _ message: String) {
        guard condition() else { fatalError(message) }
        checks += 1
    }
}
