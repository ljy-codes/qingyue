import AppKit
import Foundation

struct MermaidFlowNode: Equatable {
    let id: String
    var label: String
    var shape: MermaidFlowShape
}

enum MermaidFlowShape: Equatable { case rectangle, rounded, diamond, circle, subroutine }

struct MermaidFlowEdge: Equatable {
    let from: String
    let to: String
    let label: String
    let dashed: Bool
    var thick = false
    var directed = true
}

struct MermaidFlowchartDiagram: Equatable {
    let direction: String
    let nodes: [MermaidFlowNode]
    let edges: [MermaidFlowEdge]
}

struct MermaidFlowConnection {
    let edge: MermaidFlowEdge
    let points: [NSPoint]
}

struct MermaidFlowLayout {
    let size: NSSize
    let bounds: [String: NSRect]
    let connections: [MermaidFlowConnection]
}

enum MermaidFlowchartError: LocalizedError {
    case syntax(line: Int, reason: String)
    case limit(String)
    var errorDescription: String? {
        switch self {
        case .syntax(let line, let reason): return "第 \(line) 行：\(reason)"
        case .limit(let reason): return reason
        }
    }
}

/// Offline Mermaid flowchart subset. Unsupported syntax and oversized diagrams
/// fail explicitly so the reader can retain the original source without lying
/// about arrow meaning, node shape or direction.
enum MermaidFlowchart {
    static let maximumNodes = 100, maximumEdges = 200
    static let maximumSourceLength = 32_000, maximumLines = 1_000, maximumLabelLength = 2_000
    static let maximumWidth: CGFloat = 4096, maximumHeight: CGFloat = 8192, maximumPixels: CGFloat = 16_777_216
    private struct ParsedNode { let value: MermaidFlowNode; let defined: Bool }
    private static let reserved = Set(["subgraph", "end", "classdef", "class", "style", "linkstyle", "click", "direction", "acctitle", "accdescr"])

    static func parse(_ source: String) throws -> MermaidFlowchartDiagram {
        guard !source.isEmpty else { throw MermaidFlowchartError.limit("流程图内容不能为空。") }
        guard source.utf16.count <= maximumSourceLength else { throw MermaidFlowchartError.limit("流程图源码超过 \(maximumSourceLength) 个 UTF-16 单元。") }
        let normalized = source.trimmingCharacters(in: CharacterSet(charactersIn: "\u{FEFF}"))
            .replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        let lines = normalized.components(separatedBy: "\n")
        guard lines.count <= maximumLines else { throw MermaidFlowchartError.limit("流程图超过 \(maximumLines) 行（含空行和注释）。") }
        var direction: String?, nodes: [MermaidFlowNode] = [], indexes: [String: Int] = [:], edges: [MermaidFlowEdge] = []
        func add(_ parsed: ParsedNode) throws {
            if let index = indexes[parsed.value.id] { if parsed.defined { nodes[index] = parsed.value }; return }
            guard nodes.count < maximumNodes else { throw MermaidFlowchartError.limit("流程图节点超过 \(maximumNodes) 个。") }
            indexes[parsed.value.id] = nodes.count; nodes.append(parsed.value)
        }
        for (index, raw) in lines.enumerated() {
            let number = index + 1, line = raw.trimmingCharacters(in: .whitespaces)
            if line.contains("%%{") { throw MermaidFlowchartError.syntax(line: number, reason: "不支持 Mermaid 配置指令。") }
            if line.isEmpty || line.hasPrefix("%%") { continue }
            guard !line.contains(";") else { throw MermaidFlowchartError.syntax(line: number, reason: "不支持分号多语句，请逐行书写。") }
            if direction == nil {
                let words = line.split(whereSeparator: { $0.isWhitespace })
                guard words.count == 2, ["flowchart", "graph"].contains(words[0].lowercased()), ["TD", "TB", "BT", "LR", "RL"].contains(words[1].uppercased()) else {
                    throw MermaidFlowchartError.syntax(line: number, reason: "图必须以 flowchart 或 graph TD/TB/BT/LR/RL 开头。")
                }
                direction = words[1].uppercased() == "TB" ? "TD" : words[1].uppercased(); continue
            }
            if let word = line.split(whereSeparator: { $0.isWhitespace }).first, reserved.contains(word.lowercased()) {
                throw MermaidFlowchartError.syntax(line: number, reason: "暂不支持子图、样式、交互或局部方向指令。")
            }
            let text = line as NSString; var cursor = 0
            let from = try node(text, &cursor, line: number)
            skipSpaces(text, &cursor)
            if cursor == text.length { try add(from); continue }
            let token: String
            if let found = ["-.->", "==>", "-->", "---"].first(where: { starts(text, cursor, $0) }) { token = found; cursor += found.utf16.count }
            else { throw MermaidFlowchartError.syntax(line: number, reason: "连线仅支持 -->、---、-.-> 和 ==>。") }
            skipSpaces(text, &cursor)
            var label = ""
            if cursor < text.length && text.character(at: cursor) == 124 {
                let remainder = NSRange(location: cursor + 1, length: text.length - cursor - 1)
                let close = text.range(of: "|", options: [], range: remainder)
                guard close.location != NSNotFound else { throw MermaidFlowchartError.syntax(line: number, reason: "连线标签缺少右侧 |。") }
                label = text.substring(with: NSRange(location: cursor + 1, length: close.location - cursor - 1))
                try checkLabel(label, line: number); cursor = close.location + 1
            }
            let to = try node(text, &cursor, line: number)
            skipSpaces(text, &cursor)
            guard cursor == text.length else { throw MermaidFlowchartError.syntax(line: number, reason: "每行仅支持一个节点或一条连线；复杂语法请查看源码。") }
            try add(from); try add(to)
            guard edges.count < maximumEdges else { throw MermaidFlowchartError.limit("流程图连线超过 \(maximumEdges) 条。") }
            edges.append(MermaidFlowEdge(from: from.value.id, to: to.value.id, label: label, dashed: token == "-.->", thick: token == "==>", directed: token != "---"))
        }
        guard let direction else { throw MermaidFlowchartError.limit("缺少 flowchart 或 graph 头部。") }
        guard !nodes.isEmpty else { throw MermaidFlowchartError.limit("流程图需要至少一个节点。") }
        return MermaidFlowchartDiagram(direction: direction, nodes: nodes, edges: edges)
    }

