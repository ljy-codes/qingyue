import AppKit

extension NSColor {
    convenience init(hex: UInt32) {
        self.init(srgbRed: CGFloat((hex >> 16) & 255) / 255,
                  green: CGFloat((hex >> 8) & 255) / 255,
                  blue: CGFloat(hex & 255) / 255, alpha: 1)
    }
}

extension NSColor {
    var readerRelativeLuminance: CGFloat {
        guard let color = usingColorSpace(.sRGB) else { return 0 }
        func channel(_ value: CGFloat) -> CGFloat {
            value <= 0.03928 ? value / 12.92 : pow((value + 0.055) / 1.055, 2.4)
        }
        return 0.2126 * channel(color.redComponent)
            + 0.7152 * channel(color.greenComponent)
            + 0.0722 * channel(color.blueComponent)
    }

    func readerContrastRatio(with other: NSColor) -> CGFloat {
        let first = readerRelativeLuminance, second = other.readerRelativeLuminance
        return (max(first, second) + 0.05) / (min(first, second) + 0.05)
    }

    func readerMixed(with other: NSColor, fraction: CGFloat) -> NSColor {
        guard let a = usingColorSpace(.sRGB), let b = other.usingColorSpace(.sRGB) else { return self }
        let t = min(1, max(0, fraction))
        return NSColor(srgbRed: a.redComponent + (b.redComponent - a.redComponent) * t,
                       green: a.greenComponent + (b.greenComponent - a.greenComponent) * t,
                       blue: a.blueComponent + (b.blueComponent - a.blueComponent) * t,
                       alpha: 1)
    }

    func readerEnsureContrast(against background: NSColor, target: CGFloat) -> NSColor {
        var result = self
        let toward: NSColor = background.readerRelativeLuminance > 0.45 ? NSColor(hex: 0x11141A) : NSColor(hex: 0xFFFFFF)
        var guardCount = 0
        while result.readerContrastRatio(with: background) < target && guardCount < 28 {
            result = result.readerMixed(with: toward, fraction: 0.08)
            guardCount += 1
        }
        return result
    }
}

enum ReaderThemeID: String, CaseIterable, Codable {
    case classic, coffee, celadon, seaSalt, mistPurple, inkBlue, paper

    var label: String {
        switch self {
        case .classic: return "经典"
        case .coffee: return "咖色暖读"
        case .celadon: return "青瓷古色"
        case .seaSalt: return "海盐蓝"
        case .mistPurple: return "烟雨紫"
        case .inkBlue: return "墨蓝深色"
        case .paper: return "纸白极简"
        }
    }

    var detail: String {
        switch self {
        case .classic: return "原有蓝灰 / 暖纸"
        case .coffee: return "暖纸底 · 褐橘"
        case .celadon: return "玉色底 · 碧山绿"
        case .seaSalt: return "湖水蓝 · 釉蓝"
        case .mistPurple: return "烟雨白 · 紫幽兰"
        case .inkBlue: return "深蓝黑 · 珊瑚红"
        case .paper: return "白卡 · 深紫 / 青绿"
        }
    }

    var swatches: [UInt32] {
        switch self {
        case .classic: return [0xF6F4EF, 0xEFEAE2, 0x262A2F, 0x2F6FDB, 0xD7E5FA]
        case .coffee: return [0xEDE3D1, 0xFFF8E9, 0x3C3026, 0xA75832, 0xE8D2B6]
        case .celadon: return [0xEDF1E9, 0xFBFCFB, 0x2E3427, 0x719847, 0xDFE8D7]
        case .seaSalt: return [0xEAF4F7, 0xFFFFFF, 0x1B2B33, 0x1477A8, 0xCDEAF7]
        case .mistPurple: return [0xEFF0F5, 0xFCFCFE, 0x2B2A35, 0x686F8F, 0xE4E2F2]
        case .inkBlue: return [0xE9ECF1, 0xFBFBFC, 0x1E3B7A, 0xCA462F, 0xF1D7D3]
        case .paper: return [0xF3F4F6, 0xFFFFFF, 0x1F2937, 0x6D5BD0, 0xEDE9FE]
        }
    }

