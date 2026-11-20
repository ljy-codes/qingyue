import AppKit

/// Native annotation browser and editor. Every mutation uses the existing
/// local store; changing a selection or closing the panel never writes source.
final class AnnotationPanelWindow: NSWindowController, NSTableViewDataSource, NSTableViewDelegate {
    var jump: ((DocumentAnnotation) -> Void)?
    var onChange: (() -> Void)?

    private let store: AnnotationStore
    private let documentID: UUID
    private let path: String?
    private var items: [DocumentAnnotation] = []
    private let table = NSTableView()
    private let quote = NSTextView()
    private let note = NSTextView()
    private let tags = NSTextField()
    private let color = NSPopUpButton()
    private let status = NSTextField(wrappingLabelWithString: "")
    private var saveButton: NSButton!
    private var deleteButton: NSButton!
    private var jumpButton: NSButton!

    init(store: AnnotationStore, documentID: UUID, path: String?) {
        self.store = store; self.documentID = documentID; self.path = path
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 760, height: 670), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        panel.title = "文档批注" + (path.map { " · " + URL(fileURLWithPath: $0).lastPathComponent } ?? "")
        panel.minSize = NSSize(width: 540, height: 670); panel.isReleasedWhenClosed = false
        super.init(window: panel)
        buildInterface(); reload()
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }

    func present(relativeTo parent: NSWindow?) {
        if let parent {
            window?.appearance = parent.appearance
            window?.setFrameOrigin(NSPoint(x: parent.frame.midX - 380, y: max(40, parent.frame.midY - 335)))
        } else { window?.center() }
        showWindow(nil); window?.makeKeyAndOrderFront(nil)
    }

    /// Refresh after source-anchor repairs or another annotation editor changes.
    func reload() {
        let selected = selectedItem?.id
        items = store.forDocument(documentID: documentID, path: path)
        table.reloadData()
        if let selected, let index = items.firstIndex(where: { $0.id == selected }) {
            table.selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false)
        } else if !items.isEmpty { table.selectRowIndexes(IndexSet(integer: 0), byExtendingSelection: false) }
        else { table.deselectAll(nil) }
        updateSelection()
        status.stringValue = items.isEmpty ? "当前文档没有批注。先返回正文，选择文字后添加摘录。" : "共 \(items.count) 条批注 · 双击列表回到原文；修改后点击保存。"
        status.textColor = .secondaryLabelColor
    }

    private func buildInterface() {
        guard let root = window?.contentView else { return }
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("quote")); column.title = "摘录"; column.width = 500; column.minWidth = 250
        let anchor = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("anchor")); anchor.title = "位置状态"; anchor.width = 180; anchor.minWidth = 130
        table.addTableColumn(column); table.addTableColumn(anchor)
        table.dataSource = self; table.delegate = self; table.rowHeight = 44; table.allowsEmptySelection = true
        table.columnAutoresizingStyle = .firstColumnOnlyAutoresizingStyle
        table.target = self; table.doubleAction = #selector(doubleClickAnnotation(_:))
        table.setAccessibilityLabel("当前文档批注列表")
        let list = NSScrollView(); list.documentView = table; list.hasVerticalScroller = true; list.autohidesScrollers = true; list.borderType = .bezelBorder
        quote.isEditable = false; quote.isSelectable = true; quote.isRichText = false; quote.font = .systemFont(ofSize: 13); quote.textContainerInset = NSSize(width: 8, height: 8); quote.setAccessibilityLabel("批注摘录原文")
        note.isEditable = true; note.isRichText = false; note.allowsUndo = true; note.font = .systemFont(ofSize: 13); note.textContainerInset = NSSize(width: 8, height: 8); note.setAccessibilityLabel("批注备注")
        note.isAutomaticQuoteSubstitutionEnabled = false; note.isAutomaticDashSubstitutionEnabled = false
        let quoteScroll = textScroll(quote), noteScroll = textScroll(note)
        tags.placeholderString = "逗号分隔，最多 10 个标签"; tags.setAccessibilityLabel("批注标签")
        color.addItems(withTitles: ["琥珀", "蓝色", "绿色", "玫瑰", "紫色"]); color.setAccessibilityLabel("批注颜色")
        let quoteLabel = NSTextField(labelWithString: "摘录（只读）")
        let noteLabel = NSTextField(labelWithString: "备注")
        let tagsLabel = NSTextField(labelWithString: "标签")
        let colorLabel = NSTextField(labelWithString: "颜色")
        saveButton = NSButton(title: "保存批注", target: self, action: #selector(saveAnnotation(_:))); saveButton.keyEquivalent = "s"; saveButton.keyEquivalentModifierMask = .command
        deleteButton = NSButton(title: "删除批注", target: self, action: #selector(deleteAnnotation(_:)))
        jumpButton = NSButton(title: "回到原文", target: self, action: #selector(jumpToAnnotation(_:)))
        let actions = NSStackView(views: [jumpButton!, deleteButton!, saveButton!]); actions.orientation = .horizontal; actions.spacing = 8
        status.font = .systemFont(ofSize: 11); status.setAccessibilityLabel("批注操作状态")
        for view in [list, quoteLabel, quoteScroll, noteLabel, noteScroll, tagsLabel, tags, colorLabel, color, actions, status] {
            view.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(view)
        }
        NSLayoutConstraint.activate([
            list.topAnchor.constraint(equalTo: root.topAnchor, constant: 16), list.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 16), list.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -16), list.heightAnchor.constraint(greaterThanOrEqualToConstant: 150),
            quoteLabel.topAnchor.constraint(equalTo: list.bottomAnchor, constant: 12), quoteLabel.leadingAnchor.constraint(equalTo: list.leadingAnchor),
            quoteScroll.topAnchor.constraint(equalTo: quoteLabel.bottomAnchor, constant: 5), quoteScroll.leadingAnchor.constraint(equalTo: list.leadingAnchor), quoteScroll.trailingAnchor.constraint(equalTo: list.trailingAnchor), quoteScroll.heightAnchor.constraint(equalToConstant: 84),
            noteLabel.topAnchor.constraint(equalTo: quoteScroll.bottomAnchor, constant: 10), noteLabel.leadingAnchor.constraint(equalTo: list.leadingAnchor),
            noteScroll.topAnchor.constraint(equalTo: noteLabel.bottomAnchor, constant: 5), noteScroll.leadingAnchor.constraint(equalTo: list.leadingAnchor), noteScroll.trailingAnchor.constraint(equalTo: list.trailingAnchor), noteScroll.heightAnchor.constraint(equalToConstant: 100),
            tagsLabel.topAnchor.constraint(equalTo: noteScroll.bottomAnchor, constant: 12), tagsLabel.leadingAnchor.constraint(equalTo: list.leadingAnchor),
            tags.leadingAnchor.constraint(equalTo: tagsLabel.trailingAnchor, constant: 8), tags.centerYAnchor.constraint(equalTo: tagsLabel.centerYAnchor), tags.trailingAnchor.constraint(equalTo: colorLabel.leadingAnchor, constant: -16),
            colorLabel.centerYAnchor.constraint(equalTo: tags.centerYAnchor), colorLabel.trailingAnchor.constraint(equalTo: color.leadingAnchor, constant: -8),
            color.centerYAnchor.constraint(equalTo: tags.centerYAnchor), color.trailingAnchor.constraint(equalTo: list.trailingAnchor), color.widthAnchor.constraint(equalToConstant: 115),
            actions.topAnchor.constraint(equalTo: tags.bottomAnchor, constant: 14), actions.trailingAnchor.constraint(equalTo: list.trailingAnchor), actions.heightAnchor.constraint(equalToConstant: 32),
            status.topAnchor.constraint(equalTo: actions.bottomAnchor, constant: 8), status.leadingAnchor.constraint(equalTo: list.leadingAnchor), status.trailingAnchor.constraint(equalTo: list.trailingAnchor), status.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -16), status.heightAnchor.constraint(greaterThanOrEqualToConstant: 30)
        ])
    }
    private func textScroll(_ view: NSTextView) -> NSScrollView {
        view.isVerticallyResizable = true; view.isHorizontallyResizable = false; view.autoresizingMask = [.width]
        view.textContainer?.widthTracksTextView = true
        let scroll = NSScrollView(); scroll.documentView = view; scroll.hasVerticalScroller = true; scroll.autohidesScrollers = true; scroll.borderType = .bezelBorder
        return scroll
    }
    private var selectedItem: DocumentAnnotation? { items.indices.contains(table.selectedRow) ? items[table.selectedRow] : nil }
    private func updateSelection() {
        let selected = selectedItem
        quote.string = selected?.quote ?? ""; note.string = selected?.note ?? ""; tags.stringValue = selected?.tags.joined(separator: ", ") ?? ""
        note.undoManager?.removeAllActions()
        color.selectItem(at: AnnotationColor.allCases.firstIndex(of: selected?.color ?? .amber) ?? 0)
        note.isEditable = selected != nil; tags.isEnabled = selected != nil; color.isEnabled = selected != nil
        saveButton.isEnabled = selected != nil; deleteButton.isEnabled = selected != nil; jumpButton.isEnabled = selected != nil
    }
    func numberOfRows(in tableView: NSTableView) -> Int { items.count }
    func tableViewSelectionDidChange(_ notification: Notification) { updateSelection() }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard items.indices.contains(row) else { return nil }
        let item = items[row], anchor = tableColumn?.identifier.rawValue == "anchor"
        let field = NSTextField(labelWithString: anchor ? item.anchorState.label : item.quote.replacingOccurrences(of: "\n", with: " "))
        field.lineBreakMode = .byTruncatingTail; field.font = .systemFont(ofSize: anchor ? 11 : 13)
        field.textColor = anchor ? .secondaryLabelColor : .labelColor; field.toolTip = anchor ? "源位置 \(item.sourceStart)–\(item.sourceEnd)" : item.quote
        field.setAccessibilityLabel(field.stringValue)
        return field
    }
    @objc private func saveAnnotation(_ sender: Any?) {
        guard let item = selectedItem else { return }
        let labels = tags.stringValue.split(whereSeparator: { $0 == "," || $0 == "，" }).map(String.init)
        do {
            guard try store.update(id: item.id, note: note.string, tags: labels, color: AnnotationColor.allCases[color.indexOfSelectedItem]) else {
                reload(); showError("这条批注已被删除，请重新选择。"); return
            }
            let result = store.save()
            guard result.errors.isEmpty else { showError("批注尚未写入磁盘：" + result.errors.joined(separator: "；")); return }
            reload(); status.stringValue = "批注已保存到本机。"; onChange?()
        } catch { showError(error.localizedDescription) }
    }
    @objc private func deleteAnnotation(_ sender: Any?) {
        guard let item = selectedItem else { return }
        let confirm = NSAlert(); confirm.messageText = "删除这条批注？"; confirm.informativeText = "只删除本机批注，原文保持不变。"; confirm.addButton(withTitle: "删除"); confirm.addButton(withTitle: "取消")
        guard confirm.runModal() == .alertFirstButtonReturn else { return }
        _ = store.remove(id: item.id)
        let result = store.save()
        guard result.errors.isEmpty else { _ = store.load(); reload(); showError("删除未能保存：" + result.errors.joined(separator: "；")); return }
        reload(); status.stringValue = "批注已删除。"; onChange?()
    }
    @objc private func jumpToAnnotation(_ sender: Any?) { if let item = selectedItem { jump?(item) } }
    @objc private func doubleClickAnnotation(_ sender: Any?) {
        guard items.indices.contains(table.clickedRow) else { return }
        jump?(items[table.clickedRow])
    }
    private func showError(_ message: String) { status.stringValue = message; status.textColor = .systemRed; status.toolTip = message }
}
