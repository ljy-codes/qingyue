import AppKit
import Foundation

struct QuickOpenItem {
    let id: String
    let title: String
    let subtitle: String
    let isOpen: Bool
}

/// A picker for items already supplied by the reader. It never enumerates
/// directories or opens files, and does not change the parent document state.
final class QuickOpenController: NSWindowController, NSWindowDelegate, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate {
    private let items: [QuickOpenItem]
    private var results: [QuickOpenItem] = []
    private let onSelect: (String) -> Void
    private let search = NSSearchField()
    private let table = QuickOpenTable()
    private let summary = NSTextField(labelWithString: "")
    private let emptyLabel = NSTextField(labelWithString: "没有匹配的文件")
    private weak var parent: NSWindow?
    private var completed = false

    init(items: [QuickOpenItem], onSelect: @escaping (String) -> Void) {
        self.items = items
        self.onSelect = onSelect
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 640, height: 450),
                            styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        panel.title = "快速打开"
        panel.minSize = NSSize(width: 500, height: 300)
        panel.isReleasedWhenClosed = false
        panel.isFloatingPanel = false
        panel.hidesOnDeactivate = false
        panel.tabbingMode = .disallowed
        super.init(window: panel)
        panel.delegate = self
        buildInterface()
        reloadResults()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func present(relativeTo parent: NSWindow?) {
        self.parent = parent
        completed = false
        if let parent = parent {
            window?.appearance = parent.appearance
            if let panel = window {
                let desired = NSPoint(x: parent.frame.midX - panel.frame.width / 2,
                                      y: parent.frame.maxY - panel.frame.height - 70)
                panel.setFrameOrigin(desired)
                if let screen = parent.screen {
                    let safe = screen.visibleFrame
                    let frame = panel.frame
                    panel.setFrameOrigin(NSPoint(x: max(safe.minX, min(frame.minX, safe.maxX - frame.width)),
                                                y: max(safe.minY, min(frame.minY, safe.maxY - frame.height))))
                }
            }
        } else { window?.center() }
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
        window?.makeFirstResponder(search)
        search.selectText(nil)
    }

    /// Terms match filenames or paths, ignoring case, diacritics and width.
    /// Open documents rank first; then filename exact/prefix/substring matches.
    /// Stable ties retain the caller's tab or recent-file ordering.
    static func filteredItems(_ items: [QuickOpenItem], query: String) -> [QuickOpenItem] {
        let foldedQuery = folded(query).trimmingCharacters(in: .whitespacesAndNewlines)
        let terms = foldedQuery.split(whereSeparator: { $0.isWhitespace }).map(String.init)
        var seen = Set<String>()
        let ranked = items.enumerated().compactMap { index, item -> (item: QuickOpenItem, index: Int, rank: Int)? in
            guard seen.insert(item.id).inserted else { return nil }
            if terms.isEmpty { return (item, index, 0) }
            let title = folded(item.title), subtitle = folded(item.subtitle)
            guard terms.allSatisfy({ title.contains($0) || subtitle.contains($0) }) else { return nil }
            let rank: Int
            if title == foldedQuery { rank = 0 }
            else if title.hasPrefix(foldedQuery) { rank = 1 }
            else if title.contains(foldedQuery) { rank = 2 }
            else if terms.allSatisfy({ title.contains($0) }) { rank = 3 }
            else { rank = 4 }
            return (item, index, rank)
        }
        return ranked.sorted {
            if $0.item.isOpen != $1.item.isOpen { return $0.item.isOpen }
            if $0.rank != $1.rank { return $0.rank < $1.rank }
            return $0.index < $1.index
        }.map { $0.item }
    }

