import AppKit

/// Native workspace search with explicit replacement previews. The search result
/// carries the same normalized UTF-16 offset that the editor uses.
final class GlobalSearchWindow: NSWindowController, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate {
    var openResult: ((URL, Int) -> Void)?
    /// The host can reject files with unsaved buffers before any disk replacement.
    var canReplaceFile: ((URL) -> Bool)?
    private let rootURL: URL
    private let query = NSSearchField()
    private let replacement = NSTextField()
    private let fileFilter = NSTextField()
    private let caseSensitive = NSButton(checkboxWithTitle: "区分大小写", target: nil, action: nil)
    private let regex = NSButton(checkboxWithTitle: "正则", target: nil, action: nil)
    private let wholeWord = NSButton(checkboxWithTitle: "全词", target: nil, action: nil)
    private let table = NSTableView()
    private let status = NSTextField(labelWithString: "")
    private let applyButton = NSButton(title: "应用预览替换…", target: nil, action: nil)
    private let searchButton = NSButton(title: "搜索", target: nil, action: nil)
    private let previewButton = NSButton(title: "预览替换", target: nil, action: nil)
    private let cancelButton = NSButton(title: "取消搜索", target: nil, action: nil)
    private let queue = DispatchQueue(label: "qingyue.workspace.search", qos: .userInitiated)
    private var generation = 0
    private var task: DispatchWorkItem?
    private var results: [WorkspaceSearchResult] = []
    private var previews: [WorkspaceReplacementPreview] = []
    private var showingPreview = false
    private var applying = false
    private var detailWindows: [NSWindowController] = []

