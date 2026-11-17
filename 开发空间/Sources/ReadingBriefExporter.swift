import Foundation

struct ReadingBriefHeading {
    let title: String
    let sourceStart: Int
}

struct ReadingBriefInput {
    let title: String
    let sourcePath: String?
    let sourceText: String
    let progressPercent: Int
    let generatedAt: Date
    let headings: [ReadingBriefHeading]
    let annotations: [DocumentAnnotation]
}

enum ReadingBriefError: LocalizedError {
    case tooLarge
    var errorDescription: String? { "阅读简报超过 8 MiB 上限，请减少批注数量或备注长度。" }
}

/// Produces self-contained local reading outcomes without changing the source
/// document. User text is escaped before it enters Markdown or HTML output.
enum ReadingBriefExporter {
    static let maximumBytes = 8 * 1024 * 1024

    static func buildMarkdown(_ input: ReadingBriefInput) throws -> String {
        var output = "# 阅读简报：\(escapeInline(input.title.trimmingCharacters(in: .whitespacesAndNewlines)))\n\n"
        output += "- 来源：\(input.sourcePath.map { "`\(escapeCode($0))`" } ?? "未保存文档")\n"
        output += "- 生成时间：\(formatDate(input.generatedAt))\n"
        output += "- 阅读进度：\(min(100, max(0, input.progressPercent)))%\n"
        output += "- 批注数量：\(input.annotations.count)\n"
        for group in grouped(input) {
            output += "\n## \(escapeInline(group.title))\n"
            for annotation in group.items {
                output += "\n### 引文 · 第 \(lineNumber(input.sourceText, annotation.sourceStart)) 行 · \(annotation.anchorState.label)\n"
                output += quote(annotation.quote)
                if let note = annotation.note, !note.isEmpty {
                    output += "\n**备注**\n\n\(quote(note))"
                }
                if !annotation.tags.isEmpty {
                    output += "**标签** " + annotation.tags.map { "`\(escapeCode($0))`" }.joined(separator: " ") + "\n"
                }
            }
        }
        try ensureSize(output)
        return output
    }

    static func buildHTML(_ input: ReadingBriefInput) throws -> String {
        let title = html(input.title)
        var output = """
        <!DOCTYPE html>
        <html lang="zh-CN"><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>阅读简报：\(title)</title>
        <style>body{margin:2rem auto;max-width:860px;padding:0 1.5rem;font:16px/1.75 -apple-system,BlinkMacSystemFont,sans-serif;color:#29241e;background:#fffdf8}blockquote{margin:0;padding:.5rem 0 .5rem 1rem;border-left:3px solid #b76e3b;background:#f7efe3;white-space:normal}code{background:#f2eadf;padding:.1rem .3rem;border-radius:3px}.state{color:#8a5a2b}.meta{color:#665b4f}article{margin:1.5rem 0}</style>
        </head><body><h1>阅读简报：\(title)</h1>
        <p class="meta">来源：\(input.sourcePath.map(html) ?? "未保存文档")<br>生成时间：\(html(formatDate(input.generatedAt)))<br>阅读进度：\(min(100, max(0, input.progressPercent)))%<br>批注数量：\(input.annotations.count)</p>
        """
        for group in grouped(input) {
            output += "<h2>\(html(group.title))</h2>\n"
            for annotation in group.items {
                output += "<article><p class=\"state\">第 \(lineNumber(input.sourceText, annotation.sourceStart)) 行 · \(html(annotation.anchorState.label))</p><blockquote>\(html(annotation.quote).replacingOccurrences(of: "\n", with: "<br>"))</blockquote>"
                if let note = annotation.note, !note.isEmpty {
                    output += "<p><strong>备注</strong><br>\(html(note).replacingOccurrences(of: "\n", with: "<br>"))</p>"
                }
                if !annotation.tags.isEmpty {
                    output += "<p><strong>标签</strong> " + annotation.tags.map { "<code>\(html($0))</code>" }.joined(separator: " ") + "</p>"
                }
                output += "</article>\n"
            }
        }
        output += "</body></html>\n"
        try ensureSize(output)
        return output
    }

    private static func grouped(_ input: ReadingBriefInput) -> [(title: String, items: [DocumentAnnotation])] {
        let headings = input.headings.sorted { $0.sourceStart < $1.sourceStart }
        let annotations = input.annotations.sorted { $0.sourceStart == $1.sourceStart ? $0.createdAt < $1.createdAt : $0.sourceStart < $1.sourceStart }
        var groups: [(String, [DocumentAnnotation])] = []
        for annotation in annotations {
            let title = headings.last(where: { $0.sourceStart <= annotation.sourceStart })?.title ?? "开篇"
            if let index = groups.firstIndex(where: { $0.0 == title }) { groups[index].1.append(annotation) }
            else { groups.append((title, [annotation])) }
        }
        return groups
    }

    private static func quote(_ value: String) -> String {
        value.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
            .split(separator: "\n", omittingEmptySubsequences: false).map { "> \(escapeMarkdownUserText(String($0)))\n" }.joined()
    }

    private static func lineNumber(_ text: String, _ offset: Int) -> Int {
        let source = text as NSString, end = min(source.length, max(0, offset)); var line = 1
        for index in 0..<end where source.character(at: index) == 10 { line += 1 }
        return line
    }

    private static func escapeInline(_ value: String) -> String {
        value.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "#", with: "\\#")
            .replacingOccurrences(of: "*", with: "\\*").replacingOccurrences(of: "_", with: "\\_")
            .replacingOccurrences(of: "[", with: "\\[").replacingOccurrences(of: "]", with: "\\]")
    }

    private static func escapeMarkdownUserText(_ value: String) -> String { escapeInline(value).replacingOccurrences(of: "`", with: "\\`") }
    private static func escapeCode(_ value: String) -> String { value.replacingOccurrences(of: "`", with: "\\`") }
    private static func html(_ value: String) -> String {
        value.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&#39;")
    }
    private static func formatDate(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withColonSeparatorInTimeZone]
        return formatter.string(from: date)
    }
    private static func ensureSize(_ value: String) throws { if value.lengthOfBytes(using: .utf8) > maximumBytes { throw ReadingBriefError.tooLarge } }
}
