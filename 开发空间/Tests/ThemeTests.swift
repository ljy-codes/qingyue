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
                try expect(body >= 4.5, "\(id.rawValue) \(mode) body contrast \(body)")
                try expect(muted >= 4.5, "\(id.rawValue) \(mode) muted contrast \(muted)")
                try expect(accent >= 2.5, "\(id.rawValue) \(mode) accent contrast \(accent)")
                try expect(accentText >= 4.5, "\(id.rawValue) \(mode) accentText contrast \(accentText)")
                try expect(accentForeground >= 4.5, "\(id.rawValue) \(mode) accentForeground contrast \(accentForeground)")
                try expect(theme.window != theme.panel && theme.window != theme.editor, "\(id.rawValue) \(mode) surface hierarchy")
            }
        }
        try expect(ReaderTheme(dark: false, warmPaper: true).id == .coffee, "warm paper migrates to coffee")
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
