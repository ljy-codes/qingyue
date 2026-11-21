import AppKit

@main
struct MarkdownBreakTests {
    private static var checks = 0
    private static let palette = EditorPalette(background: .white, foreground: .black, muted: .gray, accent: .systemBlue, string: .systemGreen, number: .systemOrange, comment: .gray, key: .systemPurple)

    static func main() throws {
        _ = NSApplication.shared; NSApp.setActivationPolicy(.prohibited)
        for tag in ["<br>", "<br/>", "<br />", "<BR>", "<Br/>", "<BR  />", "<br\t/>"] {
            let document = build("上😀\(tag)下文")
            expect(document.attributedText.string == "上😀\u{2028}下文\n", "换行标签呈现为段落内换行：\(tag)")
            expect(ReadingShare.plainText(document.attributedText) == "上😀\n下文\n", "复制纯文本得到实际换行：\(tag)")
            let html = try html(document.attributedText)
            expect(html.contains("上😀<br>下文"), "HTML 输出换行而不是原始标签文字：\(tag)")
        }
        for literal in ["`a<br>b`", "``a`<br>b``", "``a`**b<br>c**`d``", "``a`*b<br>c*`d``", "``a`~~b<br>c~~`d``", "\\<br>", "&lt;br&gt;", "<bravo>", "<br onclick=\"x\">", "<br / >", "<br x>"] {
            let text = build(literal).attributedText
            expect(!text.string.contains("\u{2028}"), "代码、转义和其他标签保持文字：\(literal)")
        }
        expect(build("``a`**b<br>c**`d``").attributedText.string == "a`**b<br>c**`d\n", "多反引号代码完整保留内部强调符号和标签")
        expect(build("**外层 `a<br>b` 强调**").attributedText.string == "外层 a<br>b 强调\n", "外层强调不影响内部代码保护")
        for fenced in ["```html\nA<br>B\n```", "~~~\nA<BR/>B\n~~~", "    A<br>B"] {
            let text = build(fenced).attributedText
            expect(text.string.contains("A<br>B") || text.string.contains("A<BR/>B"), "块级代码不解释换行标签")
            expect(!text.string.contains("\u{2028}"), "代码没有额外换行")
        }
        let styled = build("**重点<br/>下一行** 与 [链接<br>文字](https://example.invalid)").attributedText
        let stylePoint = (styled.string as NSString).range(of: "下一行").location
        let font = styled.attribute(.font, at: stylePoint, effectiveRange: nil) as! NSFont
        expect(NSFontManager.shared.traits(of: font).contains(.boldFontMask), "换行后保留强调样式")
        let linkPoint = (styled.string as NSString).range(of: "文字").location
        expect((styled.attribute(.link, at: linkPoint, effectiveRange: nil) as? URL)?.absoluteString == "https://example.invalid", "链接文字内换行保留目标")

        let source = "# 疑问点\n\n| 问题 | 参考范围 | 推荐项 |\n| --- | :---: | ---: |\n| 计划口径 | A. 报单金额😀<br>B. 出库金额<br/>C. 销售数量 | 推荐A<BR />分析 |\n\n## 后续标题\n\n尾部原文\n"
        let document = build(source), text = document.attributedText
        expect(text.string.contains("A. 报单金额😀\u{2028}B. 出库金额\u{2028}C. 销售数量"), "截图中的多行内容在同一单元格内换行")
        expect(text.string.contains("推荐A\u{2028}分析"), "其他单元格也正确换行")
        let ns = text.string as NSString
        let a = ns.range(of: "A. 报单金额").location, b = ns.range(of: "B. 出库金额").location
        let first = cell(text, a)!, second = cell(text, b)!
        expect(first.table === second.table && first.startingRow == second.startingRow && first.startingColumn == second.startingColumn, "换行不会拆成新行或新单元格")
        expect(ns.paragraphRange(for: NSRange(location: a, length: 0)) == ns.paragraphRange(for: NSRange(location: b, length: 0)), "换行保持同一原生段落")
        let htmlTable = try html(text)
        expect(htmlTable.components(separatedBy: "<td ").count == 7, "HTML 表格仍为两行三列")
        expect(htmlTable.contains("A. 报单金额😀<br>B. 出库金额<br>C. 销售数量"), "HTML 单元格内输出真正的 br")
        expect(htmlTable.contains("text-align:center") && htmlTable.contains("text-align:right"), "表格对齐保持")
        let plain = ReadingShare.plainText(text)
        expect(plain.contains("A. 报单金额😀\nB. 出库金额\nC. 销售数量"), "纯文本保留表格里的换行")
        let clipboard = try ReadingShare.clipboard(text)
        expect(String(data: clipboard[.string]!, encoding: .utf8) == plain, "剪贴板纯文本与显示换行一致")
        expect(clipboard[.html] != nil && clipboard[.rtf] != nil, "图文剪贴板包含 HTML 和 RTF")
        let rtf = try NSAttributedString(data: clipboard[.rtf]!, options: [.documentType: NSAttributedString.DocumentType.rtf], documentAttributes: nil)
        expect(rtf.string.replacingOccurrences(of: "\u{2028}", with: "\n").contains("A. 报单金额😀\nB. 出库金额\nC. 销售数量"), "RTF 经系统读取后保留单元格换行")
        let selected = text.attributedSubstring(from: NSRange(location: a, length: b - a + "B. 出库金额".utf16.count))
        let selectionHTML = try html(selected)
        expect(selectionHTML.contains("<br>") && !selectionHTML.contains("C. 销售数量"), "选区复制保留换行且不会扩大范围")
        let literal = build("| 内容 |\n| --- |\n| `A<br>B` |\n").attributedText
        try expect(try html(literal).contains("A&lt;br&gt;B"), "代码里的字面 br 在HTML中安全转义")
        let heading = document.headings.first { $0.title == "后续标题" }!
        expect((text.string as NSString).substring(with: document.displayRange(forSourceOffset: heading.sourceRange.location)).contains("后续标题"), "表格换行后目录源位置仍正确")
        expect(document.sourceRange(forDisplayOffset: b).location == (source as NSString).range(of: "| 问题").location, "显示换行仍映射到原表格源码")
        try checkNativeLayout(text, a: a, b: b)
        print("MarkdownBreakTests: \(checks) checks passed")
    }

