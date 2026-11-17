import Foundation

@main
struct MarkdownTableEditingTests {
    private static var checks = 0
    static func main() {
        let source = "| 名称 | 内容 |\n| --- | :---: |\n| a\\|b | `x|y` |\n| 二 | 😀 |\n"
        let cursor = (source as NSString).range(of: "x|y").location
        expect(MarkdownTableEditing.table(in: source, at: cursor)?.columnCount == 2, "识别带转义竖线和代码竖线的 Markdown 表格")
        let centered = MarkdownTableEditing.edit(text: source, selection: NSRange(location: cursor, length: 0), action: .align(.right))!
        expect(centered.replacement.contains("---: |"), "列对齐更新分隔行")
        let row = MarkdownTableEditing.edit(text: source, selection: NSRange(location: cursor, length: 0), action: .insertRow)!
        expect(row.replacement.components(separatedBy: "\n").count == 6 && row.replacement.contains("|  |"), "表格中插入空行并保留表头")
        let column = MarkdownTableEditing.edit(text: source, selection: NSRange(location: cursor, length: 0), action: .insertColumn)!
        expect(column.replacement.contains("|  |"), "表格中插入空列")
        let tsv = MarkdownTableEditing.markdownTable(fromTSV: "A\tB\n1\t2")!
        expect(tsv == "| A | B |\n| --- | --- |\n| 1 | 2 |\n", "TSV 转换为标准 Markdown 表格")
        let tab = MarkdownTableEditing.edit(text: source, selection: NSRange(location: cursor, length: 0), action: .tab(backward: false))!
        expect(tab.replacement.isEmpty && tab.selection.location > cursor, "Tab 在表格单元格间移动")
        print("MarkdownTableEditingTests: \(checks) checks passed")
    }
    private static func expect(_ value: @autoclosure () -> Bool, _ message: String) { guard value() else { fatalError(message) }; checks += 1 }
}
