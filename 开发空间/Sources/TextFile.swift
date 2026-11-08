import Foundation
import CoreFoundation
import CryptoKit
import Darwin

enum TextFileError: LocalizedError {
    case externalChange
    case mixedLineEndings
    case decoding
    case encoding
    case tooLarge
    case missingDestination
    case readOnly
    case binaryFile
    case recoveryInvalid
    case io(String)

    var errorDescription: String? {
        switch self {
        case .externalChange: return "磁盘上的文件已被修改、移动或替换。请重新载入或另存为，确认后才覆盖。"
        case .mixedLineEndings: return "文件含有混合换行。请先选择统一为 LF 或 CRLF，再保存修改。"
        case .decoding: return "无法以所选编码完整读取文件。请尝试其他编码；原文件没有被修改。"
        case .encoding: return "当前编码无法完整表示这些文字。请切换为 UTF-8 后再保存。"
        case .tooLarge: return "此预览版最多编辑 50 MB 文本。请选择较小的文件。"
        case .missingDestination: return "请先选择保存位置。"
        case .readOnly: return "文件没有写入权限。请使用“另存为”保存到其他位置。"
        case .binaryFile: return "此文件包含二进制内容，或使用尚不支持的编码。请选择文本文件，或手动指定编码重新打开。"
        case .recoveryInvalid: return "恢复草稿的格式无效，未写入任何原文件。"
        case .io(let message): return message
        }
    }
}

enum TextCodec: String, CaseIterable, Codable {
    case utf8, utf8BOM, utf16LE, utf16BE, gb18030

    var label: String {
        switch self {
        case .utf8: return "UTF-8"
        case .utf8BOM: return "UTF-8 BOM"
        case .utf16LE: return "UTF-16 LE"
        case .utf16BE: return "UTF-16 BE"
        case .gb18030: return "GB18030"
        }
    }

    private var encoding: String.Encoding {
        switch self {
        case .utf8, .utf8BOM: return .utf8
        case .utf16LE: return .utf16LittleEndian
        case .utf16BE: return .utf16BigEndian
        case .gb18030:
            return String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(
                CFStringEncoding(CFStringEncodings.GB_18030_2000.rawValue)))
        }
    }

    fileprivate var bom: Data {
        switch self {
        case .utf8BOM: return Data([0xEF, 0xBB, 0xBF])
        case .utf16LE: return Data([0xFF, 0xFE])
        case .utf16BE: return Data([0xFE, 0xFF])
        default: return Data()
        }
    }

    func decode(_ data: Data) throws -> String {
        var body = data
        if !bom.isEmpty && body.starts(with: bom) { body.removeFirst(bom.count) }
        // A UTF-8 BOM is a marker even when explicitly reopening as plain UTF-8.
        if self == .utf8 && body.starts(with: [0xEF, 0xBB, 0xBF]) { body.removeFirst(3) }
        if self == .utf16LE || self == .utf16BE {
            guard body.count % 2 == 0 else { throw TextFileError.decoding }
            let bytes = Array(body)
            var index = 0
            func unit(_ offset: Int) -> UInt16 {
                self == .utf16LE
                    ? UInt16(bytes[offset]) | UInt16(bytes[offset + 1]) << 8
                    : UInt16(bytes[offset]) << 8 | UInt16(bytes[offset + 1])
            }
            while index < bytes.count {
                let value = unit(index)
                if (0xD800...0xDBFF).contains(value) {
                    guard index + 3 < bytes.count, (0xDC00...0xDFFF).contains(unit(index + 2)) else {
                        throw TextFileError.decoding
                    }
                    index += 4
                } else {
                    guard !(0xDC00...0xDFFF).contains(value) else { throw TextFileError.decoding }
                    index += 2
                }
            }
        }
        guard let result = String(data: body, encoding: encoding),
              let roundtrip = result.data(using: encoding, allowLossyConversion: false),
              roundtrip == body else { throw TextFileError.decoding }
        return result
    }

    func encode(_ text: String, includeBOM: Bool = true) throws -> Data {
        guard let body = text.data(using: encoding, allowLossyConversion: false),
              let decoded = String(data: body, encoding: encoding),
              decoded.utf8.elementsEqual(text.utf8) else { throw TextFileError.encoding }
        if self == .utf8BOM || (includeBOM && (self == .utf16LE || self == .utf16BE)) {
            return bom + body
        }
        return body
    }

    fileprivate static func detect(_ data: Data) throws -> TextCodec {
        // Do not mistake a UTF-32 marker for a UTF-16 marker.
        if data.starts(with: [0xFF, 0xFE, 0x00, 0x00]) || data.starts(with: [0x00, 0x00, 0xFE, 0xFF]) {
            throw TextFileError.binaryFile
        }
        if data.starts(with: TextCodec.utf8BOM.bom) { return .utf8BOM }
        if data.starts(with: TextCodec.utf16LE.bom) { return .utf16LE }
        if data.starts(with: TextCodec.utf16BE.bom) { return .utf16BE }
        if data.contains(0) { throw TextFileError.binaryFile }
        if (try? TextCodec.utf8.decode(data)) != nil { return .utf8 }
        if (try? TextCodec.gb18030.decode(data)) != nil { return .gb18030 }
        throw TextFileError.decoding
    }
}

