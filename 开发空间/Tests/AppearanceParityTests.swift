import AppKit
import Foundation

@main
struct AppearanceParityTests {
    private static var checks = 0

    static func main() throws {
        _ = NSApplication.shared
        NSApp.setActivationPolicy(.prohibited)
        let dark = ReaderTheme(dark: true), light = ReaderTheme(dark: false)
        let expectedDark: [(NSColor, UInt32)] = [
            (dark.window, 0x2B2B2B), (dark.panel, 0x333333), (dark.editor, 0x3F3F3F),
            (dark.text, 0xDCDCCC), (dark.muted, 0xBDBDB5), (dark.border, 0x585858),
            (dark.accent, 0x4782FF), (dark.selection, 0x365579), (dark.hover, 0x4A4A4A),
            (dark.accentText, 0x88C8FF), (dark.palette.key, 0x88C8FF),
            (dark.palette.string, 0xFFD096), (dark.palette.number, 0x74DEC7),
            (dark.palette.comment, 0xBAC5BA), (dark.unsaved, 0xFFBE72), (dark.saved, 0x74D6AD)
        ]
        let expectedLight: [(NSColor, UInt32)] = [
            (light.window, 0xF3F5F8), (light.panel, 0xE9EDF3), (light.editor, 0xFFFFFF),
            (light.text, 0x17253B), (light.muted, 0x53647A), (light.border, 0xCFD7E3),
            (light.accent, 0x2464DB), (light.selection, 0xCFDFFF), (light.hover, 0xDFE7F4),
            (light.accentText, 0x1F60BD), (light.palette.key, 0x175BB5),
            (light.palette.string, 0xA64A0A), (light.palette.number, 0x087563),
            (light.palette.comment, 0x5C6D59), (light.unsaved, 0xA95A00), (light.saved, 0x19764E)
        ]
        for (color, hex) in expectedDark + expectedLight { expect(rgb(color) == hex, "Theme token follows Windows sRGB value \(String(hex, radix: 16))") }
        expect(rgb(dark.tabSelected) == rgb(dark.panel) && rgb(light.tabSelected) == rgb(light.panel), "Selected tabs share the Windows panel background")
        expect(contrast(dark.text, dark.editor) >= 4.5, "Dark document foreground remains legible")
        expect(contrast(light.text, light.editor) >= 4.5, "Light document foreground remains legible")
        expect(contrast(dark.muted, dark.panel) >= 4.5 && contrast(light.muted, light.panel) >= 4.5, "Auxiliary labels have sufficient contrast in both themes")

        var actions = 0
        let button = ActionButton("打开文件") { actions += 1 }
        expect(button.controlHeight == 32 && button.cornerRadius == 6, "Button geometry matches Windows MinHeight and CornerRadius")
        expect(button.horizontalPadding == 14 && button.font?.pointSize == 13, "Button padding and text size match Windows")
        let textWidth = (button.title as NSString).size(withAttributes: [.font: button.font!]).width
        expect(button.intrinsicContentSize.width >= textWidth + 28, "Intrinsic width includes fourteen point padding on both sides")
        expect(button.accessibilityLabel() == "打开文件", "Native button retains accessible name")
        button.performClick(nil)
        expect(actions == 1, "Button keeps its native action")
        button.isEnabled = false; button.performClick(nil)
        expect(actions == 1, "Disabled button cannot execute its action")
        button.isEnabled = true
        button.title = "复制全文（含图片）"
        expect(button.accessibilityLabel() == button.title && button.intrinsicContentSize.width > textWidth + 28, "Changing title refreshes layout and accessible name")
        button.controlHeight = 22; button.horizontalPadding = 0
        expect(button.constraints.contains { $0.firstAttribute == .height && $0.constant == 22 }, "Compact close buttons can opt into the Windows twenty-two point height")

        let editor = EditorTextView(frame: NSRect(x: 0, y: 0, width: 200, height: 100))
        editor.isEditable = true; editor.string = "中文😀\n"; editor.setSelectedRange(NSRange(location: 4, length: 0))
        let source = editor.string; editor.insertTab(nil)
        expect(editor.string == "中文😀  \n", "Styling keeps native editor commands unchanged")
        editor.breakUndoCoalescing(); editor.tabUndo.undo()
        expect(editor.string == source, "Editor undo survives the appearance change")

        // Render controls without requiring screen capture or controlling any
        // user's application. Native bitmap inspection catches regressions such
        // as a tab using the accent as its entire background.
        for theme in [dark, light] {
            let tab = ActionButton("文档.md") { }
            tab.fill = theme.tabSelected; tab.ink = theme.text; tab.hoverFill = theme.hover
            tab.borderColor = .clear; tab.bottomAccent = theme.accent; tab.cornerRadius = 0
            tab.frame = NSRect(x: 0, y: 0, width: 170, height: 34)
            let bitmap = try image(of: tab)
            expect(close(bitmap.colorAt(x: 3, y: 3), theme.panel), "Selected tab has a panel-colored face")
            // Bitmap and AppKit coordinate conventions differ; the accent must
            // appear on exactly one horizontal edge, not fill the tab body.
            let edge1 = bitmap.colorAt(x: 80, y: 0), edge2 = bitmap.colorAt(x: 80, y: bitmap.pixelsHigh - 1)
            expect(close(edge1, theme.accent) || close(edge2, theme.accent), "Selected tab exposes a narrow accent edge")
            expect(close(bitmap.colorAt(x: 8, y: bitmap.pixelsHigh / 2), theme.panel), "Accent edge does not become a solid-blue selected tab")
        }
        try drawPreview(dark: dark, light: light)
        print("AppearanceParityTests: \(checks) checks passed")
    }

