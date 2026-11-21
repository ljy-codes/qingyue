import AppKit

/// A native, editable presentation of the shared local reading list. Actions
/// only change list metadata; opening a row delegates source-position restoration
/// to the main reader through openItem.
final class ReadingListWindow: NSWindowController, NSTableViewDataSource, NSTableViewDelegate {
    var openItem: ((ReadingListItem) -> Void)?
    private let store: ReadingListStore
    private var rows: [ReadingListItem] = []
    private let table = ReadingListTableView()
    private let status = NSTextField(labelWithString: "")
    private let openButton = NSButton(title: "打开", target: nil, action: nil)
    private let pinButton = NSButton(title: "固定到顶部", target: nil, action: nil)
    private let readButton = NSButton(title: "标为已读", target: nil, action: nil)
    private let removeButton = NSButton(title: "移除记录", target: nil, action: nil)
    private let retryButton = NSButton(title: "重试保存", target: nil, action: nil)
    private var unsaved = false

    init(store: ReadingListStore, openItem: ((ReadingListItem) -> Void)? = nil) {
        self.store = store; self.openItem = openItem
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 820, height: 520), styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        window.title = "阅读清单"; window.minSize = NSSize(width: 640, height: 320); window.isReleasedWhenClosed = false
        super.init(window: window)
        buildInterface(); reloadData()
        if let warning = store.lastWarning { status.stringValue = warning; status.textColor = .systemOrange }
        window.center()
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }

    private func buildInterface() {
        guard let window, let root = window.contentView else { return }
        let explanation = NSTextField(labelWithString: "双击打开文件并继续阅读；移除记录不会删除原文件。")
        explanation.font = .systemFont(ofSize: 12); explanation.textColor = .secondaryLabelColor
        explanation.setAccessibilityLabel("阅读清单说明")
        let refresh = NSButton(title: "刷新", target: self, action: #selector(refreshList(_:)))
        let heading = NSStackView(views: [explanation, refresh]); heading.orientation = .horizontal; heading.spacing = 12
        explanation.setContentHuggingPriority(.defaultLow, for: .horizontal)

        table.dataSource = self; table.delegate = self; table.rowHeight = 32; table.usesAlternatingRowBackgroundColors = true
        table.columnAutoresizingStyle = .lastColumnOnlyAutoresizingStyle; table.allowsMultipleSelection = false
        table.setAccessibilityLabel("阅读清单文件列表")
        table.target = self; table.doubleAction = #selector(openSelected(_:))
        table.openAction = { [weak self] in self?.openSelected(nil) }
        for (identifier, title, width) in [("name", "文件", 190.0), ("folder", "文件夹", 250.0), ("pin", "固定", 62.0), ("state", "状态", 66.0), ("progress", "阅读进度", 95.0)] {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(identifier)); column.title = title; column.width = width
            column.minWidth = identifier == "name" || identifier == "folder" ? 100 : 56
            table.addTableColumn(column)
        }
        let scroll = NSScrollView(); scroll.documentView = table; scroll.hasVerticalScroller = true; scroll.hasHorizontalScroller = true; scroll.autohidesScrollers = true
        let actions: [(NSButton, Selector)] = [(openButton, #selector(openSelected(_:))), (pinButton, #selector(togglePinned(_:))), (readButton, #selector(toggleRead(_:))), (removeButton, #selector(removeSelected(_:))), (retryButton, #selector(retrySave(_:)))]
        for (button, action) in actions { button.target = self; button.action = action; button.bezelStyle = .rounded }
        openButton.keyEquivalent = "\r"; removeButton.toolTip = "仅移除阅读清单记录，保留磁盘原文件"
        retryButton.setAccessibilityLabel("重试保存阅读清单")
        let toolbar = NSStackView(views: actions.map(\.0)); toolbar.orientation = .horizontal; toolbar.spacing = 8
        status.font = .systemFont(ofSize: 11); status.textColor = .secondaryLabelColor; status.lineBreakMode = .byTruncatingMiddle
        status.setAccessibilityLabel("阅读清单状态")
        for view in [heading, scroll, toolbar, status] { view.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(view) }
        NSLayoutConstraint.activate([
            heading.topAnchor.constraint(equalTo: root.topAnchor, constant: 14), heading.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 16), heading.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -16),
            scroll.topAnchor.constraint(equalTo: heading.bottomAnchor, constant: 12), scroll.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 16), scroll.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -16), scroll.bottomAnchor.constraint(equalTo: toolbar.topAnchor, constant: -12),
            toolbar.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 16), toolbar.trailingAnchor.constraint(lessThanOrEqualTo: root.trailingAnchor, constant: -16),
            status.topAnchor.constraint(equalTo: toolbar.bottomAnchor, constant: 8), status.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 16), status.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -16), status.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -12)
        ])
    }

    func present(relativeTo parent: NSWindow?) {
        if let parent { window?.appearance = parent.appearance }
        reloadData(); showWindow(nil); window?.makeKeyAndOrderFront(nil)
    }
    func reloadData() {
        let id = selectedItem?.id
        rows = store.items.sorted {
            if $0.pinned != $1.pinned { return $0.pinned }
            if $0.updatedAt != $1.updatedAt { return $0.updatedAt > $1.updatedAt }
            return $0.path.localizedStandardCompare($1.path) == .orderedAscending
        }
        table.reloadData()
        if let id, let index = rows.firstIndex(where: { $0.id == id }) { table.selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false) }
        else if !rows.isEmpty { table.selectRowIndexes(IndexSet(integer: 0), byExtendingSelection: false) }
        updateActions()
        if !unsaved { status.stringValue = rows.isEmpty ? "阅读清单为空；在主窗口将当前文件加入清单。" : "\(rows.count) 个文件 · \(rows.filter(\.completed).count) 个已读"; status.textColor = .secondaryLabelColor }
    }
    private var selectedItem: ReadingListItem? { rows.indices.contains(table.selectedRow) ? rows[table.selectedRow] : nil }
    private func updateActions() {
        let item = selectedItem
        for button in [openButton, pinButton, readButton, removeButton] { button.isEnabled = item != nil }
        pinButton.title = item?.pinned == true ? "取消固定" : "固定到顶部"
        readButton.title = item?.completed == true ? "标为未读" : "标为已读"
        retryButton.isEnabled = unsaved
    }
    func numberOfRows(in tableView: NSTableView) -> Int { rows.count }
    func tableViewSelectionDidChange(_ notification: Notification) { updateActions() }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard rows.indices.contains(row), let column = tableColumn else { return nil }
        let item = rows[row], identifier = column.identifier
        let field = (tableView.makeView(withIdentifier: identifier, owner: self) as? NSTextField) ?? NSTextField(labelWithString: "")
        field.identifier = identifier; field.font = .systemFont(ofSize: 12); field.lineBreakMode = .byTruncatingMiddle
        switch identifier.rawValue {
        case "name": field.stringValue = URL(fileURLWithPath: item.path).lastPathComponent
        case "folder": field.stringValue = URL(fileURLWithPath: item.path).deletingLastPathComponent().path
        case "pin": field.stringValue = item.pinned ? "已固定" : ""
        case "state": field.stringValue = item.completed ? "已读" : "未读"
        case "progress": field.stringValue = "\(Int((item.progress * 100).rounded()))%"
        default: field.stringValue = ""
        }
        field.toolTip = item.path; field.textColor = identifier.rawValue == "folder" ? .secondaryLabelColor : .labelColor
        field.setAccessibilityLabel(column.title + "：" + field.stringValue)
        return field
    }
    @objc private func refreshList(_ sender: Any?) { reloadData() }
    @objc private func openSelected(_ sender: Any?) {
        guard let item = selectedItem else { return }
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: item.path, isDirectory: &isDirectory), !isDirectory.boolValue else {
            status.stringValue = "文件不存在或已移动：\(item.path)"; status.textColor = .systemOrange; return
        }
        openItem?(item)
    }
    @objc private func togglePinned(_ sender: Any?) {
        guard let item = selectedItem else { return }
        do { _ = try store.upsert(path: item.path, pinned: !item.pinned); persistChange() }
        catch { showError(error) }
    }
    @objc private func toggleRead(_ sender: Any?) {
        guard let item = selectedItem else { return }
        do { _ = try store.upsert(path: item.path, completed: !item.completed); persistChange() }
        catch { showError(error) }
    }
    @objc private func removeSelected(_ sender: Any?) {
        guard let item = selectedItem else { return }
        _ = store.remove(path: item.path); persistChange()
    }
    private func persistChange() {
        unsaved = true
        do { try store.save(); unsaved = false; reloadData() }
        catch { reloadData(); showError(error) }
    }
    @objc private func retrySave(_ sender: Any?) { persistChange() }
    private func showError(_ error: Error) {
        status.stringValue = "保存失败，清单改动尚未写入磁盘：\(error.localizedDescription)"; status.textColor = .systemRed
        status.toolTip = status.stringValue; retryButton.isEnabled = unsaved
    }
}

private final class ReadingListTableView: NSTableView {
    var openAction: (() -> Void)?
    override func keyDown(with event: NSEvent) {
        if event.keyCode == 36 || event.keyCode == 76 { openAction?() }
        else { super.keyDown(with: event) }
    }
}
