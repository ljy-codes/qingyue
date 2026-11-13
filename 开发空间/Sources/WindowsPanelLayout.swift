import AppKit

/// Mirrors the Windows WrapPanel: context actions wrap at narrow window widths.
final class WindowsFlowToolbar: NSView {
    private(set) var arrangedSubviews: [NSView] = []
    func addArrangedSubview(_ view: NSView) {
        view.translatesAutoresizingMaskIntoConstraints = true
        addSubview(view); arrangedSubviews.append(view)
    }
    @discardableResult func arrange(width: CGFloat) -> CGFloat {
        var x: CGFloat = 0, y: CGFloat = 0
        let height: CGFloat = 32, gap: CGFloat = 8
        for view in arrangedSubviews where !view.isHidden {
            let intrinsic = view.intrinsicContentSize.width
            let desired = min(max(48, intrinsic > 0 ? intrinsic : view.fittingSize.width), max(48, width))
            if x > 0 && x + desired > width { x = 0; y += height + gap }
            view.frame = NSRect(x: x, y: y, width: desired, height: height)
            x += desired + gap
        }
        return max(height, y + height)
    }
    override var isFlipped: Bool { true }
    override func layout() { super.layout(); arrange(width: bounds.width) }
}

/// Windows recent-file rows show a filename and its folder on separate lines.
final class RecentFileRow: NSControl {
    let nameLabel = NSTextField(labelWithString: "")
    let folderLabel = NSTextField(labelWithString: "")
    var activate: (() -> Void)?
    var rowColor = NSColor.clear
    init(url: URL, foreground: NSColor, muted: NSColor, activate: @escaping () -> Void) {
        super.init(frame: .zero)
        self.activate = activate
        nameLabel.stringValue = url.lastPathComponent; folderLabel.stringValue = url.deletingLastPathComponent().lastPathComponent
        nameLabel.font = .systemFont(ofSize: 13); folderLabel.font = .systemFont(ofSize: 10)
        nameLabel.textColor = foreground; folderLabel.textColor = muted
        for field in [nameLabel, folderLabel] { field.lineBreakMode = .byTruncatingMiddle; field.translatesAutoresizingMaskIntoConstraints = false; addSubview(field) }
        translatesAutoresizingMaskIntoConstraints = false; heightAnchor.constraint(equalToConstant: 52).isActive = true
        NSLayoutConstraint.activate([
            nameLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 10), nameLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -10), nameLabel.topAnchor.constraint(equalTo: topAnchor, constant: 8),
            folderLabel.leadingAnchor.constraint(equalTo: nameLabel.leadingAnchor), folderLabel.trailingAnchor.constraint(equalTo: nameLabel.trailingAnchor), folderLabel.topAnchor.constraint(equalTo: nameLabel.bottomAnchor, constant: 4)
        ])
        toolTip = url.path; setAccessibilityElement(true); setAccessibilityRole(.button); setAccessibilityLabel(url.lastPathComponent + "，" + url.deletingLastPathComponent().path)
    }
    required init?(coder: NSCoder) { fatalError() }
    override var acceptsFirstResponder: Bool { true }
    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 { activate?() }
        else { window?.makeFirstResponder(self); needsDisplay = true }
    }
    override func keyDown(with event: NSEvent) { if event.keyCode == 36 { activate?() } else { super.keyDown(with: event) } }
    override func accessibilityPerformPress() -> Bool { activate?(); return true }
    override func draw(_ dirtyRect: NSRect) {
        if window?.firstResponder === self { rowColor.setFill(); NSBezierPath(roundedRect: bounds, xRadius: 5, yRadius: 5).fill() }
    }
}

final class WindowsPopupButton: NSPopUpButton {
    private var themeBackground = NSColor.controlBackgroundColor
    private var themeForeground = NSColor.labelColor
    private var themeBorder = NSColor.separatorColor
    func applyTheme(_ theme: ReaderTheme) {
        themeBackground = theme.editor; themeForeground = theme.text; themeBorder = theme.border
        contentTintColor = themeForeground; font = .systemFont(ofSize: 12); bezelStyle = .rounded; isBordered = true
    }
    override func draw(_ dirtyRect: NSRect) {
        themeBackground.setFill(); dirtyRect.fill()
        themeBorder.setStroke(); NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 4, yRadius: 4).stroke()
        super.draw(dirtyRect)
    }
}