    init(rootURL: URL) {
        self.rootURL = rootURL.standardizedFileURL
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 900, height: 580), styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        window.title = "全局搜索 · \(rootURL.lastPathComponent)"; window.minSize = NSSize(width: 680, height: 400); window.isReleasedWhenClosed = false
        super.init(window: window); build(); window.center()
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }
    private func build() {
        guard let content = window?.contentView else { return }
        query.placeholderString = "搜索内容"; query.setAccessibilityLabel("工作区搜索内容"); query.target = self; query.action = #selector(search); query.delegate = self
        replacement.placeholderString = "替换内容（正则捕获组 $1）"; replacement.setAccessibilityLabel("工作区替换内容"); replacement.delegate = self
        fileFilter.placeholderString = "文件名过滤（包含）"; fileFilter.setAccessibilityLabel("搜索文件名过滤"); fileFilter.delegate = self
        searchButton.target = self; searchButton.action = #selector(search)
        previewButton.target = self; previewButton.action = #selector(previewReplacement)
        applyButton.target = self; applyButton.action = #selector(applyReplacement); applyButton.isEnabled = false
        cancelButton.target = self; cancelButton.action = #selector(cancelSearch); cancelButton.isEnabled = false
        for button in [caseSensitive, regex, wholeWord] { button.target = self; button.action = #selector(optionsChanged) }
        let path = NSTextField(labelWithString: rootURL.path); path.lineBreakMode = .byTruncatingMiddle; path.textColor = .secondaryLabelColor; path.font = .systemFont(ofSize: 11); path.toolTip = rootURL.path
        let searchRow = NSStackView(views: [query, searchButton, cancelButton]); searchRow.spacing = 8
        let replaceRow = NSStackView(views: [replacement, previewButton, applyButton]); replaceRow.spacing = 8
        let options = NSStackView(views: [caseSensitive, regex, wholeWord, fileFilter]); options.spacing = 12
        query.setContentHuggingPriority(.defaultLow, for: .horizontal); replacement.setContentHuggingPriority(.defaultLow, for: .horizontal); fileFilter.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let scroll = NSScrollView(); scroll.hasVerticalScroller = true; scroll.hasHorizontalScroller = true; scroll.autohidesScrollers = true; scroll.borderType = .bezelBorder
        for (id, title, width) in [("file", "文件", 260.0), ("position", "行 / 列", 90.0), ("preview", "正文 / 替换预览", 480.0)] {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id)); column.title = title; column.width = width; column.minWidth = 70; table.addTableColumn(column)
        }
        table.dataSource = self; table.delegate = self; table.target = self; table.doubleAction = #selector(openSelected); table.rowHeight = 25; table.usesAlternatingRowBackgroundColors = true; table.setAccessibilityLabel("全局搜索结果")
        scroll.documentView = table
        status.font = .systemFont(ofSize: 11); status.textColor = .secondaryLabelColor; status.stringValue = "搜索只读取本机文本；替换必须先预览并确认。"; status.lineBreakMode = .byTruncatingTail
        for view in [path, searchRow, replaceRow, options, scroll, status] { view.translatesAutoresizingMaskIntoConstraints = false; content.addSubview(view) }
        NSLayoutConstraint.activate([
            path.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 12), path.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -12), path.topAnchor.constraint(equalTo: content.topAnchor, constant: 10),
            searchRow.topAnchor.constraint(equalTo: path.bottomAnchor, constant: 8), searchRow.leadingAnchor.constraint(equalTo: path.leadingAnchor), searchRow.trailingAnchor.constraint(equalTo: path.trailingAnchor),
            options.topAnchor.constraint(equalTo: searchRow.bottomAnchor, constant: 8), options.leadingAnchor.constraint(equalTo: path.leadingAnchor), options.trailingAnchor.constraint(equalTo: path.trailingAnchor),
            replaceRow.topAnchor.constraint(equalTo: options.bottomAnchor, constant: 8), replaceRow.leadingAnchor.constraint(equalTo: path.leadingAnchor), replaceRow.trailingAnchor.constraint(equalTo: path.trailingAnchor),
            scroll.topAnchor.constraint(equalTo: replaceRow.bottomAnchor, constant: 10), scroll.leadingAnchor.constraint(equalTo: path.leadingAnchor), scroll.trailingAnchor.constraint(equalTo: path.trailingAnchor),
            status.topAnchor.constraint(equalTo: scroll.bottomAnchor, constant: 8), status.leadingAnchor.constraint(equalTo: path.leadingAnchor), status.trailingAnchor.constraint(equalTo: path.trailingAnchor), status.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -10),
            fileFilter.widthAnchor.constraint(greaterThanOrEqualToConstant: 150)
        ])
    }
    func controlTextDidChange(_ obj: Notification) { invalidate() }
    @objc private func optionsChanged() { invalidate() }
    private func invalidate() {
        guard !applying else { return }
        generation += 1; task?.cancel(); cancellation?.cancel(); task = nil; results = []; previews = []; showingPreview = false; applyButton.isEnabled = false; cancelButton.isEnabled = false
        table.reloadData(); status.stringValue = "条件已改变，请重新搜索或预览。"
    }
    @objc private func cancelSearch() { invalidate(); status.stringValue = "已取消搜索。" }
    @objc private func search() { start(preview: false) }
    @objc private func previewReplacement() { start(preview: true) }
    private func start(preview: Bool) {
        guard !query.stringValue.isEmpty, !applying else { return }
        invalidate(); showingPreview = preview
        let token = generation, text = query.stringValue, replacementText = replacement.stringValue, root = rootURL
        var options = WorkspaceSearchOptions(); options.fileNameContains = fileFilter.stringValue
        options.search.caseSensitive = caseSensitive.state == .on; options.search.regularExpression = regex.state == .on; options.search.wholeWord = wholeWord.state == .on
        status.stringValue = preview ? "正在生成替换预览…" : "正在搜索…"; cancelButton.isEnabled = true
        let operation = WorkspaceSearchCancellation()
        let work = DispatchWorkItem { [weak self] in
            do {
                let files = try WorkspaceScanner.scan(root: root)
                guard !operation.cancelled else { return }
                let matches = preview ? [] : try GlobalSearchEngine.search(files: files, query: text, options: options, shouldCancel: { operation.cancelled })
                let proposals = preview ? try GlobalSearchEngine.previewReplacements(files: files, query: text, replacement: replacementText, options: options, shouldCancel: { operation.cancelled }) : []
                DispatchQueue.main.async {
                    guard let self, self.generation == token, !operation.cancelled else { return }
                    self.results = matches; self.previews = proposals; self.cancelButton.isEnabled = false; self.applyButton.isEnabled = preview && !proposals.isEmpty
                    self.table.reloadData(); self.status.stringValue = preview ? "预览 \(proposals.count) 个文件，\(proposals.reduce(0) { $0 + $1.matchCount }) 处。双击查看前后内容；尚未改写磁盘。" : "找到 \(matches.count) 处。双击打开并定位。"
                }
            } catch {
                DispatchQueue.main.async { guard let self, self.generation == token else { return }; self.cancelButton.isEnabled = false; self.status.stringValue = error.localizedDescription }
            }
        }
        task = work
        // DispatchWorkItem cancellation doesn't interrupt an executing closure;
        // the separate flag lets the file loop stop at the next file boundary.
        cancellation = operation
        queue.async(execute: work)
    }
    private var cancellation: WorkspaceSearchCancellation? { willSet { cancellation?.cancel() } }
    @objc private func applyReplacement() {
        guard !applying, !previews.isEmpty else { return }
        let permitted = previews.filter { canReplaceFile?(URL(fileURLWithPath: $0.file.path)) ?? true }
        let skipped = previews.count - permitted.count
        guard !permitted.isEmpty else { status.stringValue = "所有文件都含有未保存正文，未执行替换。"; return }
        let confirm = NSAlert(); confirm.messageText = "应用 \(permitted.count) 个文件的替换？"
        confirm.informativeText = "将改写磁盘文本。替换前会重新检查文件版本；冲突文件会保留。\(skipped > 0 ? "另有 \(skipped) 个未保存文件跳过。" : "")"
        confirm.addButton(withTitle: "应用替换"); confirm.addButton(withTitle: "取消")
        guard confirm.runModal() == .alertFirstButtonReturn else { return }
        applying = true; setControlsEnabled(false); status.stringValue = "正在应用替换…"
        queue.async { [weak self] in
            var applied = 0, errors: [String] = []
            for proposal in permitted {
                do { try GlobalSearchEngine.apply(proposal); applied += 1 }
                catch { errors.append("\(proposal.file.relativePath)：\(error.localizedDescription)") }
            }
            DispatchQueue.main.async {
                guard let self else { return }; self.applying = false; self.setControlsEnabled(true); self.invalidate()
                self.status.stringValue = "已替换 \(applied) 个文件；跳过 \(skipped)，失败/冲突 \(errors.count)。"
                self.status.toolTip = errors.joined(separator: "\n")
            }
        }
    }
    private func setControlsEnabled(_ enabled: Bool) {
        for control in [query, replacement, fileFilter, caseSensitive, regex, wholeWord, searchButton, previewButton, applyButton, cancelButton] as [NSControl] { control.isEnabled = enabled }
    }
    func numberOfRows(in tableView: NSTableView) -> Int { showingPreview ? previews.count : results.count }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let id = tableColumn?.identifier.rawValue
        let value: String
        if showingPreview {
            guard previews.indices.contains(row) else { return nil }; let proposal = previews[row]
            value = id == "file" ? proposal.file.relativePath : id == "position" ? "\(proposal.matchCount) 处" : "双击查看替换前后内容"
        } else {
            guard results.indices.contains(row) else { return nil }; let result = results[row]
            value = id == "file" ? result.file.relativePath : id == "position" ? "\(result.line):\(result.column)" : result.preview
        }
        let field = NSTextField(labelWithString: value); field.lineBreakMode = .byTruncatingTail; field.toolTip = value; return field
    }
    @objc private func openSelected() {
        let row = table.clickedRow; guard row >= 0 else { return }
        if showingPreview {
            guard previews.indices.contains(row) else { return }; let proposal = previews[row]
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 720, height: 500), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
            window.title = "替换预览 · \(proposal.file.relativePath)"; window.isReleasedWhenClosed = false
            let scroll = NSScrollView(frame: window.contentView!.bounds); scroll.autoresizingMask = [.width, .height]; scroll.hasVerticalScroller = true
            let view = NSTextView(frame: scroll.bounds); view.isEditable = false; view.autoresizingMask = [.width]; view.font = .monospacedSystemFont(ofSize: 12, weight: .regular)
            view.string = "替换前：\n" + String(proposal.originalText.prefix(100_000)) + "\n\n替换后：\n" + String(proposal.replacementText.prefix(100_000))
            scroll.documentView = view; window.contentView?.addSubview(scroll)
            detailWindows.removeAll { $0.window?.isVisible != true }; let controller = NSWindowController(window: window); detailWindows.append(controller); controller.showWindow(nil)
        } else if results.indices.contains(row) { let result = results[row]; openResult?(URL(fileURLWithPath: result.file.path), result.range.location) }
    }
    override func close() { task?.cancel(); cancellation?.cancel(); generation += 1; super.close() }
}

private final class WorkspaceSearchCancellation {
    private let lock = NSLock()
    private var value = false
    var cancelled: Bool { lock.lock(); defer { lock.unlock() }; return value }
    func cancel() { lock.lock(); value = true; lock.unlock() }
}