enum LineEnding: String, CaseIterable, Codable {
    case lf, crlf, cr, mixed

    var label: String {
        switch self {
        case .lf: return "LF"
        case .crlf: return "CRLF"
        case .cr: return "CR"
        case .mixed: return "混合换行"
        }
    }

    fileprivate static func detect(_ text: String) -> LineEnding {
        var crlf = false, lf = false, cr = false
        let bytes = Array(text.utf8)
        var index = 0
        while index < bytes.count {
            if bytes[index] == 13 {
                if index + 1 < bytes.count && bytes[index + 1] == 10 { crlf = true; index += 1 }
                else { cr = true }
            } else if bytes[index] == 10 { lf = true }
            index += 1
        }
        let count = [crlf, lf, cr].filter { $0 }.count
        if count > 1 { return .mixed }
        if crlf { return .crlf }
        if cr { return .cr }
        return .lf
    }

    fileprivate func apply(to text: String) -> String {
        let normalized = Self.normalize(text)
        switch self {
        case .crlf: return normalized.replacingOccurrences(of: "\n", with: "\r\n")
        case .cr: return normalized.replacingOccurrences(of: "\n", with: "\r")
        case .lf, .mixed: return normalized
        }
    }

    fileprivate static func normalize(_ text: String) -> String {
        text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
    }
}

final class TextFile {
    static let maximumSize = 50 * 1024 * 1024

    let id: UUID
    var url: URL?
    var text: String
    var codec: TextCodec = .utf8
    var lineEnding: LineEnding = .lf
    private(set) var originalData: Data?
    private var baselineText: String
    private var baselineCodec: TextCodec = .utf8
    private var baselineLineEnding: LineEnding = .lf
    private var baselineDigest: String?
    private var loadedResolvedURL: URL?
    private var hasBOM = false
    private var wasSaved = false

    // Swift String equality treats canonically equivalent Unicode sequences as equal.
    // A text editor must retain the user's exact sequence, including normalization edits.
    private var textChanged: Bool { !text.utf8.elementsEqual(baselineText.utf8) }

    var isDirty: Bool {
        !wasSaved || textChanged || codec != baselineCodec || lineEnding != baselineLineEnding
    }

    var title: String { url?.lastPathComponent ?? "未命名" }

    init(text: String = "", url: URL? = nil) {
        id = UUID()
        self.text = LineEnding.normalize(text)
        baselineText = self.text
        self.url = url
    }

    private init(id: UUID, text: String, url: URL?) {
        self.id = id
        self.text = text
        baselineText = text
        self.url = url
    }