    /// Logical coordinates are flipped (top-left origin) in both layout and
    /// NSImage rendering. Direction therefore controls actual positions.
    static func layout(_ diagram: MermaidFlowchartDiagram, fontSize: CGFloat = 14) throws -> MermaidFlowLayout {
        guard fontSize.isFinite, fontSize > 0, fontSize <= 48,
              !diagram.nodes.isEmpty, diagram.nodes.count <= maximumNodes,
              diagram.edges.count <= maximumEdges, ["TD", "TB", "BT", "LR", "RL"].contains(diagram.direction) else {
            throw MermaidFlowchartError.limit("流程图排版参数无效或超限。")
        }
        let horizontal = diagram.direction == "LR" || diagram.direction == "RL"
        let reversed = diagram.direction == "BT" || diagram.direction == "RL"
        let width = max(148, fontSize * 10 + 32), height = max(64, fontSize * 2.8 + 24), gap = max(54, fontSize * 3.4)
        let gutter: CGFloat = 36, count = CGFloat(diagram.nodes.count)
        let hasLoops = diagram.edges.contains { $0.from == $0.to }
        let size = NSSize(width: horizontal ? gutter * 2 + count * width + max(0, count - 1) * gap + (hasLoops ? 60 : 0) : gutter * 2 + width + 180,
                          height: horizontal ? gutter * 2 + height + 100 : gutter * 2 + count * height + max(0, count - 1) * gap)
        guard size.width <= maximumWidth, size.height <= maximumHeight, size.width * size.height <= maximumPixels else {
            throw MermaidFlowchartError.limit("流程图渲染超过 4096×8192 或 1600 万像素预算，保留源码。")
        }
        var bounds: [String: NSRect] = [:]
        for (index, node) in diagram.nodes.enumerated() {
            guard bounds[node.id] == nil else { throw MermaidFlowchartError.limit("流程图节点标识重复。") }
            let position = CGFloat(reversed ? diagram.nodes.count - 1 - index : index)
            bounds[node.id] = NSRect(x: horizontal ? gutter + position * (width + gap) : gutter + 90,
                                     y: horizontal ? gutter + 50 : gutter + position * (height + gap), width: width, height: height)
        }
        var connections: [MermaidFlowConnection] = []
        for edge in diagram.edges {
            guard let from = bounds[edge.from], let to = bounds[edge.to] else { throw MermaidFlowchartError.limit("流程图连线引用了不存在的节点。") }
            let points: [NSPoint]
            if edge.from == edge.to {
                let upper = NSPoint(x: from.maxX, y: from.minY + from.height * 0.25)
                let lower = NSPoint(x: from.maxX, y: from.minY + from.height * 0.75)
                points = [upper, NSPoint(x: upper.x + 44, y: upper.y), NSPoint(x: lower.x + 44, y: lower.y), lower]
            } else if horizontal {
                let sign: CGFloat = to.midX > from.midX ? 1 : -1
                points = [NSPoint(x: from.midX + sign * from.width / 2, y: from.midY), NSPoint(x: to.midX - sign * to.width / 2, y: to.midY)]
            } else {
                let sign: CGFloat = to.midY > from.midY ? 1 : -1
                points = [NSPoint(x: from.midX, y: from.midY + sign * from.height / 2), NSPoint(x: to.midX, y: to.midY - sign * to.height / 2)]
            }
            connections.append(MermaidFlowConnection(edge: edge, points: points))
        }
        return MermaidFlowLayout(size: size, bounds: bounds, connections: connections)
    }