    private static func build(_ source: String) -> ReadingDocument {
        ReadingDocument.build(text: source, kind: .markdown, palette: palette, fontSize: 15, readingFontName: "system", lineSpacing: 4)
    }
    private static func html(_ text: NSAttributedString) throws -> String {
        String(data: try ReadingShare.exportHTML(text), encoding: .utf8)!
    }
    private static func cell(_ text: NSAttributedString, _ offset: Int) -> NSTextTableBlock? {
        (text.attribute(.paragraphStyle, at: offset, effectiveRange: nil) as? NSParagraphStyle)?.textBlocks.compactMap { $0 as? NSTextTableBlock }.first
    }
    private static func checkNativeLayout(_ text: NSAttributedString, a: Int, b: Int) throws {
        let view = NSTextView(frame: NSRect(x: 0, y: 0, width: 940, height: 700))
        view.appearance = NSAppearance(named: .aqua); view.backgroundColor = .white
        view.isEditable = false; view.isRichText = true
        view.textStorage?.setAttributedString(text)
        let manager = view.layoutManager!, container = view.textContainer!
        container.containerSize = NSSize(width: 920, height: CGFloat.greatestFiniteMagnitude)
        manager.ensureLayout(for: container)
        let first = manager.lineFragmentRect(forGlyphAt: manager.glyphIndexForCharacter(at: a), effectiveRange: nil)
        let second = manager.lineFragmentRect(forGlyphAt: manager.glyphIndexForCharacter(at: b), effectiveRange: nil)
        expect(second.minY > first.minY, "TextKit 实际把 B 排到 A 的下一行")
        let root = NSView(frame: NSRect(x: 0, y: 0, width: 980, height: 700))
        root.addSubview(view)
        if let bitmap = root.bitmapImageRepForCachingDisplay(in: root.bounds) {
            root.cacheDisplay(in: root.bounds, to: bitmap)
            try bitmap.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: "/private/tmp/qingyue-br-table-preview.png"))
        }
    }
    private static func expect(_ condition: @autoclosure () throws -> Bool, _ message: String) rethrows {
        if try !condition() { fatalError(message) }; checks += 1
    }
}
