import AppKit

final class ReaderTab {
    let file: TextFile
    let undo = UndoManager()
    var selection = NSRange(location: 0, length: 0)
    var scroll = NSPoint.zero
    var readingScroll = NSPoint.zero
    var isReading = false
    var sourceAnchor = 0
    var automaticKind = true
    var rendered: ReadingDocument?
    var kind: TextKind
    init(_ file: TextFile) {
        self.file = file
        kind = TextKind.detect(url: file.url, text: file.text)
        undo.levelsOfUndo = 100
    }
    func updateKindAfterPathChange() {
        if automaticKind { kind = TextKind.detect(url: file.url, text: file.text) }
    }
}

final class ReaderWindow: NSWindowController, NSWindowDelegate, NSTextViewDelegate, NSSearchFieldDelegate {
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
    private var readingWidth = CGFloat(UserDefaults.standard.object(forKey: "readingWidth") as? Double ?? 680)
    private var showingSidebar = true
    private var searchVisible = false
    private var recent: [String] { get { UserDefaults.standard.stringArray(forKey: "recent") ?? [] } set { UserDefaults.standard.set(newValue, forKey: "recent") } }
    private var active: ReaderTab? { tabs.indices.contains(current) ? tabs[current] : nil }
    private let root = ColorView()
    private let sidebar = ColorView()
    private let canvas = ColorView()
    private let header = NSView()
    private var branding: NSStackView!
    private var focusHiddenActions: [NSView] = []
    private let titleLabel = NSTextField(labelWithString: "轻阅")
    private let subtitle = NSTextField(labelWithString: "本机文本 · 阅读与编辑")
    private let sideTitle = NSTextField(labelWithString: "最近打开")
    private let sideNote = NSTextField(wrappingLabelWithString: "⌘O  打开文件\n⌘S  保存修改\n⌘F  查找文本")
    private let recentStack = FlippedStackView()
    private let tabsStack = NSStackView()
    private var tabViewIDs: [UUID] = []
    private var tabTitleButtons: [ActionButton] = []
    private let tabScroll = NSScrollView()
    private let editorScroll = NSScrollView()
    private let editor = EditorTextView(frame: .zero)
    private let preview = NSTextView(frame: .zero)
    private var ruler: LineNumberRuler!
    private let tools = NSStackView()
    private let toolsContainer = NSView()
    private let searchBar = NSStackView()
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
    private var showLineNumbers = UserDefaults.standard.object(forKey: "lineNumbers") as? Bool ?? true
    private var readingFontName = UserDefaults.standard.string(forKey: "readingFont") ?? "PingFang SC"
    private var readingLineSpacing = CGFloat(UserDefaults.standard.object(forKey: "lineSpacing") as? Double ?? 6)
    private let searchField = NSSearchField()
    private let replaceField = NSTextField()
    private let statusLeft = NSTextField(labelWithString: "就绪")
    private let statusRight = NSTextField(labelWithString: "行 1，列 1")
    private let kindPopup = NSPopUpButton()
    private let codecPopup = NSPopUpButton()
    private let endingPopup = NSPopUpButton()
    private var sidebarWidth: NSLayoutConstraint!
    private var rulerWidth: NSLayoutConstraint!
    private var searchHeight: NSLayoutConstraint!
    private var buttons: [ActionButton] = []
    private var themeButton: ActionButton!
    private var readButton: ActionButton!
    private var wrapButton: ActionButton!
    private var formatButton: ActionButton!
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
    private let supportOverride: URL?
    private var dataDirectory: URL {
        if let path = supportOverride { return path }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("LightReader", isDirectory: true)
    }
    private var recoveryURL: URL { dataDirectory.appendingPathComponent("Recovery.json") }

