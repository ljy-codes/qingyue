import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    var reader: ReaderWindow!
    var quitApproved = false
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        if reader == nil { reader = ReaderWindow() }
        buildMenus()
        reader.showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
        let paths = CommandLine.arguments.dropFirst().filter { !$0.hasPrefix("-") }
        if !paths.isEmpty { reader.openURLs(paths.map { URL(fileURLWithPath: $0) }) }
    }
    func application(_ sender: NSApplication, openFiles filenames: [String]) {
        if reader == nil { reader = ReaderWindow() }
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
        NSApp.orderFrontStandardAboutPanel(options: [.applicationName: "轻阅", .applicationVersion: "0.1.0 · 本机体验版", .credits: NSAttributedString(string: "轻量、本机、原生。\nTXT · JSON · YAML · Markdown · 配置文件")])
    }
    private func buildMenus() {
        let main = NSMenu()
        func submenu(_ title: String) -> NSMenu { let root = NSMenuItem(title: title, action: nil, keyEquivalent: ""); let m = NSMenu(title: title); root.submenu = m; main.addItem(root); return m }
        func item(_ menu: NSMenu, _ title: String, _ action: Selector, _ key: String = "", shift: Bool = false, target: AnyObject? = nil) {
            let i = NSMenuItem(title: title, action: action, keyEquivalent: key)
            if shift { i.keyEquivalentModifierMask = [.command, .shift] }
            i.target = target; menu.addItem(i)
        }
        let app = submenu("轻阅")
        item(app, "关于轻阅", #selector(about(_:)), target: self); app.addItem(.separator())
        item(app, "设置…", #selector(ReaderWindow.showPreferences(_:)), ",", target: reader)
        item(app, "隐藏轻阅", #selector(NSApplication.hide(_:)), "h"); item(app, "显示全部", #selector(NSApplication.unhideAllApplications(_:)))
        app.addItem(.separator()); item(app, "退出轻阅", #selector(NSApplication.terminate(_:)), "q")
        let file = submenu("文件")
        item(file, "新建", #selector(ReaderWindow.newDocument(_:)), "n", target: reader)
        item(file, "打开…", #selector(ReaderWindow.openDocument(_:)), "o", target: reader)
        item(file, "保存", #selector(ReaderWindow.saveDocument(_:)), "s", target: reader)
        item(file, "另存为…", #selector(ReaderWindow.saveAs(_:)), "s", shift: true, target: reader)
        file.addItem(.separator())
        item(file, "重新载入磁盘文件", #selector(ReaderWindow.reloadDocument(_:)), "r", shift: true, target: reader)
        let reopen = NSMenuItem(title: "按编码重新打开", action: nil, keyEquivalent: ""); let codecs = NSMenu()
        for (index, codec) in TextCodec.allCases.enumerated() { let i = NSMenuItem(title: codec.label, action: #selector(ReaderWindow.reopenEncoding(_:)), keyEquivalent: ""); i.tag = index; i.target = reader; codecs.addItem(i) }
        reopen.submenu = codecs; file.addItem(reopen); file.addItem(.separator())
        item(file, "清除最近文件记录", #selector(ReaderWindow.clearRecent(_:)), target: reader)
        item(file, "关闭标签", #selector(ReaderWindow.closeCurrent(_:)), "w", target: reader)
        let edit = submenu("编辑")
        item(edit, "撤销", #selector(ReaderWindow.undoEdit(_:)), "z", target: reader); item(edit, "重做", #selector(ReaderWindow.redoEdit(_:)), "z", shift: true, target: reader); edit.addItem(.separator())
        item(edit, "剪切", #selector(NSText.cut(_:)), "x"); item(edit, "复制", #selector(NSText.copy(_:)), "c"); item(edit, "粘贴", #selector(NSText.paste(_:)), "v"); item(edit, "全选", #selector(NSText.selectAll(_:)), "a")
        edit.addItem(.separator())
        item(edit, "查找与替换", #selector(ReaderWindow.showFind(_:)), "f", target: reader)
        item(edit, "查找下一个", #selector(ReaderWindow.findNext(_:)), "g", target: reader)
        item(edit, "查找上一个", #selector(ReaderWindow.findPrevious(_:)), "g", shift: true, target: reader)
        item(edit, "跳转到行…", #selector(ReaderWindow.goToLine(_:)), "l", target: reader)
        item(edit, "整理 JSON", #selector(ReaderWindow.formatJSON(_:)), "j", shift: true, target: reader)
        let view = submenu("显示")
        item(view, "阅读 / 编辑模式", #selector(ReaderWindow.toggleReading(_:)), "r", target: reader)
        item(view, "切换明亮 / 深色", #selector(ReaderWindow.toggleTheme(_:)), "t", shift: true, target: reader)
        item(view, "显示 / 隐藏侧栏", #selector(ReaderWindow.toggleSidebar(_:)), "b", target: reader)
        item(view, "自动换行", #selector(ReaderWindow.toggleWrap(_:)), target: reader)
        item(view, "放大字体", #selector(ReaderWindow.increaseFont(_:)), "+", target: reader)
        item(view, "缩小字体", #selector(ReaderWindow.decreaseFont(_:)), "-", target: reader)
        item(view, "Markdown 标题目录", #selector(ReaderWindow.showOutline(_:)), "o", shift: true, target: reader)
        let help = submenu("帮助"); item(help, "使用说明", #selector(ReaderWindow.showGuide(_:)), target: reader)
        NSApp.mainMenu = main
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
