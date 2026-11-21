import AppKit

/// Local workspace browsing is a native outline, with no embedded browser or
/// persistent index. Scanning runs off the main thread and publishes one result.
final class WorkspaceBrowserWindow: NSWindowController, NSOutlineViewDataSource, NSOutlineViewDelegate, NSSearchFieldDelegate {
    private final class Node {
        let title: String
        let url: URL
        let file: LocalWorkspaceFile?
        var children: [Node] = []
        init(title: String, url: URL, file: LocalWorkspaceFile? = nil) { self.title = title; self.url = url; self.file = file }
    }
    var openFile: ((URL) -> Void)?
    var rootChanged: ((URL) -> Void)?
    private(set) var rootURL: URL
    private let outline = NSOutlineView()
    private let nameFilter = NSSearchField()
    private let typeFilter = NSSearchField()
    private let favorites = NSPopUpButton()
    private let star = NSButton()
    private let rootLabel = NSTextField(labelWithString: "")
    private let status = NSTextField(labelWithString: "")
    private let queue = DispatchQueue(label: "qingyue.workspace.browser", qos: .userInitiated)
    private var generation = 0
    private var files: [LocalWorkspaceFile] = []
    private var nodes: [Node] = []
    private var filterTask: DispatchWorkItem?
    private let favoritesKey = "workspaceFavorites"

