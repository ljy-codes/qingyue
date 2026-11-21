import AppKit
import Foundation

enum SequenceEventKind { case message, note }

struct SequenceParticipant {
    let id: String
    let label: String
    let isActor: Bool
}

struct SequenceEvent {
    let from: String
    let to: String
    let text: String
    let kind: SequenceEventKind
    let dashed: Bool
    /// `>` is a line with no arrowhead; `>>` solid, `)` open, `x` cross.
    let arrow: String
    let placement: String
}

struct SequenceDiagram {
    let participants: [SequenceParticipant]
    let events: [SequenceEvent]
    let autoNumber: Bool
}

enum MermaidSequenceError: LocalizedError {
    case syntax(line: Int, reason: String)
    case drawingLimit(String)
    var errorDescription: String? {
        switch self {
        case .syntax(let line, let reason): return line > 0 ? "第 \(line) 行：\(reason)" : reason
        case .drawingLimit(let reason): return reason
        }
    }
}

/// A bounded, offline sequenceDiagram subset. Neither parsing nor drawing
/// interprets HTML, runs script, downloads media, or touches the source file.
enum MermaidSequence {
    static let maximumSourceLength = 32_000
    static let maximumLines = 1_000
    static let maximumParticipants = 20
    static let maximumEvents = 200
    static let maximumLabelLength = 2_000
    static let maximumDrawingArea: CGFloat = 24_000_000

    static func parse(_ source: String) throws -> SequenceDiagram {
        var parser = Parser()
        return try parser.parse(source)
    }

    /// Drawing is vector-backed: creating the preview does not preallocate a
    /// huge Retina bitmap. A caller may scale this image to fit a text column,
    /// retaining its full natural size for a horizontally scrollable viewer.
    static func render(_ source: String, fontSize: CGFloat = 14, dark: Bool = false) throws -> NSImage {
        let diagram = try parse(source)
        let plan = try layout(diagram, fontSize: fontSize)
        let palette = Palette(dark: dark)
        let image = NSImage(size: plan.size, flipped: true) { _ in
            NSGraphicsContext.saveGraphicsState()
            defer { NSGraphicsContext.restoreGraphicsState() }
            palette.background.setFill()
            NSRect(origin: .zero, size: plan.size).fill()
            let transform = NSAffineTransform()
            transform.translateX(by: plan.translation.x, yBy: plan.translation.y)
            transform.concat()
            for shape in plan.shapes { shape.draw(palette: palette) }
            return true
        }
        image.accessibilityDescription = "Mermaid 时序图：\(diagram.participants.count) 个参与者，\(diagram.events.count) 个事件"
        return image
    }

    // Internal value-only geometry makes overflow and clipping testable without
    // loading NSViews or allocating a bitmap for every test diagram.
    struct Layout {
        let size: NSSize
        let translation: NSPoint
        let shapes: [Shape]
        let bounds: NSRect
    }

    enum Tone { case text, muted, accent, panel, note }
    enum Shape {
        case text(String, NSRect, CGFloat, Bool, NSTextAlignment, Tone)
        case box(NSRect, CGFloat, Tone, Tone)
        case line([NSPoint], CGFloat, Bool, Tone)
        case solidArrow([NSPoint])

