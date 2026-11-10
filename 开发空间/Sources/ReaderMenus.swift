import AppKit

/// Both the system menu bar and the window use these same menu objects and
/// command targets, including the live recent-file submenu.
final class ReaderMenus {
    let mainMenu = NSMenu()
    private(set) var windowGroups: [WindowMenuGroup] = []

    init(reader: ReaderWindow, applicationTarget: AnyObject, aboutAction: Selector) {
        func menu(_ title: String, inWindow: Bool = true) -> NSMenu {
            let root = NSMenuItem(title: title, action: nil, keyEquivalent: "")
            let result = NSMenu(title: title); root.submenu = result; mainMenu.addItem(root)
            if inWindow { windowGroups.append(WindowMenuGroup(title: title, menu: result)) }
            return result
        }
        func item(_ m: NSMenu, _ title: String, _ action: Selector, _ key: String = "", modifiers: NSEvent.ModifierFlags = [.command], target: AnyObject? = nil) {
            let i = NSMenuItem(title: title, action: action, keyEquivalent: key)
            i.keyEquivalentModifierMask = modifiers; i.target = target; m.addItem(i)
        }
        let app = menu("轻阅", inWindow: false)
        item(app, "关于轻阅", aboutAction, target: applicationTarget)
        item(app, "设置…", #selector(ReaderWindow.showPreferences(_:)), ",", target: reader)
        app.addItem(.separator())
        item(app, "隐藏轻阅", #selector(NSApplication.hide(_:)), "h")
        item(app, "显示全部", #selector(NSApplication.unhideAllApplications(_:)))
        app.addItem(.separator()); item(app, "退出轻阅", #selector(NSApplication.terminate(_:)), "q")

        let file = menu("文件")
        item(file, "新建", #selector(ReaderWindow.newDocument(_:)), "n", target: reader)
        item(file, "打开…", #selector(ReaderWindow.openDocument(_:)), "o", target: reader)
        item(file, "快速打开…", #selector(ReaderWindow.quickOpen(_:)), "p", modifiers: [.command, .shift], target: reader)
        let recent = NSMenuItem(title: "最近打开", action: nil, keyEquivalent: "")
        let recentMenu = NSMenu(title: "最近打开"); recent.submenu = recentMenu; file.addItem(recent); reader.attachRecentMenu(recentMenu)
        file.addItem(.separator())
        item(file, "保存", #selector(ReaderWindow.saveDocument(_:)), "s", target: reader)
        item(file, "另存为…", #selector(ReaderWindow.saveAs(_:)), "s", modifiers: [.command, .shift], target: reader)
        item(file, "保存全部", #selector(ReaderWindow.saveAllDocuments(_:)), "s", modifiers: [.command, .option], target: reader)
        item(file, "重命名…", #selector(ReaderWindow.renameCurrentDocument(_:)), target: reader)
        item(file, "导出自包含 HTML（含图片）…", #selector(ReaderWindow.exportReadingHTML(_:)), target: reader)
        item(file, "重新载入磁盘文件", #selector(ReaderWindow.reloadDocument(_:)), "r", modifiers: [.command, .shift], target: reader)
        let reopen = NSMenuItem(title: "按编码重新打开", action: nil, keyEquivalent: "")
        let codecs = NSMenu(title: "按编码重新打开")
        for (index, codec) in TextCodec.allCases.enumerated() {
            let i = NSMenuItem(title: codec.label, action: #selector(ReaderWindow.reopenEncoding(_:)), keyEquivalent: "")
            i.tag = index; i.target = reader; codecs.addItem(i)
        }
        reopen.submenu = codecs; file.addItem(reopen)
        file.addItem(.separator())
        item(file, "复制当前文件路径", #selector(ReaderWindow.copyCurrentPath(_:)), target: reader)
        item(file, "在 Finder 中显示", #selector(ReaderWindow.revealCurrentFile(_:)), target: reader)
        item(file, "清除最近文件与位置", #selector(ReaderWindow.clearRecent(_:)), target: reader)
        item(file, "会话与恢复草稿…", #selector(ReaderWindow.showDraftManagement(_:)), target: reader)
        item(file, "当前文件草稿恢复开关", #selector(ReaderWindow.toggleCurrentDraftRecovery(_:)), target: reader)
        item(file, "恢复详情…", #selector(ReaderWindow.showRestoreNotices(_:)), target: reader)
        file.addItem(.separator())
        item(file, "关闭标签", #selector(ReaderWindow.closeCurrent(_:)), "w", target: reader)
        item(file, "关闭全部标签", #selector(ReaderWindow.closeAllTabs(_:)), "w", modifiers: [.command, .shift], target: reader)

        let edit = menu("编辑")
        item(edit, "撤销", #selector(ReaderWindow.undoEdit(_:)), "z", target: reader)
        item(edit, "重做", #selector(ReaderWindow.redoEdit(_:)), "z", modifiers: [.command, .shift], target: reader)
        edit.addItem(.separator())
        item(edit, "剪切", #selector(NSText.cut(_:)), "x")
        item(edit, "复制", #selector(NSText.copy(_:)), "c")
        item(edit, "粘贴", #selector(NSText.paste(_:)), "v")
        item(edit, "全选", #selector(NSText.selectAll(_:)), "a")
        item(edit, "复制全文（含图片）", #selector(ReaderWindow.copyReadingFull(_:)), target: reader)
        item(edit, "复制全文纯文本", #selector(ReaderWindow.copyReadingPlain(_:)), target: reader)
        item(edit, "复制 Markdown 源文", #selector(ReaderWindow.copyMarkdownSource(_:)), target: reader)
        item(edit, "复制当前代码块", #selector(ReaderWindow.copyCodeBlock(_:)), target: reader)
        item(edit, "复制当前表格", #selector(ReaderWindow.copyTableBlock(_:)), target: reader)
        edit.addItem(.separator())
        item(edit, "整理 JSON", #selector(ReaderWindow.formatJSON(_:)), "j", modifiers: [.command, .shift], target: reader)
        item(edit, "格式化 / 整理文本", #selector(ReaderWindow.formatDocument(_:)), "f", modifiers: [.command, .option], target: reader)

        let search = menu("搜索")
        item(search, "查找与替换", #selector(ReaderWindow.showFind(_:)), "f", target: reader)
        item(search, "查找下一个", #selector(ReaderWindow.findNext(_:)), "g", target: reader)
        item(search, "查找上一个", #selector(ReaderWindow.findPrevious(_:)), "g", modifiers: [.command, .shift], target: reader)
        item(search, "跳转到行…", #selector(ReaderWindow.goToLine(_:)), "l", target: reader)
        item(search, "文档目录", #selector(ReaderWindow.showOutline(_:)), "o", modifiers: [.command, .shift], target: reader)
        item(search, "返回跳转前位置", #selector(ReaderWindow.returnReadingPosition(_:)), "\u{F702}", modifiers: [.option], target: reader)

        let view = menu("视图")
        item(view, "显示侧栏", #selector(ReaderWindow.toggleSidebar(_:)), "b", target: reader)
        item(view, "阅读模式", #selector(ReaderWindow.toggleReading(_:)), "r", target: reader)
        item(view, "专注模式", #selector(ReaderWindow.toggleFocus(_:)), "f", modifiers: [.command, .shift], target: reader)
        item(view, "自动换行", #selector(ReaderWindow.toggleWrap(_:)), target: reader)
        item(view, "Markdown 分屏预览", #selector(ReaderWindow.toggleSplitPreview(_:)), target: reader)
        item(view, "JSON 结构", #selector(ReaderWindow.showJSONStructure(_:)), target: reader)
        view.addItem(.separator())
        item(view, "放大字体", #selector(ReaderWindow.increaseFont(_:)), "+", target: reader)
        item(view, "缩小字体", #selector(ReaderWindow.decreaseFont(_:)), "-", target: reader)
        item(view, "切换明亮 / 深色", #selector(ReaderWindow.toggleTheme(_:)), "t", modifiers: [.command, .shift], target: reader)
        item(view, "设置…", #selector(ReaderWindow.showPreferences(_:)), ",", target: reader)

        let tabs = menu("标签")
        item(tabs, "上一个标签", #selector(ReaderWindow.previousTab(_:)), "[", modifiers: [.command, .shift], target: reader)
        item(tabs, "下一个标签", #selector(ReaderWindow.nextTab(_:)), "]", modifiers: [.command, .shift], target: reader)
        item(tabs, "向左移动", #selector(ReaderWindow.moveCurrentTabLeft(_:)), target: reader)
        item(tabs, "向右移动", #selector(ReaderWindow.moveCurrentTabRight(_:)), target: reader)
        item(tabs, "重命名当前标签…", #selector(ReaderWindow.renameCurrentDocument(_:)), target: reader)
        tabs.addItem(.separator())
        for index in 0..<9 {
            let i = NSMenuItem(title: index == 8 ? "最后一个标签" : "标签 \(index + 1)", action: #selector(ReaderWindow.selectNumberedTab(_:)), keyEquivalent: "\(index + 1)")
            i.target = reader; i.tag = index; tabs.addItem(i)
        }

        let help = menu("帮助")
        item(help, "使用说明", #selector(ReaderWindow.showGuide(_:)), target: reader)
        item(help, "关于轻阅", aboutAction, target: applicationTarget)
    }
}