    static func stored(_ raw: String?, legacy: String? = nil) -> ReaderThemeID {
        if let raw = raw, let value = ReaderThemeID(rawValue: raw) { return value }
        switch (legacy ?? "").lowercased() {
        case "warmpaper": return .coffee
        case "dark", "light": return .classic
        default: return .classic
        }
    }
}

struct ReaderThemeDefinition {
    let window: UInt32
    let panel: UInt32
    let editor: UInt32
    let text: UInt32
    let muted: UInt32
    let border: UInt32
    let accent: UInt32
    let selection: UInt32
    let string: UInt32
    let number: UInt32
    let key: UInt32
    let comment: UInt32
    var main: UInt32? = nil
    var hover: UInt32? = nil
    var accentText: UInt32? = nil
    var unsaved: UInt32? = nil
    var saved: UInt32? = nil
}

enum ReaderThemeCatalog {
    static func definition(for id: ReaderThemeID, dark: Bool) -> ReaderThemeDefinition {
        switch id {
        case .classic:
            return dark
                ? ReaderThemeDefinition(window: 0x191B1F, panel: 0x23262B, editor: 0x30343A,
                                        text: 0xE8E2D8, muted: 0xBEB8AE, border: 0x4B4D52,
                                        accent: 0x2F6FDB, selection: 0x355A91,
                                        string: 0xFFD39A, number: 0x82DFC9, key: 0x8DC5FF, comment: 0xC3C9BF,
                                        main: 0x24354A, hover: 0x34383F, accentText: 0x8DC5FF,
                                        unsaved: 0xFFC078, saved: 0x7BDEB3)
                : ReaderThemeDefinition(window: 0xF6F4EF, panel: 0xEFEAE2, editor: 0xF8FAFC,
                                        text: 0x262A2F, muted: 0x6B6A66, border: 0xD8D1C5,
                                        accent: 0x2F6FDB, selection: 0xD7E5FA,
                                        string: 0xA64A0A, number: 0x087563, key: 0x175BB5, comment: 0x5C6D59,
                                        main: 0xE6F0FA, hover: 0xF3EDE4, accentText: 0x175BB5,
                                        unsaved: 0xA95A00, saved: 0x19764E)
        case .coffee:
            return dark
                ? ReaderThemeDefinition(window: 0x1A1310, panel: 0x241A15, editor: 0x1E1612,
                                        text: 0xEFDFD2, muted: 0xA58F7E, border: 0x3A2B23,
                                        accent: 0xE17A84, selection: 0x4A2D31,
                                        string: 0x9FCB9B, number: 0xD9A05B, key: 0x85B9EF, comment: 0x8C7667,
                                        main: 0x2A1F18, hover: 0x2F231B, accentText: 0xF0A0AA,
                                        unsaved: 0xFFC078, saved: 0x7BDEB3)
                : ReaderThemeDefinition(window: 0xEDE3D1, panel: 0xE5D6C0, editor: 0xFFF8E9,
                                        text: 0x3C3026, muted: 0x6A5C4D, border: 0xCDB99C,
                                        accent: 0xA75832, selection: 0xE8D2B6,
                                        string: 0xA64A0A, number: 0x087563, key: 0xA75832, comment: 0x5C6D59,
                                        main: 0xE7EEF0, hover: 0xF1E4D1, accentText: 0xA75832,
                                        unsaved: 0xA95A00, saved: 0x19764E)
        case .celadon:
            return dark
                ? ReaderThemeDefinition(window: 0x101612, panel: 0x18201B, editor: 0x141A16,
                                        text: 0xE4EEE5, muted: 0x94A799, border: 0x2B3A31,
                                        accent: 0x8FBF6B, selection: 0x31452F,
                                        string: 0x9FCB9B, number: 0xD9B26A, key: 0x85B9EF, comment: 0x8C9E92)
                : ReaderThemeDefinition(window: 0xEDF1E9, panel: 0xF6F8F4, editor: 0xFBFCFB,
                                        text: 0x2E3427, muted: 0x647267, border: 0xDDE3D8,
                                        accent: 0x719847, selection: 0xDFE8D7,
                                        string: 0x5B8C43, number: 0xA96A1E, key: 0x3E7C86, comment: 0x8A968B)
        case .seaSalt:
            return dark
                ? ReaderThemeDefinition(window: 0x0B1419, panel: 0x121D24, editor: 0x0F181E,
                                        text: 0xE3EEF4, muted: 0x8FA7B2, border: 0x23343D,
                                        accent: 0x4FB3E8, selection: 0x1E3E52,
                                        string: 0x9FCB9B, number: 0xD9B26A, key: 0x85B9EF, comment: 0x8198A4)
                : ReaderThemeDefinition(window: 0xEAF4F7, panel: 0xF5FAFB, editor: 0xFFFFFF,
                                        text: 0x1B2B33, muted: 0x62757E, border: 0xD5E4E8,
                                        accent: 0x1477A8, selection: 0xCDEAF7,
                                        string: 0x2F7D64, number: 0xA96A1E, key: 0x1660AB, comment: 0x7C929B)
        case .mistPurple:
            return dark
                ? ReaderThemeDefinition(window: 0x111118, panel: 0x191923, editor: 0x15151E,
                                        text: 0xE8E7F0, muted: 0x9C9BB2, border: 0x2D2D3D,
                                        accent: 0x9AA2D6, selection: 0x35354F,
                                        string: 0x9FCB9B, number: 0xD9B26A, key: 0x9AB3FF, comment: 0x8D8CA3)
                : ReaderThemeDefinition(window: 0xEFF0F5, panel: 0xF7F7FA, editor: 0xFCFCFE,
                                        text: 0x2B2A35, muted: 0x706F86, border: 0xDEDEE6,
                                        accent: 0x686F8F, selection: 0xE4E2F2,
                                        string: 0x5E7F6A, number: 0xA96A1E, key: 0x5B6FA8, comment: 0x8B8AA0)
        case .inkBlue:
            return dark
                ? ReaderThemeDefinition(window: 0x13151B, panel: 0x1C1F26, editor: 0x101114,
                                        text: 0xECE9E9, muted: 0x9AA0AD, border: 0x2B2F3A,
                                        accent: 0xCA462F, selection: 0x642920,
                                        string: 0x9FCB9B, number: 0xD9B26A, key: 0x85B9EF, comment: 0x8D93A0)
                : ReaderThemeDefinition(window: 0xE9ECF1, panel: 0xF4F5F8, editor: 0xFBFBFC,
                                        text: 0x1E3B7A, muted: 0x626E87, border: 0xD8DEE8,
                                        accent: 0xCA462F, selection: 0xF1D7D3,
                                        string: 0x5E7F6A, number: 0xA96A1E, key: 0x2A5FA8, comment: 0x8A93A6)
        case .paper:
            return dark
                ? ReaderThemeDefinition(window: 0x111318, panel: 0x171A21, editor: 0x14171D,
                                        text: 0xE7EAF0, muted: 0x9AA1AD, border: 0x2A2F38,
                                        accent: 0x9B8BFF, selection: 0x322B5A,
                                        string: 0x4FD1C5, number: 0xE3A15A, key: 0xB3A6FF, comment: 0x8A919C)
                : ReaderThemeDefinition(window: 0xF3F4F6, panel: 0xFFFFFF, editor: 0xFFFFFF,
                                        text: 0x1F2937, muted: 0x6B7280, border: 0xE5E7EB,
                                        accent: 0x6D5BD0, selection: 0xEDE9FE,
                                        string: 0x0F9F8F, number: 0xB45309, key: 0x6D5BD0, comment: 0x9CA3AF)
        }
    }
}

