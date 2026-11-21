import AppKit
import CryptoKit
import Darwin

final class AppDelegate: NSObject, NSApplicationDelegate {
    var reader: ReaderWindow!
    var quitApproved = false
    private var instanceDescriptor: Int32 = -1
    private var openEvent = NSNotification.Name("qingyue.open")
    private func makeReader() -> ReaderWindow {
        let override = ProcessInfo.processInfo.environment["QINGYUE_SUPPORT_DIRECTORY"].map { URL(fileURLWithPath: $0, isDirectory: true) }
        return ReaderWindow(supportDirectory: override)
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        do {
            let root = ProcessInfo.processInfo.environment["QINGYUE_SUPPORT_DIRECTORY"].map { URL(fileURLWithPath: $0, isDirectory: true) }
                ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("LightReader")
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            let identity = SHA256.hash(data: Data(root.standardizedFileURL.path.utf8)).prefix(12).map { String(format: "%02x", $0) }.joined()
            openEvent = NSNotification.Name("qingyue.open." + identity)
            instanceDescriptor = Darwin.open(root.appendingPathComponent("Instance.lock").path, O_CREAT | O_RDWR | O_NOFOLLOW, 0o600)
            guard instanceDescriptor >= 0 else { throw NSError(domain: "Qingyue", code: 1, userInfo: [NSLocalizedDescriptionKey: "无法取得应用数据目录锁，已停止启动以保护草稿。 "]) }
            if flock(instanceDescriptor, LOCK_EX | LOCK_NB) != 0 {
                DistributedNotificationCenter.default().postNotificationName(openEvent, object: nil, userInfo: ["paths": Array(CommandLine.arguments.dropFirst().filter { !$0.hasPrefix("-") })], deliverImmediately: true)
                quitApproved = true; NSApp.terminate(nil); return
            }
            DistributedNotificationCenter.default().addObserver(self, selector: #selector(receiveOpen(_:)), name: openEvent, object: nil)
        } catch {
            let a = NSAlert(error: error); a.runModal(); quitApproved = true; NSApp.terminate(nil); return
        }
        if reader == nil { reader = makeReader() }
        buildMenus()
        reader.showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
        let paths = CommandLine.arguments.dropFirst().filter { !$0.hasPrefix("-") }
        if !paths.isEmpty { reader.openURLs(paths.map { URL(fileURLWithPath: $0) }) }
    }
    @objc private func receiveOpen(_ notification: Notification) {
        if reader == nil { reader = makeReader() }
        if let paths = notification.userInfo?["paths"] as? [String], !paths.isEmpty { reader.openURLs(paths.map { URL(fileURLWithPath: $0) }) }
        reader.showWindow(nil); NSApp.activate(ignoringOtherApps: true)
    }
    func applicationWillTerminate(_ notification: Notification) {
        DistributedNotificationCenter.default().removeObserver(self)
        if instanceDescriptor >= 0 { close(instanceDescriptor) }
    }
    func application(_ sender: NSApplication, openFiles filenames: [String]) {
        if reader == nil { reader = makeReader() }
        reader.openURLs(filenames.map { URL(fileURLWithPath: $0) }); reader.showWindow(nil)
        sender.reply(toOpenOrPrint: .success)
    }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if quitApproved { return .terminateNow }
        if reader?.confirmQuit() ?? true { quitApproved = true; return .terminateNow }
        return .terminateCancel
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { reader.showWindow(nil); return true }
    @objc func about(_ sender: Any?) {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.1.0"
        let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "4"
        NSApp.orderFrontStandardAboutPanel(options: [.applicationName: "轻阅", .applicationVersion: "\(version) · Mac 构建 \(build)", .credits: NSAttributedString(string: "轻量、本机、原生。\nTXT · JSON · YAML · Markdown · 配置文件")])
    }
    private func buildMenus() {
        let menus = ReaderMenus(reader: reader, applicationTarget: self, aboutAction: #selector(about(_:)))
        NSApp.mainMenu = menus.mainMenu
        reader.configureWindowMenus(menus.windowGroups)
    }

}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
