import AppKit

final class ReaderTab {
    let file: TextFile
    let undo = UndoManager()
    var selection = NSRange(location: 0, length: 0)
    var scroll = NSPoint.zero
    var readingScroll = NSPoint.zero
    var isReading = false
    var splitPreview = false
    var renderKey: String?
    var mediaTask: ReadingMediaTask?
    var mediaGeneration = 0
    var mediaNotices: [String] = []
    var navigation: [Int] = []
    var openedText: String
    var savedText: String
    var sourceAnchor = 0
    var automaticallyNamed = false
    var automaticKind = true
    var recoverDraft = true
    var hasWorkspacePosition = false
    var rendered: ReadingDocument?
    var kind: TextKind
    init(_ file: TextFile) {
        self.file = file
        openedText = file.text; savedText = file.text
        kind = TextKind.detect(url: file.url, text: file.text)
        isReading = kind == .markdown
        undo.levelsOfUndo = 100
    }
    func updateKindAfterPathChange() {
        if automaticKind { kind = TextKind.detect(url: file.url ?? file.suggestedName.map { URL(fileURLWithPath: $0) }, text: file.text) }
    }
}

final class ReaderWindow: NSWindowController, NSWindowDelegate, NSTextViewDelegate, NSSearchFieldDelegate, NSMenuItemValidation, NSSplitViewDelegate {
    var sidebarToggleForTests: NSButton { sidebarToggleButton }
    private var tabs: [ReaderTab] = []
    private var current = 0
    private var changing = false
    private var lineIndexUpdated = false
    private var dark = UserDefaults.standard.object(forKey: "dark") as? Bool ?? true
    private var theme: ReaderTheme { ReaderTheme(dark: dark) }
    private var fontSize = CGFloat(UserDefaults.standard.double(forKey: "fontSize") == 0 ? 14 : UserDefaults.standard.double(forKey: "fontSize"))
    private var wrapping = UserDefaults.standard.object(forKey: "wrapping") as? Bool ?? true
    private var reading = false
    private var focused = false
    private var restoringLocation = false
    private var liveResizeAnchor: Int?
    private var followSystemAppearance = UserDefaults.standard.object(forKey: "followSystemAppearance") as? Bool ?? true
    private var readingWidth = CGFloat(UserDefaults.standard.object(forKey: "readingWidth") as? Double ?? 1_000_000)
    private var showingSidebar = true
    private var searchVisible = false
    private var recent: [String] { get { UserDefaults.standard.stringArray(forKey: "recent") ?? [] } set { UserDefaults.standard.set(newValue, forKey: "recent") } }
    private var active: ReaderTab? { tabs.indices.contains(current) ? tabs[current] : nil }
    private let root = ColorView()
    private let sidebar = ColorView()
    private let canvas = ColorView()
    private let header = NSView()
    private let menuRow = ColorView()
    private let windowMenuBar = WindowMenuBar()
    private var menuRowHeight: NSLayoutConstraint!
    private var sidebarToggleButton: ActionButton!
    private var branding: NSStackView!
    private var focusHiddenActions: [NSView] = []
    private let titleLabel = NSTextField(labelWithString: "轻阅")
    private let subtitle = NSTextField(labelWithString: "本机文本 · 阅读与编辑")
    private let sideTitle = NSTextField(labelWithString: "最近打开")
    private let sideNote = NSTextField(wrappingLabelWithString: "⌘O  打开文件\n⌘S  保存修改\n⌘F  查找文本")
    private let recentStack = FlippedStackView()
    private let tabsStack = TabStripView()
    private var tabViewIDs: [UUID] = []
    private var tabTitleButtons: [ActionButton] = []
    private let tabScroll = NSScrollView()
    private let editorScroll = NSScrollView()
    private let editor = EditorTextView(frame: .zero)
    private let preview = ReadingTextView(frame: .zero)
    private let previewScroll = NSScrollView()
    private let documentSplit = NSSplitView()
    private var layingOutPanes = false
    private var splitFraction: CGFloat = 0.5
    private var presentationGeneration = 0
    private var splitTask: DispatchWorkItem?
    private var searchReturnOffset: Int?
    private var contextualOffset = 0
    private var resourceWindows: [NSWindowController] = []
    private var externalCheckRunning = false
    private var operation: ReaderOperation?
    private var installedPresentation: NSAttributedString?
    private var outlineMode = false
    private let sidebarMode = NSSegmentedControl(labels: ["目录", "最近"], trackingMode: .selectOne, target: nil, action: nil)
    private var outlineButtons: [(Int, ActionButton)] = []
    private var ruler: LineNumberRuler!
    private let tools = WindowsFlowToolbar()
    private let toolsContainer = ColorView()
    private let tabRow = ColorView()
    private let workspaceBody = NSView()
    private let footer = ColorView()
    private var clearRecentButton: ActionButton!
    private var sidebarRecentButton: ActionButton!
    private var sidebarOutlineButton: ActionButton!
    private var toolbarCopyButton: ActionButton!
    private var toolbarOptions: WindowsPopupButton!
    private var returnPositionButton: ActionButton!
    private let presentationHint = NSTextField(labelWithString: "")
    private let searchBar = NSStackView()
    private let replaceBar = NSStackView()
    private var replacementVisible = false
    private let searchContainer = NSView()
    private let searchOptionsRow = NSStackView()
    private let caseCheck = NSButton(checkboxWithTitle: "区分大小写", target: nil, action: nil)
    private let regexCheck = NSButton(checkboxWithTitle: "正则", target: nil, action: nil)
    private let wordCheck = NSButton(checkboxWithTitle: "全词", target: nil, action: nil)
    private let findCounter = NSTextField(labelWithString: "")
    private let searchQueue = DispatchQueue(label: "qingyue.search", qos: .userInitiated)
    private var searchTask: DispatchWorkItem?
    private var searchVersion = 0
    private var searchReady = false
    private var searchMatches: [SearchMatch] = []
    private var searchIndex = -1
    private var pendingFind: Bool?
    private var openingPaths = Set<String>()
    private var openingGeneration = 0
    private let fileOpenQueue = DispatchQueue(label: "qingyue.file-open", qos: .userInitiated)
    private var showLineNumbers = UserDefaults.standard.object(forKey: "lineNumbers") as? Bool ?? true
    private var readingFontName = UserDefaults.standard.string(forKey: "readingFont") ?? "PingFang SC"
    private var readingLineSpacing = CGFloat(UserDefaults.standard.object(forKey: "lineSpacing") as? Double ?? 6)
    private let searchField = NSSearchField()
    private let replaceField = NSTextField()
    private let statusLeft = NSTextField(labelWithString: "就绪")
    private let statusRight = NSTextField(labelWithString: "行 1，列 1")
    private let kindPopup = WindowsPopupButton()
    private let codecPopup = WindowsPopupButton()
    private let endingPopup = WindowsPopupButton()
    private var sidebarWidth: NSLayoutConstraint!
    private var rulerWidth: NSLayoutConstraint!
    private var searchHeight: NSLayoutConstraint!
    private var buttons: [ActionButton] = []
    private var themeButton: ActionButton!
    private var readButton: ActionButton!
    private var wrapButton: ActionButton!
    private var formatButton: ActionButton!
    private var splitButton: ActionButton!
    private var structureButton: ActionButton!
    private var focusButton: ActionButton!
    private var replacementButtons: [ActionButton] = []
    private var tabsHeight: NSLayoutConstraint!
    private var toolsHeight: NSLayoutConstraint!
    private var headerHeight: NSLayoutConstraint!
    private var canvasInsetLeading: NSLayoutConstraint!
    private var canvasFullLeading: NSLayoutConstraint!
    private var statusNormalTrailing: NSLayoutConstraint!
    private var statusReadingTrailing: NSLayoutConstraint!
    private var highlightTask: DispatchWorkItem?
    private var recoveryTask: DispatchWorkItem?
    private var generation = 0
    private var shuttingDown = false
    private lazy var workspace = WorkspaceStore(root: dataDirectory)
    private var restoringWorkspace = false
    private var deferredOpenURLs: [URL] = []
    private var pendingNewDocument = false
    private var recoveryEnabled = UserDefaults.standard.object(forKey: "recoveryEnabled") as? Bool ?? true
    private var restoreSessionEnabled = UserDefaults.standard.object(forKey: "restoreSession") as? Bool ?? true
    private var restoreNotices: [String] = []
    private var excludedDraftPaths: Set<String> { get { Set(UserDefaults.standard.stringArray(forKey: "excludedDraftPaths") ?? []) } set { UserDefaults.standard.set(Array(newValue).sorted(), forKey: "excludedDraftPaths") } }
    private var persistenceGeneration = 0
    private var lastDraftSave: Date?
    private var quickOpenController: QuickOpenController?
    private var recentMenu: NSMenu?
    private var modalDepth = 0
    private var deferredDocumentMutations: [() -> Void] = []
    private var savingAll = false
    private var closeBatch = false
    private let supportOverride: URL?
    private var dataDirectory: URL {
        if let path = supportOverride { return path }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("LightReader", isDirectory: true)
    }
    private var recoveryURL: URL { dataDirectory.appendingPathComponent("Recovery.json") }

