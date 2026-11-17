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
        print("AnnotationStoreTests: \(checks) checks passed")
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