    static func render(_ source: String, fontSize: CGFloat = 14, dark: Bool = false) throws -> NSImage {
        let diagram = try parse(source), layout = try layout(diagram, fontSize: fontSize)
        let image = NSImage(size: layout.size, flipped: true) { _ in
            let background = NSColor(srgbRed: dark ? 63.0 / 255 : 1, green: dark ? 63.0 / 255 : 1, blue: dark ? 63.0 / 255 : 1, alpha: 1)
            let foreground = NSColor(srgbRed: dark ? 220.0 / 255 : 23.0 / 255, green: dark ? 220.0 / 255 : 37.0 / 255, blue: dark ? 204.0 / 255 : 59.0 / 255, alpha: 1)
            let panel = NSColor(srgbRed: dark ? 51.0 / 255 : 233.0 / 255, green: dark ? 51.0 / 255 : 237.0 / 255, blue: dark ? 51.0 / 255 : 243.0 / 255, alpha: 1)
            let accent = NSColor(srgbRed: dark ? 136.0 / 255 : 31.0 / 255, green: dark ? 200.0 / 255 : 96.0 / 255, blue: dark ? 1 : 189.0 / 255, alpha: 1)
            background.setFill(); NSRect(origin: .zero, size: layout.size).fill()
            for connection in layout.connections {
                let path = NSBezierPath(); path.move(to: connection.points[0])
                for point in connection.points.dropFirst() { path.line(to: point) }
                path.lineWidth = connection.edge.thick ? 2.8 : 1.8
                if connection.edge.dashed { path.setLineDash([5, 3], count: 2, phase: 0) }
                accent.setStroke(); path.stroke()
                if connection.edge.directed, let tip = connection.points.last {
                    let start = connection.points[connection.points.count - 2], angle = atan2(tip.y - start.y, tip.x - start.x)
                    let arrow = NSBezierPath(); arrow.move(to: tip)
                    arrow.line(to: NSPoint(x: tip.x - cos(angle - .pi / 6) * 11, y: tip.y - sin(angle - .pi / 6) * 11))
                    arrow.line(to: NSPoint(x: tip.x - cos(angle + .pi / 6) * 11, y: tip.y - sin(angle + .pi / 6) * 11)); arrow.close()
                    accent.setFill(); arrow.fill()
                }
                if !connection.edge.label.isEmpty {
                    let start = connection.points[0], end = connection.points[connection.points.count - 1]
                    let style = NSMutableParagraphStyle(); style.alignment = .center; style.lineBreakMode = .byTruncatingTail
                    let rect = NSRect(x: (start.x + end.x) / 2 - 78, y: (start.y + end.y) / 2 - 23, width: 156, height: 22)
                    background.setFill(); rect.fill()
                    (connection.edge.label as NSString).draw(in: rect, withAttributes: [.font: NSFont.systemFont(ofSize: max(10, fontSize - 1)), .foregroundColor: foreground, .paragraphStyle: style])
                }
            }
            for node in diagram.nodes {
                guard let rect = layout.bounds[node.id] else { continue }
                let path: NSBezierPath
                switch node.shape {
                case .diamond:
                    path = NSBezierPath(); path.move(to: NSPoint(x: rect.midX, y: rect.minY)); path.line(to: NSPoint(x: rect.maxX, y: rect.midY)); path.line(to: NSPoint(x: rect.midX, y: rect.maxY)); path.line(to: NSPoint(x: rect.minX, y: rect.midY)); path.close()
                case .circle: path = NSBezierPath(ovalIn: rect)
                case .rounded: path = NSBezierPath(roundedRect: rect, xRadius: 14, yRadius: 14)
                case .rectangle, .subroutine: path = NSBezierPath(rect: rect)
                }
                panel.setFill(); path.fill(); accent.setStroke(); path.lineWidth = 1; path.stroke()
                if node.shape == .subroutine { NSBezierPath(rect: rect.insetBy(dx: 6, dy: 6)).stroke() }
                let style = NSMutableParagraphStyle(); style.alignment = .center; style.lineBreakMode = .byTruncatingTail
                let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: fontSize), .foregroundColor: foreground, .paragraphStyle: style]
                let available = rect.insetBy(dx: node.shape == .diamond ? 32 : 12, dy: 10)
                let label = node.label.replacingOccurrences(of: "<br/>", with: "\n").replacingOccurrences(of: "<br>", with: "\n") as NSString
                let measured = label.boundingRect(with: available.size, options: [.usesLineFragmentOrigin], attributes: attributes).height
                label.draw(in: NSRect(x: available.minX, y: max(available.minY, rect.midY - measured / 2), width: available.width, height: min(available.height, measured + 2)), withAttributes: attributes)
            }
            return true
        }
        image.accessibilityDescription = "Mermaid 流程图：\(diagram.nodes.count) 个节点，\(diagram.edges.count) 条连线"
        return image
    }

    private static func node(_ source: NSString, _ cursor: inout Int, line: Int) throws -> ParsedNode {
        skipSpaces(source, &cursor); let start = cursor
        while cursor < source.length {
            let unit = source.character(at: cursor)
            if [9, 32, 91, 40, 123].contains(unit) || ["-->", "---", "-.->", "==>"].contains(where: { starts(source, cursor, $0) }) { break }
            cursor += 1
        }
        let id = source.substring(with: NSRange(location: start, length: cursor - start))
        guard !id.isEmpty, id.utf16.count <= maximumLabelLength, id.first != "-", id.first != ".", id.last != "-", id.last != ".", !id.contains("--"),
              id.unicodeScalars.allSatisfy({ CharacterSet.alphanumerics.contains($0) || "_.-".unicodeScalars.contains($0) }) else {
            throw MermaidFlowchartError.syntax(line: line, reason: "节点标识只能包含 Unicode 字母、数字、下划线、点或连字符。")
        }
        var shape = MermaidFlowShape.rectangle, label = id, defined = false
        if cursor < source.length && [91, 40, 123].contains(source.character(at: cursor)) {
            defined = true
            if ["[/", "[\\", "([", "{{"].contains(where: { starts(source, cursor, $0) }) {
                throw MermaidFlowchartError.syntax(line: line, reason: "暂不支持这种节点形状，请查看源码。")
            }
            let close: String
            if starts(source, cursor, "[[") { shape = .subroutine; close = "]]"; cursor += 2 }
            else if starts(source, cursor, "((") { shape = .circle; close = "))"; cursor += 2 }
            else {
                let opener = source.character(at: cursor)
                shape = opener == 91 ? .rectangle : opener == 40 ? .rounded : .diamond
                close = opener == 91 ? "]" : opener == 40 ? ")" : "}"; cursor += 1
            }
            let end = source.range(of: close, options: [], range: NSRange(location: cursor, length: source.length - cursor))
            guard end.location != NSNotFound else { throw MermaidFlowchartError.syntax(line: line, reason: "节点标签缺少右括号。") }
            label = source.substring(with: NSRange(location: cursor, length: end.location - cursor)); try checkLabel(label, line: line)
            if label.hasPrefix("\"") && label.hasSuffix("\"") && label.count >= 2 { label = String(label.dropFirst().dropLast()) }
            cursor = end.location + close.utf16.count
        }
        return ParsedNode(value: MermaidFlowNode(id: id, label: label, shape: shape), defined: defined)
    }
    private static func checkLabel(_ label: String, line: Int) throws {
        guard label.utf16.count <= maximumLabelLength else { throw MermaidFlowchartError.syntax(line: line, reason: "节点或连线标签超过 \(maximumLabelLength) 个 UTF-16 单元。") }
    }
    private static func skipSpaces(_ source: NSString, _ cursor: inout Int) { while cursor < source.length && [9, 32].contains(source.character(at: cursor)) { cursor += 1 } }
    private static func starts(_ source: NSString, _ position: Int, _ value: String) -> Bool { position >= 0 && value.utf16.count <= source.length - position && source.substring(with: NSRange(location: position, length: value.utf16.count)) == value }
}
