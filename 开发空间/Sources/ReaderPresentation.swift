import AppKit

final class ReaderOperation: NSObject {
    let panel: NSPanel
    private let label = NSTextField(labelWithString: "正在读取…")
    private let time = NSTextField(labelWithString: "")
    private let started = Date()
    private var timer: Timer?
    private(set) var cancelled = false
    init(title: String, parent: NSWindow?) {
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 390, height: 145), styleMask: [.titled], backing: .buffered, defer: false)
        super.init()
        panel.title = title; panel.isReleasedWhenClosed = false
        let bar = NSProgressIndicator(frame: NSRect(x: 20, y: 60, width: 350, height: 14))
        bar.isIndeterminate = true; bar.style = .bar; bar.startAnimation(nil)
        label.frame = NSRect(x: 20, y: 95, width: 350, height: 25)
        time.frame = NSRect(x: 20, y: 25, width: 220, height: 20); time.font = .systemFont(ofSize: 11)
        let cancel = NSButton(title: "取消", target: self, action: #selector(cancelOperation)); cancel.frame = NSRect(x: 285, y: 18, width: 85, height: 30)
        [label, time, bar, cancel].forEach { panel.contentView?.addSubview($0) }
        if let window = parent { panel.setFrameOrigin(NSPoint(x: window.frame.midX - 195, y: window.frame.midY - 72)); window.addChildWindow(panel, ordered: .above) }
        panel.orderFront(nil)
        timer = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: true) { [weak self] _ in
            guard let self = self else { return }; self.time.stringValue = String(format: "已用 %.1f 秒", Date().timeIntervalSince(self.started))
        }
    }
    func stage(_ text: String) { label.stringValue = text }
    @objc func cancelOperation() { cancelled = true; finish() }
    func finish() { timer?.invalidate(); timer = nil; panel.parent?.removeChildWindow(panel); panel.orderOut(nil) }
    deinit { timer?.invalidate() }
}

final class ReadingTextView: NSTextView {
    var copyReading: ((NSRange) -> Void)?
    var menuBuilder: ((Int) -> NSMenu)?
    var openAttachment: ((NSTextAttachment) -> Void)?
    override func copy(_ sender: Any?) { if let action = copyReading { action(selectedRange()) } else { super.copy(sender) } }
    override func menu(for event: NSEvent) -> NSMenu? {
        let point = convert(event.locationInWindow, from: nil)
        let index = layoutManager?.characterIndex(for: NSPoint(x: point.x - textContainerOrigin.x, y: point.y - textContainerOrigin.y), in: textContainer!, fractionOfDistanceBetweenInsertionPoints: nil) ?? selectedRange().location
        return menuBuilder?(min(index, string.utf16.count)) ?? super.menu(for: event)
    }
    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        if let lm = layoutManager, let tc = textContainer {
            let glyph = lm.glyphIndex(for: NSPoint(x: point.x - textContainerOrigin.x, y: point.y - textContainerOrigin.y), in: tc)
            if glyph < lm.numberOfGlyphs {
                let index = lm.characterIndexForGlyph(at: glyph)
                let bounds = lm.boundingRect(forGlyphRange: NSRange(location: glyph, length: 1), in: tc).offsetBy(dx: textContainerOrigin.x, dy: textContainerOrigin.y)
                if bounds.contains(point), index < textStorage?.length ?? 0, let attachment = textStorage?.attribute(.attachment, at: index, effectiveRange: nil) as? NSTextAttachment {
                    openAttachment?(attachment); return
                }
            }
        }
        super.mouseDown(with: event)
    }
}

private final class ZoomImageView: NSImageView {
    var zoom: ((CGFloat) -> Void)?
    override func scrollWheel(with event: NSEvent) {
        if event.modifierFlags.contains(.control) || event.modifierFlags.contains(.command) { zoom?(event.scrollingDeltaY > 0 ? 1.12 : 1 / 1.12) }
        else { super.scrollWheel(with: event) }
    }
}

final class ReaderResourceWindow: NSWindowController {
    private let scroll = NSScrollView()
    private var imageView: ZoomImageView?
    private var originalSize = NSSize.zero
    private var factor: CGFloat = 1
    private var monitor: Any?
    init(image: NSImage, title: String) {
        let win = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 850, height: 650), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        win.title = title; win.isReleasedWhenClosed = false
        super.init(window: win); setup()
        let view = ZoomImageView(frame: NSRect(origin: .zero, size: image.size)); view.image = image; view.imageScaling = .scaleAxesIndependently
        originalSize = image.size; imageView = view; scroll.documentView = view
        view.zoom = { [weak self] delta in self?.scale(delta) }
        let controls = NSStackView(); controls.orientation = .horizontal; controls.spacing = 8
        for (title, action) in [("适应窗口", #selector(fit)), ("解码尺寸", #selector(actual)), ("放大", #selector(zoomIn)), ("缩小", #selector(zoomOut)), ("复制位图", #selector(copyImage))] {
            controls.addArrangedSubview(NSButton(title: title, target: self, action: action))
        }
        controls.frame = NSRect(x: 12, y: 606, width: 810, height: 36); controls.autoresizingMask = [.width, .minYMargin]; win.contentView?.addSubview(controls)
        fit()
    }
    init(text: NSAttributedString, title: String) {
        let win = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1000, height: 640), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        win.title = title; win.isReleasedWhenClosed = false
        super.init(window: win); setup()
        let view = NSTextView(frame: NSRect(x: 0, y: 0, width: 1800, height: 600)); view.isEditable = false
        view.textStorage?.setAttributedString(text); view.textContainerInset = NSSize(width: 20, height: 20)
        view.isHorizontallyResizable = false; view.isVerticallyResizable = true; view.textContainer?.containerSize = NSSize(width: 1760, height: CGFloat.greatestFiniteMagnitude)
        view.textContainer?.widthTracksTextView = false; scroll.documentView = view
    }
    required init?(coder: NSCoder) { fatalError() }
    private func setup() {
        guard let win = window, let root = win.contentView else { return }
        scroll.frame = NSRect(x: 0, y: 0, width: root.bounds.width, height: root.bounds.height - 50); scroll.autoresizingMask = [.width, .height]
        scroll.hasHorizontalScroller = true; scroll.hasVerticalScroller = true; root.addSubview(scroll); win.center()
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            if event.window === self?.window && event.keyCode == 53 { self?.close(); return nil }; return event
        }
    }
    @objc private func fit() { factor = min(1, min(scroll.contentSize.width / max(1, originalSize.width), scroll.contentSize.height / max(1, originalSize.height))); resize() }
    @objc private func actual() { factor = 1; resize() }
    @objc private func zoomIn() { scale(1.25) }
    @objc private func zoomOut() { scale(0.8) }
    private func scale(_ delta: CGFloat) { factor = min(8, max(0.05, factor * delta)); resize() }
    private func resize() { imageView?.setFrameSize(NSSize(width: originalSize.width * factor, height: originalSize.height * factor)) }
    @objc private func copyImage() { if let image = imageView?.image { NSPasteboard.general.clearContents(); NSPasteboard.general.writeObjects([image]) } }
    deinit { if let monitor = monitor { NSEvent.removeMonitor(monitor) } }
}