    private static func drawPreview(dark: ReaderTheme, light: ReaderTheme) throws {
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 720, pixelsHigh: 180, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        let context = NSGraphicsContext(bitmapImageRep: bitmap)!
        NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current = context
        for (row, theme) in [dark, light].enumerated() {
            let y = CGFloat(row * 90)
            theme.window.setFill(); NSRect(x: 0, y: y, width: 720, height: 90).fill()
            for (column, title) in ["打开文件", "保存", "编辑原文", "专注"].enumerated() {
                let button = ActionButton(title) { }
                button.fill = column == 2 ? theme.accent : theme.button
                button.ink = column == 2 ? .white : theme.text
                button.borderColor = theme.border
                button.frame = NSRect(x: 0, y: 0, width: 100, height: 32)
                NSGraphicsContext.saveGraphicsState()
                context.cgContext.translateBy(x: CGFloat(18 + column * 111), y: y + 45)
                button.draw(button.bounds)
                NSGraphicsContext.restoreGraphicsState()
            }
            for (column, selected) in [true, false].enumerated() {
                let tab = ActionButton(column == 0 ? "文档.md" : "配置.json") { }
                tab.fill = selected ? theme.panel : theme.window; tab.ink = selected ? theme.text : theme.muted
                tab.bottomAccent = selected ? theme.accent : nil; tab.cornerRadius = 0
                tab.frame = NSRect(x: 0, y: 0, width: 160, height: 34)
                NSGraphicsContext.saveGraphicsState()
                context.cgContext.translateBy(x: CGFloat(18 + column * 163), y: y + 4)
                tab.draw(tab.bounds)
                NSGraphicsContext.restoreGraphicsState()
            }
        }
        NSGraphicsContext.restoreGraphicsState()
        let output = URL(fileURLWithPath: "/private/tmp/qingyue-appearance-preview.png")
        try bitmap.representation(using: .png, properties: [:])!.write(to: output)
    }

    private static func image(of view: NSView) throws -> NSBitmapImageRep {
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(view.bounds.width), pixelsHigh: Int(view.bounds.height), bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
              let context = NSGraphicsContext(bitmapImageRep: bitmap) else { throw NSError(domain: "AppearanceParityTests", code: 1) }
        NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current = context
        view.draw(view.bounds)
        NSGraphicsContext.restoreGraphicsState()
        return bitmap
    }
    private static func rgb(_ color: NSColor) -> UInt32 {
        let rgb = color.usingColorSpace(.sRGB)!
        return UInt32((rgb.redComponent * 255).rounded()) << 16 | UInt32((rgb.greenComponent * 255).rounded()) << 8 | UInt32((rgb.blueComponent * 255).rounded())
    }
    private static func close(_ first: NSColor?, _ second: NSColor) -> Bool {
        guard let first = first?.usingColorSpace(.sRGB), let second = second.usingColorSpace(.sRGB) else { return false }
        return abs(first.redComponent - second.redComponent) < 0.025 && abs(first.greenComponent - second.greenComponent) < 0.025 && abs(first.blueComponent - second.blueComponent) < 0.025
    }
    private static func contrast(_ first: NSColor, _ second: NSColor) -> CGFloat {
        func luminance(_ color: NSColor) -> CGFloat {
            let c = color.usingColorSpace(.sRGB)!
            let p = [c.redComponent, c.greenComponent, c.blueComponent].map { $0 <= 0.04045 ? $0 / 12.92 : pow(($0 + 0.055) / 1.055, 2.4) }
            return p[0] * 0.2126 + p[1] * 0.7152 + p[2] * 0.0722
        }
        let a = luminance(first), b = luminance(second)
        return (max(a, b) + 0.05) / (min(a, b) + 0.05)
    }
    private static func expect(_ condition: @autoclosure () -> Bool, _ text: String) {
        guard condition() else { fatalError(text) }
        checks += 1
    }
}
