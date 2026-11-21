import Foundation

@main
struct MarkdownLinksTests {
    private static var checks = 0
    static func main() {
        let source = "[官网](https://example.com/docs) [参考][guide] https://swift.org\n\n[guide]: <https://example.com/guide>\n\n`https://ignored.example`\n```\n[代码](https://ignored.example)\n```"
        let refs = MarkdownLinks.references(in: source)
        expect(refs["guide"]?.destination == "https://example.com/guide", "解析参考链接定义")
        let links = MarkdownLinks.occurrences(in: source)
        expect(links.count == 3, "识别内联、参考和自动链接: \(links.map { $0.destination })")
        expect(links.contains { $0.destination == "https://example.com/guide" }, "参考链接解析到目标")
        expect(links.allSatisfy { !$0.destination.contains("ignored") }, "代码和围栏中的链接不被识别")
        let unsafe = MarkdownLinks.occurrences(in: "[危险](javascript:alert(1)) <file:///tmp/a>")
        expect(unsafe.isEmpty, "拒绝脚本和 file URL")
        print("MarkdownLinksTests: \(checks) checks passed")
    }
    private static func expect(_ value: @autoclosure () -> Bool, _ message: String) { guard value() else { fatalError(message) }; checks += 1 }
}