struct ReaderTheme {
    let id: ReaderThemeID
    let dark: Bool

    init(dark: Bool) { self.init(id: .classic, dark: dark) }
    init(dark: Bool, warmPaper: Bool) { self.init(id: warmPaper ? .coffee : .classic, dark: warmPaper ? false : dark) }
    init(id: ReaderThemeID, dark: Bool) { self.id = id; self.dark = dark }

    var warmPaper: Bool { id == .coffee }

    private var definition: ReaderThemeDefinition { ReaderThemeCatalog.definition(for: id, dark: dark) }

    var window: NSColor { NSColor(hex: definition.window) }
    var panel: NSColor { NSColor(hex: definition.panel) }
    var editor: NSColor { NSColor(hex: definition.editor) }
    var text: NSColor { NSColor(hex: definition.text) }
    var muted: NSColor { NSColor(hex: definition.muted) }
    var border: NSColor { NSColor(hex: definition.border) }
    var accent: NSColor { NSColor(hex: definition.accent) }
    var selection: NSColor { NSColor(hex: definition.selection) }
    var main: NSColor { definition.main.map { NSColor(hex: $0) } ?? panel.readerMixed(with: editor, fraction: 0.35) }
    var hover: NSColor { definition.hover.map { NSColor(hex: $0) } ?? panel.readerMixed(with: text, fraction: 0.06) }
    var button: NSColor { panel }
    var tabSelected: NSColor { panel }
    var accentText: NSColor { definition.accentText.map { NSColor(hex: $0) } ?? accent.readerEnsureContrast(against: editor, target: 4.5) }
    var unsaved: NSColor { NSColor(hex: definition.unsaved ?? (dark ? 0xFFC078 : 0xA95A00)) }
    var saved: NSColor { NSColor(hex: definition.saved ?? (dark ? 0x7BDEB3 : 0x19764E)) }
    var accentForeground: NSColor {
        let light = NSColor(hex: 0xFFFFFF), dark = NSColor(hex: 0x11141A)
        return light.readerContrastRatio(with: accent) >= dark.readerContrastRatio(with: accent) ? light : dark
    }
    var palette: EditorPalette {
        EditorPalette(background: editor, foreground: text, muted: muted, accent: accentText,
                      string: NSColor(hex: definition.string), number: NSColor(hex: definition.number),
                      comment: NSColor(hex: definition.comment), key: NSColor(hex: definition.key))
    }
}