    init(supportDirectory: URL? = nil) {
        supportOverride = supportDirectory
        let win = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1120, height: 762), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        win.title = "轻阅"
        win.minSize = NSSize(width: 820, height: 492)
        win.isReleasedWhenClosed = false
        win.tabbingMode = .disallowed
        super.init(window: win)
        win.delegate = self
        buildInterface()
        applyTheme()
        if !win.setFrameUsingName("QingyueMainWindow") { win.center() }
        win.setFrameAutosaveName("QingyueMainWindow")
        if tabs.isEmpty { newDocument(nil) }
        restoreWorkspace()
        refreshRecent()
        NotificationCenter.default.addObserver(self, selector: #selector(scrolled), name: NSView.boundsDidChangeNotification, object: editorScroll.contentView)
        NotificationCenter.default.addObserver(self, selector: #selector(scrolled), name: NSView.boundsDidChangeNotification, object: previewScroll.contentView)
        NotificationCenter.default.addObserver(self, selector: #selector(becameActive), name: NSApplication.didBecomeActiveNotification, object: nil)
        DistributedNotificationCenter.default().addObserver(self, selector: #selector(systemAppearanceChanged), name: NSNotification.Name("AppleInterfaceThemeChangedNotification"), object: nil)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }

    private func label(_ text: String, _ size: CGFloat = 12, bold: Bool = false) -> NSTextField {
        let field = NSTextField(labelWithString: text)
        field.font = .systemFont(ofSize: size, weight: bold ? .semibold : .regular)
        field.textColor = theme.muted
        return field
    }
    private func button(_ title: String, symbol: String? = nil, primary: Bool = false, _ action: @escaping () -> Void) -> ActionButton {
        let b = ActionButton(title, symbol: symbol, action: action)
        b.isEmphasized = primary; buttons.append(b)
        return b
    }
    private func fixed(_ view: NSView, height: CGFloat) {
        view.translatesAutoresizingMaskIntoConstraints = false
        view.heightAnchor.constraint(equalToConstant: height).isActive = true
    }
    private func add(_ child: NSView, to parent: NSView) {
        child.translatesAutoresizingMaskIntoConstraints = false; parent.addSubview(child)
    }
    private func stack(_ views: [NSView], spacing: CGFloat = 8) -> NSStackView {
        let s = NSStackView(views: views); s.orientation = .horizontal; s.alignment = .centerY; s.spacing = spacing
        return s
    }
    private func buildInterface() {
        guard let window = window else { return }
        window.contentView = root
        func identify(_ view: NSView, _ name: String) { view.identifier = NSUserInterfaceItemIdentifier(name) }
        identify(menuRow, "QingyueMenuRow"); identify(header, "QingyueHeader"); identify(tabRow, "QingyueTabRow")
        identify(searchContainer, "QingyueSearchPanel"); identify(workspaceBody, "QingyueBody"); identify(sidebar, "QingyueSidebar")
        identify(toolsContainer, "QingyueContextToolbar"); identify(footer, "QingyueFooter")
        [menuRow, header, tabRow, searchContainer, workspaceBody, footer].forEach { add($0, to: root) }
        add(windowMenuBar, to: menuRow)
        windowMenuBar.prepareMenu = { [weak self] _ in self?.refreshRecentMenu() }
        sidebarToggleButton = button("", symbol: "sidebar.left") { [weak self] in self?.toggleSidebar(nil) }
        sidebarToggleButton.setAccessibilityLabel("显示或隐藏侧栏"); sidebarToggleButton.toolTip = "显示 / 隐藏侧栏 · ⌘B"
        sidebarToggleButton.setAccessibilityIdentifier("QingyueSidebarToggle")
        sidebarToggleButton.widthAnchor.constraint(equalToConstant: 36).isActive = true
        add(sidebarToggleButton, to: header)
        titleLabel.font = .systemFont(ofSize: 22, weight: .semibold); subtitle.font = .systemFont(ofSize: 11)
        branding = NSStackView(views: [titleLabel, subtitle]); branding.orientation = .vertical; branding.alignment = .leading; branding.spacing = 3; add(branding, to: header)
        let open = button("打开文件") { [weak self] in self?.openDocument(nil) }
        let save = button("保存") { [weak self] in _ = self?.saveCurrent() }
        readButton = button("阅读模式", primary: true) { [weak self] in self?.toggleReading(nil) }
        focusButton = button("专注") { [weak self] in self?.toggleFocus(nil) }
        let headerActions = stack([open, save, readButton, focusButton], spacing: 8); add(headerActions, to: header)
        // Theme/settings remain in their menu locations, as in Windows.
        themeButton = button("切换明暗主题") { [weak self] in self?.toggleTheme(nil) }
        tabsStack.orientation = .horizontal; tabsStack.alignment = .centerY; tabsStack.spacing = 3; tabsStack.translatesAutoresizingMaskIntoConstraints = false
        tabsStack.reorder = { [weak self] id, insertion in self?.reorderTab(id: id, insertion: insertion) }
        tabScroll.drawsBackground = false; tabScroll.hasHorizontalScroller = true; tabScroll.autohidesScrollers = true; tabScroll.scrollerStyle = .overlay; tabScroll.documentView = tabsStack; add(tabScroll, to: tabRow)
        let plus = button("+") { [weak self] in self?.newDocument(nil) }; plus.font = .systemFont(ofSize: 20); plus.setAccessibilityLabel("新建文档"); add(plus, to: tabRow)
        let tabGesture = NSClickGestureRecognizer(target: self, action: #selector(tabBlankDoubleClick(_:))); tabGesture.numberOfClicksRequired = 2; tabScroll.addGestureRecognizer(tabGesture)

        searchField.placeholderString = "输入查找内容"; searchField.font = .systemFont(ofSize: 13); searchField.setAccessibilityLabel("查找文本"); searchField.target = self; searchField.action = #selector(findNext); searchField.delegate = self
        replaceField.placeholderString = "替换内容；正则捕获组使用 $1"; replaceField.font = .systemFont(ofSize: 13); replaceField.setAccessibilityLabel("替换为"); replaceField.delegate = self
        for row in [searchBar, replaceBar, searchOptionsRow] { row.orientation = .horizontal; row.alignment = .centerY; row.spacing = 8; add(row, to: searchContainer) }
        let previous = button("上一个") { [weak self] in self?.find(backward: true) }
        let next = button("下一个") { [weak self] in self?.find(backward: false) }
        let close = button("关闭") { [weak self] in self?.hideFind() }; close.toolTip = "Esc"
        [searchField, previous, next, close].forEach { searchBar.addArrangedSubview($0) }
        searchField.setContentHuggingPriority(.defaultLow, for: .horizontal)
        replacementButtons = [button("替换") { [weak self] in self?.replaceOne(nil) }, button("全部替换") { [weak self] in self?.replaceAll(nil) }]
        [replaceField, replacementButtons[0], replacementButtons[1]].forEach { replaceBar.addArrangedSubview($0) }; replaceField.setContentHuggingPriority(.defaultLow, for: .horizontal)
        for c in [caseCheck, regexCheck, wordCheck] { c.font = .systemFont(ofSize: 12); c.target = self; c.action = #selector(searchOptionsChanged) }
        findCounter.font = .systemFont(ofSize: 11); findCounter.lineBreakMode = .byTruncatingTail
        [caseCheck, regexCheck, wordCheck, findCounter].forEach { searchOptionsRow.addArrangedSubview($0) }
        searchContainer.isHidden = true; replaceBar.isHidden = true

        add(sidebar, to: workspaceBody); add(canvas, to: workspaceBody)
        let sideTabs = NSStackView(); sideTabs.orientation = .horizontal; sideTabs.spacing = 0
        sidebarRecentButton = button("最近打开") { [weak self] in self?.setSidebarOutline(false) }
        sidebarOutlineButton = button("文档目录") { [weak self] in self?.setSidebarOutline(true) }
        [sidebarRecentButton!, sidebarOutlineButton!].forEach { b in b.controlHeight = 34; b.cornerRadius = 0; b.horizontalPadding = 10; sideTabs.addArrangedSubview(b) }; add(sideTabs, to: sidebar)
        let recentScroll = NSScrollView(); recentScroll.contentView = FlippedClipView(); recentScroll.drawsBackground = false; recentScroll.hasVerticalScroller = true; recentScroll.autohidesScrollers = true
        recentStack.orientation = .vertical; recentStack.alignment = .leading; recentStack.spacing = 4; recentStack.translatesAutoresizingMaskIntoConstraints = false
        recentScroll.documentView = recentStack; add(recentScroll, to: sidebar)
        clearRecentButton = button("清除最近记录") { [weak self] in self?.clearRecent(nil) }; clearRecentButton.font = .systemFont(ofSize: 11); add(clearRecentButton, to: sidebar)
        sideNote.stringValue = "留一点空间给阅读。\n⌘O 打开 · ⌘R 阅读"; sideNote.font = .systemFont(ofSize: 11); add(sideNote, to: sidebar)

        add(toolsContainer, to: canvas); add(tools, to: toolsContainer)
        formatButton = button("格式化", primary: true) { [weak self] in self?.formatDocument(nil) }
        structureButton = button("JSON 结构") { [weak self] in self?.showJSONStructure(nil) }
        splitButton = button("分屏编辑") { [weak self] in self?.toggleSplitPreview(nil) }
        toolbarCopyButton = button("复制全文（含图片）") { [weak self] in self?.copyReadingFull(nil) }
        toolbarOptions = WindowsPopupButton(frame: .zero, pullsDown: true); toolbarOptions.font = .systemFont(ofSize: 12); toolbarOptions.setAccessibilityLabel("复制 / 导出")
        let options = NSMenu(); options.autoenablesItems = true
        options.addItem(NSMenuItem(title: "复制 / 导出 ▾", action: nil, keyEquivalent: ""))
        for (title, action) in [("复制全文纯文本", #selector(copyReadingPlain(_:))), ("复制 Markdown 源文", #selector(copyMarkdownSource(_:))), ("复制当前代码块", #selector(copyCodeBlock(_:))), ("复制当前表格", #selector(copyTableBlock(_:))), ("导出 HTML（含图片）…", #selector(exportReadingHTML(_:)))] { let i = NSMenuItem(title: title, action: action, keyEquivalent: ""); i.target = self; options.addItem(i) }
        toolbarOptions.menu = options
        returnPositionButton = button("返回位置") { [weak self] in self?.returnReadingPosition(nil) }; returnPositionButton.toolTip = "返回目录跳转前的阅读位置 · ⌥←"
        presentationHint.font = .systemFont(ofSize: 11); presentationHint.lineBreakMode = .byTruncatingTail
        [formatButton!, structureButton!, splitButton!, toolbarCopyButton!, toolbarOptions!, returnPositionButton!, presentationHint].forEach { tools.addArrangedSubview($0) }
        // Retain legacy actions only through menus, rather than scattering controls.
        wrapButton = button("自动折行") { [weak self] in self?.toggleWrap(nil) }
        kindPopup.addItems(withTitles: ["自动识别"] + TextKind.allCases.map { $0.label }); kindPopup.target = self; kindPopup.action = #selector(kindChanged); kindPopup.font = .systemFont(ofSize: 11); kindPopup.setAccessibilityLabel("文档语言")
        codecPopup.addItems(withTitles: TextCodec.allCases.map { $0.label }); codecPopup.target = self; codecPopup.action = #selector(codecChanged); codecPopup.font = .systemFont(ofSize: 11); codecPopup.setAccessibilityLabel("保存编码")
        endingPopup.addItems(withTitles: ["LF · Unix / macOS", "CRLF · Windows", "CR · 旧版 Mac", "混合换行"]); endingPopup.target = self; endingPopup.action = #selector(endingChanged); endingPopup.font = .systemFont(ofSize: 11); endingPopup.setAccessibilityLabel("文件换行方式")
        [statusLeft, statusRight, kindPopup, codecPopup, endingPopup].forEach { add($0, to: footer) }
        statusLeft.font = .systemFont(ofSize: 11); statusLeft.lineBreakMode = .byTruncatingMiddle; statusLeft.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        statusRight.font = .monospacedDigitSystemFont(ofSize: 10, weight: .regular)

        editor.isRichText = false; editor.importsGraphics = false; editor.allowsUndo = true
        editor.isAutomaticQuoteSubstitutionEnabled = false; editor.isAutomaticDashSubstitutionEnabled = false; editor.isAutomaticTextReplacementEnabled = false; editor.isAutomaticSpellingCorrectionEnabled = false; editor.isAutomaticLinkDetectionEnabled = false; editor.isContinuousSpellCheckingEnabled = false; editor.isAutomaticDataDetectionEnabled = false; editor.isGrammarCheckingEnabled = false; editor.usesFindBar = false
        editor.textContainerInset = NSSize(width: 18, height: 20); editor.isVerticallyResizable = true; editor.isHorizontallyResizable = false; editor.autoresizingMask = [.width]; editor.minSize = .zero; editor.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        editor.delegate = self; editor.setAccessibilityLabel("文本编辑区"); editor.cancelHandler = { [weak self] in self?.cancelFind() }; editor.filesDropped = { [weak self] in self?.openURLs($0) }; editor.registerForDraggedTypes([.fileURL])
        editorScroll.hasVerticalScroller = true; editorScroll.hasHorizontalScroller = false; editorScroll.autohidesScrollers = true; editorScroll.borderType = .noBorder; editorScroll.documentView = editor; editorScroll.contentView.postsBoundsChangedNotifications = true
        ruler = LineNumberRuler(editor: editor, scroll: editorScroll); add(ruler, to: canvas)
        documentSplit.isVertical = true; documentSplit.dividerStyle = .thin; documentSplit.delegate = self; documentSplit.addSubview(editorScroll); documentSplit.addSubview(previewScroll); editorScroll.translatesAutoresizingMaskIntoConstraints = true; previewScroll.translatesAutoresizingMaskIntoConstraints = true; add(documentSplit, to: canvas)
        previewScroll.hasVerticalScroller = true; previewScroll.autohidesScrollers = true; previewScroll.documentView = preview; previewScroll.contentView.postsBoundsChangedNotifications = true
        preview.copyReading = { [weak self] in self?.copyReadingRange($0) }; preview.menuBuilder = { [weak self] in self?.readingContextMenu(offset: $0) ?? NSMenu() }; preview.openAttachment = { [weak self] in self?.showAttachment($0) }
        preview.isEditable = false; preview.isSelectable = true; preview.isRichText = true; preview.textContainerInset = NSSize(width: 32, height: 26); preview.autoresizingMask = [.width]; preview.isVerticallyResizable = true; preview.setAccessibilityLabel("阅读区"); preview.delegate = self; preview.textContainer?.widthTracksTextView = true
        sidebarWidth = sidebar.widthAnchor.constraint(equalToConstant: 218); rulerWidth = ruler.widthAnchor.constraint(equalToConstant: showLineNumbers ? 48 : 0)
        searchHeight = searchContainer.heightAnchor.constraint(equalToConstant: 0); tabsHeight = tabRow.heightAnchor.constraint(equalToConstant: 44); toolsHeight = toolsContainer.heightAnchor.constraint(equalToConstant: 49); headerHeight = header.heightAnchor.constraint(equalToConstant: 78); menuRowHeight = menuRow.heightAnchor.constraint(equalToConstant: 30)
        canvasInsetLeading = canvas.leadingAnchor.constraint(equalTo: sidebar.trailingAnchor); canvasFullLeading = canvas.leadingAnchor.constraint(equalTo: workspaceBody.leadingAnchor)
        // Legacy footer trailing constraints remain inactive; footer has one shared layout.
        statusNormalTrailing = statusRight.trailingAnchor.constraint(lessThanOrEqualTo: kindPopup.leadingAnchor, constant: -10)
        statusReadingTrailing = statusRight.trailingAnchor.constraint(lessThanOrEqualTo: kindPopup.leadingAnchor, constant: -10)
        NSLayoutConstraint.activate([
            menuRow.topAnchor.constraint(equalTo: root.topAnchor), menuRow.leadingAnchor.constraint(equalTo: root.leadingAnchor), menuRow.trailingAnchor.constraint(equalTo: root.trailingAnchor), menuRowHeight,
            windowMenuBar.leadingAnchor.constraint(equalTo: menuRow.leadingAnchor, constant: 10), windowMenuBar.centerYAnchor.constraint(equalTo: menuRow.centerYAnchor), windowMenuBar.heightAnchor.constraint(equalToConstant: 28), windowMenuBar.trailingAnchor.constraint(lessThanOrEqualTo: menuRow.trailingAnchor, constant: -10),
            header.topAnchor.constraint(equalTo: menuRow.bottomAnchor), header.leadingAnchor.constraint(equalTo: root.leadingAnchor), header.trailingAnchor.constraint(equalTo: root.trailingAnchor), headerHeight,
            sidebarToggleButton.leadingAnchor.constraint(equalTo: header.leadingAnchor, constant: 20), sidebarToggleButton.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            branding.leadingAnchor.constraint(equalTo: sidebarToggleButton.trailingAnchor, constant: 14), branding.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            headerActions.trailingAnchor.constraint(equalTo: header.trailingAnchor, constant: -20), headerActions.centerYAnchor.constraint(equalTo: header.centerYAnchor), headerActions.leadingAnchor.constraint(greaterThanOrEqualTo: branding.trailingAnchor, constant: 12),
            tabRow.topAnchor.constraint(equalTo: header.bottomAnchor), tabRow.leadingAnchor.constraint(equalTo: root.leadingAnchor), tabRow.trailingAnchor.constraint(equalTo: root.trailingAnchor), tabsHeight,
            tabScroll.leadingAnchor.constraint(equalTo: tabRow.leadingAnchor), tabScroll.topAnchor.constraint(equalTo: tabRow.topAnchor), tabScroll.bottomAnchor.constraint(equalTo: tabRow.bottomAnchor), tabScroll.trailingAnchor.constraint(equalTo: plus.leadingAnchor, constant: -5),
            plus.trailingAnchor.constraint(equalTo: tabRow.trailingAnchor, constant: -5), plus.centerYAnchor.constraint(equalTo: tabRow.centerYAnchor), plus.widthAnchor.constraint(equalToConstant: 40),
            tabsStack.leadingAnchor.constraint(equalTo: tabScroll.contentView.leadingAnchor), tabsStack.topAnchor.constraint(equalTo: tabScroll.contentView.topAnchor), tabsStack.heightAnchor.constraint(equalTo: tabScroll.contentView.heightAnchor),
            searchContainer.topAnchor.constraint(equalTo: tabRow.bottomAnchor), searchContainer.leadingAnchor.constraint(equalTo: root.leadingAnchor), searchContainer.trailingAnchor.constraint(equalTo: root.trailingAnchor), searchHeight,
            searchBar.leadingAnchor.constraint(equalTo: searchContainer.leadingAnchor, constant: 18), searchBar.trailingAnchor.constraint(equalTo: searchContainer.trailingAnchor, constant: -18), searchBar.topAnchor.constraint(equalTo: searchContainer.topAnchor, constant: 10), searchBar.heightAnchor.constraint(equalToConstant: 32),
            searchOptionsRow.topAnchor.constraint(equalTo: searchBar.bottomAnchor, constant: 5), searchOptionsRow.leadingAnchor.constraint(equalTo: searchBar.leadingAnchor), searchOptionsRow.heightAnchor.constraint(equalToConstant: 23), searchOptionsRow.trailingAnchor.constraint(lessThanOrEqualTo: searchBar.trailingAnchor),
            replaceBar.topAnchor.constraint(equalTo: searchOptionsRow.bottomAnchor, constant: 6), replaceBar.leadingAnchor.constraint(equalTo: searchBar.leadingAnchor), replaceBar.trailingAnchor.constraint(equalTo: searchBar.trailingAnchor), replaceBar.heightAnchor.constraint(equalToConstant: 32),
            workspaceBody.topAnchor.constraint(equalTo: searchContainer.bottomAnchor), workspaceBody.leadingAnchor.constraint(equalTo: root.leadingAnchor), workspaceBody.trailingAnchor.constraint(equalTo: root.trailingAnchor), workspaceBody.bottomAnchor.constraint(equalTo: footer.topAnchor),
            sidebar.topAnchor.constraint(equalTo: workspaceBody.topAnchor), sidebar.bottomAnchor.constraint(equalTo: workspaceBody.bottomAnchor), sidebar.leadingAnchor.constraint(equalTo: workspaceBody.leadingAnchor), sidebarWidth,
            sideTabs.leadingAnchor.constraint(equalTo: sidebar.leadingAnchor, constant: 14), sideTabs.trailingAnchor.constraint(lessThanOrEqualTo: sidebar.trailingAnchor, constant: -14), sideTabs.topAnchor.constraint(equalTo: sidebar.topAnchor, constant: 18),
            recentScroll.topAnchor.constraint(equalTo: sideTabs.bottomAnchor, constant: 12), recentScroll.leadingAnchor.constraint(equalTo: sidebar.leadingAnchor, constant: 14), recentScroll.trailingAnchor.constraint(equalTo: sidebar.trailingAnchor, constant: -14), recentScroll.bottomAnchor.constraint(equalTo: clearRecentButton.topAnchor, constant: -10),
            recentStack.leadingAnchor.constraint(equalTo: recentScroll.contentView.leadingAnchor), recentStack.topAnchor.constraint(equalTo: recentScroll.contentView.topAnchor), recentStack.widthAnchor.constraint(equalTo: recentScroll.contentView.widthAnchor),
            clearRecentButton.leadingAnchor.constraint(equalTo: sidebar.leadingAnchor, constant: 14), clearRecentButton.trailingAnchor.constraint(equalTo: sidebar.trailingAnchor, constant: -14), clearRecentButton.bottomAnchor.constraint(equalTo: sideNote.topAnchor, constant: -18),
            sideNote.leadingAnchor.constraint(equalTo: sidebar.leadingAnchor, constant: 20), sideNote.trailingAnchor.constraint(equalTo: sidebar.trailingAnchor, constant: -14), sideNote.bottomAnchor.constraint(equalTo: sidebar.bottomAnchor, constant: -20),
            canvasInsetLeading, canvas.trailingAnchor.constraint(equalTo: workspaceBody.trailingAnchor), canvas.topAnchor.constraint(equalTo: workspaceBody.topAnchor), canvas.bottomAnchor.constraint(equalTo: workspaceBody.bottomAnchor),
            toolsContainer.leadingAnchor.constraint(equalTo: canvas.leadingAnchor), toolsContainer.trailingAnchor.constraint(equalTo: canvas.trailingAnchor), toolsContainer.topAnchor.constraint(equalTo: canvas.topAnchor), toolsHeight,
            tools.leadingAnchor.constraint(equalTo: toolsContainer.leadingAnchor, constant: 12), tools.trailingAnchor.constraint(equalTo: toolsContainer.trailingAnchor, constant: -12), tools.topAnchor.constraint(equalTo: toolsContainer.topAnchor, constant: 8), tools.bottomAnchor.constraint(equalTo: toolsContainer.bottomAnchor, constant: -8),
            ruler.leadingAnchor.constraint(equalTo: canvas.leadingAnchor), ruler.topAnchor.constraint(equalTo: documentSplit.topAnchor), ruler.bottomAnchor.constraint(equalTo: documentSplit.bottomAnchor), rulerWidth,
            documentSplit.topAnchor.constraint(equalTo: toolsContainer.bottomAnchor), documentSplit.leadingAnchor.constraint(equalTo: ruler.trailingAnchor), documentSplit.trailingAnchor.constraint(equalTo: canvas.trailingAnchor), documentSplit.bottomAnchor.constraint(equalTo: canvas.bottomAnchor),
            footer.leadingAnchor.constraint(equalTo: root.leadingAnchor), footer.trailingAnchor.constraint(equalTo: root.trailingAnchor), footer.bottomAnchor.constraint(equalTo: root.bottomAnchor), footer.heightAnchor.constraint(equalToConstant: 50),
            statusLeft.leadingAnchor.constraint(equalTo: footer.leadingAnchor, constant: 16), statusLeft.topAnchor.constraint(equalTo: footer.topAnchor, constant: 7), statusLeft.trailingAnchor.constraint(lessThanOrEqualTo: kindPopup.leadingAnchor, constant: -12),
            statusRight.leadingAnchor.constraint(equalTo: statusLeft.leadingAnchor), statusRight.topAnchor.constraint(equalTo: statusLeft.bottomAnchor, constant: 3), statusRight.bottomAnchor.constraint(lessThanOrEqualTo: footer.bottomAnchor, constant: -7),
            kindPopup.trailingAnchor.constraint(equalTo: codecPopup.leadingAnchor, constant: -14), kindPopup.widthAnchor.constraint(equalToConstant: 143), kindPopup.centerYAnchor.constraint(equalTo: footer.centerYAnchor),
            codecPopup.trailingAnchor.constraint(equalTo: endingPopup.leadingAnchor, constant: -14), codecPopup.widthAnchor.constraint(equalToConstant: 131), codecPopup.centerYAnchor.constraint(equalTo: footer.centerYAnchor),
            endingPopup.trailingAnchor.constraint(equalTo: footer.trailingAnchor, constant: -16), endingPopup.widthAnchor.constraint(equalToConstant: 171), endingPopup.centerYAnchor.constraint(equalTo: footer.centerYAnchor)
        ])
    }

    private func editorFont() -> NSFont {
        if reading { return NSFont(name: readingFontName, size: fontSize + 1) ?? .systemFont(ofSize: fontSize + 1) }
        return .monospacedSystemFont(ofSize: fontSize, weight: .regular)
    }
    private func applyTheme() {
        if followSystemAppearance { dark = NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua }
        window?.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        root.color = theme.window; sidebar.color = theme.panel; canvas.color = theme.editor
        menuRow.color = theme.panel; tabRow.color = theme.window; footer.color = theme.panel; toolsContainer.color = theme.panel
        for view in [menuRow, header, tabRow, sidebar, toolsContainer, footer] { view.wantsLayer = true; view.layer?.borderColor = theme.border.cgColor; view.layer?.borderWidth = 0.5 }
        windowMenuBar.applyAppearance(foreground: theme.text, background: theme.panel, highlight: theme.hover)
        for popup in [kindPopup, codecPopup, endingPopup, toolbarOptions!] { popup.applyTheme(theme) }
        for field in [searchField, replaceField] { field.backgroundColor = theme.editor; field.textColor = theme.text }
        presentationHint.textColor = theme.muted
        titleLabel.textColor = theme.text; subtitle.textColor = theme.muted; sideTitle.textColor = theme.muted; sideNote.textColor = theme.muted
        statusLeft.textColor = theme.muted; statusRight.textColor = theme.muted
        findCounter.textColor = theme.muted
        editor.backgroundColor = theme.editor; editor.textColor = theme.text; editor.insertionPointColor = theme.accent
        editor.selectedTextAttributes = [.backgroundColor: theme.selection, .foregroundColor: theme.text]
        previewScroll.backgroundColor = theme.editor
        editorScroll.backgroundColor = theme.editor; preview.backgroundColor = theme.editor; preview.textColor = theme.text
        ruler.ink = theme.muted; ruler.background = theme.panel; ruler.savedInk = theme.saved; ruler.unsavedInk = theme.unsaved; ruler.needsDisplay = true
        for b in buttons { style(b) }
        themeButton.title = followSystemAppearance ? "随系统" : (dark ? "明亮" : "深色")
        refreshTabs(); refreshRecent(); scheduleHighlight()
        if reading || active?.splitPreview == true { renderReading() }
    }
    private func style(_ b: ActionButton) {
        b.fill = b.isEmphasized ? theme.accent : theme.button; b.hoverFill = theme.hover; b.borderColor = theme.border
        b.ink = b.isEmphasized ? .white : theme.text
    }
    private func refreshTabs() {
        let ids = tabs.map { $0.file.id }
        if ids != tabViewIDs {
            tabsStack.arrangedSubviews.forEach { tabsStack.removeArrangedSubview($0); $0.removeFromSuperview() }
            tabViewIDs = ids; tabTitleButtons = []
            for tab in tabs {
                let id = tab.file.id
                let b = DraggableTabButton(tab.file.title) { [weak self] in
                    if let self = self, let i = self.tabs.firstIndex(where: { $0.file.id == id }) { self.selectTab(i) }
                }
                b.doubleClickHandler = { [weak self] in self?.renameDocument(id: id) }
                b.tabID = id; b.controlHeight = 42; b.cornerRadius = 0; b.borderColor = .clear
                b.widthAnchor.constraint(equalToConstant: 116).isActive = true
                let context = NSMenu()
                for (title, action) in [("重命名…", #selector(renameContextTab(_:))), ("关闭此标签", #selector(closeContextTab(_:))), ("关闭其他标签", #selector(closeOtherTabs(_:))), ("关闭全部标签", #selector(closeAllTabs(_:))), ("向左移动", #selector(moveContextLeft(_:))), ("向右移动", #selector(moveContextRight(_:))), ("切换此文件草稿恢复", #selector(toggleContextRecovery(_:))), ("复制路径", #selector(copyContextPath(_:))), ("在 Finder 中显示", #selector(revealContextFile(_:)))] {
                    let item = NSMenuItem(title: title, action: action, keyEquivalent: ""); item.target = self; item.representedObject = id; context.addItem(item)
                }
                b.menu = context
                let x = ActionButton("×") { [weak self] in
                    if let self = self, let i = self.tabs.firstIndex(where: { $0.file.id == id }) { self.closeTab(i) }
                }
                x.fill = .clear; x.ink = theme.muted; x.borderColor = .clear; x.cornerRadius = 0; x.controlHeight = 22; x.horizontalPadding = 0
                x.widthAnchor.constraint(equalToConstant: 22).isActive = true; x.setAccessibilityLabel("关闭 \(tab.file.title)")
                tabsStack.addArrangedSubview(stack([b, x], spacing: 0)); tabTitleButtons.append(b)
            }
        }
        for (index, tab) in tabs.enumerated() {
            let b = tabTitleButtons[index]
            let title = (tab.file.isDirty ? "● " : "") + tab.file.title
            if b.title != title { b.title = title }
            b.fill = index == current ? theme.tabSelected : theme.window; b.ink = index == current ? theme.text : theme.muted
            b.bottomAccent = index == current ? theme.accent : .clear; b.borderColor = .clear; b.hoverFill = theme.hover
            if let cell = b.superview { cell.wantsLayer = true; cell.layer?.backgroundColor = b.fill.cgColor; cell.layer?.borderWidth = 0
                if let close = cell.subviews.compactMap({ $0 as? ActionButton }).first(where: { $0 !== b }) { close.ink = theme.muted; close.fill = b.fill; close.hoverFill = theme.hover }
            }
            b.toolTip = tab.file.url?.path ?? "尚未保存的文档"
            b.constraints.first(where: { $0.firstAttribute == .width && $0.relation == .equal })?.constant = min(210, max(116, CGFloat(tab.file.title.count * 8 + 36)))
        }
        window?.title = "\(active?.file.title ?? "轻阅") — 轻阅"
        window?.isDocumentEdited = active?.file.isDirty ?? false
        window?.representedURL = active?.file.url
    }
    private func refreshRecent() {
        recentStack.arrangedSubviews.forEach { recentStack.removeArrangedSubview($0); $0.removeFromSuperview() }
        outlineButtons = []
        if outlineMode { refreshOutlineSidebar(); return }
        updateSidebarTabs()
        let files = recent.filter { FileManager.default.fileExists(atPath: $0) }
        if files.isEmpty {
            let hint = label("打开文件后\n会显示在这里", 12); hint.maximumNumberOfLines = 0
            recentStack.addArrangedSubview(hint)
        }
        for path in files.prefix(15) {
            let row = RecentFileRow(url: URL(fileURLWithPath: path), foreground: theme.text, muted: theme.muted) { [weak self] in self?.openURLs([URL(fileURLWithPath: path)]) }
            row.rowColor = theme.selection; recentStack.addArrangedSubview(row); row.widthAnchor.constraint(equalTo: recentStack.widthAnchor).isActive = true
        }
    }
    private func remember(_ url: URL) {
        var items = recent.filter { $0 != url.path }; items.insert(url.path, at: 0); recent = Array(items.prefix(15)); refreshRecent(); refreshRecentMenu()
    }
    @objc func clearRecent(_ sender: Any?) {
        recent = []; UserDefaults.standard.removeObject(forKey: "positions"); refreshRecent(); refreshRecentMenu()
    }
    func configureWindowMenus(_ groups: [WindowMenuGroup]) { windowMenuBar.setGroups(groups) }
    func validateMenuItem(_ item: NSMenuItem) -> Bool {
        guard let action = item.action else { return true }
        if action == #selector(toggleSidebar(_:)) { item.state = showingSidebar ? .on : .off }
        if action == #selector(toggleReading(_:)) { item.state = reading ? .on : .off }
        if action == #selector(toggleFocus(_:)) { item.state = focused ? .on : .off }
        if action == #selector(toggleWrap(_:)) { item.state = wrapping ? .on : .off }
        if action == #selector(toggleCurrentDraftRecovery(_:)) { item.state = active?.recoverDraft == true ? .on : .off }
        if modalDepth > 0 { return false }
        if action == #selector(clearRecent(_:)) || action == #selector(showGuide(_:)) || action == #selector(showRestoreNotices(_:)) { return true }
        if restoringWorkspace { return false }
        if action == #selector(undoEdit(_:)) || action == #selector(redoEdit(_:)) {
            let fieldUndo = (window?.firstResponder as? NSTextView).flatMap { $0.isFieldEditor ? $0.undoManager : nil }
            if reading && fieldUndo == nil { return false }
            let manager = fieldUndo ?? active?.undo
            return action == #selector(undoEdit(_:)) ? manager?.canUndo == true : manager?.canRedo == true
        }
        if action == #selector(formatJSON(_:)) { return active?.kind == .json && !reading }
        if action == #selector(copyCurrentPath(_:)) || action == #selector(revealCurrentFile(_:)) || action == #selector(reloadDocument(_:)) || action == #selector(reopenEncoding(_:)) { return active?.file.url != nil }
        if action == #selector(selectNumberedTab(_:)) { return !tabs.isEmpty && (item.tag == 8 || item.tag < tabs.count) }
        if action == #selector(moveCurrentTabLeft(_:)) { return current > 0 }
        if action == #selector(moveCurrentTabRight(_:)) { return current + 1 < tabs.count }
        return active != nil
    }
    @objc func renameCurrentDocument(_ sender: Any?) { if let id = active?.file.id { renameDocument(id: id) } }
    @objc private func renameContextTab(_ item: NSMenuItem) { if let id = item.representedObject as? UUID { renameDocument(id: id) } }
    private func renameDocument(id: UUID) {
        guard !restoringWorkspace, modalDepth == 0, let i = tabs.firstIndex(where: { $0.file.id == id }) else { return }
        selectTab(i); stashCurrent()
        let tab = tabs[i], file = tab.file
        let oldPath = file.url?.path
        let a = NSAlert(); a.messageText = "重命名"
        a.informativeText = file.url == nil ? "先为文档起名；首次保存时再选择位置。" : "修改磁盘上的文件名，尚未保存的正文修改会继续保留在编辑器中。"
        let input = NSTextField(frame: NSRect(x: 0, y: 0, width: 340, height: 26))
        input.stringValue = file.title == "未命名" ? "未命名.txt" : file.title
        input.setAccessibilityLabel("新的文件名"); a.accessoryView = input
        a.addButton(withTitle: "重命名"); a.addButton(withTitle: "取消")
        a.window.initialFirstResponder = input
        guard runDocumentModal({ a.runModal() }) == .alertFirstButtonReturn else { return }
        do {
            try file.rename(toName: input.stringValue)
            tab.automaticallyNamed = false
            if let before = oldPath, let after = file.url?.path, before != after {
                recent = recent.map { $0 == before ? after : $0 }
                var positions = UserDefaults.standard.dictionary(forKey: "positions") as? [String: [Double]] ?? [:]
                if let position = positions.removeValue(forKey: before) { positions[after] = position }
                UserDefaults.standard.set(positions, forKey: "positions")
                var excluded = excludedDraftPaths
                if excluded.remove(before) != nil { excluded.insert(after) }; excludedDraftPaths = excluded
                remember(file.url!)
            }
            refreshAutomaticKind(tab); refreshTabs(); writeRecovery(); updateStatus("已重命名为 \(file.title)")
        } catch { refreshTabs(); showError(error) }
    }
    func attachRecentMenu(_ menu: NSMenu) { recentMenu = menu; refreshRecentMenu() }
    private func refreshRecentMenu() {
        guard let menu = recentMenu else { return }
        menu.removeAllItems()
        for path in recent {
            let url = URL(fileURLWithPath: path)
            let item = NSMenuItem(title: "\(url.lastPathComponent) — \(url.deletingLastPathComponent().lastPathComponent)", action: #selector(openRecentItem(_:)), keyEquivalent: "")
            item.target = self; item.representedObject = path; item.toolTip = path
            menu.addItem(item)
        }
        if menu.items.isEmpty { let item = NSMenuItem(title: "暂无最近文件", action: nil, keyEquivalent: ""); item.isEnabled = false; menu.addItem(item) }
        menu.addItem(.separator())
        let clear = NSMenuItem(title: "清除最近文件与位置", action: #selector(clearRecent(_:)), keyEquivalent: ""); clear.target = self; menu.addItem(clear)
    }
    @objc private func openRecentItem(_ item: NSMenuItem) { if let path = item.representedObject as? String { openURLs([URL(fileURLWithPath: path)]) } }
    @objc func quickOpen(_ sender: Any?) {
        guard !restoringWorkspace else { return }
        let opened = tabs.map { tab in QuickOpenItem(id: "tab:" + tab.file.id.uuidString, title: tab.file.title, subtitle: tab.file.url?.path ?? "未保存的文本", isOpen: true) }
        let paths = Set(tabs.compactMap { $0.file.url?.standardizedFileURL.path })
        let recentItems = recent.filter { !paths.contains($0) }.map { path in QuickOpenItem(id: "path:" + path, title: URL(fileURLWithPath: path).lastPathComponent, subtitle: path, isOpen: false) }
        quickOpenController?.close()
        quickOpenController = QuickOpenController(items: opened + recentItems) { [weak self] id in
            guard let self = self else { return }
            if id.hasPrefix("tab:"), let uuid = UUID(uuidString: String(id.dropFirst(4))), let i = self.tabs.firstIndex(where: { $0.file.id == uuid }) { self.selectTab(i) }
            else if id.hasPrefix("path:") { self.openURLs([URL(fileURLWithPath: String(id.dropFirst(5)))]) }
            self.window?.makeKeyAndOrderFront(nil)
        }
        quickOpenController?.present(relativeTo: window)
    }
    @objc func showDraftManagement(_ sender: Any?) {
        guard !restoringWorkspace else { return }
        let a = NSAlert(); a.messageText = "会话与恢复草稿"
        a.informativeText = "恢复草稿在本机按文档后台保存，不会自动写回原文件。手动关闭标签会移除对应会话与草稿；退出时可选择保留草稿。"
        let recover = NSButton(checkboxWithTitle: "自动保存恢复草稿", target: nil, action: nil); recover.state = recoveryEnabled ? .on : .off
        let session = NSButton(checkboxWithTitle: "启动时恢复上次标签会话", target: nil, action: nil); session.state = restoreSessionEnabled ? .on : .off
        let currentDraft = NSButton(checkboxWithTitle: "当前文件允许保存草稿", target: nil, action: nil); currentDraft.state = active?.recoverDraft == false ? .off : .on; currentDraft.isEnabled = active != nil
        let time = lastDraftSave.map { DateFormatter.localizedString(from: $0, dateStyle: .none, timeStyle: .medium) } ?? "本次尚未保存"
        let detail = label("最近成功写入：" + time)
        let content = NSStackView(views: [recover, session, currentDraft, detail]); content.orientation = .vertical; content.alignment = .leading; content.spacing = 12
        content.frame = NSRect(x: 0, y: 0, width: 400, height: 125); a.accessoryView = content
        a.addButton(withTitle: "应用"); a.addButton(withTitle: "取消"); a.addButton(withTitle: "清除已存草稿")
        let answer = runDocumentModal({ a.runModal() })
        if answer == .alertThirdButtonReturn { clearDraftFiles(); return }
        guard answer == .alertFirstButtonReturn else { return }
        if recoveryEnabled && recover.state == .off {
            guard alert("关闭恢复并清除已存草稿？", "当前编辑内容仍在窗口内，但关闭后无法依靠草稿恢复，请及时保存原文件。", buttons: ["关闭并清除", "取消"]) == .alertFirstButtonReturn else { return }
        }
        recoveryEnabled = recover.state == .on; restoreSessionEnabled = session.state == .on
        UserDefaults.standard.set(recoveryEnabled, forKey: "recoveryEnabled"); UserDefaults.standard.set(restoreSessionEnabled, forKey: "restoreSession")
        if let tab = active {
            tab.recoverDraft = currentDraft.state == .on
            if let path = tab.file.url?.standardizedFileURL.path {
                var excluded = excludedDraftPaths
                if tab.recoverDraft { excluded.remove(path) } else { excluded.insert(path) }
                excludedDraftPaths = excluded
            }
        }
        writeRecovery(); updateStatus("会话与草稿设置已更新")
    }
    @objc func toggleCurrentDraftRecovery(_ sender: Any?) {
        guard let tab = active else { return }
        if tab.recoverDraft && alert("排除此文档的恢复草稿？", "当前内容仍在编辑器中，已存草稿会清除；关闭前请保存原文件。", buttons: ["排除并清除", "取消"]) != .alertFirstButtonReturn { return }
        tab.recoverDraft.toggle()
        if let path = tab.file.url?.standardizedFileURL.path {
            var paths = excludedDraftPaths
            if tab.recoverDraft { paths.remove(path) } else { paths.insert(path) }
            excludedDraftPaths = paths
        }
        writeRecovery(); updateStatus(tab.recoverDraft ? "当前文档允许恢复草稿" : "当前文档已排除恢复草稿")
    }
    private func clearDraftFiles() {
        guard alert("清除全部恢复草稿？", "不会删除原文件或窗口内的编辑内容。继续编辑后，已启用恢复的文档会重新生成草稿。", buttons: ["清除", "取消"]) == .alertFirstButtonReturn else { return }
        recoveryTask?.cancel(); persistenceGeneration += 1
        workspace.clearDrafts { [weak self] error in
            if let error = error { self?.showError(error) }
            else { self?.lastDraftSave = nil; self?.updateStatus("已清除草稿 · 继续编辑后按设置重新保存") }
        }
    }
    @objc func showRestoreNotices(_ sender: Any?) {
        _ = alert("会话恢复详情", restoreNotices.isEmpty ? "没有恢复错误。" : restoreNotices.joined(separator: "\n"), buttons: ["关闭"])
    }
    private func rememberPosition(_ tab: ReaderTab) {
        guard let path = tab.file.url?.path else { return }
        var positions = UserDefaults.standard.dictionary(forKey: "positions") as? [String: [Double]] ?? [:]
        positions[path] = [Double(tab.selection.location), Double(tab.selection.length), Double(tab.scroll.x), Double(tab.scroll.y), Double(tab.readingScroll.y), tab.isReading ? 1 : 0, Double(tab.sourceAnchor)]
        if positions.count > 100 { positions = positions.filter { recent.prefix(80).contains($0.key) || $0.key == path } }
        UserDefaults.standard.set(positions, forKey: "positions")
    }
    private func restorePosition(_ tab: ReaderTab) {
        guard let path = tab.file.url?.path,
              let p = (UserDefaults.standard.dictionary(forKey: "positions") as? [String: [Double]])?[path], p.count >= 5,
              p.allSatisfy({ $0.isFinite && $0 >= 0 && $0 < 100_000_000 }) else { return }
        tab.selection = NSRange(location: Int(p[0]), length: Int(p[1]))
        tab.scroll = NSPoint(x: p[2], y: p[3]); tab.readingScroll = NSPoint(x: 0, y: p[4])
        if p.count >= 7 { tab.isReading = p[5] == 1; tab.sourceAnchor = min(Int(p[6]), tab.file.text.utf16.count) }
    }

    @objc func newDocument(_ sender: Any?) {
        if modalDepth > 0 { deferredDocumentMutations.append { [weak self] in self?.newDocument(sender) }; return }
        if restoringWorkspace { pendingNewDocument = true; return }
        stashCurrent()
        var number = 1; while tabs.contains(where: { $0.file.title == "新文件 \(number).txt" }) { number += 1 }
        let file = TextFile(); file.suggestedName = "新文件 \(number).txt"
        let newTab = ReaderTab(file); newTab.automaticallyNamed = true; tabs.append(newTab); selectTab(tabs.count - 1, stash: false); scheduleRecovery()
    }
    @objc func openDocument(_ sender: Any?) {
        let p = NSOpenPanel(); p.allowsMultipleSelection = true; p.canChooseDirectories = false
        p.message = "打开本机文本文件 · TXT / JSON / YAML / MD / 配置文件"
        if runDocumentModal({ p.runModal() }) == .OK { openURLs(p.urls) }
    }
    func openURLs(_ urls: [URL]) {
        if modalDepth > 0 { deferredDocumentMutations.append { [weak self] in self?.openURLs(urls) }; return }
        if restoringWorkspace { deferredOpenURLs += urls; return }
        for url in urls {
            let resolved = url.resolvingSymlinksInPath().standardizedFileURL
            if let i = tabs.firstIndex(where: { $0.file.url?.resolvingSymlinksInPath().standardizedFileURL == resolved }) { selectTab(i); continue }
            guard openingPaths.insert(resolved.path).inserted else { continue }
            if tabs.count >= 200 { updateStatus("最多同时打开200份文档"); openingPaths.remove(resolved.path); continue }
            openingGeneration += 1; let openToken = openingGeneration
            let progress = ReaderOperation(title: "打开 \(url.lastPathComponent)", parent: window); operation = progress
            statusLeft.stringValue = "正在读取 \(url.lastPathComponent)…"
            fileOpenQueue.async { [weak self] in
                let result = Result { try TextFile.open(url) }
                DispatchQueue.main.async {
                    guard let self = self else { progress.finish(); return }
                    self.performDocumentMutation { [weak self] in
                        guard let self = self, !self.shuttingDown else { progress.finish(); return }
                        self.openingPaths.remove(resolved.path)
                        guard !progress.cancelled else { progress.finish(); self.updateStatus("打开已取消"); return }
                        switch result {
                        case .success(let file):
                            progress.stage("正在解析…")
                            // Yield once between reading and native presentation so cancellation
                            // can be processed before building the view.
                            DispatchQueue.main.async {
                                guard !progress.cancelled else { progress.finish(); return }
                                self.performDocumentMutation {
                                    self.stashCurrent()
                                    if self.tabs.count == 1, let initial = self.tabs.first, initial.file.url == nil, initial.file.text.isEmpty, initial.file.suggestedName == nil || initial.automaticallyNamed { self.tabs.removeAll() }
                                    let tab = ReaderTab(file); tab.recoverDraft = !self.excludedDraftPaths.contains(url.standardizedFileURL.path); self.restorePosition(tab)
                                    progress.stage("正在排版…")
                                    self.tabs.append(tab); self.selectTab(self.tabs.count - 1, stash: false); self.remember(url); self.scheduleRecovery()
                                    if self.window?.isVisible == false { self.showWindow(nil) }
                                    progress.finish()
                                    if self.openingGeneration == openToken { self.operation = nil }
                                }
                            }
                        case .failure(let error): progress.finish(); self.showError(error)
                        }
                    }
                }
            }
        }
    }
    private func stashCurrent() {
        guard let tab = active else { return }
        editor.breakUndoCoalescing()
        tab.selection = editor.selectedRange()
        if reading { tab.readingScroll = previewScroll.contentView.bounds.origin }
        else { tab.scroll = editorScroll.contentView.bounds.origin }
        if !reading { tab.file.text = editor.string }
        tab.isReading = reading
        if !restoringLocation { tab.sourceAnchor = currentSourceOffset() }
        rememberPosition(tab)
    }
    private func selectTab(_ index: Int, stash: Bool = true) {
        guard modalDepth == 0 else { return }
        guard tabs.indices.contains(index) else { return }
        if stash { stashCurrent() }
        pendingFind = nil
        changing = true; current = index
        guard let tab = active else { changing = false; return }
        reading = tab.isReading
        editor.tabUndo = tab.undo
        editor.string = tab.file.text; editor.font = editorFont()
        editor.setSelectedRange(NSRange(location: min(tab.selection.location, (editor.string as NSString).length), length: min(tab.selection.length, max(0, (editor.string as NSString).length - tab.selection.location))))
        kindPopup.selectItem(at: tab.automaticKind ? 0 : (TextKind.allCases.firstIndex(of: tab.kind) ?? 0) + 1)
        codecPopup.selectItem(at: TextCodec.allCases.firstIndex(of: tab.file.codec) ?? 0)
        endingPopup.selectItem(at: [.lf, .crlf, .cr, .mixed].firstIndex(of: tab.file.lineEnding) ?? 0)
        ruler.updateLines(editor.string); changing = false
        updateWrap(); refreshTabs(); updateStatus(); scheduleHighlight()
        updateModeControls()
        let anchor = tab.sourceAnchor
        if reading || active?.splitPreview == true { renderReading() }
        window?.contentView?.layoutSubtreeIfNeeded()
        if reading || tab.hasWorkspacePosition { scrollToSource(anchor); tab.hasWorkspacePosition = false }
        else {
            restoringLocation = true
            editorScroll.contentView.scroll(to: tab.scroll); editorScroll.reflectScrolledClipView(editorScroll.contentView)
            restoringLocation = false
        }
        scheduleSearch(); updateChangeMarks()
        window?.makeFirstResponder(reading ? preview : editor)
        scheduleRecovery(); checkExternalFile(tab)
    }
    @objc func saveDocument(_ sender: Any?) { _ = saveCurrent() }
    @objc func undoEdit(_ sender: Any?) {
        if let field = window?.firstResponder as? NSTextView, field.isFieldEditor { field.undoManager?.undo(); return }
        guard !reading, let undo = active?.undo, undo.canUndo else { return }
        editor.breakUndoCoalescing(); window?.makeFirstResponder(editor); undo.undo()
    }
    @objc func redoEdit(_ sender: Any?) {
        if let field = window?.firstResponder as? NSTextView, field.isFieldEditor { field.undoManager?.redo(); return }
        guard !reading, let undo = active?.undo, undo.canRedo else { return }
        editor.breakUndoCoalescing(); window?.makeFirstResponder(editor); undo.redo()
    }
    @objc func saveAs(_ sender: Any?) { _ = saveCurrent(saveAs: true) }
    @discardableResult private func saveCurrent(saveAs: Bool = false, overwrite: Bool = false, chosenDestination: URL? = nil) -> Bool {
        guard let tab = active else { return true }
        stashCurrent()
        var destination: URL? = chosenDestination
        if chosenDestination == nil && (saveAs || tab.file.url == nil) {
            let panel = NSSavePanel(); panel.nameFieldStringValue = tab.file.url?.lastPathComponent ?? "未命名.txt"
            panel.canCreateDirectories = true; panel.message = "保存到本机，保留所选编码与换行符"
            if tab.file.url == nil, let name = tab.file.suggestedName { panel.nameFieldStringValue = name }
            if let dir = tab.file.url?.deletingLastPathComponent() { panel.directoryURL = dir }
            guard runDocumentModal({ panel.runModal() }) == .OK, let url = panel.url else { return false }
            destination = url
        }
        do {
            try tab.file.save(to: destination, allowOverwrite: overwrite)
            refreshAutomaticKind(tab)
            if !tab.recoverDraft, let path = tab.file.url?.path { var excluded = excludedDraftPaths; excluded.insert(path); excludedDraftPaths = excluded }
            if let url = tab.file.url { remember(url) }
            tab.savedText = tab.file.text; updateChangeMarks()
            refreshTabs(); updateStatus("已保存"); writeRecovery(); return true
        } catch TextFileError.externalChange {
            let result = alert("文件已在外部修改", "为避免覆盖其他程序的修改，请选择处理方式。当前编辑内容仍然保留。", buttons: ["另存为", "取消", "覆盖磁盘文件"])
            if result == .alertFirstButtonReturn { return saveCurrent(saveAs: true) }
            if result == .alertThirdButtonReturn { return saveCurrent(overwrite: true, chosenDestination: destination) }
            return false
        } catch TextFileError.mixedLineEndings {
            let result = alert("文件包含混合换行", "继续保存需要统一换行符。请选择目标格式，或取消以保留当前编辑内容。", buttons: ["统一为 LF", "取消", "统一为 CRLF"])
            if result == .alertSecondButtonReturn { return false }
            tab.file.lineEnding = result == .alertFirstButtonReturn ? .lf : .crlf
            endingPopup.selectItem(at: result == .alertFirstButtonReturn ? 0 : 1)
            // Keep the Save As destination selected earlier.
            do { try tab.file.save(to: destination, allowOverwrite: overwrite); refreshAutomaticKind(tab); refreshTabs(); updateStatus("已保存"); writeRecovery(); return true }
            catch { showError(error); return false }
        } catch { showError(error); return false }
    }
    @objc func closeCurrent(_ sender: Any?) { closeTab(current) }
    @objc func saveAllDocuments(_ sender: Any?) {
        guard !savingAll && !restoringWorkspace else { return }
        stashCurrent(); savingAll = true
        let selected = active?.file.id
        var summary = ""
        defer {
            savingAll = false
            if let id = selected, let i = tabs.firstIndex(where: { $0.file.id == id }) { selectTab(i) }
            writeRecovery()
            updateStatus(summary)
        }
        let ids = tabs.filter { $0.file.isDirty && (!$0.file.text.isEmpty || $0.file.url != nil || $0.file.suggestedName != nil && !$0.automaticallyNamed) }.map { $0.file.id }
        var count = 0
        for id in ids {
            guard let i = tabs.firstIndex(where: { $0.file.id == id }) else { continue }
            selectTab(i)
            guard saveCurrent() else { summary = "保存全部已停止 · 已保存 \(count) 份，其余内容仍保留"; return }
            count += 1
        }
        summary = "保存全部完成 · \(count) 份"
    }
    @objc func closeAllTabs(_ sender: Any?) {
        guard !restoringWorkspace else { return }
        closeBatch = true
        defer { closeBatch = false; if tabs.isEmpty { window?.performClose(nil) }; writeRecovery() }
        let ids = tabs.map { $0.file.id }
        for id in ids.reversed() { if let i = tabs.firstIndex(where: { $0.file.id == id }), !closeTab(i) { break } }
    }
    private func reorderTab(id: UUID, insertion: Int) {
        guard let from = tabs.firstIndex(where: { $0.file.id == id }) else { return }
        stashCurrent(); let selected = active?.file.id
        let tab = tabs.remove(at: from)
        tabs.insert(tab, at: min(tabs.count, max(0, insertion > from ? insertion - 1 : insertion)))
        current = tabs.firstIndex(where: { $0.file.id == selected }) ?? 0
        refreshTabs(); writeRecovery()
    }
    @objc func moveCurrentTabLeft(_ sender: Any?) { if let id = active?.file.id, current > 0 { reorderTab(id: id, insertion: current - 1) } }
    @objc func moveCurrentTabRight(_ sender: Any?) { if let id = active?.file.id, current + 1 < tabs.count { reorderTab(id: id, insertion: current + 2) } }
    @objc private func moveContextLeft(_ item: NSMenuItem) { if let i = contextIndex(item), i > 0 { reorderTab(id: tabs[i].file.id, insertion: i - 1) } }
    @objc private func moveContextRight(_ item: NSMenuItem) { if let i = contextIndex(item), i + 1 < tabs.count { reorderTab(id: tabs[i].file.id, insertion: i + 2) } }
    @objc private func toggleContextRecovery(_ item: NSMenuItem) { if let i = contextIndex(item) { selectTab(i); toggleCurrentDraftRecovery(nil) } }
    @discardableResult private func closeTab(_ index: Int) -> Bool {
        guard tabs.indices.contains(index) else { return true }
        selectTab(index)
        if let file = active?.file, file.isDirty && (!file.text.isEmpty || file.url != nil || file.suggestedName != nil && active?.automaticallyNamed != true) {
            let result = alert("保存“\(active!.file.title)”的修改？", "保存会写回本机文件。", buttons: ["保存", "取消", "不保存"])
            if result == .alertSecondButtonReturn { return false }
            if result == .alertFirstButtonReturn && !saveCurrent() { return false }
        }
        changing = true; tabs.remove(at: index); changing = false
        if tabs.isEmpty { current = 0; if !closeBatch { window?.performClose(nil) } }
        else { selectTab(min(index, tabs.count - 1), stash: false) }
        writeRecovery()
        return true
    }
    private func confirmDocumentsClosure(allowKeep: Bool = false) -> (confirmed: Bool, discarded: Set<UUID>) {
        stashCurrent()
        var discarded = Set<UUID>()
        for index in tabs.indices where tabs[index].file.isDirty && (!tabs[index].file.text.isEmpty || tabs[index].file.url != nil || tabs[index].file.suggestedName != nil && !tabs[index].automaticallyNamed) {
            selectTab(index)
            let tab = tabs[index]
            let canKeep = allowKeep && recoveryEnabled && tab.recoverDraft
            let choices = canKeep ? ["保存", "取消", "保留草稿", "不保存"] : ["保存", "取消", "不保存"]
            let detail = canKeep ? "可保存原文件，或保留本机草稿供下次恢复。" : "此文档没有启用恢复草稿，退出前请保存或明确放弃修改。"
            let result = alert("如何处理“\(tab.file.title)”的修改？", detail, buttons: choices)
            if result == .alertSecondButtonReturn { return (false, []) }
            if result == .alertFirstButtonReturn && !saveCurrent() { return (false, []) }
            if (!canKeep && result == .alertThirdButtonReturn) || (canKeep && result.rawValue == NSApplication.ModalResponse.alertFirstButtonReturn.rawValue + 3) { discarded.insert(tab.file.id) }
        }
        return (true, discarded)
    }
    func confirmQuit() -> Bool {
        if shuttingDown { return true }
        if restoringWorkspace { updateStatus("正在恢复上次会话，请稍候再退出"); return false }
        let outcome = confirmDocumentsClosure(allowKeep: true)
        guard outcome.confirmed else { return false }
        recoveryTask?.cancel()
        if !tabs.isEmpty {
            stashCurrent()
            let snapshot = workspaceSnapshot(discarding: outcome.discarded)
            let result = workspace.saveAndWait(session: snapshot.0, drafts: snapshot.1)
            if !result.errors.isEmpty { updateStatus("会话或草稿保存失败"); showPersistenceErrors(result.errors); return false }
        } else { workspace.flush() }
        shuttingDown = true
        return true
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard !restoringWorkspace else { return false }
        // Closing the document window closes its tabs. Quitting the app preserves
        // the session, so users can choose which behavior they want.
        let outcome = confirmDocumentsClosure()
        guard outcome.confirmed else { return false }
        recoveryTask?.cancel(); searchTask?.cancel(); searchVersion += 1
        tabs.removeAll(); current = 0; reading = false
        changing = true; editor.string = ""; preview.string = ""; changing = false
        refreshTabs(); writeRecovery()
        return true
    }
    override func showWindow(_ sender: Any?) {
        if tabs.isEmpty { newDocument(nil) }
        super.showWindow(sender)
    }
    @objc func nextTab(_ sender: Any?) { if !tabs.isEmpty { selectTab((current + 1) % tabs.count) } }
    @objc func previousTab(_ sender: Any?) { if !tabs.isEmpty { selectTab((current + tabs.count - 1) % tabs.count) } }
    @objc func selectNumberedTab(_ sender: NSMenuItem) {
        if !tabs.isEmpty { selectTab(sender.tag == 8 ? tabs.count - 1 : min(sender.tag, tabs.count - 1)) }
    }
    private func contextIndex(_ item: NSMenuItem) -> Int? { guard let id = item.representedObject as? UUID else { return nil }; return tabs.firstIndex { $0.file.id == id } }
    @objc private func closeContextTab(_ item: NSMenuItem) { if let i = contextIndex(item) { closeTab(i) } }
    @objc private func closeOtherTabs(_ item: NSMenuItem) {
        guard let keep = item.representedObject as? UUID else { return }
        let ids = tabs.map { $0.file.id }.filter { $0 != keep }
        for id in ids.reversed() { if let i = tabs.firstIndex(where: { $0.file.id == id }), !closeTab(i) { break } }
        if let i = tabs.firstIndex(where: { $0.file.id == keep }) { selectTab(i) }
    }
    @objc private func copyContextPath(_ item: NSMenuItem) {
        guard let i = contextIndex(item), let url = tabs[i].file.url else { return }
        NSPasteboard.general.clearContents(); NSPasteboard.general.setString(url.path, forType: .string)
    }
    @objc private func revealContextFile(_ item: NSMenuItem) { if let i = contextIndex(item), let url = tabs[i].file.url { NSWorkspace.shared.activateFileViewerSelecting([url]) } }
    @objc func copyCurrentPath(_ sender: Any?) {
        guard let url = active?.file.url else { return }
        NSPasteboard.general.clearContents(); NSPasteboard.general.setString(url.path, forType: .string)
    }
    @objc func revealCurrentFile(_ sender: Any?) { if let url = active?.file.url { NSWorkspace.shared.activateFileViewerSelecting([url]) } }
    @objc func becameActive() {
        guard !shuttingDown, let tab = active, tab.file.url != nil else { return }
        checkExternalFile(tab)
    }
    @objc func reloadDocument(_ sender: Any?) {
        guard let tab = active, let url = tab.file.url else { return }
        if tab.file.isDirty && alert("重新载入磁盘文件？", "当前尚未保存的修改将被放弃。", buttons: ["取消", "重新载入"]) != .alertSecondButtonReturn { return }
        do { let file = try TextFile.open(url); tabs[current] = ReaderTab(file); selectTab(current, stash: false); writeRecovery() }
        catch { showError(error) }
    }
    func textDidChange(_ notification: Notification) {
        guard !changing, !reading, let tab = active else { return }
        tab.file.text = editor.string
        tab.rendered = nil; tab.renderKey = nil
        scheduleSplitRender()
        updateChangeMarks()
        if !lineIndexUpdated { ruler.updateLines(editor.string) }
        lineIndexUpdated = false; ruler.needsDisplay = true
        refreshTabs(); updateStatus(); scheduleHighlight(); scheduleRecovery(); scheduleSearch()
    }
    func textView(_ textView: NSTextView, shouldChangeTextIn affectedCharRange: NSRange, replacementString: String?) -> Bool {
        guard textView === editor else { return false }
        if !changing, !reading, let replacement = replacementString {
            ruler.applyEdit(range: affectedCharRange, replacement: replacement)
            lineIndexUpdated = true
        }
        return true
    }
    func textViewDidChangeSelection(_ notification: Notification) { if !changing { updateStatus() } }
    @objc private func scrolled() {
        ruler.needsDisplay = true
        if !restoringLocation && liveResizeAnchor == nil { active?.sourceAnchor = currentSourceOffset() }
        if reading { updateStatus(); highlightOutline() }
        if !restoringLocation && liveResizeAnchor == nil { scheduleRecovery() }
    }
    private func cancelFind() { if searchVisible { hideFind() } else if focused { toggleFocus(nil) } }
    func textView(_ textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if commandSelector == #selector(NSResponder.cancelOperation(_:)), !textView.hasMarkedText() { cancelFind(); return true }
        return false
    }
    func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if commandSelector == #selector(NSResponder.cancelOperation(_:)), !textView.hasMarkedText() { cancelFind(); return true }
        return false
    }
    private func updateStatus(_ message: String? = nil) {
        guard let file = active?.file else { return }
        let text = editor.string as NSString
        let location = min(editor.selectedRange().location, text.length)
        var low = 0, high = ruler.starts.count
        while low < high { let mid = (low + high) / 2; if ruler.starts[mid] <= location { low = mid + 1 } else { high = mid } }
        let line = max(1, low)
        // UTF-16 columns avoid scanning very long JSON lines on every selection change.
        let column = location - ruler.starts[max(0, min(line - 1, ruler.starts.count - 1))] + 1
        if reading {
            let view = preview
            let maximum = max(1, view.bounds.height - previewScroll.contentSize.height)
            let progress = min(100, max(0, Int(previewScroll.contentView.bounds.origin.y / maximum * 100)))
            statusRight.stringValue = "阅读 · \(progress)%"
        } else { statusRight.stringValue = "行 \(line)，列 \(column)" }
        statusLeft.stringValue = message ?? "\(file.isDirty ? "未保存" : "已保存") · \(file.url?.path ?? "新建本机文本")"
        statusLeft.toolTip = file.url?.path
        formatButton.isEnabled = active != nil && !restoringWorkspace
    }
    private func scheduleHighlight() {
        highlightTask?.cancel(); generation += 1
        let token = generation
        let task = DispatchWorkItem { [weak self] in
            guard let self = self, self.generation == token, !self.reading, let tab = self.active, let storage = self.editor.textStorage, !self.editor.hasMarkedText() else { return }
            self.changing = true
            if storage.length <= 200_000 {
                TextFeatures.highlight(storage, kind: tab.kind, palette: self.theme.palette, font: self.editorFont())
                if let diagnostic = TextFeatures.diagnostic(tab.file.text, kind: tab.kind) { self.updateStatus(diagnostic) }
            } else {
                storage.addAttributes([.font: self.editorFont(), .foregroundColor: self.theme.text], range: NSRange(location: 0, length: storage.length))
                self.updateStatus("大文本模式 · 已暂停高亮与实时检查")
            }
            self.editor.typingAttributes = [.font: self.editorFont(), .foregroundColor: self.theme.text]
            self.changing = false
            self.paintSearchMatches()
        }
        highlightTask = task; DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: task)
    }
    @objc func systemAppearanceChanged() { if followSystemAppearance { applyTheme() } }
    @objc func toggleTheme(_ sender: Any?) {
        followSystemAppearance = false; dark.toggle()
        UserDefaults.standard.set(false, forKey: "followSystemAppearance"); UserDefaults.standard.set(dark, forKey: "dark"); applyTheme()
    }
    @objc func toggleSidebar(_ sender: Any?) {
        let anchor = currentSourceOffset()
        showingSidebar.toggle(); updateModeControls()
        if reading { updateReadingWidth(); scrollToSource(anchor) } else { updateWrap() }
    }
    @objc func toggleFocus(_ sender: Any?) {
        let anchor = currentSourceOffset(); focused.toggle(); updateModeControls(); updateReadingWidth(); scrollToSource(anchor)
    }
    private func updateModeControls() {
        let hideSidebar = focused || !showingSidebar
        sidebar.isHidden = hideSidebar
        sidebarToggleButton.title = ""; sidebarToggleButton.setAccessibilityLabel(showingSidebar ? "收起侧栏" : "展开侧栏")
        sidebarToggleButton.toolTip = (showingSidebar ? "收起侧栏" : "展开侧栏") + " · ⌘B"
        menuRow.isHidden = focused; menuRowHeight.constant = focused ? 0 : 30
        canvasInsetLeading.isActive = !hideSidebar; canvasFullLeading.isActive = hideSidebar
        tabsHeight.constant = focused ? 0 : 44; tabRow.isHidden = focused
        headerHeight.constant = 78; branding.isHidden = false
        focusButton.title = focused ? "退出专注" : "专注"
        readButton.title = reading ? "编辑原文" : "返回预览"
        let kind = active?.kind
        let md = kind == .markdown, json = kind == .json || kind == .jsonc
        splitButton.isHidden = !md; splitButton.title = active?.splitPreview == true ? "关闭分屏" : "分屏编辑"
        structureButton.isHidden = !json
        toolbarCopyButton.isHidden = !md || (!reading && active?.splitPreview != true)
        toolbarOptions.isHidden = !md
        returnPositionButton.isEnabled = active?.navigation.isEmpty == false
        formatButton.title = kind == .plain ? "整理 TXT" : "格式化"
        presentationHint.stringValue = md ? (reading ? "Markdown 预览 · 需要修改时点击编辑原文" : active?.splitPreview == true ? "左侧编辑 · 右侧预览" : "Markdown 原文 · 可开启分屏编辑") : ""
        kindPopup.isHidden = false; codecPopup.isHidden = false; endingPopup.isHidden = false; statusLeft.isHidden = false
        replaceBar.isHidden = !replacementVisible
        editorScroll.isHidden = reading; previewScroll.isHidden = !reading && active?.splitPreview != true
        ruler.isHidden = reading || !showLineNumbers; rulerWidth.constant = reading || !showLineNumbers ? 0 : 48
        window?.contentView?.layoutSubtreeIfNeeded(); updateContextToolbar()
        layoutDocumentPanes(); updateSidebarTabs()
    }
    private func updateContextToolbar() {
        let width = max(100, canvas.bounds.width - 24)
        let height = tools.arrange(width: width)
        if abs(toolsHeight.constant - (height + 16)) > 0.5 { toolsHeight.constant = height + 16 }
    }
    private func updateSidebarTabs() {
        clearRecentButton.isHidden = outlineMode
        for (button, selected) in [(sidebarRecentButton, !outlineMode), (sidebarOutlineButton, outlineMode)] {
            guard let button = button else { continue }; button.fill = selected ? theme.panel : theme.window; button.ink = selected ? theme.text : theme.muted
            button.borderColor = .clear; button.bottomAccent = selected ? theme.accent : .clear; button.hoverFill = theme.hover
        }
    }
    private func setSidebarOutline(_ value: Bool) { outlineMode = value; refreshRecent() }
    @objc func toggleWrap(_ sender: Any?) { wrapping.toggle(); UserDefaults.standard.set(wrapping, forKey: "wrapping"); wrapButton.title = wrapping ? "自动换行 ✓" : "自动换行"; updateWrap() }
    private func updateWrap() {
        editorScroll.hasHorizontalScroller = !wrapping && !reading
        editor.isHorizontallyResizable = !wrapping
        editor.autoresizingMask = wrapping ? [.width] : []
        editor.textContainer?.widthTracksTextView = wrapping
        editor.textContainer?.containerSize = NSSize(width: wrapping ? max(100, editorScroll.contentSize.width - 36) : 1_000_000, height: CGFloat.greatestFiniteMagnitude)
        if wrapping { editor.setFrameSize(NSSize(width: editorScroll.contentSize.width, height: max(editorScroll.contentSize.height, editor.frame.height))) }
        ruler.needsDisplay = true
    }
    private func changeFont(_ delta: CGFloat) {
        active?.sourceAnchor = currentSourceOffset()
        fontSize = min(30, max(10, fontSize + delta)); UserDefaults.standard.set(Double(fontSize), forKey: "fontSize")
        editor.font = editorFont(); scheduleHighlight(); if reading || active?.splitPreview == true { renderReading() }
    }
    @objc func increaseFont(_ sender: Any?) { changeFont(1) }
    @objc func decreaseFont(_ sender: Any?) { changeFont(-1) }
    @objc func validateDocument(_ sender: Any?) {
        guard let tab = active else { return }
        let diagnostic = TextFeatures.diagnostic(tab.file.text, kind: tab.kind)
        let supported = tab.kind == .json || tab.kind == .jsonc
        updateStatus(diagnostic ?? (supported ? "检查通过" : tab.kind == .yaml ? "未发现缩进 Tab（仅基础检查）" : "此类型没有完整校验器"))
    }
    @objc func toggleLineNumbers(_ sender: Any?) {
        showLineNumbers.toggle(); UserDefaults.standard.set(showLineNumbers, forKey: "lineNumbers"); updateModeControls()
    }
    @objc func changeReadingWidth(_ sender: NSMenuItem) {
        let anchor = currentSourceOffset(); readingWidth = CGFloat(sender.tag == 5000 ? 1_000_000 : sender.tag)
        UserDefaults.standard.set(Double(readingWidth), forKey: "readingWidth"); updateReadingWidth(); scrollToSource(anchor)
    }
    @objc func closeOtherDocuments(_ sender: Any?) {
        guard let id = active?.file.id else { return }; let item = NSMenuItem(); item.representedObject = id; closeOtherTabs(item)
    }
    @objc func showDiagnostics(_ sender: Any?) {
        let details = ["Mac 0.1.0 · 构建 6", "原生 AppKit / TextKit", "打开文件上限 50 MiB，富预览上限 50 万 UTF-16 单元", "当前标签数：\(tabs.count)"] + restoreNotices + (active?.mediaNotices ?? [])
        _ = alert("打开与阅读诊断", details.joined(separator: "\n"), buttons: ["关闭"])
    }
    @objc func showPreferences(_ sender: Any?) {
        let a = NSAlert(); a.messageText = "阅读与编辑设置"; a.addButton(withTitle: "应用"); a.addButton(withTitle: "取消")
        let numbers = NSButton(checkboxWithTitle: "编辑时显示行号", target: nil, action: nil); numbers.state = showLineNumbers ? .on : .off
        let wrap = NSButton(checkboxWithTitle: "自动换行", target: nil, action: nil); wrap.state = wrapping ? .on : .off
        let size = NSPopUpButton(); size.addItems(withTitles: (10...30).map(String.init)); size.selectItem(withTitle: String(Int(fontSize)))
        let face = NSPopUpButton(); face.addItems(withTitles: ["苹方", "宋体", "系统字体"]); face.selectItem(at: readingFontName == "Songti SC" ? 1 : (readingFontName == "system" ? 2 : 0))
        let spacing = NSPopUpButton(); spacing.addItems(withTitles: ["紧凑", "舒适", "宽松"]); spacing.selectItem(at: readingLineSpacing < 4 ? 0 : (readingLineSpacing > 8 ? 2 : 1))
        let systemTheme = NSButton(checkboxWithTitle: "外观跟随系统", target: nil, action: nil); systemTheme.state = followSystemAppearance ? .on : .off
        let width = NSPopUpButton(); width.addItems(withTitles: ["紧凑", "标准", "宽", "全宽"])
        width.selectItem(at: readingWidth > 1000 ? 3 : (readingWidth < 600 ? 0 : (readingWidth > 750 ? 2 : 1)))
        let content = NSStackView(views: [numbers, wrap, systemTheme, stack([label("字号"), size]), stack([label("阅读字体"), face]), stack([label("阅读行距"), spacing]), stack([label("阅读版心"), width])])
        content.orientation = .vertical; content.alignment = .leading; content.spacing = 12; content.frame = NSRect(x: 0, y: 0, width: 320, height: 260)
        a.accessoryView = content
        guard runDocumentModal({ a.runModal() }) == .alertFirstButtonReturn else { return }
        active?.sourceAnchor = currentSourceOffset()
        showLineNumbers = numbers.state == .on; wrapping = wrap.state == .on
        fontSize = CGFloat(Int(size.titleOfSelectedItem ?? "14") ?? 14)
        readingFontName = ["PingFang SC", "Songti SC", "system"][face.indexOfSelectedItem]
        readingLineSpacing = [2, 6, 11][spacing.indexOfSelectedItem]
        readingWidth = [520, 680, 840, 1_000_000][width.indexOfSelectedItem]
        followSystemAppearance = systemTheme.state == .on
        let d = UserDefaults.standard
        d.set(showLineNumbers, forKey: "lineNumbers"); d.set(wrapping, forKey: "wrapping"); d.set(Double(fontSize), forKey: "fontSize")
        d.set(readingFontName, forKey: "readingFont"); d.set(Double(readingLineSpacing), forKey: "lineSpacing")
        d.set(Double(readingWidth), forKey: "readingWidth"); d.set(followSystemAppearance, forKey: "followSystemAppearance")
        ruler.isHidden = reading || !showLineNumbers; rulerWidth.constant = reading || !showLineNumbers ? 0 : 48
        wrapButton.title = wrapping ? "自动换行 ✓" : "自动换行"; window?.contentView?.layoutSubtreeIfNeeded()
        editor.font = editorFont(); updateWrap(); applyTheme()
    }
    @objc func toggleReading(_ sender: Any?) {
        guard let tab = active else { return }
        let anchor = currentSourceOffset()
        pendingFind = nil
        stashCurrent(); reading.toggle(); tab.isReading = reading; tab.sourceAnchor = anchor
        updateModeControls()
        if reading || active?.splitPreview == true { renderReading() } else { editor.font = editorFont(); updateWrap(); scheduleHighlight() }
        scrollToSource(anchor); scheduleSearch()
        window?.makeFirstResponder(reading ? preview : editor); updateStatus()
    }
    private var displayedTextView: NSTextView { reading ? preview : editor }
    private func visibleOffset(in view: NSTextView) -> Int {
        guard let lm = view.layoutManager, let tc = view.textContainer, !view.string.isEmpty else { return 0 }
        let rect = view.visibleRect.offsetBy(dx: -view.textContainerOrigin.x, dy: -view.textContainerOrigin.y)
        let glyphs = lm.glyphRange(forBoundingRect: rect, in: tc)
        let visible = lm.characterRange(forGlyphRange: glyphs, actualGlyphRange: nil)
        let selected = view.selectedRange()
        if selected.location >= visible.location && selected.location <= NSMaxRange(visible) { return selected.location }
        let top = NSPoint(x: 2, y: max(0, rect.minY + 5))
        return min(lm.characterIndex(for: top, in: tc, fractionOfDistanceBetweenInsertionPoints: nil), view.string.utf16.count)
    }
    private func currentSourceOffset() -> Int {
        let offset = visibleOffset(in: displayedTextView)
        return reading ? (active?.rendered?.sourceRange(forDisplayOffset: offset).location ?? offset) : offset
    }
    private func scrollToSource(_ offset: Int) {
        guard let tab = active else { return }
        let view = displayedTextView
        let point = reading ? (tab.rendered?.displayRange(forSourceOffset: offset).location ?? offset) : offset
        let location = max(0, min(point, view.string.utf16.count))
        guard let lm = view.layoutManager else { return }
        restoringLocation = true
        defer { restoringLocation = false }
        lm.ensureLayout(forCharacterRange: NSRange(location: location, length: 0))
        view.setSelectedRange(NSRange(location: location, length: 0))
        let y: CGFloat
        if location == view.string.utf16.count {
            if !lm.extraLineFragmentRect.isEmpty { y = lm.extraLineFragmentRect.minY }
            else if location > 0 { y = lm.lineFragmentRect(forGlyphAt: lm.glyphIndexForCharacter(at: location - 1), effectiveRange: nil).minY }
            else { y = 0 }
        }
        else {
            let glyph = lm.glyphIndexForCharacter(at: location)
            y = lm.lineFragmentRect(forGlyphAt: glyph, effectiveRange: nil).minY
        }
        let scroll = reading ? previewScroll : editorScroll
        scroll.contentView.scroll(to: NSPoint(x: 0, y: max(0, y + view.textContainerOrigin.y - 6)))
        scroll.reflectScrolledClipView(scroll.contentView)
        tab.sourceAnchor = min(max(0, offset), tab.file.text.utf16.count)
    }
    private func updateReadingWidth() {
        guard reading || active?.splitPreview == true else { return }
        let available = max(100, previewScroll.contentSize.width)
        let width = min(max(100, available - 48), readingWidth)
        let margin = max(24, (available - width) / 2)
        preview.textContainerInset = NSSize(width: margin, height: 26)
        preview.setFrameSize(NSSize(width: available, height: max(previewScroll.contentSize.height, preview.frame.height)))
        preview.textContainer?.widthTracksTextView = false
        preview.textContainer?.containerSize = NSSize(width: width, height: CGFloat.greatestFiniteMagnitude)
        if let storage = preview.textStorage {
            ReadingMediaLoader.resizeAttachments(in: storage, maxWidth: width)
            storage.enumerateAttribute(.attachment, in: NSRange(location: 0, length: storage.length)) { value, _, _ in
                if let attachment = value as? NSTextAttachment, !(attachment is ReadingImageAttachment), let image = attachment.image {
                    let scaled = min(width, image.size.width)
                    attachment.bounds = NSRect(x: 0, y: 0, width: scaled, height: image.size.height * scaled / max(1, image.size.width))
                }
            }
        }
    }
    func windowWillStartLiveResize(_ notification: Notification) { liveResizeAnchor = currentSourceOffset() }
    func windowDidEndLiveResize(_ notification: Notification) {
        if let anchor = liveResizeAnchor, reading { updateReadingWidth(); scrollToSource(anchor) }
        liveResizeAnchor = nil
    }
    func windowDidResize(_ notification: Notification) {
        if toolsHeight != nil { updateContextToolbar() }
        layoutDocumentPanes()
        guard reading, let anchor = liveResizeAnchor ?? active?.sourceAnchor else { updateWrap(); return }
        updateReadingWidth(); scrollToSource(anchor)
    }
    private func renderReading() {
        guard let tab = active else { return }
        presentationGeneration += 1
        let anchor = tab.sourceAnchor
        let key = "\(tab.kind.rawValue)|\(fontSize)|\(readingFontName)|\(readingLineSpacing)|\(dark)|\(tab.file.url?.path ?? "")"
        if tab.renderKey != key || tab.rendered == nil {
            tab.mediaGeneration += 1; tab.mediaTask?.cancel(); tab.mediaTask = nil; tab.mediaNotices = []
            tab.rendered = ReadingDocument.build(text: tab.file.text, kind: tab.kind, palette: theme.palette, fontSize: fontSize + 1, readingFontName: readingFontName, lineSpacing: readingLineSpacing)
            tab.renderKey = key
            if tab.kind == .markdown, let document = tab.rendered {
                let diagrams = addSequenceDiagrams(document.attributedText, source: tab.file.text)
                tab.rendered = document.replacingPresentation(diagrams)
            }
        }
        guard let document = tab.rendered else { return }
        let presentationChanged = installedPresentation !== document.attributedText
        if presentationChanged {
            preview.textStorage?.setAttributedString(document.attributedText); installedPresentation = document.attributedText
            updateReadingWidth()
            if reading { scrollToSource(anchor) }
        } else { updateReadingWidth() }
        if tab.kind == .markdown, tab.mediaTask == nil {
            tab.mediaGeneration += 1; let mediaGeneration = tab.mediaGeneration
            tab.mediaTask = ReadingMediaLoader.load(document: document.attributedText, baseURL: tab.file.url, maxWidth: min(readingWidth, max(100, previewScroll.contentSize.width - 48))) { [weak self, weak tab] text, notices in
                guard let self = self, let tab = tab, tab.renderKey == key, tab.mediaGeneration == mediaGeneration, self.tabs.contains(where: { $0 === tab }) else { return }
                tab.mediaNotices = Array(Set(tab.mediaNotices + notices)).sorted()
                tab.rendered = document.replacingPresentation(text)
                if self.active === tab, tab.renderKey == key {
                    let position = self.currentSourceOffset()
                    self.preview.textStorage?.setAttributedString(text); self.updateReadingWidth()
                    self.installedPresentation = tab.rendered?.attributedText
                    if self.reading { self.scrollToSource(position) }; self.scheduleSearch()
                    if !tab.mediaNotices.isEmpty { self.updateStatus(tab.mediaNotices.joined(separator: "；")) }
                }
            }
        }
        if outlineMode { refreshRecent() }
        if presentationChanged { scheduleSearch() }
    }
    private func refreshAutomaticKind(_ tab: ReaderTab) {
        tab.updateKindAfterPathChange()
        kindPopup.selectItem(at: tab.automaticKind ? 0 : (TextKind.allCases.firstIndex(of: tab.kind) ?? 0) + 1)
        tab.rendered = nil; tab.renderKey = nil; tab.mediaGeneration += 1; tab.mediaTask?.cancel(); tab.mediaTask = nil
        if reading || active?.splitPreview == true { renderReading() } else { scheduleHighlight() }
    }
    @objc private func kindChanged() {
        guard let tab = active else { return }
        let index = kindPopup.indexOfSelectedItem
        tab.automaticKind = index == 0
        if index > 0 { tab.kind = TextKind.allCases[index - 1] }
        refreshAutomaticKind(tab); updateStatus()
    }
    @objc private func codecChanged() {
        guard let file = active?.file else { return }
        let chosen = TextCodec.allCases[codecPopup.indexOfSelectedItem]
        if chosen == file.codec { return }
        let result = alert("保存编码改为 \(chosen.label)？", "这会在下次保存时转换文件编码。若只是打开后发现乱码，请取消并从“文件”菜单选择按编码重新打开。", buttons: ["转换编码", "取消"])
        if result == .alertFirstButtonReturn { file.codec = chosen; refreshTabs(); scheduleRecovery(); updateStatus("编码已选择，下次保存生效") }
        else { codecPopup.selectItem(at: TextCodec.allCases.firstIndex(of: file.codec) ?? 0) }
    }
    @objc private func endingChanged() {
        guard let file = active?.file else { return }
        let chosen: [LineEnding] = [.lf, .crlf, .cr, .mixed]
        if endingPopup.indexOfSelectedItem == 3 && file.lineEnding != .mixed { endingPopup.selectItem(at: chosen.firstIndex(of: file.lineEnding) ?? 0); return }
        file.lineEnding = chosen[endingPopup.indexOfSelectedItem]; refreshTabs(); scheduleRecovery(); updateStatus("换行符已选择，下次保存生效")
    }
    @objc func reopenEncoding(_ sender: NSMenuItem) {
        guard let tab = active, let url = tab.file.url, TextCodec.allCases.indices.contains(sender.tag) else { return }
        if tab.file.isDirty && alert("按编码重新打开？", "当前未保存的内容将被替换。", buttons: ["取消", "重新打开"]) != .alertSecondButtonReturn { return }
        do { tabs[current] = ReaderTab(try TextFile.open(url, codec: TextCodec.allCases[sender.tag])); selectTab(current, stash: false); writeRecovery() } catch { showError(error) }
    }
    @objc func formatJSON(_ sender: Any?) {
        guard !reading, let tab = active, tab.kind == .json else { return }
        do {
            let result = try TextFeatures.formatJSON(editor.string)
            let range = NSRange(location: 0, length: (editor.string as NSString).length)
            if editor.shouldChangeText(in: range, replacementString: result) { editor.replaceCharacters(in: range, with: result); editor.didChangeText() }
        } catch { showError(error) }
    }

    @objc func showFind(_ sender: Any?) {
        if !searchVisible { searchReturnOffset = currentSourceOffset() }
        replacementVisible = false; showSearchPanel()
    }
    @objc func showReplace(_ sender: Any?) {
        if !searchVisible { searchReturnOffset = currentSourceOffset() }
        replacementVisible = true; showSearchPanel()
    }
    private func showSearchPanel() {
        searchVisible = true; searchContainer.isHidden = false; searchBar.isHidden = false; searchOptionsRow.isHidden = false
        replaceBar.isHidden = !replacementVisible; searchHeight.constant = replacementVisible ? 110 : 76
        if displayedTextView.selectedRange().length > 0 { searchField.stringValue = (displayedTextView.string as NSString).substring(with: displayedTextView.selectedRange()) }
        scheduleSearch(); window?.makeFirstResponder(searchField)
    }
    private func hideFind() {
        searchVisible = false; replacementVisible = false; searchContainer.isHidden = true; searchBar.isHidden = true; searchOptionsRow.isHidden = true; replaceBar.isHidden = true; searchHeight.constant = 0
        searchVersion += 1; searchTask?.cancel(); searchMatches = []; searchReady = false; pendingFind = nil
        paintSearchMatches(); if let offset = searchReturnOffset { scrollToSource(offset) }; searchReturnOffset = nil
        window?.makeFirstResponder(displayedTextView)
    }
    @objc private func searchOptionsChanged() { scheduleSearch() }
    func controlTextDidChange(_ obj: Notification) { scheduleSearch() }
    private func scheduleSearch() {
        searchTask?.cancel(); searchVersion += 1; searchReady = false; searchMatches = []; searchIndex = -1
        paintSearchMatches()
        guard searchVisible, let tab = active else { return }
        let query = searchField.stringValue
        if query.isEmpty { findCounter.stringValue = "输入查找内容"; return }
        let version = searchVersion, id = tab.file.id, mode = reading, source = displayedTextView.string, replacement = reading ? "" : replaceField.stringValue
        var options = SearchOptions(); options.caseSensitive = caseCheck.state == .on; options.regularExpression = regexCheck.state == .on; options.wholeWord = wordCheck.state == .on
        findCounter.stringValue = "正在查找…"; findCounter.textColor = theme.muted
        let task = DispatchWorkItem { [weak self] in
            guard let self = self else { return }
            self.searchQueue.async { [weak self] in
                let result = Result { try TextSearch.matches(in: source, query: query, replacement: replacement, options: options) }
                DispatchQueue.main.async {
                    guard let self = self, self.searchVersion == version, self.active?.file.id == id, self.reading == mode, self.searchVisible else { return }
                    switch result {
                    case .success(let matches):
                        self.searchMatches = matches; self.searchReady = true; self.findCounter.stringValue = "共 \(matches.count) 处"
                        self.paintSearchMatches()
                        if self.pendingFind == nil && !matches.isEmpty { self.find(backward: false) }
                        if let backward = self.pendingFind { self.pendingFind = nil; self.find(backward: backward) }
                    case .failure(let error): self.findCounter.stringValue = error.localizedDescription; self.findCounter.textColor = .systemRed; self.pendingFind = nil
                    }
                }
            }
        }
        searchTask = task; DispatchQueue.main.asyncAfter(deadline: .now() + 0.22, execute: task)
    }
    private func paintSearchMatches() {
        for view in [editor, preview] {
            view.layoutManager?.removeTemporaryAttribute(.backgroundColor, forCharacterRange: NSRange(location: 0, length: view.string.utf16.count))
        }
        guard searchVisible, let lm = displayedTextView.layoutManager else { return }
        let length = displayedTextView.string.utf16.count
        for match in searchMatches.prefix(3000) where match.range.length > 0 && NSMaxRange(match.range) <= length {
            lm.addTemporaryAttribute(.backgroundColor, value: theme.accent.withAlphaComponent(0.18), forCharacterRange: match.range)
        }
    }
    @objc func findNext(_ sender: Any?) { find(backward: false) }
    @objc func findPrevious(_ sender: Any?) { find(backward: true) }
    private func find(backward: Bool) {
        if !searchVisible { showFind(nil) }
        guard !searchField.stringValue.isEmpty else { return }
        guard searchReady else { pendingFind = backward; return }
        guard !searchMatches.isEmpty else { updateStatus("没有找到匹配内容"); return }
        let selected = displayedTextView.selectedRange()
        if searchMatches.indices.contains(searchIndex), searchMatches[searchIndex].range == selected {
            searchIndex = (searchIndex + (backward ? searchMatches.count - 1 : 1)) % searchMatches.count
        } else if backward {
            searchIndex = searchMatches.lastIndex(where: { $0.range.location < selected.location }) ?? searchMatches.count - 1
        } else {
            searchIndex = searchMatches.firstIndex(where: { $0.range.location >= NSMaxRange(selected) }) ?? 0
        }
        let match = searchMatches[searchIndex].range
        displayedTextView.setSelectedRange(match); displayedTextView.scrollRangeToVisible(match)
        findCounter.stringValue = "\(searchIndex + 1) / \(searchMatches.count) 处"
    }
    @objc func replaceOne(_ sender: Any?) {
        if reading { toggleReading(nil); pendingFind = false; return }
        guard searchReady else { return }
        let selected = editor.selectedRange()
        let match = searchMatches.first(where: { $0.range == selected })
        guard let match = match else { find(backward: false); return }
        if match.range.length == 0 && match.replacement.isEmpty { find(backward: false); return }
        editor.breakUndoCoalescing()
        if editor.shouldChangeText(in: match.range, replacementString: match.replacement) {
            editor.replaceCharacters(in: match.range, with: match.replacement); editor.didChangeText()
            editor.setSelectedRange(NSRange(location: match.range.location + match.replacement.utf16.count, length: 0))
            pendingFind = false
        }
    }
    @objc func replaceAll(_ sender: Any?) {
        if reading { toggleReading(nil); pendingFind = false; return }
        guard searchReady, !searchMatches.isEmpty else { return }
        let count = searchMatches.count
        if count > 1000 && alert("替换全部 \(count) 处？", "这会修改当前文件，保存前仍可按 ⌘Z 撤销。", buttons: ["替换", "取消"]) != .alertFirstButtonReturn { return }
        let original = editor.string
        let result = TextSearch.replacingAll(in: original, matches: searchMatches)
        if result.utf8.elementsEqual(original.utf8) { updateStatus("替换前后内容相同"); return }
        let range = NSRange(location: 0, length: (original as NSString).length)
        editor.breakUndoCoalescing()
        if editor.shouldChangeText(in: range, replacementString: result) {
            editor.replaceCharacters(in: range, with: result); editor.didChangeText(); editor.breakUndoCoalescing()
            updateStatus("已替换 \(count) 处 · ⌘Z 撤销")
        }
    }
    @objc func goToLine(_ sender: Any?) {
        if reading { toggleReading(nil) }
        let a = NSAlert(); a.messageText = "跳转到行"; a.addButton(withTitle: "跳转"); a.addButton(withTitle: "取消")
        let input = NSTextField(frame: NSRect(x: 0, y: 0, width: 220, height: 26)); input.placeholderString = "行号，如 42"; a.accessoryView = input
        guard runDocumentModal({ a.runModal() }) == .alertFirstButtonReturn, let number = Int(input.stringValue), number > 0 else { return }
        let starts = ruler.starts; let index = min(number - 1, starts.count - 1)
        editor.setSelectedRange(NSRange(location: starts[index], length: 0)); editor.scrollRangeToVisible(editor.selectedRange()); window?.makeFirstResponder(editor)
    }
    @objc func showOutline(_ sender: Any?) {
        guard let tab = active else { return }
        if tab.rendered == nil { tab.rendered = ReadingDocument.build(text: tab.file.text, kind: tab.kind, palette: theme.palette, fontSize: fontSize + 1, readingFontName: readingFontName, lineSpacing: readingLineSpacing) }
        let headings = tab.rendered?.headings ?? []
        guard !headings.isEmpty else { updateStatus("当前文档没有可识别的目录标题"); return }
        let menu = NSMenu()
        for heading in headings.prefix(500) {
            let item = NSMenuItem(title: heading.title, action: #selector(jumpHeading(_:)), keyEquivalent: "")
            item.target = self; item.representedObject = heading.sourceRange.location; menu.addItem(item)
        }
        menu.popUp(positioning: nil, at: NSPoint(x: 20, y: 0), in: focused ? header : tools)
    }
    @objc private func jumpHeading(_ item: NSMenuItem) {
        guard let location = item.representedObject as? Int else { return }
        pushNavigation(); scrollToSource(location); window?.makeFirstResponder(displayedTextView); updateStatus()
    }
    @objc func showGuide(_ sender: Any?) {
        if let url = Bundle.main.url(forResource: "使用说明", withExtension: "md"), let text = try? String(contentsOf: url, encoding: .utf8) {
            let alert = NSAlert(); alert.messageText = "轻阅 · 使用说明"; alert.addButton(withTitle: "关闭")
            let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 620, height: 420))
            scroll.hasVerticalScroller = true
            let textView = NSTextView(frame: scroll.bounds); textView.isEditable = false
            textView.isVerticallyResizable = true; textView.autoresizingMask = [.width]
            textView.textContainer?.widthTracksTextView = true; textView.textContainerInset = NSSize(width: 12, height: 12)
            textView.backgroundColor = theme.editor
            textView.textStorage?.setAttributedString(TextFeatures.markdownPreview(text, palette: theme.palette, fontSize: 13))
            scroll.documentView = textView; alert.accessoryView = scroll; runDocumentModal({ alert.runModal() })
        }
    }
    private func scheduleRecovery() {
        guard !restoringWorkspace && !shuttingDown else { return }
        recoveryTask?.cancel()
        let task = DispatchWorkItem { [weak self] in self?.writeRecovery() }; recoveryTask = task
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5, execute: task)
    }
    private func workspaceSnapshot(discarding ids: Set<UUID> = []) -> (WorkspaceSession, [TextFile.RecoverySnapshot]) {
        let visible = tabs.filter { !ids.contains($0.file.id) || $0.file.url != nil }
        let entries = visible.map { tab in
            WorkspaceEntry(id: tab.file.id, path: tab.file.url?.path, isReading: tab.isReading, sourceAnchor: tab.sourceAnchor,
                           selectionLocation: tab.selection.location, selectionLength: tab.selection.length,
                           automaticKind: tab.automaticKind, kindRaw: tab.kind.rawValue, recoverDraft: tab.recoverDraft)
        }
        let drafts = recoveryEnabled ? visible.filter { !ids.contains($0.file.id) && $0.recoverDraft && $0.file.isDirty && (!$0.file.text.isEmpty || $0.file.url != nil || $0.file.suggestedName != nil && !$0.automaticallyNamed) }.map { $0.file.recoverySnapshot() } : []
        let draftIDs = Set(drafts.map { $0.id })
        let storedEntries = restoreSessionEnabled ? entries : entries.filter { draftIDs.contains($0.id) }
        return (WorkspaceSession(version: 1, entries: storedEntries, activeID: storedEntries.contains(where: { $0.id == active?.file.id }) ? active?.file.id : nil), drafts)
    }
    private func writeRecovery() {
        guard !shuttingDown && !restoringWorkspace else { return }
        stashCurrent()
        let value = workspaceSnapshot()
        persistenceGeneration += 1; let generation = persistenceGeneration
        workspace.save(session: value.0, drafts: value.1) { [weak self] result in
            guard let self = self, generation == self.persistenceGeneration, !self.shuttingDown else { return }
            if result.errors.isEmpty { self.lastDraftSave = result.savedAt }
            else { self.updateStatus("本机会话/草稿部分保存失败 · 请保存原文件"); self.statusLeft.toolTip = result.errors.joined(separator: "\n") }
        }
    }
    private func restoreWorkspace() {
        restoringWorkspace = true; editor.isEditable = false; updateStatus("正在恢复上次会话…")
        workspace.load { [weak self] result in
            guard let self = self else { return }
            let loaded = result.entries.filter { self.restoreSessionEnabled || $0.file.isDirty }
            if !loaded.isEmpty {
                self.tabs = loaded.map { loaded in
                    let tab = ReaderTab(loaded.file), entry = loaded.entry
                    tab.isReading = entry.isReading; tab.sourceAnchor = max(0, min(entry.sourceAnchor, tab.file.text.utf16.count)); tab.hasWorkspacePosition = true
                    tab.selection = NSRange(location: max(0, entry.selectionLocation), length: max(0, entry.selectionLength))
                    tab.automaticKind = entry.automaticKind
                    if !entry.automaticKind { tab.kind = TextKind(rawValue: entry.kindRaw) ?? tab.kind }
                    tab.recoverDraft = entry.recoverDraft && !(tab.file.url.map { self.excludedDraftPaths.contains($0.path) } ?? false)
                    return tab
                }
                self.current = loaded.firstIndex(where: { $0.entry.id == result.activeID }) ?? 0
            }
            self.restoringWorkspace = false; self.editor.isEditable = true
            if self.tabs.isEmpty { self.newDocument(nil) } else { self.selectTab(min(self.current, self.tabs.count - 1), stash: false) }
            self.restoreNotices = result.warnings
            if !result.warnings.isEmpty { self.updateStatus("部分会话未能恢复 · 文件菜单可查看详情") }
            else if !loaded.isEmpty { self.updateStatus("已恢复 \(loaded.count) 个标签") }
            let urls = self.deferredOpenURLs; self.deferredOpenURLs = []
            if !urls.isEmpty { self.openURLs(urls) }
            if self.pendingNewDocument { self.pendingNewDocument = false; self.newDocument(nil) }
        }
    }
    private func showPersistenceErrors(_ messages: [String]) {
        _ = alert("本机状态未完整保存", messages.joined(separator: "\n"), buttons: ["知道了"])
    }
    private func layoutDocumentPanes() {
        guard !layingOutPanes else { return }
        layingOutPanes = true
        defer { layingOutPanes = false }
        let size = documentSplit.bounds.size
        guard size.width > 0 else { return }
        let both = !reading && active?.splitPreview == true
        if both {
            let width = max(100, (size.width - documentSplit.dividerThickness) * splitFraction)
            editorScroll.frame = NSRect(x: 0, y: 0, width: width, height: size.height)
            previewScroll.frame = NSRect(x: width + documentSplit.dividerThickness, y: 0, width: max(100, size.width - width - documentSplit.dividerThickness), height: size.height)
        } else {
            editorScroll.frame = NSRect(origin: .zero, size: size)
            previewScroll.frame = NSRect(origin: .zero, size: size)
        }
        if both { documentSplit.setPosition(max(100, size.width * splitFraction), ofDividerAt: 0) }
        updateWrap(); updateReadingWidth()
    }
    func splitViewDidResizeSubviews(_ notification: Notification) {
        if !layingOutPanes && !reading && active?.splitPreview == true && documentSplit.bounds.width > 0 {
            splitFraction = min(0.8, max(0.2, editorScroll.frame.width / documentSplit.bounds.width))
        }
        updateWrap(); updateReadingWidth()
    }
    @objc func toggleSplitPreview(_ sender: Any?) {
        guard let tab = active, tab.kind == .markdown else { return }
        if reading { toggleReading(nil) }
        tab.splitPreview.toggle(); updateModeControls()
        if tab.splitPreview { renderReading() }
        splitButton.title = tab.splitPreview ? "关闭分屏" : "分屏编辑"
    }
    private func scheduleSplitRender() {
        splitTask?.cancel(); active?.mediaGeneration += 1; active?.mediaTask?.cancel(); active?.mediaTask = nil
        guard active?.splitPreview == true else { return }
        let task = DispatchWorkItem { [weak self] in guard let self = self, !self.editor.hasMarkedText() else { return }; self.renderReading() }
        splitTask = task; DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: task)
    }
    private func pushNavigation() {
        guard let tab = active else { return }
        tab.navigation.append(currentSourceOffset()); if tab.navigation.count > 64 { tab.navigation.removeFirst() }; returnPositionButton.isEnabled = true
    }
    @objc func returnReadingPosition(_ sender: Any?) {
        guard let offset = active?.navigation.popLast() else { return }
        scrollToSource(offset); returnPositionButton.isEnabled = active?.navigation.isEmpty == false; updateStatus("已返回跳转前位置")
    }
    @objc private func sidebarModeChanged() { outlineMode = sidebarMode.selectedSegment == 0; refreshRecent() }
    private func refreshOutlineSidebar() {
        updateSidebarTabs()
        let headings = active?.rendered?.headings ?? []
        if headings.isEmpty { recentStack.addArrangedSubview(label("暂无可识别的章节")); return }
        for heading in headings.prefix(500) {
            let button = ActionButton(heading.title) { [weak self] in
                self?.pushNavigation(); self?.scrollToSource(heading.sourceRange.location); self?.highlightOutline()
            }
            button.toolTip = heading.title; button.font = .systemFont(ofSize: 11)
            button.fill = theme.panel; button.ink = theme.text
            recentStack.addArrangedSubview(button); button.widthAnchor.constraint(equalTo: recentStack.widthAnchor).isActive = true
            outlineButtons.append((heading.sourceRange.location, button))
        }
        highlightOutline()
    }
    private func highlightOutline() {
        guard outlineMode else { return }
        let offset = currentSourceOffset()
        let index = outlineButtons.lastIndex(where: { $0.0 <= offset }) ?? 0
        for (i, item) in outlineButtons.enumerated() { let color = i == index ? theme.selection : theme.panel; if item.1.fill != color { item.1.fill = color } }
    }
    @objc private func tabBlankDoubleClick(_ gesture: NSClickGestureRecognizer) {
        let point = gesture.location(in: tabsStack)
        if !tabsStack.arrangedSubviews.contains(where: { $0.frame.contains(point) }) { newDocument(nil) }
    }
    private func updateChangeMarks() {
        guard let tab = active else { return }
        let id = tab.file.id, text = tab.file.text, saved = tab.savedText, original = tab.openedText
        fileOpenQueue.async { [weak self] in
            let marks = LineChanges.marks(original: original, saved: saved, current: text)
            DispatchQueue.main.async {
                guard let self = self, self.active?.file.id == id, self.active?.file.text == text else { return }
                self.ruler.lineMarks = Dictionary(marks.map { ($0.line, $0.saved) }, uniquingKeysWith: { _, last in last }); self.ruler.needsDisplay = true
            }
        }
    }
    @objc func showJSONStructure(_ sender: Any?) {
        guard let tab = active, tab.kind == .json || tab.kind == .jsonc else { return }
        do { let controller = try JSONStructureController(text: tab.file.text, allowComments: tab.kind == .jsonc); retainResource(controller); controller.present(relativeTo: window) }
        catch { showError(error) }
    }
    @objc func formatDocument(_ sender: Any?) {
        guard let tab = active else { return }
        stashCurrent()
        let source = tab.file.text, kind = tab.kind.rawValue, id = tab.file.id
        if source.utf16.count > 500_000 { updateStatus("格式化上限50万文本单元，请分段处理"); return }
        operation?.cancelOperation(); let progress = ReaderOperation(title: "格式化 / 整理文本", parent: window); operation = progress
        progress.stage("正在分析排版…")
        fileOpenQueue.async { [weak self] in
            let result = Result { try TextFormatting.format(source, kind: kind) }
            DispatchQueue.main.async {
                guard let self = self else { progress.finish(); return }
                progress.finish()
                guard !progress.cancelled, self.active?.file.id == id, self.active?.file.text == source else { return }
                switch result {
                case .failure(let error): self.showError(error)
                case .success(let text):
                    guard !text.utf8.elementsEqual(source.utf8) else { self.updateStatus("当前内容无需整理"); return }
                    if !["plain", "markdown", "json", "jsonc", "xml"].contains(kind) {
                        let a = NSAlert(); a.messageText = "整理空白预览"; a.informativeText = "此类型没有专用格式化器，仅整理行尾空白与空行。确认后可撤销。"
                        let view = NSTextView(frame: NSRect(x: 0, y: 0, width: 650, height: 320)); view.isEditable = false
                        view.string = "整理前：\n" + String(source.prefix(6000)) + "\n\n整理后：\n" + String(text.prefix(6000))
                        let scroll = NSScrollView(frame: view.frame); scroll.hasVerticalScroller = true; scroll.documentView = view; a.accessoryView = scroll
                        a.addButton(withTitle: "应用"); a.addButton(withTitle: "取消")
                        guard self.runDocumentModal({ a.runModal() }) == .alertFirstButtonReturn else { return }
                    }
                    if self.reading { self.toggleReading(nil) }
                    let range = NSRange(location: 0, length: self.editor.string.utf16.count)
                    self.editor.breakUndoCoalescing()
                    if self.editor.shouldChangeText(in: range, replacementString: text) { self.editor.replaceCharacters(in: range, with: text); self.editor.didChangeText(); self.editor.breakUndoCoalescing() }
                }
            }
        }
    }
    private func checkExternalFile(_ tab: ReaderTab) {
        guard !externalCheckRunning, !restoringWorkspace, modalDepth == 0, let url = tab.file.url else { return }
        externalCheckRunning = true
        let id = tab.file.id, snapshot = tab.file.recoverySnapshot(), wasDirty = tab.file.isDirty, oldText = tab.file.text
        fileOpenQueue.async { [weak self] in
            let result = Result { () -> TextFile? in
                let captured = try TextFile.restore(snapshot: snapshot)
                guard try captured.diskChanged() else { return nil }
                if wasDirty { return captured }
                return try TextFile.open(url)
            }
            DispatchQueue.main.async {
                guard let self = self else { return }; self.externalCheckRunning = false
                self.performDocumentMutation {
                    guard self.active?.file.id == id, tab.file.text == oldText else { return }
                    switch result {
                    case .success(nil): break
                    case .success(let updated?):
                        if wasDirty || tab.file.isDirty { self.updateStatus("磁盘文件已变化 · 当前修改已保留，保存时需处理冲突"); return }
                        self.stashCurrent()
                        let replacement = ReaderTab(updated); replacement.isReading = tab.isReading; replacement.splitPreview = tab.splitPreview
                        replacement.sourceAnchor = TextAnchorMapper.map(oldText: oldText, newText: updated.text, oldAnchor: tab.sourceAnchor)
                        let selectionStart = TextAnchorMapper.map(oldText: oldText, newText: updated.text, oldAnchor: tab.selection.location)
                        let selectionEnd = TextAnchorMapper.map(oldText: oldText, newText: updated.text,
                                                                 oldAnchor: tab.selection.location + tab.selection.length)
                        replacement.selection = NSRange(location: selectionStart, length: max(0, selectionEnd - selectionStart))
                        replacement.hasWorkspacePosition = true; replacement.recoverDraft = tab.recoverDraft
                        replacement.automaticKind = tab.automaticKind; if !tab.automaticKind { replacement.kind = tab.kind }
                        self.tabs[self.current] = replacement; self.selectTab(self.current, stash: false); self.updateStatus("已读取磁盘最新内容")
                    case .failure: self.updateStatus("磁盘文件暂不可读 · 窗口正文已保留，可重试")
                    }
                }
            }
        }
    }
    private func addSequenceDiagrams(_ text: NSAttributedString, source: String) -> NSAttributedString {
        guard source.utf16.count <= 500_000 else { return text }
        let output = NSMutableAttributedString(attributedString: text)
        guard let regex = try? NSRegularExpression(pattern: #"(?m)^ {0,3}(`{3,}|~{3,})mermaid[^\r\n]*\r?\n([\s\S]*?)^ {0,3}\1[ \t]*(?:\r?\n|$)"#) else { return text }
        let ns = source as NSString
        let matches = regex.matches(in: source, range: NSRange(location: 0, length: ns.length))
        var additions: [(Int, NSAttributedString)] = [], total = 0, events = 0
        for match in matches.prefix(12) {
            let body = ns.substring(with: match.range(at: 2)); total += body.utf16.count
            if total > 64_000 { break }
            do {
                let diagram = try MermaidSequence.parse(body); events += diagram.events.count
                if events > 400 { break }
                let image = try MermaidSequence.render(body, fontSize: fontSize + 1, dark: dark)
                let attachment = NSTextAttachment(); attachment.image = image
                let width = min(680, image.size.width); attachment.bounds = NSRect(x: 0, y: 0, width: width, height: image.size.height * width / max(1, image.size.width))
                let label = NSMutableAttributedString(attachment: attachment)
                label.addAttributes([NSAttributedString.Key("qingyue.sourceRange"): NSValue(range: NSRange(location: match.range.location, length: 0)), NSAttributedString.Key("qingyue.blockKind"): "diagram", NSAttributedString.Key("qingyue.mermaidSource"): body], range: NSRange(location: 0, length: label.length))
                label.append(NSAttributedString(string: "\n"))
                var position: Int?
                text.enumerateAttribute(ReadingDocument.sourceRangeAttribute, in: NSRange(location: 0, length: text.length)) { value, range, stop in
                    if let sourceRange = (value as? NSValue)?.rangeValue, NSIntersectionRange(sourceRange, match.range(at: 2)).length > 0 { position = range.location; stop.pointee = true }
                }
                if let position = position { additions.append((position, label)) }
            } catch {
                let reason = "时序图保留源码：" + error.localizedDescription
                active?.mediaNotices.append(reason)
                var position: Int?
                text.enumerateAttribute(ReadingDocument.sourceRangeAttribute, in: NSRange(location: 0, length: text.length)) { value, range, stop in
                    if let sr = (value as? NSValue)?.rangeValue, NSIntersectionRange(sr, match.range(at: 2)).length > 0 { position = range.location; stop.pointee = true }
                }
                if let position = position { additions.append((position, NSAttributedString(string: reason + "\n", attributes: [.foregroundColor: theme.muted, .font: NSFont.systemFont(ofSize: fontSize), ReadingDocument.sourceRangeAttribute: NSValue(range: NSRange(location: match.range.location, length: 0))]))) }
            }
        }
        for addition in additions.reversed() { output.insert(addition.1, at: addition.0) }
        return output
    }
    private func retainResource(_ controller: NSWindowController) {
        resourceWindows.removeAll { $0.window?.isVisible == false }
        resourceWindows.append(controller); if resourceWindows.count > 8 { resourceWindows.removeFirst().close() }
    }
    private func showAttachment(_ attachment: NSTextAttachment) {
        guard let image = attachment.image else { return }
        let controller = ReaderResourceWindow(image: image, title: "图片 / 图表 · Ctrl+滚轮缩放")
        retainResource(controller); controller.showWindow(nil)
    }
    private func readingContextMenu(offset: Int) -> NSMenu {
        contextualOffset = offset
        let menu = NSMenu()
        for (title, action) in [("复制选中图文", #selector(copyReadingSelection(_:))), ("复制全文（含图片）", #selector(copyReadingFull(_:))), ("复制全文纯文本", #selector(copyReadingPlain(_:))), ("复制 Markdown 源文", #selector(copyMarkdownSource(_:))), ("复制当前代码块", #selector(copyCodeBlock(_:))), ("复制当前表格", #selector(copyTableBlock(_:))), ("展开表格（横向滚动）", #selector(expandTable(_:))), ("查看图片 / 图表", #selector(openContextAttachment(_:)))] {
            let item = NSMenuItem(title: title, action: action, keyEquivalent: ""); item.target = self; menu.addItem(item)
        }
        return menu
    }
    @objc private func openContextAttachment(_ sender: Any?) {
        guard let text = preview.textStorage, text.length > 0 else { return }
        let index = min(contextualOffset, text.length - 1)
        if let attachment = text.attribute(.attachment, at: index, effectiveRange: nil) as? NSTextAttachment { showAttachment(attachment) }
    }
    private func readingBlock(_ kind: String) -> NSRange? {
        guard let text = preview.textStorage, text.length > 0 else { return nil }
        let index = min(max(0, contextualOffset), text.length - 1)
        var range = NSRange()
        guard text.attribute(NSAttributedString.Key("qingyue.blockKind"), at: index, longestEffectiveRange: &range, in: NSRange(location: 0, length: text.length)) as? String == kind else { return nil }
        return range
    }
    @objc func copyCodeBlock(_ sender: Any?) { if let range = readingBlock("code") { copyReadingRange(range) } else { updateStatus("请先在代码块内右键操作") } }
    @objc func copyTableBlock(_ sender: Any?) { if let range = readingBlock("table") { copyReadingRange(range) } else { updateStatus("请先在表格内右键操作") } }
    @objc func expandTable(_ sender: Any?) {
        guard let range = readingBlock("table"), let text = preview.textStorage else { updateStatus("请先在表格内右键操作"); return }
        let controller = ReaderResourceWindow(text: text.attributedSubstring(from: range), title: "展开表格 · 横向滚动")
        retainResource(controller); controller.showWindow(nil)
    }
    @objc func copyReadingSelection(_ sender: Any?) { copyReadingRange(preview.selectedRange()) }
    private func copyReadingRange(_ range: NSRange) {
        guard let text = preview.textStorage, range.length > 0, range.location <= text.length, range.length <= text.length - range.location else { updateStatus("请先选择阅读内容"); return }
        do {
            let data = try ReadingShare.clipboard(text.attributedSubstring(from: range))
            NSPasteboard.general.declareTypes(Array(data.keys), owner: nil)
            for (kind, value) in data { NSPasteboard.general.setData(value, forType: kind) }
            updateStatus("已复制选中图文，接收应用支持程度可能不同")
        } catch { showError(error) }
    }
    private func preparedReading(_ action: @escaping (NSAttributedString) -> Void) {
        guard let tab = active else { return }
        let oldSelection = preview.selectedRange(), oldOrigin = previewScroll.contentView.bounds.origin
        let oldPresentation = tab.rendered?.attributedText
        stashCurrent(); renderReading()
        if oldPresentation === tab.rendered?.attributedText {
            preview.setSelectedRange(oldSelection); previewScroll.contentView.scroll(to: oldOrigin)
        }
        let id = tab.file.id, text = tab.file.text
        let wait = ReaderOperation(title: "准备图片与图文", parent: window); wait.stage("正在等待图片，最多5秒…")
        let deadline = Date(timeIntervalSinceNow: 5)
        func poll() {
            guard !wait.cancelled, self.active?.file.id == id, self.active?.file.text == text else { wait.finish(); return }
            if tab.mediaTask?.isFinished == true || tab.kind != .markdown || Date() >= deadline {
                wait.finish()
                if Date() >= deadline || !tab.mediaNotices.isEmpty {
                    guard self.alert("部分图片未准备完成", "可继续复制当前内容，或取消后重试。\n" + tab.mediaNotices.joined(separator: "\n"), buttons: ["继续", "取消"]) == .alertFirstButtonReturn else { return }
                }
                if let attributed = tab.rendered?.attributedText { action(attributed) }
            } else { DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { poll() } }
        }
        poll()
    }
    @objc func copyReadingFull(_ sender: Any?) {
        preparedReading { text in
            do { let values = try ReadingShare.clipboard(text); NSPasteboard.general.declareTypes(Array(values.keys), owner: nil); for (type, data) in values { NSPasteboard.general.setData(data, forType: type) }; self.updateStatus("已复制全文图文") }
            catch { self.showError(error) }
        }
    }
    @objc func copyReadingPlain(_ sender: Any?) { preparedReading { text in NSPasteboard.general.clearContents(); NSPasteboard.general.setString(ReadingShare.plainText(text), forType: .string); self.updateStatus("已复制全文纯文本") } }
    @objc func copyMarkdownSource(_ sender: Any?) { if let source = active?.file.text { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(source, forType: .string); updateStatus("已复制源文") } }
    @objc func exportReadingHTML(_ sender: Any?) {
        preparedReading { text in
            let identity = self.active?.file.id
            let source = self.active?.file.text
            let title = self.active?.file.title ?? "轻阅文档"
            let panel = NSSavePanel(); panel.nameFieldStringValue = (self.active?.file.title ?? "文档") + ".html"
            guard self.runDocumentModal({ panel.runModal() }) == .OK, let url = panel.url else { return }
            guard self.active?.file.id == identity, self.active?.file.text == source else { self.updateStatus("文档已变化，请重新导出"); return }
            guard !self.tabs.contains(where: { $0.file.url?.resolvingSymlinksInPath().standardizedFileURL == url.resolvingSymlinksInPath().standardizedFileURL }) else { self.updateStatus("导出不能覆盖正在打开的源文件，请使用新的.html文件名"); return }
            do { try ReadingShare.exportHTML(text, title: title).write(to: url, options: .atomic); self.updateStatus("已导出自包含HTML") } catch { self.showError(error) }
        }
    }
    private func performDocumentMutation(_ action: @escaping () -> Void) {
        if modalDepth > 0 { deferredDocumentMutations.append(action) } else { action() }
    }
    @discardableResult private func runDocumentModal<T>(_ action: () -> T) -> T {
        modalDepth += 1
        defer {
            modalDepth -= 1
            if modalDepth == 0 {
                DispatchQueue.main.async { [weak self] in
                    guard let self = self, self.modalDepth == 0, !self.shuttingDown else { return }
                    let actions = self.deferredDocumentMutations; self.deferredDocumentMutations = []
                    for action in actions { self.performDocumentMutation(action) }
                }
            }
        }
        return action()
    }
    private func alert(_ title: String, _ detail: String, buttons: [String]) -> NSApplication.ModalResponse {
        let a = NSAlert(); a.messageText = title; a.informativeText = detail; a.alertStyle = .warning
        buttons.forEach { a.addButton(withTitle: $0) }; return runDocumentModal({ a.runModal() })
    }
    private func showError(_ error: Error) {
        let a = NSAlert(); a.messageText = "未能完成操作"; a.informativeText = error.localizedDescription; a.alertStyle = .warning; a.addButton(withTitle: "知道了"); runDocumentModal({ a.runModal() })
        updateStatus("操作未完成，编辑内容已保留")
    }
}