    static func open(_ url: URL, codec preferredCodec: TextCodec? = nil) throws -> TextFile {
        let resolved = resolvedFileURL(url)
        let attributes = try FileManager.default.attributesOfItem(atPath: resolved.path)
        guard attributes[.type] as? FileAttributeType == .typeRegular else {
            throw TextFileError.io("请选择普通文本文件；文件夹和设备文件无法编辑。")
        }
        guard (attributes[.size] as? NSNumber)?.intValue ?? 0 <= maximumSize else { throw TextFileError.tooLarge }
        let data = try Data(contentsOf: resolved, options: .mappedIfSafe)
        guard data.count <= maximumSize else { throw TextFileError.tooLarge }
        let selectedCodec = try preferredCodec ?? TextCodec.detect(data)
        let rawText = try selectedCodec.decode(data)
        guard !rawText.contains("\0") else { throw TextFileError.binaryFile }
        let file = TextFile(text: rawText, url: url.standardizedFileURL)
        file.codec = selectedCodec
        file.lineEnding = LineEnding.detect(rawText)
        file.originalData = data.count <= 4 * 1024 * 1024 ? data : nil
        file.baselineText = file.text
        file.baselineCodec = selectedCodec
        file.baselineLineEnding = file.lineEnding
        file.baselineDigest = digest(data)
        file.loadedResolvedURL = resolved
        file.hasBOM = !selectedCodec.bom.isEmpty && data.starts(with: selectedCodec.bom)
        file.wasSaved = true
        return file
    }

    func diskChanged() throws -> Bool {
        guard let url = url, wasSaved else { return false }
        let resolved = Self.resolvedFileURL(url)
        guard resolved == loadedResolvedURL else { return true }
        guard FileManager.default.fileExists(atPath: resolved.path) else { return true }
        let attributes = try FileManager.default.attributesOfItem(atPath: resolved.path)
        guard attributes[.type] as? FileAttributeType == .typeRegular,
              (attributes[.size] as? NSNumber)?.intValue ?? 0 <= Self.maximumSize else { return true }
        let current = try Data(contentsOf: resolved, options: .mappedIfSafe)
        guard current.count <= Self.maximumSize else { return true }
        return Self.digest(current) != baselineDigest
    }

