import AppKit

struct ReaderPreferencesState: Equatable {
    var themeID: ReaderThemeID
    var dark: Bool
    var followSystem: Bool
    var showLineNumbers: Bool
    var wrapping: Bool
    var fontSize: CGFloat
    var readingFontName: String
    var readingLineSpacing: CGFloat
    var readingParagraphSpacing: CGFloat
    var readingWidth: CGFloat
    var iconTheme: AppIconTheme

    static func defaults(dark: Bool) -> ReaderPreferencesState {
        ReaderPreferencesState(themeID: .classic, dark: dark, followSystem: true,
                               showLineNumbers: true, wrapping: true, fontSize: 14,
                               readingFontName: "PingFang SC", readingLineSpacing: 6,
                               readingParagraphSpacing: 13, readingWidth: 680, iconTheme: .sky)
    }
}

private func systemDarkAppearance() -> Bool {
    NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
}

private func preferencesLabel(_ text: String, size: CGFloat = 12, color: NSColor = .secondaryLabelColor) -> NSTextField {
    let field = NSTextField(labelWithString: text)
    field.font = .systemFont(ofSize: size)
    field.textColor = color
    return field
}

private func preferencesButton(_ title: String, target: AnyObject, action: Selector, primary: Bool = false) -> NSButton {
    let button = NSButton(title: title, target: target, action: action)
    button.bezelStyle = .rounded
    button.font = .systemFont(ofSize: 12)
    if primary { button.keyEquivalent = "\r" }
    return button
}

/// A custom card is appropriate here because the swatches are the content of
/// the control, not decorative window chrome.
final class ThemeCardButton: NSButton {
    let themeID: ReaderThemeID
    var dark: Bool
    var selected: Bool { didSet { needsDisplay = true } }
    var onSelect: (() -> Void)?

    init(themeID: ReaderThemeID, dark: Bool, selected: Bool) {
        self.themeID = themeID
        self.dark = dark
        self.selected = selected
        super.init(frame: .zero)
        isBordered = false
        title = ""
        target = self
        action = #selector(selectTheme)
        translatesAutoresizingMaskIntoConstraints = false
        setAccessibilityLabel(themeID.label)
        setAccessibilityHelp(themeID.detail)
        toolTip = themeID.detail
        heightAnchor.constraint(equalToConstant: 78).isActive = true
        widthAnchor.constraint(equalToConstant: 176).isActive = true
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }
    @objc private func selectTheme() { onSelect?() }

    func update(dark: Bool, selected: Bool) {
        self.dark = dark
        self.selected = selected
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        let theme = ReaderTheme(id: themeID, dark: dark)
        let rect = bounds.insetBy(dx: 1, dy: 1)
        let path = NSBezierPath(roundedRect: rect, xRadius: 10, yRadius: 10)
        (selected ? theme.selection : theme.panel).setFill()
        path.fill()
        (selected ? theme.accent : theme.border).setStroke()
        path.lineWidth = selected ? 2 : 1
        path.stroke()

        let swatches = themeID.swatches
        let swatchRect = NSRect(x: 12, y: bounds.height - 34, width: bounds.width - 24, height: 18)
        let swatchWidth = swatchRect.width / CGFloat(swatches.count)
        for (index, value) in swatches.enumerated() {
            let r = NSRect(x: swatchRect.minX + CGFloat(index) * swatchWidth,
                           y: swatchRect.minY, width: swatchWidth - 2, height: swatchRect.height)
            NSColor(hex: value).setFill()
            NSBezierPath(roundedRect: r, xRadius: 4, yRadius: 4).fill()
        }

        NSAttributedString(string: themeID.label, attributes: [
            .font: NSFont.systemFont(ofSize: 12.5, weight: .medium),
            .foregroundColor: theme.text
        ]).draw(at: NSPoint(x: 12, y: 26))
        NSAttributedString(string: themeID.detail, attributes: [
            .font: NSFont.systemFont(ofSize: 10.5),
            .foregroundColor: theme.muted
        ]).draw(at: NSPoint(x: 12, y: 10))
    }
}

private final class ThemePreviewPanel: NSView {
    let textView = NSTextView()
    private let scrollView = NSScrollView()
    private let titleField: NSTextField
    private var theme = ReaderTheme(id: .classic, dark: false)

