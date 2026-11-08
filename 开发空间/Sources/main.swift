import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    var reader: ReaderWindow!
    var quitApproved = false
    private func makeReader() -> ReaderWindow {
        let override = ProcessInfo.processInfo.environment["QINGYUE_SUPPORT_DIRECTORY"].map { URL(fileURLWithPath: $0, isDirectory: true) }
        return ReaderWindow(supportDirectory: override)
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        if reader == nil { reader = makeReader() }
        buildMenus()
        reader.showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
        let paths = CommandLine.arguments.dropFirst().filter { !$0.hasPrefix("-") }
        if !paths.isEmpty { reader.openURLs(paths.map { URL(fileURLWithPath: $0) }) }
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
