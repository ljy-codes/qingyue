import AppKit
import Foundation

// Opt-in GUI diagnostic: real NSSavePanel uses macOS remote UI/XPC services.
// Run with normal desktop permissions; a restricted execution sandbox can exit
// before test assertions when those services are unavailable. All documents and
// recovery files remain in a disposable temporary directory.

@main
struct ModalSafetyTests {
    private static var checks = 0

    private final class ModalDriver {
        var failure: String?
        var promptSeen = false
        var savePanelSeen = false
        var originalStayedActive = false
        var finished = false
        let reader: ReaderWindow
        let window: NSWindow
        let app: NSApplication
        let chooseSave: Bool
        init(reader: ReaderWindow, app: NSApplication, chooseSave: Bool) {
            self.reader = reader; self.window = reader.window!; self.app = app; self.chooseSave = chooseSave
        }
        func start() {
            // The read has enough time to finish while NSAlert is pumping its
            // modal run loop. Its UI mutation must remain deferred.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.45) { self.answerPrompt() }
            DispatchQueue.main.asyncAfter(deadline: .now() + 5) {
                guard !self.finished else { return }
                self.failure = self.failure ?? "Modal workflow exceeded the test timeout"
                if let panel = self.app.modalWindow as? NSSavePanel { panel.cancel(nil) }
                else { self.app.abortModal() }
            }
        }
        private func answerPrompt() {
            guard let modal = app.modalWindow else { failure = "Expected the original document save prompt"; return }
            promptSeen = true
            originalStayedActive = window.representedURL == nil
                && ModalSafetyTests.activeEditor(window)?.string == "ORIGINAL UNSAVED 😀"
            let title = chooseSave ? "保存" : "取消"
            guard let button = ModalSafetyTests.allViews(modal.contentView).compactMap({ $0 as? NSButton }).first(where: { $0.title == title }) else {
                failure = "Missing modal button: " + title; app.abortModal(); return
            }
            if chooseSave {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { self.cancelSavePanel() }
            }
            button.performClick(nil)
        }
        private func cancelSavePanel() {
            guard let panel = app.modalWindow as? NSSavePanel else {
                failure = "Saving an untitled original must show its save panel, not save the newly opened file"
                if app.modalWindow != nil { app.abortModal() }
                return
            }
            savePanelSeen = true
            if window.representedURL != nil { failure = "A pending open changed the active document inside the save panel" }
            panel.cancel(nil)
        }
    }