final class ColorView: NSView {
    var color: NSColor = .clear { didSet { needsDisplay = true } }
    var radius: CGFloat = 0
    override func draw(_ dirtyRect: NSRect) {
        color.setFill()
        NSBezierPath(roundedRect: bounds, xRadius: radius, yRadius: radius).fill()
    }
}

final class FlippedClipView: NSClipView {
    override var isFlipped: Bool { true }
}

final class FlippedStackView: NSStackView {
    override var isFlipped: Bool { true }
}

class ActionButton: NSButton {
    override var isEnabled: Bool { didSet { needsDisplay = true } }
    override var title: String { didSet { setAccessibilityLabel(title); invalidateIntrinsicContentSize(); needsDisplay = true } }
    var actionHandler: (() -> Void)?
    var fill: NSColor = .clear { didSet { needsDisplay = true } }
    var ink: NSColor = .labelColor { didSet { needsDisplay = true } }
    var hoverFill: NSColor? { didSet { needsDisplay = true } }
    var borderColor: NSColor = .clear { didSet { needsDisplay = true } }
    var bottomAccent: NSColor? { didSet { needsDisplay = true } }
    var cornerRadius: CGFloat = 6 { didSet { needsDisplay = true } }
    var horizontalPadding: CGFloat = 14 { didSet { invalidateIntrinsicContentSize(); needsDisplay = true } }
    var controlHeight: CGFloat = 32 {
        didSet { heightConstraint?.constant = controlHeight; invalidateIntrinsicContentSize(); needsDisplay = true }
    }
    private var heightConstraint: NSLayoutConstraint?
    private var tracking: NSTrackingArea?
    private var hovered = false
    var isEmphasized = false
    init(_ title: String, symbol: String? = nil, action: @escaping () -> Void) {
        super.init(frame: .zero)
        self.title = title
        self.actionHandler = action
        self.target = self
        self.action = #selector(performAction)
        isBordered = false
        font = .systemFont(ofSize: 13, weight: .regular)
        setAccessibilityLabel(title)
        toolTip = title
        if let symbol = symbol { image = NSImage(systemSymbolName: symbol, accessibilityDescription: title); imagePosition = .imageLeading }
        translatesAutoresizingMaskIntoConstraints = false
        heightConstraint = heightAnchor.constraint(equalToConstant: controlHeight)
        heightConstraint?.isActive = true
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }
    override var intrinsicContentSize: NSSize {
        let measured = (title as NSString).size(withAttributes: [.font: font ?? NSFont.systemFont(ofSize: 13)])
        let imageWidth: CGFloat = image == nil ? 0 : (title.isEmpty ? 14 : 21)
        return NSSize(width: ceil(measured.width + horizontalPadding * 2 + imageWidth), height: controlHeight)
    }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let old = tracking { removeTrackingArea(old) }
        let area = NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self)
        addTrackingArea(area); tracking = area
    }
    override func mouseEntered(with event: NSEvent) { hovered = true; needsDisplay = true }
    override func mouseExited(with event: NSEvent) { hovered = false; needsDisplay = true }
    @objc private func performAction() { if isEnabled { actionHandler?() } }
    override func draw(_ dirtyRect: NSRect) {
        NSGraphicsContext.saveGraphicsState()
        defer { NSGraphicsContext.restoreGraphicsState() }
        if !isEnabled { NSGraphicsContext.current?.cgContext.setAlpha(0.42) }
        else if isHighlighted { NSGraphicsContext.current?.cgContext.setAlpha(0.75) }
        let bg = isEnabled && hovered ? hoverFill ?? fill : fill
        bg.setFill()
        let frame = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: cornerRadius, yRadius: cornerRadius)
        frame.fill()
        borderColor.setStroke(); frame.lineWidth = 1; frame.stroke()
        if let accent = bottomAccent {
            accent.setFill(); NSRect(x: 0, y: 0, width: bounds.width, height: 2).fill()
        }
        let drawInk = ink
        let attrs: [NSAttributedString.Key: Any] = [.font: font ?? NSFont.systemFont(ofSize: 13), .foregroundColor: drawInk]
        let style = NSMutableParagraphStyle()
        style.alignment = .center; style.lineBreakMode = .byTruncatingTail
        var textAttributes = attrs; textAttributes[.paragraphStyle] = style
        let str = NSAttributedString(string: title, attributes: textAttributes)
        let size = str.size()
        var x = (bounds.width - size.width) / 2
        if let image = image {
            let img = image.copy() as! NSImage
            img.isTemplate = false
            let imageSize: CGFloat = 14
            x -= title.isEmpty ? 0 : 10
            drawInk.setFill()
            let rect = NSRect(x: title.isEmpty ? (bounds.width - imageSize) / 2 : x,
                              y: (bounds.height - imageSize) / 2, width: imageSize, height: imageSize)
            img.lockFocus()
            drawInk.set()
            NSRect(origin: .zero, size: img.size).fill(using: .sourceAtop)
            img.unlockFocus()
            img.draw(in: rect)
            x += 21
        }
        // Windows' tab headers use CharacterEllipsis. Keep long filenames inside
        // their cell without drawing through the close button or a nearby tab.
        let leading = image == nil ? max(4, horizontalPadding) : max(4, x)
        let textRect = NSRect(x: leading + (image == nil ? 0 : 21),
                              y: (bounds.height - size.height) / 2,
                              width: max(0, bounds.width - leading - max(4, horizontalPadding) - (image == nil ? 0 : 21)),
                              height: size.height)
        str.draw(in: textRect)
        if window?.firstResponder === self {
            NSColor.keyboardFocusIndicatorColor.setStroke()
            let ring = NSBezierPath(roundedRect: bounds.insetBy(dx: 1, dy: 1), xRadius: cornerRadius, yRadius: cornerRadius)
            ring.lineWidth = 1; ring.stroke()
        }
    }
}

