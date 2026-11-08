import AppKit

extension NSColor {
    convenience init(hex: UInt32) {
        self.init(calibratedRed: CGFloat((hex >> 16) & 255) / 255,
                  green: CGFloat((hex >> 8) & 255) / 255,
                  blue: CGFloat(hex & 255) / 255, alpha: 1)
    }
}

struct ReaderTheme {
    let dark: Bool
    var window: NSColor { NSColor(hex: dark ? 0x11141A : 0xEFF2F7) }
    var panel: NSColor { NSColor(hex: dark ? 0x191D26 : 0xFFFFFF) }
    var editor: NSColor { NSColor(hex: dark ? 0x151920 : 0xFFFFFF) }
    var text: NSColor { NSColor(hex: dark ? 0xEDF1F8 : 0x243044) }
    var muted: NSColor { NSColor(hex: dark ? 0x929DB0 : 0x6B778C) }
    var border: NSColor { NSColor(hex: dark ? 0x2B3342 : 0xDDE3ED) }
    var accent: NSColor { NSColor(hex: 0x4782FF) }
    var selection: NSColor { NSColor(hex: dark ? 0x284674 : 0xDBE7FF) }
    var palette: EditorPalette {
        EditorPalette(background: editor, foreground: text, muted: muted, accent: accent,
                      string: NSColor(hex: dark ? 0x9FCB9B : 0x387545),
                      number: NSColor(hex: dark ? 0xD7ABEB : 0x8652A8),
                      comment: muted, key: NSColor(hex: dark ? 0x85B9EF : 0x246CAB))
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
    override var title: String { didSet { setAccessibilityLabel(title); needsDisplay = true } }
    var actionHandler: (() -> Void)?
    var fill: NSColor = .clear { didSet { needsDisplay = true } }
    var ink: NSColor = .labelColor { didSet { needsDisplay = true } }
    var isEmphasized = false
    init(_ title: String, symbol: String? = nil, action: @escaping () -> Void) {
        super.init(frame: .zero)
        self.title = title
        self.actionHandler = action
        self.target = self
        self.action = #selector(performAction)
        isBordered = false
        font = .systemFont(ofSize: 12, weight: .medium)
        setAccessibilityLabel(title)
        toolTip = title
        if let symbol = symbol { image = NSImage(systemSymbolName: symbol, accessibilityDescription: title); imagePosition = .imageLeading }
        translatesAutoresizingMaskIntoConstraints = false
        heightAnchor.constraint(equalToConstant: 32).isActive = true
        widthAnchor.constraint(greaterThanOrEqualToConstant: symbol == nil ? 48 : 74).isActive = true
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }
    @objc private func performAction() { if isEnabled { actionHandler?() } }
    override func draw(_ dirtyRect: NSRect) {
        let bg = isHighlighted ? fill.blended(withFraction: 0.15, of: ink) ?? fill : fill
        bg.setFill()
        NSBezierPath(roundedRect: bounds.insetBy(dx: 0, dy: 1), xRadius: 7, yRadius: 7).fill()
        let drawInk = isEnabled ? ink : ink.withAlphaComponent(0.35)
        let attrs: [NSAttributedString.Key: Any] = [.font: font!, .foregroundColor: drawInk]
        let str = NSAttributedString(string: title, attributes: attrs)
        let size = str.size()
        var x = (bounds.width - size.width) / 2
        if let image = image {
            let img = image.copy() as! NSImage
            img.isTemplate = false
            let imageSize: CGFloat = 14
            x -= 10
            drawInk.setFill()
            let rect = NSRect(x: x, y: (bounds.height - imageSize) / 2, width: imageSize, height: imageSize)
            img.lockFocus()
            drawInk.set()
            NSRect(origin: .zero, size: img.size).fill(using: .sourceAtop)
            img.unlockFocus()
            img.draw(in: rect)
            x += 21
        }
        str.draw(at: NSPoint(x: x, y: (bounds.height - size.height) / 2))
        if window?.firstResponder === self {
            NSColor.keyboardFocusIndicatorColor.setStroke()
            let ring = NSBezierPath(roundedRect: bounds.insetBy(dx: 2, dy: 3), xRadius: 6, yRadius: 6)
            ring.lineWidth = 2; ring.stroke()
        }
    }
}

private let qingyueTabPasteboardType = NSPasteboard.PasteboardType("local.xiaoyu.LightReader.tab")

final class DraggableTabButton: ActionButton, NSDraggingSource {
    var tabID: UUID?
    override func mouseDown(with event: NSEvent) {
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
            let point = NSPoint(x: ruleThickness - label.size().width - 10, y: y + inset.y - visible.minY + 1)
            label.draw(at: point)
            i += 1
        }
    }
}
