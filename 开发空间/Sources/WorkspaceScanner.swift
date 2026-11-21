import Foundation

struct LocalWorkspaceFile: Equatable, Codable {
    let path: String
    let relativePath: String
    let fileExtension: String
    let byteCount: Int64
    let modifiedAt: Date?
}

struct WorkspaceScanOptions {
    var includeHidden = false
    var allowedExtensions: Set<String> = []
    var ignoredDirectories: Set<String> = [".git", ".vs", "bin", "obj", "node_modules", ".build"]
    var maximumFiles = 10_000
    var maximumBytes = Int64(512 * 1024 * 1024)
}

enum WorkspaceScannerError: LocalizedError {
    case invalidRoot
    case tooManyFiles
    case tooLarge
    var errorDescription: String? {
        switch self {
        case .invalidRoot: return "工作区目录不可用。"
        case .tooManyFiles: return "工作区文件数量超过上限。"
        case .tooLarge: return "工作区扫描容量超过上限。"
        }
    }
}

enum WorkspaceScanner {
    static let defaultExtensions: Set<String> = ["txt", "md", "markdown", "json", "jsonc", "yaml", "yml", "ini", "conf", "config", "cfg", "toml", "log", "xml", "env", "js", "ts", "py", "java", "cs", "sql", "html", "css", "go", "rs", "sh", "ps1", "csv"]

    static func scan(root: URL, options: WorkspaceScanOptions = WorkspaceScanOptions()) throws -> [LocalWorkspaceFile] {
        guard root.isFileURL else { throw WorkspaceScannerError.invalidRoot }
        let root = root.standardizedFileURL
        let rootValues = try? root.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        guard rootValues?.isDirectory == true, rootValues?.isSymbolicLink != true else { throw WorkspaceScannerError.invalidRoot }
        guard options.maximumFiles >= 0, options.maximumBytes >= 0 else { throw WorkspaceScannerError.invalidRoot }
        let resolvedRoot = root.resolvingSymlinksInPath().standardizedFileURL
        let rootPrefix = resolvedRoot.path.hasSuffix("/") ? resolvedRoot.path : resolvedRoot.path + "/"
        var result: [LocalWorkspaceFile] = []; var totalBytes: Int64 = 0
        let keys: [URLResourceKey] = [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey, .isHiddenKey, .fileSizeKey, .contentModificationDateKey]
        guard let enumerator = FileManager.default.enumerator(at: resolvedRoot, includingPropertiesForKeys: keys, options: [.skipsPackageDescendants], errorHandler: { _, _ in true }) else { return [] }
        let allowed = options.allowedExtensions.isEmpty ? defaultExtensions : Set(options.allowedExtensions.map { $0.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: ".")) })
        while let url = enumerator.nextObject() as? URL {
            guard let values = try? url.resourceValues(forKeys: Set(keys)) else { continue }
            let name = url.lastPathComponent
            if values.isSymbolicLink == true { enumerator.skipDescendants(); continue }
            if values.isDirectory == true {
                if options.ignoredDirectories.contains(name) || (!options.includeHidden && (values.isHidden == true || name.hasPrefix("."))) { enumerator.skipDescendants() }
                continue
            }
            if values.isRegularFile != true || (!options.includeHidden && (values.isHidden == true || name.hasPrefix("."))) { continue }
            let ext = url.pathExtension.lowercased(); guard allowed.contains(ext) else { continue }
            let size = Int64(values.fileSize ?? 0); guard size >= 0 else { continue }
            guard result.count < options.maximumFiles else { throw WorkspaceScannerError.tooManyFiles }
            guard totalBytes <= options.maximumBytes - size else { throw WorkspaceScannerError.tooLarge }
            totalBytes += size
            let filePath = url.resolvingSymlinksInPath().standardizedFileURL.path
            guard filePath.hasPrefix(rootPrefix) else { continue }
            let relative = String(filePath.dropFirst(rootPrefix.count))
            result.append(LocalWorkspaceFile(path: filePath, relativePath: relative, fileExtension: ext, byteCount: size, modifiedAt: values.contentModificationDate))
        }
        return result.sorted { $0.relativePath.localizedStandardCompare($1.relativePath) == .orderedAscending }
    }
}