    private static func folded(_ text: String) -> String {
        text.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: Locale(identifier: "en_US_POSIX"))
    }

    private func buildInterface() {
        guard let content = window?.contentView else { return }
        let heading = NSTextField(labelWithString: "搜索已打开和最近使用的文件")
        heading.font = .systemFont(ofSize: 12)
        heading.textColor = .secondaryLabelColor
        search.placeholderString = "输入文件名或路径"
        search.setAccessibilityLabel("快速打开搜索")
        search.font = .systemFont(ofSize: 15)
        search.delegate = self
        // Enter is handled through the field editor delegate. An NSSearchField
        // also sends its action when its clear button is used, which must not
        // accidentally open the first remaining item.
        search.target = nil
        search.action = nil
        search.sendsWholeSearchString = true
        search.sendsSearchStringImmediately = false
        table.headerView = nil
        table.rowHeight = 58
        table.intercellSpacing = NSSize(width: 0, height: 2)
        table.selectionHighlightStyle = .regular
        table.style = .fullWidth
        table.usesAlternatingRowBackgroundColors = false
        table.allowsEmptySelection = false
        table.dataSource = self
        table.delegate = self
        table.target = self
        table.doubleAction = #selector(chooseSelection(_:))
        table.setAccessibilityLabel("快速打开文件结果")
        table.choose = { [weak self] in self?.chooseSelection(nil) }
        table.cancel = { [weak self] in self?.cancelPicker(nil) }
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("file"))
        column.resizingMask = .autoresizingMask
        table.addTableColumn(column)
        table.columnAutoresizingStyle = .uniformColumnAutoresizingStyle
        let scroll = NSScrollView()
        scroll.borderType = .bezelBorder
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.documentView = table
        summary.font = .systemFont(ofSize: 11)
        summary.textColor = .secondaryLabelColor
        summary.setAccessibilityLabel("快速打开状态")
        emptyLabel.font = .systemFont(ofSize: 14)
        emptyLabel.textColor = .secondaryLabelColor
        emptyLabel.alignment = .center
        [heading, search, scroll, summary, emptyLabel].forEach {
            $0.translatesAutoresizingMaskIntoConstraints = false
            content.addSubview($0)
        }
        NSLayoutConstraint.activate([
            heading.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 20),
            heading.topAnchor.constraint(equalTo: content.topAnchor, constant: 16),
            search.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 20),
            search.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -20),
            search.topAnchor.constraint(equalTo: heading.bottomAnchor, constant: 9),
            search.heightAnchor.constraint(equalToConstant: 30),
            scroll.topAnchor.constraint(equalTo: search.bottomAnchor, constant: 12),
            scroll.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 16),
            scroll.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -16),
            scroll.bottomAnchor.constraint(equalTo: summary.topAnchor, constant: -10),
            summary.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 20),
            summary.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -20),
            summary.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -12),
            summary.heightAnchor.constraint(equalToConstant: 18),
            emptyLabel.centerXAnchor.constraint(equalTo: scroll.centerXAnchor),
            emptyLabel.centerYAnchor.constraint(equalTo: scroll.centerYAnchor)
        ])
    }

    private func reloadResults() {
        let oldID = results.indices.contains(table.selectedRow) ? results[table.selectedRow].id : nil
        results = Self.filteredItems(items, query: search.stringValue)
        table.reloadData()
        if !results.isEmpty {
            let row = oldID.flatMap { id in results.firstIndex(where: { $0.id == id }) } ?? 0
            table.selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false)
            table.scrollRowToVisible(row)
        }
        emptyLabel.isHidden = !results.isEmpty
        emptyLabel.stringValue = items.isEmpty ? "尚无已打开或最近使用的文件" : "没有匹配的文件"
        summary.stringValue = "\(results.count) 个结果 · ↑↓ 选择 · Return 打开 · Esc 取消"
    }

    func numberOfRows(in tableView: NSTableView) -> Int { results.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard results.indices.contains(row) else { return nil }
        let id = NSUserInterfaceItemIdentifier("QuickOpenRow")
        let cell: QuickOpenCell
        if let reused = tableView.makeView(withIdentifier: id, owner: self) as? QuickOpenCell { cell = reused }
        else { cell = QuickOpenCell(); cell.identifier = id }
        let item = results[row]
        cell.nameLabel.stringValue = item.title
        cell.pathLabel.stringValue = item.subtitle
        cell.sourceLabel.stringValue = item.isOpen ? "已打开" : "最近"
        cell.setAccessibilityLabel("\(item.title)，\(item.isOpen ? "已打开" : "最近")，\(item.subtitle)")
        cell.toolTip = item.subtitle
        return cell
    }

    func controlTextDidChange(_ obj: Notification) {
        guard obj.object as? NSSearchField === search else { return }
        reloadResults()
    }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        // Candidate handling stays with the input method until composition commits.
        guard !textView.hasMarkedText() else { return false }
        switch NSStringFromSelector(commandSelector) {
        case "moveDown:": moveSelection(by: 1); return true
        case "moveUp:": moveSelection(by: -1); return true
        case "insertNewline:": chooseSelection(nil); return true
        case "cancelOperation:": cancelPicker(nil); return true
        default: return false
        }
    }

    private func moveSelection(by delta: Int) {
        guard !results.isEmpty else { return }
        let current = max(0, table.selectedRow)
        let row = (current + delta + results.count) % results.count
        table.selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false)
        table.scrollRowToVisible(row)
    }

    @objc private func chooseSelection(_ sender: Any?) {
        guard !completed, results.indices.contains(table.selectedRow) else { return }
        if let fieldEditor = window?.firstResponder as? NSTextView, fieldEditor.hasMarkedText() { return }
        let id = results[table.selectedRow].id
        completed = true
        close()
        if parent?.isVisible == true { parent?.makeKeyAndOrderFront(nil) }
        onSelect(id)
    }

    @objc private func cancelPicker(_ sender: Any?) {
        guard !completed else { return }
        completed = true
        close()
        if parent?.isVisible == true { parent?.makeKeyAndOrderFront(nil) }
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        completed = true
        return true
    }
}

