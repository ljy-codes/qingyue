import AppKit
import Foundation

@main
struct MermaidSequenceTests {
    static var checks = 0
    static func main() throws {
        let source = """
        sequenceDiagram
        %% 离线时序图
        autonumber
        actor 人 as 使用者 😀
        participant API as 本地服务
        人->>API: 打开文件<br/>并解析
        API-->>人: 返回预览 %% 保留
        Note over 人,API: 没有网络请求
        API->>API: 整理文本
        """
        let diagram = try MermaidSequence.parse(source)
        check(diagram.participants.map { $0.id } == ["人", "API"], "participant order and Unicode IDs")
        check(diagram.participants[0].isActor && diagram.participants[0].label == "使用者 😀", "actor and alias")
        check(diagram.events.count == 4 && diagram.autoNumber, "messages, notes and autonumber")
        check(diagram.events[0].text == "打开文件\n并解析", "br tags become line breaks")
        check(diagram.events[1].text == "返回预览 %% 保留", "message comment markers remain literal")
        check(diagram.events[2].kind == .note && diagram.events[2].placement == "over", "over note")
        check(diagram.events[3].from == diagram.events[3].to, "self call")

        let tokens = ["->", "->>", "-->", "-->>", "-x", "--x", "-)", "--)"]
        let arrows = try MermaidSequence.parse("sequenceDiagram\n" + tokens.map { "A\($0)B: 消息" }.joined(separator: "\n"))
        check(arrows.events.map { $0.dashed } == [false, false, true, true, false, true, false, true], "solid and dashed lines")
        check(arrows.events.map { $0.arrow } == [">", ">>", ">", ">>", "x", "x", ")", ")"], "all arrowheads preserve semantics")
        let arrowLayout = try MermaidSequence.layout(arrows)
        check(arrowLayout.shapes.filter { if case .solidArrow = $0 { return true }; return false }.count == 2, "only filled arrows use polygons")

        let implicit = try MermaidSequence.parse("sequenceDiagram\nA->>B: First\nactor B as 新标签\nNote right of C: implicit\nNote left of A: left\nNote over A: single")
        check(implicit.participants.map { $0.id } == ["A", "B", "C"], "implicit roles retain first-use order")
        check(implicit.participants[1].label == "新标签" && implicit.participants[1].isActor, "later explicit declaration enriches implicit role")
        check(implicit.events.suffix(3).map { $0.placement } == ["right", "left", "over"], "three note placements")
        try check(try MermaidSequence.parse("\u{FEFF}sequenceDiagram %% trailing\r\nautonumber %% trailing\r\nparticipant A\r\n").autoNumber, "BOM CRLF and supported trailing comments")
        try check(try MermaidSequence.parse("sequenceDiagram\nparticipant a-b as A\na-b->>B: <script>literal</script>\n").events[0].text == "<script>literal</script>", "HTML remains literal text")

        let invalid = ["", "   ", "graph TD\nA-->B", "sequenceDiagram", "sequenceDiagram\nalt condition\nA->>B: x\nend",
                       "sequenceDiagram\nloop 3\nA->>B: x\nend", "sequenceDiagram\nactivate A", "sequenceDiagram\ndeactivate B",
                       "sequenceDiagram\nA->>+B: x", "sequenceDiagram\nA->>-B: x", "sequenceDiagram\nA=>B: bad",
                       "sequenceDiagram\nA->>B: ", "sequenceDiagram\nA->>B: first; second", "sequenceDiagram\n%%{init: x}%%",
                       "sequenceDiagram\nA->>B: x\nautonumber", "sequenceDiagram\nautonumber 1 2\nparticipant A",
                       "sequenceDiagram\nparticipant A\nparticipant A", "sequenceDiagram\nparticipant A as ",
                       "sequenceDiagram\nNote left A: x", "sequenceDiagram\nNote middle of A: x", "sequenceDiagram\nNote over A,B,C: x",
                       "sequenceDiagram\nNote left of A,B: x", "sequenceDiagram\nNote over : x", "sequenceDiagram\nA B->>C: x",
                       "sequenceDiagram\n-A->>B: x", "sequenceDiagram\nA--B->>C: x", "sequenceDiagram\nA->>B: &#59;"]
        for value in invalid { reject(value, "unsupported source rejects entire diagram") }
        reject("sequenceDiagram\n" + String(repeating: "x", count: 32_000), "source length limit")
        reject("sequenceDiagram\n" + String(repeating: "\n", count: 1_000) + "participant A", "source line limit")
        reject("sequenceDiagram\n" + (0..<21).map { "participant P\($0)" }.joined(separator: "\n"), "participant limit")
        reject("sequenceDiagram\n" + (0..<201).map { "A->>B: \($0)" }.joined(separator: "\n"), "event limit")
        reject("sequenceDiagram\nA->>B: " + String(repeating: "字", count: 2_001), "label length limit")
        let maximum = try MermaidSequence.parse("sequenceDiagram\n" + (0..<200).map { "A->>B: \($0)" }.joined(separator: "\n"))
        check(maximum.events.count == 200, "maximum accepted event count")

        // Geometry includes edge notes and self-loop labels, with padded bounds.
        let edge = try MermaidSequence.parse("sequenceDiagram\nparticipant A\nparticipant B\nNote left of A: 中文左侧\nNote right of B: right\nB->>B: self call with long text\nB->>A: reverse")
        let plan = try MermaidSequence.layout(edge, fontSize: 17)
        check(plan.bounds.minX < -70 && plan.size.width > 500, "edge notes and self call enlarge drawing")
        check(plan.size.width <= 8000 && plan.size.height <= 20_000 && plan.size.width * plan.size.height <= 24_000_000, "drawing budget")
        for shape in plan.shapes {
            let b = shape.bounds.offsetBy(dx: plan.translation.x, dy: plan.translation.y)
            check(b.minX >= 20 && b.minY >= 20 && b.maxX <= plan.size.width - 20 && b.maxY <= plan.size.height - 20, "all shapes are inside padded drawing")
        }
        let long = try MermaidSequence.parse("sequenceDiagram\nparticipant A as " + String(repeating: "标题", count: 50) + "\nA->>B: https://example.test/" + String(repeating: "long_path_", count: 60) + "\nNote left of A: " + String(repeating: "说明😀", count: 80))
        let longPlan = try MermaidSequence.layout(long)
        check(longPlan.size.height > 600 && longPlan.size.width < 1000, "long labels wrap instead of growing horizontal bounds")
        do { _ = try MermaidSequence.layout(edge, fontSize: .nan); fatalError("NaN should reject") }
        catch { checks += 1 }
        do { _ = try MermaidSequence.layout(edge, fontSize: -3); fatalError("negative size should reject") }
        catch { checks += 1 }
        let huge = try MermaidSequence.parse("sequenceDiagram\n" + (0..<20).map { "participant P\($0)" }.joined(separator: "\n") + "\n" + (0..<10).map { "P0->>P19: " + String(repeating: "内容", count: 900) + " \($0)" }.joined(separator: "\n"))
        do { _ = try MermaidSequence.layout(huge, fontSize: 30); fatalError("oversize geometry should reject") }
        catch { checks += 1 }

        // Real AppKit rasterization verifies that the vector image draws in both
        // themes. This is deliberately small; geometry tests do not allocate images.
        let light = try MermaidSequence.render(source, fontSize: 14, dark: false)
        let dark = try MermaidSequence.render(source, fontSize: 14, dark: true)
        check(light.size == dark.size, "theme does not change source layout")
        check(light.size.width > 200 && light.size.height > 200, "image natural geometry")
        let lightPNG = try rasterize(light), darkPNG = try rasterize(dark)
        check(lightPNG != darkPNG, "theme changes rendered pixels")
        let output = URL(fileURLWithPath: "/private/tmp/qingyue-sequence-test.png")
        try lightPNG.write(to: output)
        check(NSBitmapImageRep(data: lightPNG) != nil && NSBitmapImageRep(data: darkPNG) != nil, "generated PNGs decode")
        print("MermaidSequenceTests: \(checks) checks passed")
    }

    static func check(_ value: @autoclosure () throws -> Bool, _ message: String) rethrows {
        guard try value() else { fatalError(message) }
        checks += 1
    }
    static func reject(_ value: String, _ message: String) {
        do { _ = try MermaidSequence.parse(value); fatalError(message) }
        catch { check(!error.localizedDescription.isEmpty, "errors are readable") }
    }
    static func rasterize(_ image: NSImage) throws -> Data {
        let width = Int(ceil(image.size.width)), height = Int(ceil(image.size.height))
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
              let context = NSGraphicsContext(bitmapImageRep: rep) else { throw CocoaError(.fileWriteUnknown) }
        NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current = context
        image.draw(in: NSRect(origin: .zero, size: image.size))
        context.flushGraphics(); NSGraphicsContext.restoreGraphicsState()
        guard let data = rep.representation(using: .png, properties: [:]) else { throw CocoaError(.fileWriteUnknown) }
        return data
    }
}
