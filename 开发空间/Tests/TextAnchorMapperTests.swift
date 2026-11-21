import Foundation

@main
struct TextAnchorMapperTests {
    static var checks = 0

    static func main() {
        expect(TextAnchorMapper.map(oldText: "a\nb\nc", newText: "a\nnew\nb\nc", oldAnchor: 2) == 6,
               "same line moves with inserted paragraph")
        expect(TextAnchorMapper.map(oldText: "重复\n正文\n重复\n", newText: "重复\n插入\n正文\n重复\n", oldAnchor: 8) == 11,
               "repeated lines use the nearest occurrence")
        expect(TextAnchorMapper.map(oldText: "😀\n正文", newText: "😀\n新\n正文", oldAnchor: 3) == 5,
               "UTF-16 offsets preserve emoji boundaries")
        expect(TextAnchorMapper.map(oldText: "正文", newText: "前缀正文", oldAnchor: 2) == 4,
               "end anchor follows a prefixed document")
        expect(TextAnchorMapper.map(oldText: "正文", newText: "", oldAnchor: 1) == 0,
               "empty replacement clamps to zero")
        print("TextAnchorMapperTests: \(checks) checks passed")
    }

    static func expect(_ value: @autoclosure () -> Bool, _ message: String) {
        guard value() else { fatalError(message) }
        checks += 1
    }
}
