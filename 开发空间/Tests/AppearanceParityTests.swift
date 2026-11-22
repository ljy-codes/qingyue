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
            (dark.window, 0x191B1F), (dark.panel, 0x23262B), (dark.main, 0x24354A), (dark.editor, 0x30343A),
            (dark.text, 0xE8E2D8), (dark.muted, 0xBEB8AE), (dark.border, 0x4B4D52),
            (dark.accent, 0x4F86F7), (dark.selection, 0x355A91), (dark.hover, 0x34383F),
            (dark.accentText, 0x8DC5FF), (dark.palette.key, 0x8DC5FF),
            (dark.palette.string, 0xFFD39A), (dark.palette.number, 0x82DFC9),
            (dark.palette.comment, 0xC3C9BF), (dark.unsaved, 0xFFC078), (dark.saved, 0x7BDEB3)
        ]
        let expectedLight: [(NSColor, UInt32)] = [
            (light.window, 0xF6F4EF), (light.panel, 0xEFEAE2), (light.main, 0xE6F0FA), (light.editor, 0xF8FAFC),
            (light.text, 0x262A2F), (light.muted, 0x6B6A66), (light.border, 0xD8D1C5),
            (light.accent, 0x2F6FDB), (light.selection, 0xD7E5FA), (light.hover, 0xF3EDE4),
            (light.accentText, 0x175BB5), (light.palette.key, 0x175BB5),
            (light.palette.string, 0xA64A0A), (light.palette.number, 0x087563),
            (light.palette.comment, 0x5C6D59), (light.unsaved, 0xA95A00), (light.saved, 0x19764E)
        ]
        for (color, hex) in expectedDark + expectedLight { expect(rgb(color) == hex, "Theme token follows Windows sRGB value \(String(hex, radix: 16))") }
        expect(rgb(dark.tabSelected) == rgb(dark.panel) && rgb(light.tabSelected) == rgb(light.panel), "Selected tabs share the Windows panel background")
        expect(contrast(dark.text, dark.editor) >= 4.5, "Dark document foreground remains legible")
        expect(contrast(light.text, light.editor) >= 4.5, "Light document foreground remains legible")
        expect(contrast(dark.muted, dark.panel) >= 4.5 && contrast(light.muted, light.panel) >= 4.5, "Auxiliary labels have sufficient contrast in both themes")

        let warm = ReaderTheme(dark: false, warmPaper: true)
        expect(rgb(warm.main) == 0xE7EEF0 && rgb(warm.editor) == 0xFFF8E9 && rgb(warm.text) == 0x3C3026, "Warm paper uses the shared Windows palette")
        expect(contrast(warm.text, warm.editor) >= 4.5, "Warm paper foreground remains legible")
        expect(AppIconTheme.allCases.map(\.label) == ["天空蓝", "樱花紫", "薄荷珊瑚", "蜜桃紫"], "Four Windows icon themes are available")
        expect(AppIconTheme.parse(nil) == .sky && AppIconTheme.parse("unknown") == .sky, "Old and invalid icon settings default to sky")
        expect(AppIconTheme.parse("sakura") == .sakura && AppIconTheme.parse("薄荷珊瑚") == .mint, "Icon names and labels normalize correctly")
        let iconSuite = "qingyue-icon-tests-" + UUID().uuidString
        let iconDefaults = UserDefaults(suiteName: iconSuite)!
        defer { iconDefaults.removePersistentDomain(forName: iconSuite) }
        for option in AppIconTheme.allCases {
            let image = AppIconTheme.load(option)
            let maximumPixels = image?.representations.compactMap { ($0 as? NSBitmapImageRep)?.pixelsWide }.max() ?? 0
            expect(image?.isValid == true && maximumPixels >= 1024, "Bundled \(option.label) icon has a high-resolution PNG")
            AppIconTheme.apply(option, defaults: iconDefaults)
            expect(AppIconTheme.parse(iconDefaults.string(forKey: "iconTheme")) == option, "Icon choice survives local setting serialization")
        }
        AppIconTheme.apply(.sky, defaults: iconDefaults)

        let preferences = PreferencesWindowController(state: ReaderPreferencesState.defaults(dark: false))
        preferences.present(relativeTo: nil)
        defer { preferences.window?.close() }
        guard let preferencesWindow = preferences.window, let preferencesContent = preferencesWindow.contentView else {
            fatalError("Preferences window has no content")
        }
        preferencesWindow.contentView?.layoutSubtreeIfNeeded()
        let preferenceViews = descendants(preferencesContent)
        let cards = preferenceViews.compactMap { $0 as? ThemeCardButton }
        expect(cards.count == ReaderThemeID.allCases.count, "Settings exposes every theme card")
        let previewScrolls = preferenceViews.compactMap { $0 as? NSScrollView }
            .filter { $0.documentView is NSTextView }
        expect(previewScrolls.count == 2 && previewScrolls.allSatisfy { $0.hasVerticalScroller },
               "Reading and editing previews scroll independently")
        let accessibleControls = Set(preferenceViews.compactMap { $0.accessibilityLabel() })
        for label in ["外观主题", "应用图标", "字号", "阅读字体", "阅读行距", "段间距", "阅读版心"] {
            expect(accessibleControls.contains(label), "Settings control remains accessible: \(label)")
        }
        var changedState: ReaderPreferencesState?
        preferences.onChange = { changedState = $0 }
        guard let themePopup = preferenceViews.compactMap({ $0 as? NSPopUpButton }).first(where: { $0.accessibilityLabel() == "外观主题" }),
              let action = themePopup.action, let target = themePopup.target,
              let paperIndex = ReaderThemeID.allCases.firstIndex(of: .paper) else {
            fatalError("Settings theme popup is missing")
        }
        themePopup.selectItem(at: paperIndex)
        _ = NSApp.sendAction(action, to: target, from: themePopup)
        expect(changedState?.themeID == .paper, "Settings theme changes propagate immediately")

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
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 720, pixelsHigh: 180, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .calibratedRGB, bytesPerRow: 0, bitsPerPixel: 0)!
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
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(view.bounds.width), pixelsHigh: Int(view.bounds.height), bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .calibratedRGB, bytesPerRow: 0, bitsPerPixel: 0),
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
    private static func descendants(_ view: NSView) -> [NSView] {
        view.subviews.flatMap { [$0] + descendants($0) }
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