private let qingyueTabPasteboardType = NSPasteboard.PasteboardType("local.xiaoyu.LightReader.tab")

final class DraggableTabButton: ActionButton, NSDraggingSource {
    var tabID: UUID?
    var doubleClickHandler: (() -> Void)?
    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 { doubleClickHandler?(); return }
        guard let window = window, let id = tabID else { super.mouseDown(with: event); return }
        let start = event.locationInWindow
        while let next = window.nextEvent(matching: [.leftMouseDragged, .leftMouseUp]) {
            if next.type == .leftMouseUp {
                if bounds.contains(convert(next.locationInWindow, from: nil)) { performClick(nil) }
                return
            }
            let point = next.locationInWindow
            if hypot(point.x - start.x, point.y - start.y) >= 5 {
                let paste = NSPasteboardItem(); paste.setString(id.uuidString, forType: qingyueTabPasteboardType)
                let item = NSDraggingItem(pasteboardWriter: paste)
                let image = NSImage(size: bounds.size)
                image.lockFocus(); attributedTitle.draw(in: NSRect(origin: .zero, size: bounds.size)); image.unlockFocus()
                item.setDraggingFrame(bounds, contents: image)
                beginDraggingSession(with: [item], event: next, source: self)
                return
            }
        }
    }
    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation { context == .withinApplication ? .move : [] }
}