    func save(to destination: URL? = nil, allowOverwrite: Bool = false) throws {
        guard let target = destination ?? url else { throw TextFileError.missingDestination }
        let requestedURL = target.standardizedFileURL
        let resolvedURL = Self.resolvedFileURL(requestedURL)
        // A URL assigned to a new document is a proposed location, not evidence
        // that this document has ever read or owned the file already there.
        let sameDestination = wasSaved && (url?.standardizedFileURL == requestedURL
            || (loadedResolvedURL != nil && loadedResolvedURL == resolvedURL))
        if sameDestination, wasSaved, !allowOverwrite, try diskChanged() { throw TextFileError.externalChange }
        // Explicit overwrite also applies to a clean snapshot whose disk file
        // changed or disappeared. Do not report a successful save without writing.
        if sameDestination, !isDirty, !allowOverwrite { return }
        let data: Data
        if lineEnding == .mixed {
            // A real write must either copy the untouched original bytes or use
            // a newline format explicitly selected by the user. Recovery stores
            // normalized text, and large files may have no raw snapshot, so a
            // clean baseline alone cannot reconstruct mixed newline sequences.
            guard !textChanged, codec == baselineCodec, let original = originalData else {
                throw TextFileError.mixedLineEndings
            }
            data = original
        } else {
            let useBOM = codec == baselineCodec ? hasBOM : true
            data = try codec.encode(lineEnding.apply(to: text), includeBOM: useBOM)
        }
        guard data.count <= Self.maximumSize else { throw TextFileError.tooLarge }

        let fm = FileManager.default
        let attributes = fm.fileExists(atPath: resolvedURL.path)
            ? try fm.attributesOfItem(atPath: resolvedURL.path) : nil
        if !sameDestination, attributes != nil, !allowOverwrite { throw TextFileError.externalChange }
        if let attributes = attributes {
            guard attributes[.type] as? FileAttributeType == .typeRegular else {
                throw TextFileError.io("保存位置不是普通文件，请另选位置。")
            }
            let permissions = (attributes[.posixPermissions] as? NSNumber)?.uint16Value ?? 0o644
            guard permissions & 0o222 != 0, fm.isWritableFile(atPath: resolvedURL.path) else { throw TextFileError.readOnly }
            // An atomic replacement would silently detach just this directory
            // entry from its other hard links. An in-place write would lose the
            // crash safety of our save path, so require a new destination instead.
            guard (attributes[.referenceCount] as? NSNumber)?.intValue ?? 1 <= 1 else {
                throw TextFileError.io("此文件具有多个硬链接。为保留链接关系，请使用“另存为”保存到新文件。")
            }
        }
        let temporary = resolvedURL.deletingLastPathComponent().appendingPathComponent(".lightreader-\(UUID().uuidString).tmp")
        defer { try? fm.removeItem(at: temporary) }
        // Private from the instant it is created: configurations can contain credentials.
        try Self.writePrivate(data, to: temporary)
        if let attributes = attributes {
            let uid = (attributes[.ownerAccountID] as? NSNumber)?.uint32Value ?? getuid()
            let gid = (attributes[.groupOwnerAccountID] as? NSNumber)?.uint32Value ?? getgid()
            guard chown(temporary.path, uid, gid) == 0 else { throw Self.posixError("无法保留原文件所有者或用户组，请另存为") }
            let permissions = (attributes[.posixPermissions] as? NSNumber)?.uint16Value ?? 0o644
            guard chmod(temporary.path, mode_t(permissions)) == 0 else { throw Self.posixError("无法保留原文件权限") }
            guard copyfile(resolvedURL.path, temporary.path, nil, copyfile_flags_t(COPYFILE_XATTR | COPYFILE_ACL)) == 0 else {
                throw Self.posixError("无法保留原文件访问权限或扩展属性，请另存为")
            }
        }
        let descriptor = Darwin.open(temporary.path, O_RDONLY)
        guard descriptor >= 0 else { throw Self.posixError("无法校验保存文件") }
        let syncResult = fsync(descriptor)
        let syncErrno = errno
        close(descriptor)
        if syncResult != 0 { throw Self.posixError("无法完整写入文件", code: syncErrno) }
        // Recheck immediately before replacing the destination. No file is overwritten on a conflict.
        if sameDestination, wasSaved, !allowOverwrite, try diskChanged() { throw TextFileError.externalChange }
        // A new Save As target can appear while the temporary file is written.
        // RENAME_EXCL closes that race instead of silently replacing its creator's data.
        let result = !sameDestination && !allowOverwrite
            ? renamex_np(temporary.path, resolvedURL.path, UInt32(RENAME_EXCL))
            : rename(temporary.path, resolvedURL.path)
        if result != 0 {
            if errno == EEXIST { throw TextFileError.externalChange }
            throw Self.posixError("保存失败，编辑内容仍然保留")
        }
        self.url = requestedURL
        loadedResolvedURL = Self.resolvedFileURL(requestedURL)
        baselineText = text
        baselineCodec = codec
        baselineLineEnding = lineEnding
        baselineDigest = Self.digest(data)
        originalData = data.count <= 4 * 1024 * 1024 ? data : nil
        hasBOM = !codec.bom.isEmpty && data.starts(with: codec.bom)
        wasSaved = true
    }

