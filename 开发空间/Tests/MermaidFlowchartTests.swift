import AppKit

@main
struct MermaidFlowchartTests {
    private static var checks = 0
    static func main() throws {
        let source = "flowchart TD\n A[开始] --> B{继续?}\n B -->|是| C((完成))\n B -.-> D[重试]"
        let diagram = try MermaidFlowchart.parse(source)
        expect(diagram.nodes.count == 4 && diagram.edges.count == 3, "parse nodes and connections")
        expect(diagram.nodes.first?.label == "开始" && diagram.edges[1].label == "是", "labels retain visible text")
        let shapes = try MermaidFlowchart.parse("graph LR\n开始(开始)-->判定{继续?}\n判定-->子程序[[写入]]\n子程序-->结束((结束))")
        expect(shapes.nodes.map(\.shape) == [.rounded, .diamond, .subroutine, .circle], "both source and target support all finite node shapes")
        expect(shapes.nodes.map(\.id) == ["开始", "判定", "子程序", "结束"], "Unicode identifiers are accepted")
        let redefined = try MermaidFlowchart.parse("flowchart TD\nA-->B\nA[新标题]\nA-->B\nA{再次定义}")
        expect(redefined.nodes.count == 2 && redefined.nodes[0].shape == .diamond && redefined.nodes[0].label == "再次定义", "explicit later definitions update a node but bare references do not reset it")
        let arrows = try MermaidFlowchart.parse("flowchart TD\nA-->B\nB---C\nC-.->D\nD==>E")
        expect(arrows.edges.map(\.directed) == [true, false, true, true], "--- is an undirected line")
        expect(arrows.edges.map(\.dashed) == [false, false, true, false], "only -.-> is dashed")
        expect(arrows.edges.map(\.thick) == [false, false, false, true], "==> is thick")
        for direction in ["TD", "BT", "LR", "RL", "TB"] {
            let parsed = try MermaidFlowchart.parse("flowchart \(direction)\nA[]-->B[]")
            let layout = try MermaidFlowchart.layout(parsed)
            let a = layout.bounds["A"]!, b = layout.bounds["B"]!
            if direction == "LR" { expect(a.midX < b.midX && a.midY == b.midY, "LR draws left to right") }
            if direction == "RL" { expect(a.midX > b.midX && a.midY == b.midY, "RL draws right to left") }
            if direction == "BT" { expect(a.midY > b.midY && a.midX == b.midX, "BT draws bottom to top in flipped coordinates") }
            if direction == "TD" || direction == "TB" { expect(a.midY < b.midY && a.midX == b.midX, "TD/TB draws top to bottom") }
            let points = layout.connections[0].points
            expect(points.count == 2, "ordinary edge uses two endpoints")
            expect(onBoundary(points[0], a) && onBoundary(points[1], b), "edge attaches to the node boundary, not its covered center")
            expect(!a.insetBy(dx: 0.5, dy: 0.5).contains(points[0]) && !b.insetBy(dx: 0.5, dy: 0.5).contains(points[1]), "arrow remains outside node fill")
            let image = try MermaidFlowchart.render("flowchart \(direction)\nA[开始]-->B[完成]", fontSize: 13, dark: true)
            try expect(image.size == (try MermaidFlowchart.layout(parsed, fontSize: 13)).size, "render uses the validated direction layout")
        }
        let loop = try MermaidFlowchart.layout(MermaidFlowchart.parse("flowchart LR\nA-->A"))
        expect(loop.connections[0].points.count == 4 && loop.connections[0].points[0] != loop.connections[0].points.last!, "self-edge is a visible loop instead of a zero-length line")
        expect(loop.connections[0].points.allSatisfy { NSRect(origin: .zero, size: loop.size).contains($0) }, "self-loop stays inside render bounds")
        let comments = try MermaidFlowchart.parse("\u{FEFF}\n%% comment\n\nFLOWCHART\tTB\n\n A[中文😀] -->|继续| B")
        expect(comments.direction == "TD" && comments.nodes[0].label == "中文😀", "BOM/comments/blank prefix and TB alias are supported")
        for bad in ["", "flowchart", "flowchart XX\nA", "flowchart TD\nsubgraph X\nA\nend", "flowchart TD\nA-->B-->C", "flowchart TD\nA-- text -->B", "flowchart TD\nA --> B; B --> C", "flowchart TD\n%%{init: {}}%%\nA", "flowchart TD\nA ==> |missing B", "flowchart TD\nA[/unsupported/]", "flowchart TD\nA@{shape: rect}", "flowchart TD\nclick A callback", "flowchart TD\nA[broken", "flowchart TD\nA[*] --o B"] {
            do { _ = try MermaidFlowchart.parse(bad); fatalError("unsupported syntax accepted: \(bad)") }
            catch { checks += 1 }
        }
        expectThrows("source budget") { _ = try MermaidFlowchart.parse("flowchart TD\nA[" + String(repeating: "x", count: 32_001) + "]") }
        expectThrows("line budget includes comments and blanks") { _ = try MermaidFlowchart.parse("flowchart TD\nA\n" + String(repeating: "%%comment\n", count: 999)) }
        expectThrows("label budget") { _ = try MermaidFlowchart.parse("flowchart TD\nA[" + String(repeating: "中", count: 2_001) + "]") }
        expectThrows("node budget") { _ = try MermaidFlowchart.parse("flowchart TD\n" + (0...100).map { "A\($0)" }.joined(separator: "\n")) }
        expectThrows("edge budget") { _ = try MermaidFlowchart.parse("flowchart TD\n" + Array(repeating: "A-->B", count: 201).joined(separator: "\n")) }
        let large = try MermaidFlowchart.parse("flowchart TD\n" + (0..<100).map { "A\($0)" }.joined(separator: "\n"))
        expect(large.nodes.count == 100, "100 nodes remain parseable")
        expectThrows("oversized render falls back to source rather than allocating an unbounded bitmap") { _ = try MermaidFlowchart.render("flowchart TD\n" + (0..<100).map { "A\($0)" }.joined(separator: "\n")) }
        expectThrows("non-finite font size") { _ = try MermaidFlowchart.render(source, fontSize: .infinity) }
        expectThrows("horizontal bitmap budget") { _ = try MermaidFlowchart.render("flowchart LR\n" + (0..<25).map { "A\($0)" }.joined(separator: "\n")) }
        try rasterArrowCheck()
        print("MermaidFlowchartTests: \(checks) checks passed")
    }
    private static func rasterArrowCheck() throws {
        // Observe actual pixels outside the target boundary: a directed arrow
        // widens beyond the center line, while --- remains a solid thin line.
        let directed = try MermaidFlowchart.render("flowchart LR\nA[]-->B[]")
        let plain = try MermaidFlowchart.render("flowchart LR\nA[]---B[]")
        let layout = try MermaidFlowchart.layout(MermaidFlowchart.parse("flowchart LR\nA[]-->B[]"))
        let end = layout.connections[0].points.last!
        guard let firstData = directed.tiffRepresentation, let secondData = plain.tiffRepresentation,
              let first = NSBitmapImageRep(data: firstData), let second = NSBitmapImageRep(data: secondData) else { fatalError("bitmap rendering failed") }
        let x = Int(end.x - 8), y = Int(end.y + 3)
        let colorA = first.colorAt(x: x, y: y)!.usingColorSpace(.sRGB)!, colorB = second.colorAt(x: x, y: y)!.usingColorSpace(.sRGB)!
        expect(abs(colorA.redComponent - colorB.redComponent) + abs(colorA.greenComponent - colorB.greenComponent) + abs(colorA.blueComponent - colorB.blueComponent) > 0.2, "directed arrow is visible outside target; undirected line has no arrowhead")
        expect(first.pixelsWide <= Int(MermaidFlowchart.maximumWidth) && first.pixelsHigh <= Int(MermaidFlowchart.maximumHeight), "actual raster respects bitmap dimensions")
    }
    private static func onBoundary(_ point: NSPoint, _ rect: NSRect) -> Bool {
        let epsilon: CGFloat = 0.01
        return abs(point.x - rect.minX) < epsilon || abs(point.x - rect.maxX) < epsilon || abs(point.y - rect.minY) < epsilon || abs(point.y - rect.maxY) < epsilon
    }
    private static func expectThrows(_ label: String, _ action: () throws -> Void) {
        do { try action(); fatalError(label + " was not enforced") } catch { checks += 1 }
    }
    private static func expect(_ value: @autoclosure () throws -> Bool, _ message: String) rethrows { guard try value() else { fatalError(message) }; checks += 1 }
}