final class TabStripView: NSStackView {
    var reorder: ((UUID, Int) -> Void)?
    override init(frame frameRect: NSRect) { super.init(frame: frameRect); registerForDraggedTypes([qingyueTabPasteboardType]) }
    required init?(coder: NSCoder) { super.init(coder: coder); registerForDraggedTypes([qingyueTabPasteboardType]) }
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        sender.draggingPasteboard.string(forType: qingyueTabPasteboardType) == nil ? [] : .move
    }
    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation { draggingEntered(sender) }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        guard let value = sender.draggingPasteboard.string(forType: qingyueTabPasteboardType), let id = UUID(uuidString: value) else { return false }
        let x = convert(sender.draggingLocation, from: nil).x
        let insertion = arrangedSubviews.firstIndex(where: { x < $0.frame.midX }) ?? arrangedSubviews.count
        reorder?(id, insertion); return true
    }
}

final class EditorTextView: NSTextView {
    var tabUndo = UndoManager()
    var filesDropped: (([URL]) -> Void)?
    var imagesDropped: (([URL]) -> Bool)?
    var imagePasteHandler: (() -> Bool)?
    var listEditHandler: ((NSRange, MarkdownListAction) -> TextEditResult?)?
    var cancelHandler: (() -> Void)?
    override func cancelOperation(_ sender: Any?) {
        if hasMarkedText() { super.cancelOperation(sender); return }
        if let action = cancelHandler { action() } else { super.cancelOperation(sender) }
    }
    override var undoManager: UndoManager? { tabUndo }
    override func insertTab(_ sender: Any?) {
        if hasMarkedText() { super.insertTab(sender); return }
        if let edit = tableEditHandler?(selectedRange(), false) { applyEdit(edit); return }
        if let edit = listEditHandler?(selectedRange(), .indent) { applyEdit(edit); return }
        applyEdit(TextEditing.indent(text: string, selection: selectedRange(), remove: false))
    }
    override func insertBacktab(_ sender: Any?) {
        if hasMarkedText() { super.insertBacktab(sender); return }
        if let edit = tableEditHandler?(selectedRange(), true) { applyEdit(edit); return }
        if let edit = listEditHandler?(selectedRange(), .outdent) { applyEdit(edit); return }
        applyEdit(TextEditing.indent(text: string, selection: selectedRange(), remove: true))
    }
    override func insertNewline(_ sender: Any?) {
        if hasMarkedText() { super.insertNewline(sender); return }
        if let edit = tableNewlineHandler?(selectedRange()) { applyEdit(edit); return }
        if let edit = listEditHandler?(selectedRange(), .newline) { applyEdit(edit); return }
        applyEdit(TextEditing.newline(text: string, selection: selectedRange()))
    }
    @discardableResult private func applyEdit(_ edit: TextEditResult?) -> Bool {
        guard let edit = edit, isEditable, !hasMarkedText() else { return false }
        if edit.range.length == 0 && edit.replacement.isEmpty {
            setSelectedRange(edit.selection); scrollRangeToVisible(edit.selection); return true
        }
        breakUndoCoalescing()
        if shouldChangeText(in: edit.range, replacementString: edit.replacement) {
            replaceCharacters(in: edit.range, with: edit.replacement); didChangeText(); setSelectedRange(edit.selection)
            breakUndoCoalescing(); return true
        }
        breakUndoCoalescing(); return false
    }
    var tableEditHandler: ((NSRange, Bool) -> TextEditResult?)?
    var tableNewlineHandler: ((NSRange) -> TextEditResult?)?
    @discardableResult func applyExternalEdit(_ edit: TextEditResult) -> Bool { applyEdit(edit) }
    override func paste(_ sender: Any?) {
        if imagePasteHandler?() == true { return }
        super.paste(sender)
    }
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        if sender.draggingPasteboard.canReadObject(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) { return .copy }
        return super.draggingEntered(sender)
    }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        if let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty {
            let images = urls.filter { Self.isImageURL($0) }
            if !images.isEmpty, imagesDropped?(images) == true { return true }
            filesDropped?(urls); return true
        }
        return super.performDragOperation(sender)
    }
    private static func isImageURL(_ url: URL) -> Bool {
        ["png", "jpg", "jpeg", "gif", "tif", "tiff", "webp", "bmp", "heic"].contains(url.pathExtension.lowercased())
    }
}

