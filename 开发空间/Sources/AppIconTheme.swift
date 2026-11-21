import AppKit

enum AppIconTheme: String, CaseIterable {
    case sky = "Sky", sakura = "Sakura", mint = "Mint", peach = "Peach"

    var label: String {
        switch self {
        case .sky: return "天空蓝"
        case .sakura: return "樱花紫"
        case .mint: return "薄荷珊瑚"
        case .peach: return "蜜桃紫"
        }
    }
    var fileName: String { "Qingyue-\(rawValue.lowercased()).png" }
    static func parse(_ value: String?) -> AppIconTheme {
        let normalized = value?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return allCases.first { $0.rawValue.caseInsensitiveCompare(normalized) == .orderedSame || $0.label == normalized } ?? .sky
    }
    static func load(_ theme: AppIconTheme, bundle: Bundle = .main) -> NSImage? {
        let resource = bundle.resourceURL?.appendingPathComponent("IconThemes").appendingPathComponent(theme.fileName)
        if let resource, let image = NSImage(contentsOf: resource) { return image }
        // Native command-line tests run outside the application bundle.
        let source = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Resources/IconThemes").appendingPathComponent(theme.fileName)
        return NSImage(contentsOf: source)
    }
    static func apply(_ theme: AppIconTheme, defaults: UserDefaults = .standard) {
        defaults.set(theme.rawValue, forKey: "iconTheme")
        if let image = load(theme) { NSApplication.shared.applicationIconImage = image }
    }
}