private final class QuickOpenTable: NSTableView {
    var choose: (() -> Void)?
    var cancel: (() -> Void)?
    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 36, 76: choose?()
        case 53: cancel?()
        default: super.keyDown(with: event)
        }
    }
    override func cancelOperation(_ sender: Any?) { cancel?() }
}

private final class QuickOpenCell: NSTableCellView {
    let nameLabel = NSTextField(labelWithString: "")
    let pathLabel = NSTextField(labelWithString: "")
    let sourceLabel = NSTextField(labelWithString: "")

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        nameLabel.font = .systemFont(ofSize: 13, weight: .medium)
        nameLabel.lineBreakMode = .byTruncatingMiddle
        pathLabel.font = .systemFont(ofSize: 11)
        pathLabel.textColor = .secondaryLabelColor
        pathLabel.lineBreakMode = .byTruncatingMiddle
        sourceLabel.font = .systemFont(ofSize: 10, weight: .medium)
        sourceLabel.textColor = .secondaryLabelColor
        sourceLabel.alignment = .right
        textField = nameLabel
        [nameLabel, pathLabel, sourceLabel].forEach {
            $0.translatesAutoresizingMaskIntoConstraints = false
            addSubview($0)
        }
        NSLayoutConstraint.activate([
            nameLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 12),
            nameLabel.topAnchor.constraint(equalTo: topAnchor, constant: 8),
            nameLabel.trailingAnchor.constraint(lessThanOrEqualTo: sourceLabel.leadingAnchor, constant: -10),
            sourceLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -12),
            sourceLabel.centerYAnchor.constraint(equalTo: nameLabel.centerYAnchor),
            sourceLabel.widthAnchor.constraint(equalToConstant: 48),
            pathLabel.leadingAnchor.constraint(equalTo: nameLabel.leadingAnchor),
            pathLabel.topAnchor.constraint(equalTo: nameLabel.bottomAnchor, constant: 3),
            pathLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -12)
        ])
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
