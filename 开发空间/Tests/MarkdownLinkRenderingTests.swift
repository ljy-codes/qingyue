import AppKit
import Foundation

@main
struct MarkdownLinkRenderingTests {
    private static var checks = 0
    private static let palette = EditorPalette(background: .white, foreground: .black, muted: .gray, accent: .blue, string: .brown, number: .orange, comment: .gray, key: .blue)

    static func main() {
        let source = "[guide]: <https://example.com/guide> \"说明\"\n\n# [章节][guide] 😀\n\n正文 [引用][GUIDE]、[guide][] 和 [guide]。访问 https://swift.org/docs, 或 <https://example.com/a>。\n\n- **[重点][guide]**\n\n| 链接 | 内容 |\n| --- | --- |\n| [表格][guide] | `https://code.example` |\n\n```md\n[onlycode]: https://never.example\n[代码][guide] https://fence.example\n```\n\n`[内联][guide] https://inline.example`\n\n## 后续标题\n"
        let document = build(source)
        let preview = document.attributedText
        let visible = preview.string
        expect(visible.contains("章节 😀") && !visible.contains("[章节][guide]"), "参考链接在标题中显示标签")
        expect(!visible.contains("[guide]:") && visible.contains("[onlycode]:"), "只隐藏正文中的有效定义，围栏定义保留原文")
        expect(visible.contains("正文 引用、guide 和 guide。"), "完整、折叠和快捷引用均可见")
        expect(visible.contains("https://swift.org/docs,"), "自动链接不吞掉相邻标点")
        expect(link(preview, containing: "引用") == "https://example.com/guide", "参考链接可交互")
        expect(link(preview, containing: "https://swift.org/docs") == "https://swift.org/docs", "自动链接排除句尾标点")
        expect(link(preview, containing: "https://example.com/a") == "https://example.com/a", "尖括号自动链接去掉括号")
        expect(link(preview, containing: "重点") == "https://example.com/guide", "加粗内容内的链接保留目标")
        let bold = (visible as NSString).range(of: "重点")
        let font = preview.attribute(.font, at: bold.location, effectiveRange: nil) as! NSFont
        expect(NSFontManager.shared.traits(of: font).contains(.boldFontMask), "链接标签保留加粗样式")
        expect(link(preview, containing: "表格") == "https://example.com/guide", "表格中的参考链接共享当前文档定义")
        for literal in ["https://code.example", "https://fence.example", "https://inline.example"] {
            expect(link(preview, containing: literal) == nil, "代码中的链接不被激活")
        }
        let definitions = MarkdownLinks.references(in: source)
        expect(definitions["onlycode"] == nil, "围栏内定义不能污染正文参考链接")
        for heading in document.headings {
            expect(sourceRange(source, heading.sourceRange).contains(heading.title.hasPrefix("章节") ? "[章节][guide]" : "后续标题"), "标题源范围保留原始 Markdown 坐标")
            expect(document.sourceRange(forDisplayOffset: heading.displayRange.location).location == heading.sourceRange.location, "阅读标题可定位回原始行")
        }
        for segment in document.segments {
            expect(segment.sourceRange.location >= 0 && NSMaxRange(segment.sourceRange) <= source.utf16.count, "每块源范围未越界")
        }
        let afterSource = (source as NSString).range(of: "## 后续标题").location
        let afterDisplay = document.displayRange(forSourceOffset: afterSource)
        expect(sourceRange(visible, afterDisplay).contains("后续标题"), "前面链接去标记和隐藏定义不漂移后续源坐标")

        let isolated = build("[guide]\n").attributedText
        expect(isolated.string.contains("[guide]") && link(isolated, containing: "guide") == nil, "下一篇文档不继承上一篇参考定义")
        let literal = build("    https://indented.example\n\\[guide]\n\n[guide]: https://example.com\n").attributedText
        expect(link(literal, containing: "https://indented.example") == nil && link(literal, containing: "guide") == nil, "缩进代码与转义括号保持字面内容")
        let indentedLink = build("    [代码链接](https://literal.example)\n").attributedText
        expect(indentedLink.string.contains("[代码链接](https://literal.example)") && link(indentedLink, containing: "代码链接") == nil, "缩进代码中的内联链接也保持原文")
        let multiple = build("``[代码](https://double.example)``\n\n[missing][undefined]\n").attributedText
        expect(link(multiple, containing: "https://double.example") == nil, "双反引号代码内不激活链接")
        expect(multiple.string.contains("[missing][undefined]"), "未定义参考保留源码")
        let images = build("![猫][pic]\n\n[pic]: images/cat.png\n").attributedText
        let imageRange = (images.string as NSString).range(of: "〔图片：猫〕")
        expect(imageRange.location != NSNotFound && images.attribute(NSAttributedString.Key("qingyue.imageReference"), at: imageRange.location, effectiveRange: nil) as? String == "images/cat.png", "参考图片保留本地加载所需目标")
        print("MarkdownLinkRenderingTests: \(checks) checks passed")
    }
    private static func build(_ text: String) -> ReadingDocument {
        ReadingDocument.build(text: text, kind: .markdown, palette: palette, fontSize: 16, readingFontName: "system", lineSpacing: 6)
    }
    private static func sourceRange(_ source: String, _ range: NSRange) -> String { (source as NSString).substring(with: range) }
    private static func link(_ text: NSAttributedString, containing value: String) -> String? {
        let range = (text.string as NSString).range(of: value)
        guard range.location != NSNotFound else { return nil }
        return (text.attribute(.link, at: range.location, effectiveRange: nil) as? URL)?.absoluteString
    }
    private static func expect(_ value: @autoclosure () -> Bool, _ message: String) { guard value() else { fatalError(message) }; checks += 1 }
}
