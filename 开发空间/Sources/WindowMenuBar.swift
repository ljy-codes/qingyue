import AppKit

struct WindowMenuGroup {
    let title: String
    let menu: NSMenu
}

/// A compact, window-local entry point to the same commands as the macOS menu.
/// Menus are supplied by the application so actions, shortcuts and validation
/// stay in one place. Opening a menu never installs a new text first responder.
final class WindowMenuBar: NSView {
    var prepareMenu: ((NSMenu) -> Void)?
    private let titles = NSStackView()
    private var menuButtons: [WindowMenuButton] = []
    private var foreground = NSColor.labelColor
    private var background = NSColor.windowBackgroundColor
    private var highlight = NSColor.selectedContentBackgroundColor.withAlphaComponent(0.16)

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        translatesAutoresizingMaskIntoConstraints = false
        setAccessibilityElement(false)
        titles.orientation = .horizontal
        titles.alignment = .centerY
        titles.spacing = 2
        titles.translatesAutoresizingMaskIntoConstraints = false
        addSubview(titles)
        NSLayoutConstraint.activate([
            titles.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 6),
            titles.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor, constant: -6),
            titles.centerYAnchor.constraint(equalTo: centerYAnchor),
            titles.heightAnchor.constraint(equalToConstant: 28)
        ])
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var intrinsicContentSize: NSSize {
        NSSize(width: menuButtons.reduce(12) { $0 + $1.intrinsicContentSize.width } + CGFloat(max(0, menuButtons.count - 1)) * titles.spacing,
               height: 30)
    }

    func setGroups(_ groups: [WindowMenuGroup]) {
        for old in titles.arrangedSubviews {
            titles.removeArrangedSubview(old)
            old.removeFromSuperview()
        }
        menuButtons.removeAll()
        for group in groups {
            let button = WindowMenuButton(title: group.title, menu: group.menu)
            button.openMenu = { [weak self] menu in self?.prepareMenu?(menu) }
            button.applyAppearance(foreground: foreground, highlight: highlight)
            titles.addArrangedSubview(button)
            menuButtons.append(button)
        }
        invalidateIntrinsicContentSize()
        needsDisplay = true
    }

    func applyAppearance(foreground: NSColor, background: NSColor, highlight: NSColor) {
        self.foreground = foreground
        self.background = background
        self.highlight = highlight
        for button in menuButtons { button.applyAppearance(foreground: foreground, highlight: highlight) }
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        background.setFill()
        dirtyRect.fill()
    }
}

private final class WindowMenuButton: NSButton {
    let commandMenu: NSMenu
    var openMenu: ((NSMenu) -> Void)?
    private var ink = NSColor.labelColor
    private var hoverColor = NSColor.selectedContentBackgroundColor.withAlphaComponent(0.16)
    private var hovered = false
    private var tracking: NSTrackingArea?
    private var menuOpen = false

    init(title: String, menu: NSMenu) {
        commandMenu = menu
        super.init(frame: .zero)
        self.title = title
        font = .systemFont(ofSize: 13, weight: .regular)
        setButtonType(.momentaryPushIn)
        isBordered = false
        target = self
        action = #selector(showCommandMenu(_:))
        translatesAutoresizingMaskIntoConstraints = false
        setContentHuggingPriority(.required, for: .horizontal)
        setContentCompressionResistancePriority(.required, for: .horizontal)
        setAccessibilityLabel(title + "菜单")
        setAccessibilityHelp("打开" + title + "命令")
        toolTip = title
        heightAnchor.constraint(equalToConstant: 28).isActive = true
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var intrinsicContentSize: NSSize {
        let textWidth = (title as NSString).size(withAttributes: [.font: font ?? NSFont.systemFont(ofSize: 13)]).width
        return NSSize(width: max(48, ceil(textWidth) + 22), height: 28)
    }

    override var isEnabled: Bool { didSet { needsDisplay = true } }

    func applyAppearance(foreground: NSColor, highlight: NSColor) {
        ink = foreground
        hoverColor = highlight
        needsDisplay = true
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let old = tracking { removeTrackingArea(old) }
        let area = NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self)
        addTrackingArea(area)
        tracking = area
    }

    override func mouseEntered(with event: NSEvent) { hovered = true; needsDisplay = true }
    override func mouseExited(with event: NSEvent) { hovered = false; needsDisplay = true }

    @objc private func showCommandMenu(_ sender: Any?) {
        guard isEnabled, window != nil else { return }
        openMenu?(commandMenu)
        commandMenu.update()
        menuOpen = true
        needsDisplay = true
        defer { menuOpen = false; needsDisplay = true }
        // Local button coordinates are unflipped; y=0 is its lower edge.
        commandMenu.popUp(positioning: nil, at: NSPoint(x: 0, y: -2), in: self)
    }

    override func draw(_ dirtyRect: NSRect) {
        if isEnabled && (hovered || isHighlighted || menuOpen) {
            hoverColor.setFill()
            NSBezierPath(roundedRect: bounds.insetBy(dx: 1, dy: 2), xRadius: 5, yRadius: 5).fill()
        }
        let attributes: [NSAttributedString.Key: Any] = [
            .font: font ?? NSFont.systemFont(ofSize: 13),
            .foregroundColor: isEnabled ? ink : ink.withAlphaComponent(0.35)
        ]
        let text = NSAttributedString(string: title, attributes: attributes)
        let size = text.size()
        text.draw(at: NSPoint(x: (bounds.width - size.width) / 2, y: (bounds.height - size.height) / 2))
        if window?.firstResponder === self {
            NSColor.keyboardFocusIndicatorColor.setStroke()
            let ring = NSBezierPath(roundedRect: bounds.insetBy(dx: 2, dy: 3), xRadius: 4, yRadius: 4)
            ring.lineWidth = 2
            ring.stroke()
        }
    }
}
