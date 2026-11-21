import AppKit

/// A read-only structural presentation. Parsing is lexeme-based, so duplicate
/// fields and numbers beyond Double's range are not discarded or rounded.
final class JSONStructureController: NSWindowController, NSOutlineViewDataSource, NSOutlineViewDelegate {
    let rootNode: JSONStructureNode
    private let outline = NSOutlineView()

    init(text: String, allowComments: Bool) throws {
        rootNode = try JSONStructureNode.parse(text, allowComments: allowComments)
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 700, height: 560), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        panel.title = "JSON 只读结构"
        panel.minSize = NSSize(width: 420, height: 300)
        panel.isReleasedWhenClosed = false
        super.init(window: panel)
        outline.headerView = nil
        outline.rowHeight = 26
        outline.indentationPerLevel = 18
        outline.dataSource = self; outline.delegate = self
        outline.setAccessibilityLabel("JSON 只读结构，可展开和折叠")
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("node"))
        column.minWidth = 380; column.resizingMask = .autoresizingMask
        outline.addTableColumn(column); outline.outlineTableColumn = column
        outline.columnAutoresizingStyle = .uniformColumnAutoresizingStyle
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true; scroll.hasHorizontalScroller = true; scroll.autohidesScrollers = true
        scroll.documentView = outline
        panel.contentView = scroll
        outline.reloadData(); outline.expandItem(rootNode)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }
    func present(relativeTo parent: NSWindow?) {
        if let parent = parent {
            window?.appearance = parent.appearance
            window?.setFrameOrigin(NSPoint(x: parent.frame.midX - 350, y: max(40, parent.frame.midY - 280)))
        } else { window?.center() }
        showWindow(nil); window?.makeKeyAndOrderFront(nil)
    }
    func outlineView(_ outlineView: NSOutlineView, numberOfChildrenOfItem item: Any?) -> Int {
        item == nil ? 1 : (item as? JSONStructureNode)?.children.count ?? 0
    }
    func outlineView(_ outlineView: NSOutlineView, isItemExpandable item: Any) -> Bool { (item as? JSONStructureNode)?.isContainer == true }
    func outlineView(_ outlineView: NSOutlineView, child index: Int, ofItem item: Any?) -> Any {
        if let node = item as? JSONStructureNode { return node.children[index] }
        return rootNode
    }
    func outlineView(_ outlineView: NSOutlineView, viewFor tableColumn: NSTableColumn?, item: Any) -> NSView? {
        guard let node = item as? JSONStructureNode else { return nil }
        let id = NSUserInterfaceItemIdentifier("json-node")
        let field: NSTextField
        if let reused = outlineView.makeView(withIdentifier: id, owner: self) as? NSTextField { field = reused }
        else {
            field = NSTextField(labelWithString: ""); field.identifier = id
            field.font = .monospacedSystemFont(ofSize: 12, weight: .regular)
            field.lineBreakMode = .byTruncatingTail
        }
        let name = String(node.key.prefix(200)).replacingOccurrences(of: "\n", with: "\\n").replacingOccurrences(of: "\r", with: "\\r").replacingOccurrences(of: "\t", with: "\\t")
        let value = String(node.summary.prefix(1000))
        let combined = NSMutableAttributedString(string: name + "  ", attributes: [.foregroundColor: NSColor.systemBlue])
        combined.append(NSAttributedString(string: value, attributes: [.foregroundColor: node.isContainer ? NSColor.secondaryLabelColor : (node.kind == .string ? NSColor.systemOrange : NSColor.labelColor)]))
        field.attributedStringValue = combined
        field.toolTip = String((name + "  " + node.summary).prefix(2500))
        field.setAccessibilityLabel(name + "，" + value)
        return field
    }
}