    init(rootURL: URL) {
        self.rootURL = rootURL.standardizedFileURL
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 720, height: 560), styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        window.title = "本地工作区"; window.minSize = NSSize(width: 540, height: 360); window.isReleasedWhenClosed = false
        super.init(window: window)
        build(); refreshFavorites(); scan(rootURL)
        window.center()
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }
    private func build() {
        guard let content = window?.contentView else { return }
        let choose = NSButton(title: "打开文件夹…", target: self, action: #selector(chooseRoot))
        let refresh = NSButton(title: "重新扫描", target: self, action: #selector(refreshRoot))
        star.title = "收藏目录"; star.target = self; star.action = #selector(toggleFavorite)
        favorites.target = self; favorites.action = #selector(selectFavorite); favorites.setAccessibilityLabel("收藏目录")
        nameFilter.placeholderString = "筛选文件名或路径"; nameFilter.setAccessibilityLabel("工作区文件名过滤"); nameFilter.delegate = self
        typeFilter.placeholderString = "类型，例如 md,txt,json"; typeFilter.setAccessibilityLabel("工作区类型过滤"); typeFilter.delegate = self
        rootLabel.lineBreakMode = .byTruncatingMiddle; rootLabel.setAccessibilityLabel("当前工作区")
        status.font = .systemFont(ofSize: 11); status.textColor = .secondaryLabelColor
        let toolbar = NSStackView(views: [choose, refresh, star, favorites]); toolbar.spacing = 8
        let filters = NSStackView(views: [nameFilter, typeFilter]); filters.spacing = 8; filters.distribution = .fillEqually
        let scroll = NSScrollView(); scroll.hasVerticalScroller = true; scroll.autohidesScrollers = true; scroll.borderType = .bezelBorder
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("path")); column.title = "文件"; column.minWidth = 200
        outline.addTableColumn(column); outline.outlineTableColumn = column; outline.headerView = nil; outline.rowHeight = 24
        outline.dataSource = self; outline.delegate = self; outline.target = self; outline.doubleAction = #selector(openSelected)
        outline.setAccessibilityLabel("工作区文件树"); scroll.documentView = outline
        for view in [toolbar, rootLabel, filters, scroll, status] { view.translatesAutoresizingMaskIntoConstraints = false; content.addSubview(view) }
        NSLayoutConstraint.activate([
            toolbar.topAnchor.constraint(equalTo: content.topAnchor, constant: 12), toolbar.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 12), toolbar.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -12),
            rootLabel.topAnchor.constraint(equalTo: toolbar.bottomAnchor, constant: 8), rootLabel.leadingAnchor.constraint(equalTo: toolbar.leadingAnchor), rootLabel.trailingAnchor.constraint(equalTo: toolbar.trailingAnchor),
            filters.topAnchor.constraint(equalTo: rootLabel.bottomAnchor, constant: 8), filters.leadingAnchor.constraint(equalTo: toolbar.leadingAnchor), filters.trailingAnchor.constraint(equalTo: toolbar.trailingAnchor),
            scroll.topAnchor.constraint(equalTo: filters.bottomAnchor, constant: 10), scroll.leadingAnchor.constraint(equalTo: toolbar.leadingAnchor), scroll.trailingAnchor.constraint(equalTo: toolbar.trailingAnchor),
            status.topAnchor.constraint(equalTo: scroll.bottomAnchor, constant: 8), status.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -10), status.leadingAnchor.constraint(equalTo: toolbar.leadingAnchor), status.trailingAnchor.constraint(equalTo: toolbar.trailingAnchor)
        ])
    }
    @objc private func chooseRoot() {
        let panel = NSOpenPanel(); panel.canChooseFiles = false; panel.canChooseDirectories = true; panel.allowsMultipleSelection = false; panel.prompt = "打开工作区"
        guard panel.runModal() == .OK, let root = panel.url else { return }; scan(root)
    }
    @objc private func refreshRoot() { scan(rootURL) }
    func scan(_ root: URL) {
        generation += 1; let token = generation
        rootURL = root.standardizedFileURL; rootLabel.stringValue = rootURL.path; rootLabel.toolTip = rootURL.path
        status.stringValue = "正在扫描…"; refreshFavorites()
        let selected = rootURL
        queue.async { [weak self] in
            let result = Result { try WorkspaceScanner.scan(root: selected) }
            DispatchQueue.main.async {
                guard let self, self.generation == token else { return }
                switch result {
                case .success(let files): self.files = files; self.applyFilters(); self.rootChanged?(selected)
                case .failure(let error): self.files = []; self.nodes = []; self.outline.reloadData(); self.status.stringValue = error.localizedDescription
                }
            }
        }
    }
    func controlTextDidChange(_ obj: Notification) {
        filterTask?.cancel()
        let task = DispatchWorkItem { [weak self] in self?.applyFilters() }; filterTask = task
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15, execute: task)
    }
    private func applyFilters() {
        let query = nameFilter.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        let extensions = Set(typeFilter.stringValue.lowercased().split(whereSeparator: { $0 == "," || $0 == ";" || $0.isWhitespace }).map { $0.trimmingCharacters(in: CharacterSet(charactersIn: ".")) })
        let filtered = files.filter { (query.isEmpty || $0.relativePath.localizedCaseInsensitiveContains(query)) && (extensions.isEmpty || extensions.contains($0.fileExtension)) }
        nodes = []
        var folders: [String: Node] = [:]
        for file in filtered {
            let components = file.relativePath.split(separator: "/").map(String.init)
            var key = "", parent: Node?
            for component in components.dropLast() {
                key += "/" + component
                let node: Node
                if let existing = folders[key] { node = existing }
                else {
                    node = Node(title: component, url: rootURL.appendingPathComponent(String(key.dropFirst())), file: nil); folders[key] = node
                    if let parent { parent.children.append(node) } else { nodes.append(node) }
                }
                parent = node
            }
            let node = Node(title: components.last ?? file.relativePath, url: URL(fileURLWithPath: file.path), file: file)
            if let parent { parent.children.append(node) } else { nodes.append(node) }
        }
        outline.reloadData(); if !query.isEmpty || !extensions.isEmpty { outline.expandItem(nil, expandChildren: true) }
        status.stringValue = "显示 \(filtered.count) / \(files.count) 个文件"
    }
    private func refreshFavorites() {
        let paths = UserDefaults.standard.stringArray(forKey: favoritesKey) ?? []
        favorites.removeAllItems(); favorites.addItem(withTitle: "收藏目录…")
        for path in paths { favorites.addItem(withTitle: path); favorites.lastItem?.representedObject = path }
        star.title = paths.contains(rootURL.path) ? "取消收藏" : "收藏目录"
    }
    @objc private func toggleFavorite() {
        var paths = UserDefaults.standard.stringArray(forKey: favoritesKey) ?? []
        if let index = paths.firstIndex(of: rootURL.path) { paths.remove(at: index) }
        else if paths.count < 50 { paths.append(rootURL.path) }
        UserDefaults.standard.set(paths, forKey: favoritesKey); refreshFavorites()
    }
    @objc private func selectFavorite() { if let path = favorites.selectedItem?.representedObject as? String { scan(URL(fileURLWithPath: path)) } }
    @objc private func openSelected() {
        guard outline.clickedRow >= 0, let node = outline.item(atRow: outline.clickedRow) as? Node else { return }
        if node.file != nil { openFile?(node.url) }
        else if outline.isItemExpanded(node) { outline.collapseItem(node) } else { outline.expandItem(node) }
    }
    func outlineView(_ outlineView: NSOutlineView, numberOfChildrenOfItem item: Any?) -> Int { (item as? Node)?.children.count ?? nodes.count }
    func outlineView(_ outlineView: NSOutlineView, child index: Int, ofItem item: Any?) -> Any { (item as? Node)?.children[index] ?? nodes[index] }
    func outlineView(_ outlineView: NSOutlineView, isItemExpandable item: Any) -> Bool { (item as? Node)?.file == nil }
    func outlineView(_ outlineView: NSOutlineView, viewFor tableColumn: NSTableColumn?, item: Any) -> NSView? {
        guard let node = item as? Node else { return nil }
        let field = NSTextField(labelWithString: node.title); field.lineBreakMode = .byTruncatingMiddle; field.toolTip = node.url.path
        field.font = .systemFont(ofSize: 13, weight: node.file == nil ? .medium : .regular)
        return field
    }
}