    init(title: String) {
        titleField = preferencesLabel(title, size: 11)
        super.init(frame: .zero)
        translatesAutoresizingMaskIntoConstraints = false
        addSubview(titleField)
        textView.isEditable = false
        textView.isSelectable = true
        textView.setAccessibilityLabel(title + "预览")
        textView.drawsBackground = false
        textView.textContainerInset = NSSize(width: 4, height: 4)
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.autoresizingMask = [.width]
        textView.minSize = NSSize(width: 0, height: 0)
        textView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        textView.textContainer?.widthTracksTextView = true
        textView.textContainer?.containerSize = NSSize(width: 0, height: CGFloat.greatestFiniteMagnitude)
        textView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.hasVerticalScroller = true
        scrollView.autohidesScrollers = true
        scrollView.scrollerStyle = .overlay
        scrollView.drawsBackground = false
        scrollView.borderType = .noBorder
        scrollView.documentView = textView
        scrollView.setAccessibilityLabel(title + "预览滚动区域")
        addSubview(scrollView)
        titleField.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            titleField.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 12),
            titleField.topAnchor.constraint(equalTo: topAnchor, constant: 10),
            scrollView.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 12),
            scrollView.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -12),
            scrollView.topAnchor.constraint(equalTo: titleField.bottomAnchor, constant: 6),
            scrollView.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -10)
        ])
        // Keep a useful viewport in the settings window while allowing long
        // samples and large fonts to scroll inside each preview independently.
        heightAnchor.constraint(greaterThanOrEqualToConstant: 190).isActive = true
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }

    func update(theme: ReaderTheme, attributed: NSAttributedString) {
        self.theme = theme
        textView.textStorage?.setAttributedString(attributed)
        textView.backgroundColor = .clear
        textView.textColor = theme.text
        titleField.textColor = theme.muted
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        let path = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 10, yRadius: 10)
        theme.editor.setFill(); path.fill()
        theme.border.setStroke(); path.lineWidth = 1; path.stroke()
    }
}

final class PreferencesWindowController: NSWindowController {
    var onChange: ((ReaderPreferencesState) -> Void)?
    private var state: ReaderPreferencesState
    private var themeCards: [ThemeCardButton] = []
    private let readingPreview = ThemePreviewPanel(title: "阅读模式")
    private let codePreview = ThemePreviewPanel(title: "编辑模式")
    private let lineNumbersCheck = NSButton(checkboxWithTitle: "显示行号", target: nil, action: nil)
    private let wrappingCheck = NSButton(checkboxWithTitle: "自动换行", target: nil, action: nil)
    private let followSystemCheck = NSButton(checkboxWithTitle: "外观跟随系统", target: nil, action: nil)
    private let darkCheck = NSButton(checkboxWithTitle: "深色外观", target: nil, action: nil)
    private let themePopup = NSPopUpButton()
    private let iconPopup = NSPopUpButton()
    private let fontSizePopup = NSPopUpButton()
    private let fontPopup = NSPopUpButton()
    private let spacingPopup = NSPopUpButton()
    private let paragraphPopup = NSPopUpButton()
    private let widthPopup = NSPopUpButton()
    private var applyingState = false

