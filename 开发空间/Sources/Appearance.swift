import AppKit

extension NSColor {
    convenience init(hex: UInt32) {
        self.init(srgbRed: CGFloat((hex >> 16) & 255) / 255,
                  green: CGFloat((hex >> 8) & 255) / 255,
                  blue: CGFloat(hex & 255) / 255, alpha: 1)
    }
}

struct ReaderTheme {
    let dark: Bool
    // Tokens mirror Windows MainWindow.ApplyTheme and Theme.xaml. Keeping the
    // values here avoids a second visual identity on the native Mac client.
    var window: NSColor { NSColor(hex: dark ? 0x2B2B2B : 0xF3F5F8) }
    var panel: NSColor { NSColor(hex: dark ? 0x333333 : 0xE9EDF3) }
    var editor: NSColor { NSColor(hex: dark ? 0x3F3F3F : 0xFFFFFF) }
    var text: NSColor { NSColor(hex: dark ? 0xDCDCCC : 0x17253B) }
    var muted: NSColor { NSColor(hex: dark ? 0xBDBDB5 : 0x53647A) }
    var border: NSColor { NSColor(hex: dark ? 0x585858 : 0xCFD7E3) }
    var accent: NSColor { NSColor(hex: dark ? 0x4782FF : 0x2464DB) }
    var selection: NSColor { NSColor(hex: dark ? 0x365579 : 0xCFDFFF) }
    var hover: NSColor { NSColor(hex: dark ? 0x4A4A4A : 0xDFE7F4) }
    var button: NSColor { panel }
    var tabSelected: NSColor { panel }
    var accentText: NSColor { NSColor(hex: dark ? 0x88C8FF : 0x1F60BD) }
    var unsaved: NSColor { NSColor(hex: dark ? 0xFFBE72 : 0xA95A00) }
    var saved: NSColor { NSColor(hex: dark ? 0x74D6AD : 0x19764E) }
    var palette: EditorPalette {
        EditorPalette(background: editor, foreground: text, muted: muted, accent: accentText,
                      string: NSColor(hex: dark ? 0xFFD096 : 0xA64A0A),
                      number: NSColor(hex: dark ? 0x74DEC7 : 0x087563),
                      comment: NSColor(hex: dark ? 0xBAC5BA : 0x5C6D59),
                      key: NSColor(hex: dark ? 0x88C8FF : 0x175BB5))
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
    var cancelHandler: (() -> Void)?
    override func cancelOperation(_ sender: Any?) {
        if hasMarkedText() { super.cancelOperation(sender); return }
        if let action = cancelHandler { action() } else { super.cancelOperation(sender) }
    }
    override var undoManager: UndoManager? { tabUndo }
    override func insertTab(_ sender: Any?) {
        if hasMarkedText() { super.insertTab(sender); return }
        applyEdit(TextEditing.indent(text: string, selection: selectedRange(), remove: false))
    }
    override func insertBacktab(_ sender: Any?) { applyEdit(TextEditing.indent(text: string, selection: selectedRange(), remove: true)) }
    override func insertNewline(_ sender: Any?) {
        if hasMarkedText() { super.insertNewline(sender); return }
        applyEdit(TextEditing.newline(text: string, selection: selectedRange()))
    }
    private func applyEdit(_ edit: TextEditResult?) {
        guard let edit = edit, isEditable else { return }
        breakUndoCoalescing()
        if shouldChangeText(in: edit.range, replacementString: edit.replacement) {
            replaceCharacters(in: edit.range, with: edit.replacement); didChangeText(); setSelectedRange(edit.selection)
        }
        breakUndoCoalescing()
    }
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        if sender.draggingPasteboard.canReadObject(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) { return .copy }
        return super.draggingEntered(sender)
    }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        if let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty {
            filesDropped?(urls); return true
        }
        return super.performDragOperation(sender)
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