        var bounds: NSRect {
            switch self {
            case .text(_, let r, _, _, _, _), .box(let r, _, _, _): return r
            case .line(let points, let thickness, _, _):
                return MermaidSequence.pointBounds(points).insetBy(dx: -thickness / 2, dy: -thickness / 2)
            case .solidArrow(let points): return MermaidSequence.pointBounds(points)
            }
        }
        fileprivate func draw(palette: Palette) {
            switch self {
            case .text(let value, let rect, let size, let bold, let alignment, let tone):
                (value as NSString).draw(with: rect, options: [.usesLineFragmentOrigin, .usesFontLeading],
                                        attributes: MermaidSequence.textAttributes(size: size, bold: bold, alignment: alignment, color: palette.color(tone)))
            case .box(let rect, let radius, let fill, let border):
                let p = NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius)
                palette.color(fill).setFill(); p.fill()
                palette.color(border).setStroke(); p.lineWidth = 1; p.stroke()
            case .line(let points, let width, let dashed, let tone):
                guard let first = points.first else { return }
                let p = NSBezierPath(); p.move(to: first)
                for point in points.dropFirst() { p.line(to: point) }
                p.lineWidth = width; p.lineJoinStyle = .round
                if dashed { p.setLineDash([5, 3], count: 2, phase: 0) }
                palette.color(tone).setStroke(); p.stroke()
            case .solidArrow(let points):
                guard let first = points.first else { return }
                let p = NSBezierPath(); p.move(to: first)
                for point in points.dropFirst() { p.line(to: point) }
                p.close(); palette.accent.setFill(); p.fill()
            }
        }
    }

    static func layout(_ diagram: SequenceDiagram, fontSize rawSize: CGFloat = 14) throws -> Layout {
        guard rawSize.isFinite, rawSize > 0 else { throw MermaidSequenceError.drawingLimit("时序图字号无效。") }
        let fontSize = min(30, max(10, rawSize))
        guard !diagram.participants.isEmpty, diagram.participants.count <= maximumParticipants,
              diagram.events.count <= maximumEvents,
              Set(diagram.participants.map { $0.id }).count == diagram.participants.count else {
            throw MermaidSequenceError.drawingLimit("时序图结构无效或超过绘制数量限制。")
        }
        let headerWidth = max(144, fontSize * 10 + 20)
        let lane = headerWidth + max(42, fontSize * 3)
        let indexes = Dictionary(uniqueKeysWithValues: diagram.participants.enumerated().map { ($0.element.id, $0.offset) })
        func center(_ id: String) throws -> CGFloat {
            guard let i = indexes[id] else { throw MermaidSequenceError.drawingLimit("时序图引用了未知参与者：\(id)") }
            return CGFloat(i) * lane
        }
        var shapes: [Shape] = []
        let headerHeight = diagram.participants.map { p in
            textHeight(p.label, width: headerWidth - 24, size: fontSize, bold: true) + 22 + (p.isActor ? 52 : 0)
        }.max() ?? 44
        let top: CGFloat = 0
        for (i, participant) in diagram.participants.enumerated() {
            let x = CGFloat(i) * lane
            let box = NSRect(x: x - headerWidth / 2, y: top, width: headerWidth, height: headerHeight)
            if !participant.isActor { shapes.append(.box(box, 6, .panel, .muted)) }
            else {
                let head = NSRect(x: x - 7, y: 4, width: 14, height: 14)
                // The head is a small rounded square, rendered as a circle.
                shapes.append(.box(head, 7, .panel, .accent))
                shapes.append(.line([NSPoint(x: x, y: 18), NSPoint(x: x, y: 36)], 1.5, false, .accent))
                shapes.append(.line([NSPoint(x: x - 14, y: 27), NSPoint(x: x + 14, y: 27)], 1.5, false, .accent))
                shapes.append(.line([NSPoint(x: x - 12, y: 49), NSPoint(x: x, y: 36), NSPoint(x: x + 12, y: 49)], 1.5, false, .accent))
            }
            let labelRect = NSRect(x: box.minX + 12, y: participant.isActor ? 56 : 11,
                                   width: headerWidth - 24, height: headerHeight - (participant.isActor ? 62 : 20))
            shapes.append(.text(participant.label, labelRect, fontSize, true, .center, .accent))
        }
        let lifetimeStart = headerHeight + 10
        var y = lifetimeStart + 20
        var number = 0
        var eventShapes: [Shape] = []
        func arrowHead(x: CGFloat, y: CGFloat, direction: CGFloat, token: String) {
            if token == ">" { return }
            if token == "x" {
                eventShapes.append(.line([NSPoint(x: x - 5, y: y - 5), NSPoint(x: x + 5, y: y + 5)], 1.7, false, .accent))
                eventShapes.append(.line([NSPoint(x: x - 5, y: y + 5), NSPoint(x: x + 5, y: y - 5)], 1.7, false, .accent))
            } else {
                let points = [NSPoint(x: x - direction * 9, y: y - 5), NSPoint(x: x, y: y), NSPoint(x: x - direction * 9, y: y + 5)]
                if token == ")" { eventShapes.append(.line(points, 1.6, false, .accent)) }
                else { eventShapes.append(.solidArrow(points)) }
            }
        }
        for event in diagram.events {
            guard event.text.utf16.count <= maximumLabelLength else { throw MermaidSequenceError.drawingLimit("时序图单条文字超过限制。") }
            let from = try center(event.from), to = try center(event.to)
            if event.kind == .note {
                var width = min(320, lane - 26)
                let x: CGFloat
                switch event.placement {
                case "left": x = from - width - 18
                case "right": x = from + 18
                default:
                    width = max(headerWidth, abs(to - from) + headerWidth * 0.8)
                    x = (from + to - width) / 2
                }
                let height = textHeight(event.text, width: width - 22, size: fontSize) + 22
                eventShapes.append(.box(NSRect(x: x, y: y, width: width, height: height), 4, .note, .accent))
                eventShapes.append(.text(event.text, NSRect(x: x + 11, y: y + 11, width: width - 22, height: height - 20), fontSize, false, .left, .text))
                y += height + 22
            } else {
                number += 1
                let selfMessage = from == to
                let width = selfMessage ? lane - 32 : min(420, abs(to - from) - 24)
                let text = (diagram.autoNumber ? "\(number). " : "") + event.text
                let height = textHeight(text, width: width, size: fontSize)
                let x = selfMessage ? from + 12 : (from + to - width) / 2
                eventShapes.append(.text(text, NSRect(x: x, y: y, width: width, height: height), fontSize, false, .center, .text))
                let arrowY = y + height + 10
                if selfMessage {
                    let loop = max(48, lane / 3)
                    eventShapes.append(.line([NSPoint(x: from, y: arrowY), NSPoint(x: from + loop, y: arrowY), NSPoint(x: from + loop, y: arrowY + 28), NSPoint(x: from, y: arrowY + 28)], 1.6, event.dashed, .accent))
                    arrowHead(x: from, y: arrowY + 28, direction: -1, token: event.arrow)
                    y = arrowY + 54
                } else {
                    eventShapes.append(.line([NSPoint(x: from, y: arrowY), NSPoint(x: to, y: arrowY)], 1.6, event.dashed, .accent))
                    arrowHead(x: to, y: arrowY, direction: to > from ? 1 : -1, token: event.arrow)
                    y = arrowY + 28
                }
            }
            guard y <= 20_000 else { throw MermaidSequenceError.drawingLimit("时序图排版高度超过限制，请拆分为较小的图；源码仍可查看。") }
        }
        for i in diagram.participants.indices {
            shapes.append(.line([NSPoint(x: CGFloat(i) * lane, y: lifetimeStart), NSPoint(x: CGFloat(i) * lane, y: y)], 1, true, .muted))
        }
        shapes += eventShapes
        let bounds = shapes.reduce(NSRect.null) { $0.union($1.bounds) }
        let size = NSSize(width: ceil(bounds.width + 48), height: ceil(bounds.height + 48))
        guard size.width.isFinite, size.height.isFinite, size.width <= 8_000, size.height <= 20_000,
              size.width * size.height <= maximumDrawingArea else {
            throw MermaidSequenceError.drawingLimit("时序图绘制尺寸超过限制，请减少参与者或拆分图形；源码仍可查看。")
        }
        return Layout(size: size, translation: NSPoint(x: 24 - bounds.minX, y: 24 - bounds.minY), shapes: shapes, bounds: bounds)
    }

    private static func pointBounds(_ points: [NSPoint]) -> NSRect {
        guard let first = points.first else { return .zero }
        var minX = first.x, minY = first.y, maxX = first.x, maxY = first.y
        for point in points.dropFirst() { minX = min(minX, point.x); minY = min(minY, point.y); maxX = max(maxX, point.x); maxY = max(maxY, point.y) }
        return NSRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
    }
    private static func textAttributes(size: CGFloat, bold: Bool = false, alignment: NSTextAlignment = .center, color: NSColor) -> [NSAttributedString.Key: Any] {
        let style = NSMutableParagraphStyle(); style.alignment = alignment
        style.lineBreakMode = .byCharWrapping; style.lineSpacing = 3
        return [.font: NSFont.systemFont(ofSize: size, weight: bold ? .semibold : .regular), .foregroundColor: color, .paragraphStyle: style]
    }
    private static func textHeight(_ text: String, width: CGFloat, size: CGFloat, bold: Bool = false) -> CGFloat {
        let r = (text as NSString).boundingRect(with: NSSize(width: max(10, width), height: 100_000), options: [.usesLineFragmentOrigin, .usesFontLeading], attributes: textAttributes(size: size, bold: bold, color: .black))
        return max(ceil(size * 1.5), ceil(r.height) + 3)
    }

    fileprivate struct Palette {
        let dark: Bool
        var background: NSColor { dark ? NSColor(calibratedWhite: 0.10, alpha: 1) : .white }
        var accent: NSColor { dark ? NSColor(calibratedRed: 0.48, green: 0.72, blue: 1, alpha: 1) : NSColor(calibratedRed: 0.16, green: 0.36, blue: 0.7, alpha: 1) }
        func color(_ tone: Tone) -> NSColor {
            switch tone {
            case .text: return dark ? NSColor(calibratedWhite: 0.9, alpha: 1) : NSColor(calibratedWhite: 0.13, alpha: 1)
            case .muted: return dark ? NSColor(calibratedWhite: 0.5, alpha: 1) : NSColor(calibratedWhite: 0.65, alpha: 1)
            case .accent: return accent
            case .panel: return dark ? NSColor(calibratedWhite: 0.15, alpha: 1) : NSColor(calibratedWhite: 0.96, alpha: 1)
            case .note: return dark ? NSColor(calibratedRed: 0.22, green: 0.21, blue: 0.13, alpha: 1) : NSColor(calibratedRed: 1, green: 0.97, blue: 0.83, alpha: 1)
            }
        }
    }

    private struct Parser {
        var participants: [SequenceParticipant] = []
        var events: [SequenceEvent] = []
        var indexes: [String: Int] = [:]
        var explicit = Set<String>()
        var autoNumber = false
        var line = 0
        let arrows = ["-->>", "-->", "--x", "--)", "->>", "->", "-x", "-)"]

        mutating func parse(_ source: String) throws -> SequenceDiagram {
            guard !source.isEmpty else { throw failure("时序图内容不能为空。") }
            guard source.utf16.count <= maximumSourceLength else { throw failure("时序图源码不能超过 32,000 个 UTF-16 单元。") }
            var input = source
            if input.first == "\u{FEFF}" { input.removeFirst() }
            let lines = input.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n").components(separatedBy: "\n")
            // A trailing line terminator does not create an additional source line.
            let lineCount = lines.count - (input.hasSuffix("\n") || input.hasSuffix("\r") ? 1 : 0)
            guard lineCount <= maximumLines else { throw failure("时序图源码不能超过 1,000 行，含空行与注释。") }
            var headerSeen = false
            for raw in lines {
                line += 1
                if raw.contains("%%{") { throw failure("不支持 Mermaid 初始化或配置指令（%%{...}%%）。") }
                var value = raw.trimmingCharacters(in: .whitespaces)
                if value.isEmpty || value.hasPrefix("%%") { continue }
                if let comment = value.range(of: "%%") {
                    let prefix = String(value[..<comment.lowerBound]).trimmingCharacters(in: .whitespaces)
                    if equal(prefix, "sequenceDiagram") || equal(prefix, "autonumber") { value = prefix }
                }
                if value.contains(";") { throw failure("不支持半角分号（;）或分号分隔语句，请换行；文字可用全角“；”。") }
                if !headerSeen {
                    guard equal(value, "sequenceDiagram") else { throw failure("图必须以 sequenceDiagram 开头；当前只支持有限时序图。") }
                    headerSeen = true; continue
                }
                try statement(value)
            }
            guard headerSeen && !participants.isEmpty else { throw failure("时序图为空，需要参与者、消息或 Note。") }
            return SequenceDiagram(participants: participants, events: events, autoNumber: autoNumber)
        }
        mutating func statement(_ value: String) throws {
            var remaining = value
            let word = takeWord(&remaining)
            if equal(word, "participant") || equal(word, "actor") {
                let id = takeWord(&remaining)
                var label = id
                if !remaining.isEmpty {
                    guard equal(takeWord(&remaining), "as") else { throw failure("声明格式应为 participant/actor 标识 as 别名。") }
                    label = remaining
                }
                let display = try normalizedLabel(label)
                guard !explicit.contains(id) else { throw failure("参与者被重复声明，请使用唯一标识。") }
                try participant(id, label: display, actor: equal(word, "actor")); explicit.insert(id)
                return
            }
            if equal(word, "autonumber") {
                guard remaining.isEmpty else { throw failure("仅支持单独的 autonumber，不支持起始值、步长或 off。") }
                guard !events.contains(where: { $0.kind == .message }) else { throw failure("autonumber 必须在第一条消息之前。") }
                autoNumber = true; return
            }
            if equal(word, "note") { try note(remaining); return }
            if equal(word, "activate") || equal(word, "deactivate") { throw failure("不支持 activate/deactivate 激活语法。") }
            try message(value)
        }
        mutating func message(_ value: String) throws {
            guard let colon = value.firstIndex(of: ":") else { throw failure("无法识别语法；消息应为 A->>B: 文本。") }
            let endpoints = String(value[..<colon]).trimmingCharacters(in: .whitespaces)
            for index in endpoints.indices where endpoints[index] == "-" {
                let tail = endpoints[index...]
                for token in arrows where tail.hasPrefix(token) {
                    let from = String(endpoints[..<index]).trimmingCharacters(in: .whitespaces)
                    let to = String(tail.dropFirst(token.count)).trimmingCharacters(in: .whitespaces)
                    if to.hasPrefix("+") || to.hasPrefix("-") { throw failure("不支持箭头后的 +/- 激活后缀。") }
                    let text = try normalizedLabel(String(value[value.index(after: colon)...]))
                    try participant(from); try participant(to)
                    let dashed = token.hasPrefix("--")
                    try add(SequenceEvent(from: from, to: to, text: text, kind: .message, dashed: dashed, arrow: String(token.dropFirst(dashed ? 2 : 1)), placement: "over"))
                    return
                }
            }
            throw failure("不支持此箭头；仅支持 ->、->>、-->、-->>、-x、--x、-) 和 --)。")
        }
        mutating func note(_ value: String) throws {
            guard let colon = value.firstIndex(of: ":") else { throw failure("Note 缺少冒号或正文。") }
            var targets = String(value[..<colon]).trimmingCharacters(in: .whitespaces)
            let placement = takeWord(&targets).lowercased()
            if placement == "left" || placement == "right" {
                guard equal(takeWord(&targets), "of") else { throw failure("Note 左右位置应为 left/right of A。") }
            } else if placement != "over" { throw failure("Note 仅支持 over、left of 或 right of。") }
            let ids = targets.components(separatedBy: ",").map { $0.trimmingCharacters(in: .whitespaces) }
            guard ids.count <= (placement == "over" ? 2 : 1), let from = ids.first, let to = ids.last else { throw failure("Note 目标数量无效。") }
            let text = try normalizedLabel(String(value[value.index(after: colon)...]))
            try participant(from); try participant(to)
            try add(SequenceEvent(from: from, to: to, text: text, kind: .note, dashed: false, arrow: ">>", placement: placement))
        }
        mutating func participant(_ id: String, label: String? = nil, actor: Bool = false) throws {
            guard id.utf16.count <= maximumLabelLength, safeID(id) else { throw failure("参与者标识只能包含字母、数字、组合字符、下划线、点和内部连字符。") }
            if let i = indexes[id] {
                if let label = label { participants[i] = SequenceParticipant(id: id, label: label, isActor: actor) }
                return
            }
            guard participants.count < maximumParticipants else { throw failure("参与者最多 20 个，含自动声明。") }
            indexes[id] = participants.count; participants.append(SequenceParticipant(id: id, label: label ?? id, isActor: actor))
        }
        mutating func add(_ event: SequenceEvent) throws {
            guard events.count < maximumEvents else { throw failure("消息与 Note 合计最多 200 个。") }
            events.append(event)
        }
        func normalizedLabel(_ value: String) throws -> String {
            let trimmed = value.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty, trimmed.utf16.count <= maximumLabelLength else { throw failure("单条文字不能为空，且最多 2,000 个 UTF-16 单元。") }
            return trimmed.replacingOccurrences(of: "<br>", with: "\n", options: .caseInsensitive)
                .replacingOccurrences(of: "<br/>", with: "\n", options: .caseInsensitive)
                .replacingOccurrences(of: "<br />", with: "\n", options: .caseInsensitive)
        }
        func safeID(_ value: String) -> Bool {
            guard !value.isEmpty, !value.hasPrefix("-"), !value.hasSuffix("-"), !value.contains("--") else { return false }
            return value.unicodeScalars.allSatisfy { scalar in
                switch scalar.properties.generalCategory {
                case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter,
                     .decimalNumber, .letterNumber, .otherNumber, .nonspacingMark, .spacingMark, .enclosingMark, .connectorPunctuation: return true
                default: return scalar == "." || scalar == "-"
                }
            }
        }
        func equal(_ a: String, _ b: String) -> Bool { a.caseInsensitiveCompare(b) == .orderedSame }
        func takeWord(_ value: inout String) -> String {
            value = value.trimmingCharacters(in: .whitespaces)
            let word = String(value.prefix { !$0.isWhitespace })
            value = String(value.dropFirst(word.count)).trimmingCharacters(in: .whitespaces)
            return word
        }
        func failure(_ reason: String) -> MermaidSequenceError { .syntax(line: line, reason: reason) }
    }
}