final class LineNumberRuler: NSRulerView {
    var lineMarks: [Int: Bool] = [:]
    var savedInk = NSColor(hex: 0x74D6AD)
    var unsavedInk = NSColor(hex: 0xFFBE72)
    weak var editor: NSTextView?
    var ink = NSColor.secondaryLabelColor
    var background = NSColor.windowBackgroundColor
    var starts = [0]
    private var textLength = 0
    init(editor: NSTextView, scroll: NSScrollView) {
        self.editor = editor
        super.init(scrollView: scroll, orientation: .verticalRuler)
        clientView = editor
        ruleThickness = 48
    }
    required init(coder: NSCoder) { fatalError("init(coder:)") }
    func updateLines(_ text: String) {
        starts = [0]
        for (i, c) in text.utf16.enumerated() where c == 10 { starts.append(i + 1) }
        textLength = text.utf16.count
        needsDisplay = true
    }
    /// Called before an accepted text edit. Ranges and offsets use AppKit's UTF-16
    /// coordinates. A line start disappears exactly when its preceding LF is removed.
    func applyEdit(range: NSRange, replacement: String) {
        guard range.location != NSNotFound, range.location >= 0, range.length >= 0,
              range.location <= textLength, range.length <= textLength - range.location else { return }
        let end = range.location + range.length
        func upperBound(_ offset: Int) -> Int {
            var low = 0, high = starts.count
            while low < high {
                let middle = (low + high) / 2
                if starts[middle] <= offset { low = middle + 1 } else { high = middle }
            }
            return low
        }
        // Keep a start at the insertion point: its LF lies before the edit. Remove
        // starts through `end`: their LF lies within the removed half-open range.
        let first = upperBound(range.location)
        let after = upperBound(end)
        var inserted: [Int] = []
        var replacementLength = 0
        for unit in replacement.utf16 {
            replacementLength += 1
            if unit == 10 { inserted.append(range.location + replacementLength) }
        }
        let delta = replacementLength - range.length
        starts.replaceSubrange(first..<after, with: inserted)
        if delta != 0 {
            for index in (first + inserted.count)..<starts.count { starts[index] += delta }
        }
        textLength += delta
        needsDisplay = true
    }
    override func drawHashMarksAndLabels(in rect: NSRect) {
        background.setFill(); bounds.fill()
        guard let editor = editor, let lm = editor.layoutManager, let tc = editor.textContainer else { return }
        let visible = editor.visibleRect
        let inset = editor.textContainerOrigin
        let glyphs = lm.glyphRange(forBoundingRect: visible.offsetBy(dx: -inset.x, dy: -inset.y), in: tc)
        let chars = lm.characterRange(forGlyphRange: glyphs, actualGlyphRange: nil)
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .regular), .foregroundColor: ink]
        // Binary search limits drawing to visible logical lines.
        var low = 0, high = starts.count
        while low < high { let mid = (low + high) / 2; if starts[mid] < chars.location { low = mid + 1 } else { high = mid } }
        var i = max(0, low - 1)
        while i < starts.count && starts[i] <= NSMaxRange(chars) {
            let character = starts[i]
            var y: CGFloat
            if character >= lm.numberOfGlyphs && character == (editor.string as NSString).length {
                y = lm.extraLineFragmentRect.minY
            } else {
                let glyph = lm.glyphIndexForCharacter(at: min(character, max(0, (editor.string as NSString).length - 1)))
                y = lm.lineFragmentRect(forGlyphAt: glyph, effectiveRange: nil).minY
            }
            let label = NSAttributedString(string: "\(i + 1)", attributes: attrs)
            if let saved = lineMarks[i + 1] {
                (saved ? savedInk : unsavedInk).setFill()
                NSRect(x: 1, y: y + inset.y - visible.minY, width: 4, height: max(12, editor.font?.pointSize ?? 14)).fill()
            }
            let point = NSPoint(x: ruleThickness - label.size().width - 10, y: y + inset.y - visible.minY + 1)
            label.draw(at: point)
            i += 1
        }
    }
}