    init(supportDirectory: URL? = nil) {
        supportOverride = supportDirectory
        let win = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 760), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        win.title = "轻阅"
        win.minSize = NSSize(width: 820, height: 500)
        win.isReleasedWhenClosed = false
        win.tabbingMode = .disallowed
        super.init(window: win)
        win.delegate = self
        buildInterface()
        applyTheme()
        if !win.setFrameUsingName("QingyueMainWindow") { win.center() }
        win.setFrameAutosaveName("QingyueMainWindow")
        restoreRecovery()
        if tabs.isEmpty { newDocument(nil) }
        refreshRecent()
        NotificationCenter.default.addObserver(self, selector: #selector(scrolled), name: NSView.boundsDidChangeNotification, object: editorScroll.contentView)
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
        add(header, to: root)
        titleLabel.font = .systemFont(ofSize: 24, weight: .semibold)
        subtitle.font = .systemFont(ofSize: 11)
        branding = NSStackView(views: [titleLabel, subtitle]); branding.orientation = .vertical; branding.alignment = .leading; branding.spacing = 3
        add(branding, to: header)
        let open = button("打开", symbol: "folder", primary: true) { [weak self] in self?.openDocument(nil) }
        let new = button("新建", symbol: "plus") { [weak self] in self?.newDocument(nil) }
        let save = button("保存", symbol: "square.and.arrow.down") { [weak self] in _ = self?.saveCurrent() }
        themeButton = button("明亮", symbol: "sun.max") { [weak self] in self?.toggleTheme(nil) }
        readButton = button("阅读模式", symbol: "book") { [weak self] in self?.toggleReading(nil) }
        let settings = button("设置") { [weak self] in self?.showPreferences(nil) }
        focusButton = button("专注") { [weak self] in self?.toggleFocus(nil) }
        focusHiddenActions = [new, open, save, themeButton, settings]
        let headerActions = stack([new, open, save, readButton, focusButton, themeButton, settings], spacing: 7)
        add(headerActions, to: header)

        sidebar.radius = 10; add(sidebar, to: root)
        sideTitle.font = .systemFont(ofSize: 12, weight: .semibold)
        add(sideTitle, to: sidebar)
        let recentScroll = NSScrollView(); recentScroll.contentView = FlippedClipView(); recentScroll.drawsBackground = false; recentScroll.hasVerticalScroller = true; recentScroll.autohidesScrollers = true
        recentStack.orientation = .vertical; recentStack.alignment = .leading; recentStack.spacing = 7; recentStack.translatesAutoresizingMaskIntoConstraints = false
        recentScroll.documentView = recentStack; add(recentScroll, to: sidebar)
        sideNote.font = .systemFont(ofSize: 11); add(sideNote, to: sidebar)
        let sideBottom = button("收起侧栏", symbol: "sidebar.left") { [weak self] in self?.toggleSidebar(nil) }
        add(sideBottom, to: sidebar)

        canvas.radius = 10; add(canvas, to: root)
        tabsStack.orientation = .horizontal; tabsStack.alignment = .centerY; tabsStack.spacing = 4
        tabsStack.translatesAutoresizingMaskIntoConstraints = false
        tabScroll.drawsBackground = false; tabScroll.hasHorizontalScroller = true; tabScroll.autohidesScrollers = true; tabScroll.scrollerStyle = .overlay
        tabScroll.documentView = tabsStack; add(tabScroll, to: canvas)

        kindPopup.addItems(withTitles: ["自动识别"] + TextKind.allCases.map { $0.label }); kindPopup.target = self; kindPopup.action = #selector(kindChanged)
        kindPopup.font = .systemFont(ofSize: 11); kindPopup.setAccessibilityLabel("文件类型")
        formatButton = button("整理 JSON") { [weak self] in self?.formatJSON(nil) }
        wrapButton = button("自动换行 ✓") { [weak self] in self?.toggleWrap(nil) }
        let less = button("A−") { [weak self] in self?.changeFont(-1) }
        let more = button("A+") { [weak self] in self?.changeFont(1) }
        let find = button("查找", symbol: "magnifyingglass") { [weak self] in self?.showFind(nil) }
        let outline = button("目录") { [weak self] in self?.showOutline(nil) }
        tools.orientation = .horizontal; tools.alignment = .centerY; tools.spacing = 7
        let spacer = NSView(); spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        [kindPopup, formatButton, spacer, less, more, wrapButton, outline, find].forEach { tools.addArrangedSubview($0) }
        add(toolsContainer, to: canvas); add(tools, to: toolsContainer)

        searchField.placeholderString = "查找文本"; searchField.font = .systemFont(ofSize: 12); searchField.setAccessibilityLabel("查找文本")
        searchField.target = self; searchField.action = #selector(findNext); searchField.delegate = self
        replaceField.placeholderString = "替换为"; replaceField.font = .systemFont(ofSize: 12); replaceField.setAccessibilityLabel("替换为")
        replaceField.delegate = self
        searchField.widthAnchor.constraint(greaterThanOrEqualToConstant: 130).isActive = true
        replaceField.widthAnchor.constraint(greaterThanOrEqualToConstant: 100).isActive = true
        searchBar.orientation = .horizontal; searchBar.alignment = .centerY; searchBar.spacing = 6
        replacementButtons = [button("替换") { [weak self] in self?.replaceOne(nil) }, button("全部") { [weak self] in self?.replaceAll(nil) }]
        [searchField, replaceField,
         button("上一个") { [weak self] in self?.find(backward: true) },
         button("下一个") { [weak self] in self?.find(backward: false) },
         replacementButtons[0], replacementButtons[1],
         button("×") { [weak self] in self?.hideFind() }].forEach { searchBar.addArrangedSubview($0) }
        add(searchContainer, to: canvas); add(searchBar, to: searchContainer); searchBar.isHidden = true
        searchOptionsRow.orientation = .horizontal; searchOptionsRow.alignment = .centerY; searchOptionsRow.spacing = 12
        for c in [caseCheck, regexCheck, wordCheck] { c.font = .systemFont(ofSize: 11); c.target = self; c.action = #selector(searchOptionsChanged) }
        findCounter.font = .systemFont(ofSize: 11); findCounter.lineBreakMode = .byTruncatingTail
        [caseCheck, regexCheck, wordCheck, findCounter].forEach { searchOptionsRow.addArrangedSubview($0) }
        add(searchOptionsRow, to: searchContainer); searchOptionsRow.isHidden = true

        editor.isRichText = false; editor.importsGraphics = false; editor.allowsUndo = true
        editor.isAutomaticQuoteSubstitutionEnabled = false; editor.isAutomaticDashSubstitutionEnabled = false
        editor.isAutomaticTextReplacementEnabled = false; editor.isAutomaticSpellingCorrectionEnabled = false
        editor.isAutomaticLinkDetectionEnabled = false; editor.isContinuousSpellCheckingEnabled = false
        editor.isAutomaticDataDetectionEnabled = false
        editor.isGrammarCheckingEnabled = false; editor.usesFindBar = false
        editor.textContainerInset = NSSize(width: 18, height: 20)
        editor.isVerticallyResizable = true; editor.isHorizontallyResizable = false
        editor.autoresizingMask = [.width]
        editor.minSize = .zero; editor.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        editor.delegate = self; editor.setAccessibilityLabel("文本编辑区")
        editor.cancelHandler = { [weak self] in self?.cancelFind() }
        editor.filesDropped = { [weak self] urls in self?.openURLs(urls) }
        editor.registerForDraggedTypes([.fileURL])
        editorScroll.hasVerticalScroller = true; editorScroll.hasHorizontalScroller = false; editorScroll.autohidesScrollers = true
        editorScroll.borderType = .noBorder; editorScroll.documentView = editor
        editorScroll.contentView.postsBoundsChangedNotifications = true
        ruler = LineNumberRuler(editor: editor, scroll: editorScroll)
        // Keep the gutter outside the scroll view; native overlay rulers use a
        // negative clip origin that conflicts with restoring text scroll positions.
        add(ruler, to: canvas)
        add(editorScroll, to: canvas)
        preview.isEditable = false; preview.isSelectable = true; preview.isRichText = true
        preview.textContainerInset = NSSize(width: 32, height: 26); preview.autoresizingMask = [.width]
        preview.isVerticallyResizable = true; preview.setAccessibilityLabel("阅读区")
        preview.delegate = self
        preview.textContainer?.widthTracksTextView = true
        add(statusLeft, to: root); add(statusRight, to: root)
        statusLeft.font = .systemFont(ofSize: 11); statusLeft.lineBreakMode = .byTruncatingMiddle
        statusLeft.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        statusRight.font = .monospacedDigitSystemFont(ofSize: 10, weight: .regular)
        codecPopup.addItems(withTitles: TextCodec.allCases.map { $0.label }); codecPopup.font = .systemFont(ofSize: 10)
        codecPopup.target = self; codecPopup.action = #selector(codecChanged); codecPopup.setAccessibilityLabel("保存编码")
        endingPopup.addItems(withTitles: ["LF", "CRLF", "CR", "混合"]); endingPopup.font = .systemFont(ofSize: 10)
        endingPopup.target = self; endingPopup.action = #selector(endingChanged); endingPopup.setAccessibilityLabel("换行符")
        add(codecPopup, to: root); add(endingPopup, to: root)
        sidebarWidth = sidebar.widthAnchor.constraint(equalToConstant: 170)
        rulerWidth = ruler.widthAnchor.constraint(equalToConstant: showLineNumbers ? 48 : 0)
        ruler.isHidden = !showLineNumbers
        searchHeight = searchContainer.heightAnchor.constraint(equalToConstant: 0)
        tabsHeight = tabScroll.heightAnchor.constraint(equalToConstant: 39)
        toolsHeight = toolsContainer.heightAnchor.constraint(equalToConstant: 36)
        headerHeight = header.heightAnchor.constraint(equalToConstant: 54)
        canvasInsetLeading = canvas.leadingAnchor.constraint(equalTo: sidebar.trailingAnchor, constant: 12)
        canvasFullLeading = canvas.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 16)
        statusNormalTrailing = statusRight.trailingAnchor.constraint(equalTo: codecPopup.leadingAnchor, constant: -12)
        statusReadingTrailing = statusRight.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -20)
        NSLayoutConstraint.activate([
            header.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 22), header.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -22),
            header.topAnchor.constraint(equalTo: root.topAnchor, constant: 15), headerHeight,
            branding.leadingAnchor.constraint(equalTo: header.leadingAnchor), branding.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            headerActions.trailingAnchor.constraint(equalTo: header.trailingAnchor), headerActions.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            sidebar.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 16), sidebar.topAnchor.constraint(equalTo: header.bottomAnchor, constant: 14), sidebarWidth,
            sidebar.bottomAnchor.constraint(equalTo: statusLeft.topAnchor, constant: -14),
            sideTitle.leadingAnchor.constraint(equalTo: sidebar.leadingAnchor, constant: 14), sideTitle.topAnchor.constraint(equalTo: sidebar.topAnchor, constant: 16),
            recentScroll.topAnchor.constraint(equalTo: sideTitle.bottomAnchor, constant: 14), recentScroll.leadingAnchor.constraint(equalTo: sidebar.leadingAnchor, constant: 8),
            recentScroll.trailingAnchor.constraint(equalTo: sidebar.trailingAnchor, constant: -8), recentScroll.bottomAnchor.constraint(equalTo: sideNote.topAnchor, constant: -16),
            recentStack.leadingAnchor.constraint(equalTo: recentScroll.contentView.leadingAnchor), recentStack.topAnchor.constraint(equalTo: recentScroll.contentView.topAnchor),
            recentStack.widthAnchor.constraint(equalTo: recentScroll.contentView.widthAnchor),
            sideNote.leadingAnchor.constraint(equalTo: sidebar.leadingAnchor, constant: 14), sideNote.trailingAnchor.constraint(equalTo: sidebar.trailingAnchor, constant: -8),
            sideNote.bottomAnchor.constraint(equalTo: sideBottom.topAnchor, constant: -14), sideBottom.leadingAnchor.constraint(equalTo: sidebar.leadingAnchor, constant: 8), sideBottom.trailingAnchor.constraint(equalTo: sidebar.trailingAnchor, constant: -8),
            sideBottom.bottomAnchor.constraint(equalTo: sidebar.bottomAnchor, constant: -10),
            canvasInsetLeading, canvas.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -16),
            canvas.topAnchor.constraint(equalTo: sidebar.topAnchor), canvas.bottomAnchor.constraint(equalTo: sidebar.bottomAnchor),
            tabScroll.topAnchor.constraint(equalTo: canvas.topAnchor, constant: 8), tabScroll.leadingAnchor.constraint(equalTo: canvas.leadingAnchor, constant: 10),
            tabScroll.trailingAnchor.constraint(equalTo: canvas.trailingAnchor, constant: -10), tabsHeight,
            tabsStack.leadingAnchor.constraint(equalTo: tabScroll.contentView.leadingAnchor), tabsStack.topAnchor.constraint(equalTo: tabScroll.contentView.topAnchor), tabsStack.heightAnchor.constraint(equalToConstant: 32),
            toolsContainer.leadingAnchor.constraint(equalTo: canvas.leadingAnchor, constant: 12), toolsContainer.trailingAnchor.constraint(equalTo: canvas.trailingAnchor, constant: -12),
            toolsContainer.topAnchor.constraint(equalTo: tabScroll.bottomAnchor, constant: 4), toolsHeight,
            tools.leadingAnchor.constraint(equalTo: toolsContainer.leadingAnchor), tools.trailingAnchor.constraint(equalTo: toolsContainer.trailingAnchor),
            tools.centerYAnchor.constraint(equalTo: toolsContainer.centerYAnchor), tools.heightAnchor.constraint(equalToConstant: 36),
            searchContainer.topAnchor.constraint(equalTo: toolsContainer.bottomAnchor, constant: 5), searchContainer.leadingAnchor.constraint(equalTo: canvas.leadingAnchor, constant: 12),
            searchContainer.trailingAnchor.constraint(equalTo: canvas.trailingAnchor, constant: -12), searchHeight,
            searchBar.topAnchor.constraint(equalTo: searchContainer.topAnchor, constant: 2), searchBar.leadingAnchor.constraint(equalTo: searchContainer.leadingAnchor),
            searchBar.trailingAnchor.constraint(equalTo: searchContainer.trailingAnchor), searchBar.heightAnchor.constraint(equalToConstant: 32),
            searchOptionsRow.leadingAnchor.constraint(equalTo: searchContainer.leadingAnchor), searchOptionsRow.trailingAnchor.constraint(lessThanOrEqualTo: searchContainer.trailingAnchor),
            searchOptionsRow.topAnchor.constraint(equalTo: searchBar.bottomAnchor, constant: 3), searchOptionsRow.heightAnchor.constraint(equalToConstant: 24),
            ruler.leadingAnchor.constraint(equalTo: canvas.leadingAnchor, constant: 1), ruler.topAnchor.constraint(equalTo: editorScroll.topAnchor), ruler.bottomAnchor.constraint(equalTo: editorScroll.bottomAnchor), rulerWidth,
            editorScroll.topAnchor.constraint(equalTo: searchContainer.bottomAnchor, constant: 6), editorScroll.leadingAnchor.constraint(equalTo: ruler.trailingAnchor),
            editorScroll.trailingAnchor.constraint(equalTo: canvas.trailingAnchor, constant: -1), editorScroll.bottomAnchor.constraint(equalTo: canvas.bottomAnchor, constant: -1),
            statusLeft.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 22), statusLeft.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -10), statusLeft.heightAnchor.constraint(equalToConstant: 18),
            statusLeft.trailingAnchor.constraint(lessThanOrEqualTo: statusRight.leadingAnchor, constant: -12),
            statusNormalTrailing, statusRight.centerYAnchor.constraint(equalTo: statusLeft.centerYAnchor),
            codecPopup.trailingAnchor.constraint(equalTo: endingPopup.leadingAnchor, constant: -8), codecPopup.centerYAnchor.constraint(equalTo: statusLeft.centerYAnchor), codecPopup.widthAnchor.constraint(equalToConstant: 124),
            endingPopup.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -16), endingPopup.centerYAnchor.constraint(equalTo: statusLeft.centerYAnchor), endingPopup.widthAnchor.constraint(equalToConstant: 74)
        ])
    }

    private func editorFont() -> NSFont {
        if reading { return NSFont(name: readingFontName, size: fontSize + 1) ?? .systemFont(ofSize: fontSize + 1) }
        return .monospacedSystemFont(ofSize: fontSize, weight: .regular)
    }
    private func applyTheme() {
        if followSystemAppearance { dark = NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua }
        window?.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        root.color = theme.window; sidebar.color = theme.panel; canvas.color = theme.panel
        titleLabel.textColor = theme.text; subtitle.textColor = theme.muted; sideTitle.textColor = theme.muted; sideNote.textColor = theme.muted
        statusLeft.textColor = theme.muted; statusRight.textColor = theme.muted
        findCounter.textColor = theme.muted
        editor.backgroundColor = theme.editor; editor.textColor = theme.text; editor.insertionPointColor = theme.accent
        editor.selectedTextAttributes = [.backgroundColor: theme.selection, .foregroundColor: theme.text]
        editorScroll.backgroundColor = theme.editor; preview.backgroundColor = theme.editor; preview.textColor = theme.text
        ruler.ink = theme.muted; ruler.background = theme.editor; ruler.needsDisplay = true
        for b in buttons { style(b) }
        themeButton.title = followSystemAppearance ? "随系统" : (dark ? "明亮" : "深色")
        refreshTabs(); refreshRecent(); scheduleHighlight()
        if reading { renderReading() }
    }
    private func style(_ b: ActionButton) {
        b.fill = b.isEmphasized ? theme.accent : theme.border
        b.ink = b.isEmphasized ? .white : theme.text
    }
    private func refreshTabs() {
        let ids = tabs.map { $0.file.id }
        if ids != tabViewIDs {
            tabsStack.arrangedSubviews.forEach { tabsStack.removeArrangedSubview($0); $0.removeFromSuperview() }
            tabViewIDs = ids; tabTitleButtons = []
            for tab in tabs {
                let id = tab.file.id
                let b = ActionButton(tab.file.title) { [weak self] in
                    if let self = self, let i = self.tabs.firstIndex(where: { $0.file.id == id }) { self.selectTab(i) }
                }
                b.widthAnchor.constraint(equalToConstant: 116).isActive = true
                let context = NSMenu()
                for (title, action) in [("关闭此标签", #selector(closeContextTab(_:))), ("关闭其他标签", #selector(closeOtherTabs(_:))), ("复制路径", #selector(copyContextPath(_:))), ("在 Finder 中显示", #selector(revealContextFile(_:)))] {
                    let item = NSMenuItem(title: title, action: action, keyEquivalent: ""); item.target = self; item.representedObject = id; context.addItem(item)
                }
                b.menu = context
                let x = ActionButton("×") { [weak self] in
                    if let self = self, let i = self.tabs.firstIndex(where: { $0.file.id == id }) { self.closeTab(i) }
                }
                x.fill = theme.panel; x.ink = theme.muted
                x.widthAnchor.constraint(equalToConstant: 48).isActive = true; x.setAccessibilityLabel("关闭 \(tab.file.title)")
                tabsStack.addArrangedSubview(stack([b, x], spacing: 0)); tabTitleButtons.append(b)
            }
        }
        for (index, tab) in tabs.enumerated() {
            let b = tabTitleButtons[index]
            let title = (tab.file.isDirty ? "● " : "") + tab.file.title
            if b.title != title { b.title = title }
            b.fill = index == current ? theme.accent : theme.panel; b.ink = index == current ? .white : theme.muted
            b.toolTip = tab.file.url?.path ?? "尚未保存的文档"
            b.constraints.first(where: { $0.firstAttribute == .width && $0.relation == .equal })?.constant = min(210, max(116, CGFloat(tab.file.title.count * 8 + 36)))
        }
        window?.title = "\(active?.file.title ?? "轻阅") — 轻阅"
        window?.isDocumentEdited = active?.file.isDirty ?? false
        window?.representedURL = active?.file.url
    }
    private func refreshRecent() {
        recentStack.arrangedSubviews.forEach { recentStack.removeArrangedSubview($0); $0.removeFromSuperview() }
        let files = recent.filter { FileManager.default.fileExists(atPath: $0) }
        if files.isEmpty {
            let hint = label("打开文件后\n会显示在这里", 12); hint.maximumNumberOfLines = 0
            recentStack.addArrangedSubview(hint)
        }
        for path in files.prefix(12) {
            let b = ActionButton(URL(fileURLWithPath: path).lastPathComponent) { [weak self] in self?.openURLs([URL(fileURLWithPath: path)]) }
            b.font = .systemFont(ofSize: 11); b.toolTip = path; b.fill = theme.panel; b.ink = theme.text
            recentStack.addArrangedSubview(b)
            b.widthAnchor.constraint(equalTo: recentStack.widthAnchor).isActive = true
        }
    }
    private func remember(_ url: URL) {
        var items = recent.filter { $0 != url.path }; items.insert(url.path, at: 0); recent = Array(items.prefix(15)); refreshRecent()
    }
    @objc func clearRecent(_ sender: Any?) { recent = []; refreshRecent() }
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
        stashCurrent(); tabs.append(ReaderTab(TextFile())); selectTab(tabs.count - 1, stash: false)
    }
    @objc func openDocument(_ sender: Any?) {
        let p = NSOpenPanel(); p.allowsMultipleSelection = true; p.canChooseDirectories = false
        p.message = "打开本机文本文件 · TXT / JSON / YAML / MD / 配置文件"
        if p.runModal() == .OK { openURLs(p.urls) }
    }
    func openURLs(_ urls: [URL]) {
        for url in urls {
            let resolved = url.resolvingSymlinksInPath().standardizedFileURL
            if let i = tabs.firstIndex(where: { $0.file.url?.resolvingSymlinksInPath().standardizedFileURL == resolved }) { selectTab(i); continue }
            guard openingPaths.insert(resolved.path).inserted else { continue }
            statusLeft.stringValue = "正在打开 \(url.lastPathComponent)…"
            DispatchQueue.global(qos: .userInitiated).async { [weak self] in
                let result = Result { try TextFile.open(url) }
                DispatchQueue.main.async {
                    guard let self = self else { return }
                    self.openingPaths.remove(resolved.path)
                    switch result {
                    case .success(let file):
                        self.stashCurrent()
                        if self.tabs.count == 1, let initial = self.tabs.first, initial.file.url == nil, initial.file.text.isEmpty {
                            self.tabs.removeAll()
                        }
                        let tab = ReaderTab(file); self.restorePosition(tab)
                        self.tabs.append(tab); self.selectTab(self.tabs.count - 1, stash: false); self.remember(url)
                    case .failure(let error): self.showError(error)
                    }
                }
            }
        }
    }
    private func stashCurrent() {
        guard let tab = active else { return }
        editor.breakUndoCoalescing()
        tab.selection = editor.selectedRange()
        if reading { tab.readingScroll = editorScroll.contentView.bounds.origin }
        else { tab.scroll = editorScroll.contentView.bounds.origin }
        if !reading { tab.file.text = editor.string }
        tab.isReading = reading
        if !restoringLocation { tab.sourceAnchor = currentSourceOffset() }
        rememberPosition(tab)
    }
    private func selectTab(_ index: Int, stash: Bool = true) {
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
        if reading { renderReading() }
        window?.contentView?.layoutSubtreeIfNeeded()
        if reading { scrollToSource(anchor) }
        else {
            restoringLocation = true
            editorScroll.contentView.scroll(to: tab.scroll); editorScroll.reflectScrolledClipView(editorScroll.contentView)
            restoringLocation = false
        }
        scheduleSearch()
        window?.makeFirstResponder(reading ? preview : editor)
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
            if let dir = tab.file.url?.deletingLastPathComponent() { panel.directoryURL = dir }
            guard panel.runModal() == .OK, let url = panel.url else { return false }
            destination = url
        }
        do {
            try tab.file.save(to: destination, allowOverwrite: overwrite)
            refreshAutomaticKind(tab)
            if let url = tab.file.url { remember(url) }
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
    @discardableResult private func closeTab(_ index: Int) -> Bool {
        guard tabs.indices.contains(index) else { return true }
        selectTab(index)
        if let file = active?.file, file.isDirty && (!file.text.isEmpty || file.url != nil) {
            let result = alert("保存“\(active!.file.title)”的修改？", "保存会写回本机文件。", buttons: ["保存", "取消", "不保存"])
            if result == .alertSecondButtonReturn { return false }
            if result == .alertFirstButtonReturn && !saveCurrent() { return false }
        }
        changing = true; tabs.remove(at: index); changing = false
        if tabs.isEmpty { current = 0; window?.performClose(nil) }
        else { selectTab(min(index, tabs.count - 1), stash: false) }
        writeRecovery()
        return true
    }
    private func confirmDocumentsClosure() -> Bool {
        stashCurrent()
        for index in tabs.indices where tabs[index].file.isDirty && (!tabs[index].file.text.isEmpty || tabs[index].file.url != nil) {
            selectTab(index)
            let result = alert("保存“\(tabs[index].file.title)”的修改？", "退出前可以保存、取消退出，或放弃这份修改。", buttons: ["保存", "取消", "不保存"])
            if result == .alertSecondButtonReturn { return false }
            if result == .alertFirstButtonReturn && !saveCurrent() { return false }
        }
        return true
    }
    func confirmQuit() -> Bool {
        if shuttingDown { return true }
        guard confirmDocumentsClosure() else { return false }
        shuttingDown = true; recoveryTask?.cancel(); try? FileManager.default.removeItem(at: recoveryURL)
        return true
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard confirmDocumentsClosure() else { return false }
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
        do { if try tab.file.diskChanged() { updateStatus("磁盘文件已变化 · ⌘⇧R 可重新载入") } }
        catch { updateStatus("磁盘文件暂不可访问，当前内容已保留") }
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
        tab.rendered = nil
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
        if reading { updateStatus() }
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
            let maximum = max(1, view.bounds.height - editorScroll.contentSize.height)
            let progress = min(100, max(0, Int(editorScroll.contentView.bounds.origin.y / maximum * 100)))
            statusRight.stringValue = "阅读 · \(progress)%"
        } else { statusRight.stringValue = "行 \(line)，列 \(column)" }
        statusLeft.stringValue = message ?? "\(file.isDirty ? "未保存" : "已保存") · \(file.url?.path ?? "新建本机文本")"
        statusLeft.toolTip = file.url?.path
        formatButton.isEnabled = active?.kind == .json && !reading
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
    @objc func toggleSidebar(_ sender: Any?) { showingSidebar.toggle(); updateModeControls() }
    @objc func toggleFocus(_ sender: Any?) {
        let anchor = currentSourceOffset(); focused.toggle(); updateModeControls(); updateReadingWidth(); scrollToSource(anchor)
    }
    private func updateModeControls() {
        let hideSidebar = focused || !showingSidebar
        sidebar.isHidden = hideSidebar
        canvasInsetLeading.isActive = !hideSidebar; canvasFullLeading.isActive = hideSidebar
        tabsHeight.constant = focused ? 0 : 39; tabScroll.isHidden = focused
        toolsHeight.constant = focused ? 0 : 36; tools.isHidden = focused
        headerHeight.constant = focused ? 38 : 54; branding.isHidden = focused
        for action in focusHiddenActions { action.isHidden = focused }
        focusButton.title = focused ? "退出专注" : "专注"
        readButton.title = reading ? "返回编辑" : "阅读模式"
        kindPopup.isHidden = reading; formatButton.isHidden = reading; wrapButton.isHidden = reading
        replaceField.isHidden = reading; replacementButtons.forEach { $0.isHidden = reading }
        codecPopup.isHidden = reading || focused; endingPopup.isHidden = reading || focused
        statusLeft.isHidden = focused
        statusNormalTrailing.isActive = !reading && !focused; statusReadingTrailing.isActive = reading || focused
        editorScroll.documentView = reading ? preview : editor
        ruler.isHidden = reading || !showLineNumbers; rulerWidth.constant = reading || !showLineNumbers ? 0 : 48
        window?.contentView?.layoutSubtreeIfNeeded()
    }
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
        editor.font = editorFont(); scheduleHighlight(); if reading { renderReading() }
    }
    @objc func increaseFont(_ sender: Any?) { changeFont(1) }
    @objc func decreaseFont(_ sender: Any?) { changeFont(-1) }
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
        guard a.runModal() == .alertFirstButtonReturn else { return }
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
        if reading { renderReading() } else { editor.font = editorFont(); updateWrap(); scheduleHighlight() }
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
        if selected.length > 0 && NSIntersectionRange(visible, selected).length > 0 { return selected.location }
        return min(visible.location, view.string.utf16.count)
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
        editorScroll.contentView.scroll(to: NSPoint(x: 0, y: max(0, y + view.textContainerOrigin.y - 12)))
        editorScroll.reflectScrolledClipView(editorScroll.contentView)
        tab.sourceAnchor = min(max(0, offset), tab.file.text.utf16.count)
    }
    private func updateReadingWidth() {
        guard reading else { return }
        let available = max(100, editorScroll.contentSize.width)
        let width = min(max(100, available - 48), readingWidth)
        let margin = max(24, (available - width) / 2)
        preview.textContainerInset = NSSize(width: margin, height: 26)
        preview.frame.size.width = available
        preview.textContainer?.widthTracksTextView = false
        preview.textContainer?.containerSize = NSSize(width: width, height: CGFloat.greatestFiniteMagnitude)
    }
    func windowWillStartLiveResize(_ notification: Notification) { liveResizeAnchor = currentSourceOffset() }
    func windowDidEndLiveResize(_ notification: Notification) {
        if let anchor = liveResizeAnchor, reading { updateReadingWidth(); scrollToSource(anchor) }
        liveResizeAnchor = nil
    }
    func windowDidResize(_ notification: Notification) {
        guard reading, let anchor = liveResizeAnchor ?? active?.sourceAnchor else { updateWrap(); return }
        updateReadingWidth(); scrollToSource(anchor)
    }
    private func renderReading() {
        guard let tab = active else { return }
        let anchor = tab.sourceAnchor
        let document = ReadingDocument.build(text: tab.file.text, kind: tab.kind, palette: theme.palette, fontSize: fontSize + 1, readingFontName: readingFontName, lineSpacing: readingLineSpacing)
        tab.rendered = document
        preview.textStorage?.setAttributedString(document.attributedText)
        preview.frame.size.width = editorScroll.contentSize.width
        updateReadingWidth(); scrollToSource(anchor); scheduleSearch()
    }
    private func refreshAutomaticKind(_ tab: ReaderTab) {
        tab.updateKindAfterPathChange()
        kindPopup.selectItem(at: tab.automaticKind ? 0 : (TextKind.allCases.firstIndex(of: tab.kind) ?? 0) + 1)
        tab.rendered = nil
        if reading { renderReading() } else { scheduleHighlight() }
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
        searchVisible = true; searchBar.isHidden = false; searchOptionsRow.isHidden = false; searchHeight.constant = 66
        if displayedTextView.selectedRange().length > 0 { searchField.stringValue = (displayedTextView.string as NSString).substring(with: displayedTextView.selectedRange()) }
        scheduleSearch(); window?.makeFirstResponder(searchField)
    }
    private func hideFind() {
        searchVisible = false; searchBar.isHidden = true; searchOptionsRow.isHidden = true; searchHeight.constant = 0
        searchVersion += 1; searchTask?.cancel(); searchMatches = []; searchReady = false; pendingFind = nil
        paintSearchMatches(); window?.makeFirstResponder(displayedTextView)
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
        guard !reading, searchReady else { return }
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
        guard !reading, searchReady, !searchMatches.isEmpty else { return }
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
        guard a.runModal() == .alertFirstButtonReturn, let number = Int(input.stringValue), number > 0 else { return }
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
        scrollToSource(location); window?.makeFirstResponder(displayedTextView); updateStatus()
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
            scroll.documentView = textView; alert.accessoryView = scroll; alert.runModal()
        }
    }
    private func scheduleRecovery() {
        recoveryTask?.cancel()
        let task = DispatchWorkItem { [weak self] in self?.writeRecovery() }; recoveryTask = task
        DispatchQueue.main.asyncAfter(deadline: .now() + 2, execute: task)
    }
    private func writeRecovery() {
        guard !shuttingDown else { return }
        do { try TextFile.writeRecovery(tabs.map { $0.file }.filter { $0.isDirty && (!$0.text.isEmpty || $0.url != nil) }, to: recoveryURL) }
        catch { updateStatus("恢复草稿写入失败 · 请及时保存") }
    }
    private func restoreRecovery() {
        guard FileManager.default.fileExists(atPath: recoveryURL.path) else { return }
        do {
            let files = try TextFile.recovery(from: recoveryURL)
            if !files.isEmpty { tabs = files.map { ReaderTab($0) }; selectTab(0, stash: false); updateStatus("已恢复 \(files.count) 份未保存草稿") }
        } catch { showError(error) }
    }
    private func alert(_ title: String, _ detail: String, buttons: [String]) -> NSApplication.ModalResponse {
        let a = NSAlert(); a.messageText = title; a.informativeText = detail; a.alertStyle = .warning
        buttons.forEach { a.addButton(withTitle: $0) }; return a.runModal()
    }
    private func showError(_ error: Error) {
        let a = NSAlert(); a.messageText = "未能完成操作"; a.informativeText = error.localizedDescription; a.alertStyle = .warning; a.addButton(withTitle: "知道了"); a.runModal()
        updateStatus("操作未完成，编辑内容已保留")
    }
}