    static func main() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.prohibited)
        try expect(Bundle.main.bundleIdentifier != "local.xiaoyu.LightReader", "modal tests use an isolated executable")
        let defaultsDomain = Bundle.main.bundleIdentifier ?? ProcessInfo.processInfo.processName
        UserDefaults.standard.setVolatileDomain([
            "recent": [String](), "positions": [String: [Double]](),
            "fontSize": 14.0, "dark": true, "followSystemAppearance": false,
            "readingWidth": 680.0, "wrapping": true, "lineNumbers": true,
            "restoreSession": true, "recoveryEnabled": true, "excludedDraftPaths": [String]()
        ], forName: UserDefaults.argumentDomain)
        let fm = FileManager.default
        let root = fm.temporaryDirectory.appendingPathComponent("qingyue-modal-tests-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: root, withIntermediateDirectories: true)
        defer {
            UserDefaults.standard.removePersistentDomain(forName: defaultsDomain)
            UserDefaults.standard.removeVolatileDomain(forName: UserDefaults.argumentDomain)
            try? fm.removeItem(at: root)
        }

        try exercisePendingOpen(root: root.appendingPathComponent("cancel-alert"), app: app, chooseSave: false)
        try exercisePendingOpen(root: root.appendingPathComponent("cancel-save-panel"), app: app, chooseSave: true)
        print("ModalSafetyTests: \(checks) checks passed")
    }

    private static func exercisePendingOpen(root: URL, app: NSApplication, chooseSave: Bool) throws {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let incoming = root.appendingPathComponent("incoming.txt")
        let originalDisk = Data("DISK INCOMING\r\n".utf8)
        try originalDisk.write(to: incoming)
        let reader = ReaderWindow(supportDirectory: root.appendingPathComponent("isolated-support"))
        reader.showWindow(nil)
        guard let window = reader.window else { throw error("Reader window missing") }
        try wait("initial workspace load") { activeEditor(window)?.isEditable == true }
        guard let editor = activeEditor(window) else { throw error("Source editor missing") }
        editor.string = "ORIGINAL UNSAVED 😀"
        reader.textDidChange(Notification(name: NSText.didChangeNotification, object: editor))
        try expect(window.isDocumentEdited && window.representedURL == nil, "original untitled document is dirty")

        reader.openURLs([incoming])
        let driver = ModalDriver(reader: reader, app: app, chooseSave: chooseSave)
        driver.start()
        reader.closeCurrent(nil)
        driver.finished = true
        if let failure = driver.failure { throw error(failure) }
        try expect(driver.promptSeen, "close displays the original save prompt")
        try expect(driver.originalStayedActive, "pending open does not switch documents during the original prompt")
        if chooseSave { try expect(driver.savePanelSeen, "save targets the original untitled document and can be cancelled") }
        try wait("deferred open after modal completion") { window.representedURL?.standardizedFileURL == incoming.standardizedFileURL }
        try expect(activeEditor(window)?.string == "DISK INCOMING\n", "deferred file opens after the close attempt has returned")
        let first = NSMenuItem(); first.tag = 0; reader.selectNumberedTab(first)
        try expect(window.representedURL == nil, "cancelled close retains the untitled tab")
        try expect(activeEditor(window)?.string == "ORIGINAL UNSAVED 😀", "cancelled close retains all original text")
        try expect(window.isDocumentEdited, "cancelled close leaves the original marked dirty")
        let second = NSMenuItem(); second.tag = 1; reader.selectNumberedTab(second)
        try expect(window.representedURL?.standardizedFileURL == incoming.standardizedFileURL, "both original and incoming documents remain independently accessible")
        try expect(try Data(contentsOf: incoming) == originalDisk, "modal cancellation never writes the incoming file")

        // Clear the unsaved test text without a modal prompt, then close this
        // isolated controller. No real Application Support data is touched.
        reader.selectNumberedTab(first)
        activeEditor(window)?.string = ""
        if let editor = activeEditor(window) { reader.textDidChange(Notification(name: NSText.didChangeNotification, object: editor)) }
        window.performClose(nil)
        try expect(!window.isVisible, "test windows close without leaving a modal session")
        pump(0.05)
    }

    private static func allViews(_ root: NSView?) -> [NSView] {
        guard let root = root else { return [] }
        return [root] + root.subviews.flatMap { allViews($0) }
    }
    private static func activeEditor(_ window: NSWindow) -> NSTextView? {
        allViews(window.contentView).compactMap { $0 as? NSTextView }.first {
            !$0.isFieldEditor && $0.accessibilityLabel() == "文本编辑区"
        }
    }
    private static func wait(_ description: String, timeout: TimeInterval = 6, predicate: () -> Bool) throws {
        let deadline = Date(timeIntervalSinceNow: timeout)
        while !predicate() {
            guard Date() < deadline else { throw error("Timed out: " + description) }
            pump(0.02)
        }
    }
    private static func pump(_ interval: TimeInterval) {
        let deadline = Date(timeIntervalSinceNow: interval)
        repeat { _ = RunLoop.current.run(mode: .default, before: deadline) } while Date() < deadline
    }
    private static func expect(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
        guard try condition() else { throw error(message) }; checks += 1
    }
    private static func error(_ message: String) -> NSError { NSError(domain: "ModalSafetyTests", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
}