    init(state: ReaderPreferencesState) {
        self.state = state
        let panel = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 900, height: 740),
                             styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        panel.title = "阅读外观与编辑设置"
        panel.minSize = NSSize(width: 820, height: 620)
        panel.isReleasedWhenClosed = false
        panel.tabbingMode = .disallowed
        super.init(window: panel)
        buildInterface()
        update(state: state)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }

    private func buildInterface() {
        guard let content = window?.contentView else { return }
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = false
        scroll.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(scroll)
        NSLayoutConstraint.activate([
            scroll.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            scroll.topAnchor.constraint(equalTo: content.topAnchor),
            scroll.bottomAnchor.constraint(equalTo: content.bottomAnchor)
        ])
        let document = NSView()
        document.translatesAutoresizingMaskIntoConstraints = false
        scroll.documentView = document
        document.widthAnchor.constraint(equalTo: scroll.contentView.widthAnchor).isActive = true

        let stack = NSStackView()
        stack.orientation = .vertical
        stack.alignment = .width
        stack.spacing = 18
        stack.translatesAutoresizingMaskIntoConstraints = false
        document.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: document.leadingAnchor, constant: 22),
            stack.trailingAnchor.constraint(equalTo: document.trailingAnchor, constant: -22),
            stack.topAnchor.constraint(equalTo: document.topAnchor, constant: 20),
            stack.bottomAnchor.constraint(equalTo: document.bottomAnchor, constant: -20)
        ])

        let heading = NSStackView(views: [
            { let f = NSTextField(labelWithString: "外观主题"); f.font = .systemFont(ofSize: 19, weight: .semibold); return f }(),
            preferencesLabel("设置会立即应用到主窗口；阅读与编辑共用 token，但分别预览。", size: 11.5)
        ])
        heading.orientation = .vertical
        heading.alignment = .leading
        heading.spacing = 4
        stack.addArrangedSubview(heading)

        let themeGrid = NSStackView()
        themeGrid.orientation = .vertical
        themeGrid.alignment = .leading
        themeGrid.spacing = 10
        let ids = ReaderThemeID.allCases
        var index = 0
        while index < ids.count {
            let row = NSStackView()
            row.orientation = .horizontal
            row.alignment = .centerY
            row.spacing = 10
            row.distribution = .fill
            for id in ids[index..<min(index + 4, ids.count)] {
                let card = ThemeCardButton(themeID: id, dark: effectiveDark, selected: id == state.themeID)
                card.onSelect = { [weak self] in self?.selectTheme(id) }
                themeCards.append(card)
                row.addArrangedSubview(card)
            }
            themeGrid.addArrangedSubview(row)
            index += 4
        }
        stack.addArrangedSubview(themeGrid)

        let previews = NSStackView(views: [readingPreview, codePreview])
        previews.orientation = .horizontal
        previews.alignment = .top
        previews.distribution = .fillEqually
        previews.spacing = 12
        stack.addArrangedSubview(previews)

        for check in [lineNumbersCheck, wrappingCheck, followSystemCheck, darkCheck] {
            check.font = .systemFont(ofSize: 12)
            check.target = self
            check.action = #selector(settingsChanged)
        }
        themePopup.addItems(withTitles: ReaderThemeID.allCases.map { $0.label })
        themePopup.setAccessibilityLabel("外观主题")
        iconPopup.addItems(withTitles: AppIconTheme.allCases.map { $0.label })
        iconPopup.setAccessibilityLabel("应用图标")
        fontSizePopup.addItems(withTitles: (10...30).map(String.init))
        fontSizePopup.setAccessibilityLabel("字号")
        fontPopup.addItems(withTitles: ["苹方", "宋体", "系统字体"])
        fontPopup.setAccessibilityLabel("阅读字体")
        spacingPopup.addItems(withTitles: ["紧凑", "舒适", "宽松"])
        spacingPopup.setAccessibilityLabel("阅读行距")
        paragraphPopup.addItems(withTitles: ["无段距", "紧凑", "标准", "宽松"])
        paragraphPopup.setAccessibilityLabel("段间距")
        widthPopup.addItems(withTitles: ["紧凑", "标准", "宽", "全宽"])
        widthPopup.setAccessibilityLabel("阅读版心")
        for popup in [themePopup, iconPopup, fontSizePopup, fontPopup, spacingPopup, paragraphPopup, widthPopup] {
            popup.font = .systemFont(ofSize: 12)
            popup.target = self
            popup.action = #selector(settingsChanged)
        }

        func row(_ views: [NSView]) -> NSStackView {
            let row = NSStackView(views: views)
            row.orientation = .horizontal
            row.alignment = .centerY
            row.spacing = 9
            return row
        }
        let checks = row([lineNumbersCheck, wrappingCheck, followSystemCheck, darkCheck])
        checks.spacing = 18
        let themeRow = row([preferencesLabel("外观主题"), themePopup, preferencesLabel("应用图标"), iconPopup])
        let typeRow = row([preferencesLabel("字号"), fontSizePopup, preferencesLabel("阅读字体"), fontPopup,
                           preferencesLabel("行距"), spacingPopup, preferencesLabel("段距"), paragraphPopup])
        let widthRow = row([preferencesLabel("阅读版心"), widthPopup])
        let settings = NSStackView(views: [checks, themeRow, typeRow, widthRow])
        settings.orientation = .vertical
        settings.alignment = .leading
        settings.spacing = 12
        stack.addArrangedSubview(settings)

        let spacer = NSView()
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let footer = NSStackView(views: [preferencesButton("恢复默认", target: self, action: #selector(restoreDefaults)), spacer,
                                         preferencesButton("完成", target: self, action: #selector(closePreferences), primary: true)])
        footer.orientation = .horizontal
        footer.alignment = .centerY
        footer.spacing = 10
        stack.addArrangedSubview(footer)
    }

    private var effectiveDark: Bool { state.followSystem ? systemDarkAppearance() : state.dark }

    private func selectTheme(_ id: ReaderThemeID) {
        state.themeID = id
        refreshUI()
        onChange?(state)
    }

    private func refreshUI() {
        let theme = ReaderTheme(id: state.themeID, dark: effectiveDark)
        window?.appearance = NSAppearance(named: effectiveDark ? .darkAqua : .aqua)
        for card in themeCards { card.update(dark: effectiveDark, selected: card.themeID == state.themeID) }
        lineNumbersCheck.state = state.showLineNumbers ? .on : .off
        wrappingCheck.state = state.wrapping ? .on : .off
        followSystemCheck.state = state.followSystem ? .on : .off
        darkCheck.state = effectiveDark ? .on : .off
        darkCheck.isEnabled = !state.followSystem
        themePopup.selectItem(at: ReaderThemeID.allCases.firstIndex(of: state.themeID) ?? 0)
        iconPopup.selectItem(at: AppIconTheme.allCases.firstIndex(of: state.iconTheme) ?? 0)
        fontSizePopup.selectItem(withTitle: String(Int(state.fontSize)))
        fontPopup.selectItem(at: state.readingFontName == "Songti SC" ? 1 : (state.readingFontName == "system" ? 2 : 0))
        spacingPopup.selectItem(at: state.readingLineSpacing < 4 ? 0 : (state.readingLineSpacing > 8 ? 2 : 1))
        paragraphPopup.selectItem(at: state.readingParagraphSpacing < 1 ? 0 : state.readingParagraphSpacing < 10 ? 1 : state.readingParagraphSpacing > 18 ? 3 : 2)
        widthPopup.selectItem(at: state.readingWidth > 1000 ? 3 : (state.readingWidth < 600 ? 0 : (state.readingWidth > 750 ? 2 : 1)))

        let sample = "# 在山中喝茶\n\n清晨的雾还没有散，窗外的竹叶被风轻轻翻动。\n\n> 阅读不是逃离，而是把注意力还给当下。\n"
        let reading = ReadingDocument.build(text: sample, kind: .markdown, palette: theme.palette,
                                            fontSize: max(13, state.fontSize), readingFontName: state.readingFontName,
                                            lineSpacing: state.readingLineSpacing,
                                            paragraphSpacing: state.readingParagraphSpacing)
        readingPreview.update(theme: theme, attributed: reading.attributedText)

        let accentHex = String(format: "%06X", ReaderThemeCatalog.definition(for: state.themeID, dark: effectiveDark).accent)
        let code = "{\n  \"theme\": \"\(state.themeID.label)\",\n  \"accent\": \"#\(accentHex)\",\n  \"reading\": true\n}\n"
        let storage = NSTextStorage(string: code, attributes: [
            .font: NSFont.monospacedSystemFont(ofSize: 11.5, weight: .regular),
            .foregroundColor: theme.text
        ])
        TextFeatures.highlight(storage, kind: .json, palette: theme.palette,
                               font: NSFont.monospacedSystemFont(ofSize: 11.5, weight: .regular))
        codePreview.update(theme: theme, attributed: NSAttributedString(attributedString: storage))
    }

    @objc private func settingsChanged() {
        guard !applyingState else { return }
        state.showLineNumbers = lineNumbersCheck.state == .on
        state.wrapping = wrappingCheck.state == .on
        state.followSystem = followSystemCheck.state == .on
        if state.followSystem { state.dark = systemDarkAppearance() }
        else { state.dark = darkCheck.state == .on }
        state.themeID = ReaderThemeID.allCases[max(0, min(themePopup.indexOfSelectedItem, ReaderThemeID.allCases.count - 1))]
        state.iconTheme = AppIconTheme.allCases[max(0, min(iconPopup.indexOfSelectedItem, AppIconTheme.allCases.count - 1))]
        state.fontSize = CGFloat(Int(fontSizePopup.titleOfSelectedItem ?? "14") ?? 14)
        state.readingFontName = ["PingFang SC", "Songti SC", "system"][max(0, min(fontPopup.indexOfSelectedItem, 2))]
        state.readingLineSpacing = [2, 6, 11][max(0, min(spacingPopup.indexOfSelectedItem, 2))]
        state.readingParagraphSpacing = [0, 6, 13, 24][max(0, min(paragraphPopup.indexOfSelectedItem, 3))]
        state.readingWidth = [520, 680, 840, 1_000_000][max(0, min(widthPopup.indexOfSelectedItem, 3))]
        refreshUI()
        onChange?(state)
    }

    @objc private func restoreDefaults() {
        state = ReaderPreferencesState.defaults(dark: systemDarkAppearance())
        refreshUI()
        onChange?(state)
    }

    @objc private func closePreferences() { window?.performClose(nil) }

    func update(state: ReaderPreferencesState) {
        self.state = state
        applyingState = true
        refreshUI()
        applyingState = false
    }

    func present(relativeTo parent: NSWindow?) {
        if let parent = parent, let panel = window {
            let origin = NSPoint(x: parent.frame.midX - panel.frame.width / 2,
                                 y: parent.frame.midY - panel.frame.height / 2)
            panel.setFrameOrigin(origin)
        } else { window?.center() }
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
        window?.makeFirstResponder(nil)
    }
}