    private static func digest(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    // Foundation's resolvingSymlinksInPath may shorten /private/tmp to /tmp only
    // after the leaf exists. POSIX realpath gives a stable physical path before
    // and after a first save; for a new file, resolve its already-existing parent.
    private static func resolvedFileURL(_ url: URL) -> URL {
        if let resolved = realpath(url.path, nil) {
            defer { free(resolved) }
            return URL(fileURLWithPath: String(cString: resolved), isDirectory: false)
        }
        let parent = url.deletingLastPathComponent()
        if let resolvedParent = realpath(parent.path, nil) {
            defer { free(resolvedParent) }
            return URL(fileURLWithPath: String(cString: resolvedParent), isDirectory: true)
                .appendingPathComponent(url.lastPathComponent, isDirectory: false)
        }
        return url.standardizedFileURL
    }

    private static func posixError(_ prefix: String, code: Int32 = errno) -> TextFileError {
        .io("\(prefix)：\(String(cString: strerror(code)))")
    }

    private static func writePrivate(_ data: Data, to url: URL) throws {
        let fd = Darwin.open(url.path, O_WRONLY | O_CREAT | O_EXCL, 0o600)
        guard fd >= 0 else { throw posixError("无法创建临时文件") }
        var writeError: Int32?
        data.withUnsafeBytes { buffer in
            var written = 0
            while written < data.count {
                let result = Darwin.write(fd, buffer.baseAddress!.advanced(by: written), data.count - written)
                if result < 0 {
                    if errno == EINTR { continue }
                    writeError = errno
                    break
                }
                if result == 0 { writeError = EIO; break }
                written += result
            }
        }
        let synced = fsync(fd)
        let syncErrno = errno
        close(fd)
        if let code = writeError { throw posixError("文件写入失败", code: code) }
        if synced != 0 { throw posixError("文件写入失败", code: syncErrno) }
    }

    private struct RecoveryEnvelope: Codable {
        let version: Int
        let documents: [RecoveryRecord]
    }

    private struct RecoveryRecord: Codable {
        let id: UUID
        let path: String?
        let resolvedPath: String?
        let text: String
        let codec: TextCodec
        let lineEnding: LineEnding
        let baselineText: String
        let baselineCodec: TextCodec
        let baselineLineEnding: LineEnding
        let baselineDigest: String?
        let hasBOM: Bool
        let wasSaved: Bool

        init(_ file: TextFile) {
            id = file.id
            path = file.url?.path
            resolvedPath = file.loadedResolvedURL?.path
            text = file.text
            codec = file.codec
            lineEnding = file.lineEnding
            baselineText = file.baselineText
            baselineCodec = file.baselineCodec
            baselineLineEnding = file.baselineLineEnding
            baselineDigest = file.baselineDigest
            hasBOM = file.hasBOM
            wasSaved = file.wasSaved
        }

        func restore() -> TextFile {
            let file = TextFile(id: id, text: text, url: path.map { URL(fileURLWithPath: $0) })
            // This is the physical location recorded when the snapshot was read.
            // Resolving it again could bless a newly introduced symlink and hide
            // a retargeting conflict after recovery.
            file.loadedResolvedURL = resolvedPath.map { URL(fileURLWithPath: $0, isDirectory: false) }
            file.codec = codec
            file.lineEnding = lineEnding
            file.baselineText = baselineText
            file.baselineCodec = baselineCodec
            file.baselineLineEnding = baselineLineEnding
            file.baselineDigest = baselineDigest
            file.hasBOM = hasBOM
            file.wasSaved = wasSaved
            return file
        }
    }

    static func writeRecovery(_ docs: [TextFile], to url: URL) throws {
        // Keep the writer's limits consistent with its reader. Otherwise one
        // oversize/new tab could replace a valid recovery file with unreadable state.
        guard docs.count <= 200,
              docs.allSatisfy({ $0.text.utf8.count <= maximumSize && $0.baselineText.utf8.count <= maximumSize }) else {
            throw TextFileError.io("恢复草稿超出容量限制，请先保存部分文档。原有草稿仍然保留。")
        }
        let envelope = RecoveryEnvelope(version: 1, documents: docs.map(RecoveryRecord.init))
        let data = try JSONEncoder().encode(envelope)
        guard data.count <= 150 * 1024 * 1024 else { throw TextFileError.io("草稿总量过大，请先保存部分文档。") }
        let fm = FileManager.default
        try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true,
                               attributes: [.posixPermissions: 0o700])
        // Keep the directory and recovery file private to this account.
        guard chmod(url.deletingLastPathComponent().path, 0o700) == 0 else { throw posixError("无法保护草稿目录") }
        let temporary = url.deletingLastPathComponent().appendingPathComponent(".recovery-\(UUID().uuidString).tmp")
        defer { try? fm.removeItem(at: temporary) }
        try writePrivate(data, to: temporary)
        guard rename(temporary.path, url.path) == 0 else { throw posixError("恢复草稿更新失败") }
    }

    static func recovery(from url: URL) throws -> [TextFile] {
        guard FileManager.default.fileExists(atPath: url.path) else { return [] }
        let size = try FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber
        guard (size?.intValue ?? 0) <= 150 * 1024 * 1024 else { throw TextFileError.recoveryInvalid }
        let envelope: RecoveryEnvelope
        do { envelope = try JSONDecoder().decode(RecoveryEnvelope.self, from: Data(contentsOf: url)) }
        catch { throw TextFileError.recoveryInvalid }
        guard envelope.version == 1, envelope.documents.count <= 200,
              envelope.documents.allSatisfy({ $0.text.utf8.count <= maximumSize && $0.baselineText.utf8.count <= maximumSize }) else {
            throw TextFileError.recoveryInvalid
        }
        return envelope.documents.map { $0.restore() }
    }
}
