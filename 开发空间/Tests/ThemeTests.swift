import AppKit
import Foundation

@main
struct ThemeTests {
    private static var checks = 0

    static func main() throws {
        _ = NSApplication.shared
        NSApp.setActivationPolicy(.prohibited)
        try expect(ReaderThemeID.allCases.count == 7, "seven color themes")
        for id in ReaderThemeID.allCases {
            for dark in [false, true] {
                let theme = ReaderTheme(id: id, dark: dark)
                let mode = dark ? "dark" : "light"
                let body = theme.text.readerContrastRatio(with: theme.editor)
                let muted = theme.muted.readerContrastRatio(with: theme.panel)
                let accent = theme.accent.readerContrastRatio(with: theme.editor)
                let accentText = theme.accentText.readerContrastRatio(with: theme.editor)
                let accentForeground = theme.accentForeground.readerContrastRatio(with: theme.accent)
                let mainText = theme.text.readerContrastRatio(with: theme.main)
                let hoverText = theme.text.readerContrastRatio(with: theme.hover)
                try expect(body >= 4.5, "\(id.rawValue) \(mode) body contrast \(body)")
                try expect(muted >= 4.5, "\(id.rawValue) \(mode) muted contrast \(muted)")
                try expect(accent >= 3.0, "\(id.rawValue) \(mode) accent contrast \(accent)")
                try expect(accentText >= 4.5, "\(id.rawValue) \(mode) accentText contrast \(accentText)")
                try expect(accentForeground >= 4.5, "\(id.rawValue) \(mode) accentForeground contrast \(accentForeground)")
                try expect(mainText >= 4.5, "\(id.rawValue) \(mode) main/text contrast \(mainText)")
                try expect(hoverText >= 4.5, "\(id.rawValue) \(mode) hover/text contrast \(hoverText)")
                try expect(theme.window != theme.panel && theme.window != theme.editor, "\(id.rawValue) \(mode) surface hierarchy")
            }
        }
        try expect(ReaderTheme(dark: false, warmPaper: true).id == .coffee, "warm paper migrates to coffee")
        let legacyWarm = ReaderTheme(dark: true, warmPaper: true)
        try expect(legacyWarm.id == .coffee && legacyWarm.dark == false, "legacy warm paper stays light regardless of stored dark flag")
        try expect(legacyWarm.editor.readerContrastRatio(with: legacyWarm.text) >= 4.5, "legacy warm paper keeps readable text")
        try expect(ReaderTheme(dark: true).id == .classic, "legacy dark stays classic")
        try expect(ReaderThemeID.stored("coffee") == .coffee, "theme identifier restores")
        try expect(ReaderThemeID.stored(nil, legacy: "WarmPaper") == .coffee, "legacy warm paper setting migrates")
        try expect(ReaderThemeID.stored(nil, legacy: "Dark") == .classic, "legacy dark setting migrates")
        try expect(ReaderThemeID.stored("unknown", legacy: nil) == .classic, "unknown setting falls back")
        for id in ReaderThemeID.allCases {
            try expect(ReaderThemeID.stored(id.rawValue) == id, "\(id.rawValue) roundtrips")
            try expect(id.swatches.count == 5 && !id.label.isEmpty && !id.detail.isEmpty, "\(id.rawValue) has a usable card")
        }
        print("ThemeTests: \(checks) checks passed")
    }

    private static func expect(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
        guard try condition() else { throw NSError(domain: "ThemeTests", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
        checks += 1
    }
}
