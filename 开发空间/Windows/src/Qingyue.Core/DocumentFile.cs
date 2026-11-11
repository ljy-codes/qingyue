using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Qingyue.Core;

public sealed class DocumentFile
{
    public const int MaximumSize = 50 * 1024 * 1024;
    private string _text = "";
    private string _baselineText = "";
    private TextCodec _baselineCodec = TextCodec.Utf8;
    private LineEnding _baselineLineEnding = LineEnding.Lf;
    private string? _baselineDigest;
    private bool _hadBom;
    private bool _wasSaved;
    private TextCodec _codec = TextCodec.Utf8;
    private LineEnding _lineEnding = LineEnding.Lf;
    // Process-local only: snapshots keep their existing format. This also detects
    // edit/undo and identical saves while an external read is in flight.
    internal long ChangeVersion { get; private set; }
    // Mixed endings cannot be reconstructed from normalized snapshot text.
    private byte[]? _originalMixedBytes;

    static DocumentFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public DocumentFile(string text = "") { Text = text; }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string? Path { get; private set; }
    public string? SuggestedName { get; private set; }
    public string Title => Path is null ? SuggestedName ?? "未命名" : System.IO.Path.GetFileName(Path);
    public string Text
    {
        get => _text;
        set
        {
            var text = Normalize(value ?? throw new ArgumentNullException(nameof(value)));
            if (string.Equals(_text, text, StringComparison.Ordinal)) return;
            _text = text;
            ChangeVersion++;
        }
    }
    public TextCodec Codec
    {
        get => _codec;
        set { if (_codec != value) { _codec = value; ChangeVersion++; } }
    }
    public LineEnding LineEnding
    {
        get => _lineEnding;
        set { if (_lineEnding != value) { _lineEnding = value; ChangeVersion++; } }
    }
    public bool IsDirty => !string.Equals(Text, _baselineText, StringComparison.Ordinal)
        || Codec != _baselineCodec || LineEnding != _baselineLineEnding;

    public static DocumentFile Open(string path, TextCodec? codec = null)
    {
        try
        {
            path = FullPath(path);
            var bytes = ReadLimited(path);
            var (text, detected, hadBom) = Decode(bytes, codec);
            var ending = DetectLineEnding(text);
            var file = new DocumentFile(text) { Path = path, Codec = detected, LineEnding = ending };
            file.SetBaseline(bytes, hadBom);
            return file;
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("无法读取文件：访问被拒绝。", ex); }
        catch (IOException ex) when (ex is not FileConflictException)
        { throw new IOException("无法打开文件：" + ex.Message, ex); }
    }

    internal static DocumentFile OpenForSession(string path, Guid id, TextCodec? codec)
    {
        var file = Open(path, codec);
        file.Id = id;
        return file;
    }

    public bool DiskChanged()
    {
        if (!_wasSaved || Path is null || _baselineDigest is null) return false;
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), _baselineDigest, StringComparison.OrdinalIgnoreCase);
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (UnauthorizedAccessException ex) { throw new IOException("无法检查磁盘文件：访问被拒绝。", ex); }
        catch (IOException ex) { throw new IOException("无法检查磁盘文件：" + ex.Message, ex); }
    }

    public void Save(string? destination = null, bool allowOverwrite = false)
    {
        string? temporary = null;
        SafeFileHandle? guard = null;
        try
        {
            var target = FullPath(destination ?? Path ?? throw new IOException("请先选择另存为路径。"));
            bool same = _wasSaved && Path is not null && SamePath(target, Path);
            var bytes = EncodeForSave(out bool bom);
            if (same && !allowOverwrite && DiskChanged()) throw new FileConflictException();
            RejectReparseComponents(target);
            bool exists = File.Exists(target);
            if (exists && !same && !allowOverwrite) throw new FileConflictException("目标文件已存在，请选择其他名称或明确覆盖。");
            if (Directory.Exists(target)) throw new IOException("目标是目录，请选择文件路径。");
            string? targetSecurity = exists && OperatingSystem.IsWindows()
                ? new FileInfo(target).GetAccessControl(AccessControlSections.Access)
                    .GetSecurityDescriptorSddlForm(AccessControlSections.Access)
                : null;
            // Keep this handle alive across the final digest check and replacement. Deny
            // concurrent writers while checking the target. The replacement fallback
            // below closes it immediately before MoveFileEx because this Windows
            // runtime rejects File.Replace with ERROR_ACCESS_DENIED.
            guard = exists ? OpenSafeTarget(target) : null;
            if (same && !allowOverwrite && DiskChanged()) throw new FileConflictException();
            temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, ".qingyue-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (targetSecurity is not null)
                ApplyAccessSecurity(temporary, targetSecurity);
            RejectReparseComponents(target);
            if (same && !allowOverwrite && DiskChanged()) throw new FileConflictException();
            if (exists)
            {
                guard?.Dispose();
                guard = null;
                using (OpenSafeTarget(target)) { }
                File.Move(temporary, target, overwrite: true);
                if (targetSecurity is not null)
                    ApplyAccessSecurity(target, targetSecurity);
            }
            else File.Move(temporary, target, overwrite: false);
            temporary = null;
            Path = target;
            SuggestedName = null;
            SetBaseline(bytes, bom);
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("保存失败：文件只读或访问被拒绝，编辑内容已保留。", ex); }
        catch (IOException ex) when (ex is not FileConflictException && ex is not MixedLineEndingsException)
        { throw new IOException("保存失败，编辑内容已保留：" + ex.Message, ex); }
        finally
        {
            guard?.Dispose();
            TryDelete(temporary);
        }
    }

    public void Rename(string name)
    {
        ValidateName(name);
        if (Path is null) { SuggestedName = name; ChangeVersion++; return; }
        var source = Path;
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, name);
        if (string.Equals(source, target, StringComparison.Ordinal)) return;
        try
        {
            if (DiskChanged()) throw new FileConflictException();
            RejectReparseComponents(source);
            RejectReparseComponents(target);
            using var guard = OpenSafeTarget(source);
            if (DiskChanged()) throw new FileConflictException();
            if (!SamePath(source, target))
            {
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException("同目录已有同名文件，不能覆盖重命名。");
                File.Move(source, target, false);
            }
            else
            {
                // A two-step rename forces the stored casing to change on Windows.
                var intermediate = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, ".qingyue-rename-" + Guid.NewGuid().ToString("N"));
                File.Move(source, intermediate, false);
                try { File.Move(intermediate, target, false); }
                catch
                {
                    try { File.Move(intermediate, source, false); }
                    catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                    {
                        Path = intermediate; // Keep the actual recoverable location visible.
                        throw new IOException("大小写重命名失败，文件保留在：" + intermediate, rollback);
                    }
                    throw;
                }
            }
            Path = target;
            SuggestedName = null;
            ChangeVersion++;
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("重命名失败：访问被拒绝，编辑内容已保留。", ex); }
        catch (IOException ex) when (ex is not FileConflictException)
        { throw new IOException("重命名失败，编辑内容已保留：" + ex.Message, ex); }
    }

    public DocumentSnapshot CaptureSnapshot() => new(1, Id, Path, SuggestedName, Text,
        Codec, LineEnding, _baselineText, _baselineCodec, _baselineLineEnding, _baselineDigest, _hadBom, _wasSaved);

    // Must run on the document's owning thread. The replacement was safely opened on
    // a worker; transfer its raw mixed-ending bytes as well as its digest/BOM baseline.
    internal bool TryAcceptExternalVersion(DocumentSnapshot expected, long version, DocumentFile replacement)
    {
        if (ChangeVersion != version || CaptureSnapshot() != expected || IsDirty || !_wasSaved
            || Path is null || replacement.Path is null || !SamePath(Path, replacement.Path)
            || !replacement._wasSaved || replacement.IsDirty) return false;
        _text = replacement._text;
        _codec = replacement._codec;
        _lineEnding = replacement._lineEnding;
        _baselineText = replacement._baselineText;
        _baselineCodec = replacement._baselineCodec;
        _baselineLineEnding = replacement._baselineLineEnding;
        _baselineDigest = replacement._baselineDigest;
        _hadBom = replacement._hadBom;
        _wasSaved = replacement._wasSaved;
        _originalMixedBytes = replacement._originalMixedBytes;
        ChangeVersion++;
        return true;
    }

    public static DocumentFile Restore(DocumentSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Version != 1 || snapshot.Id == Guid.Empty
            || snapshot.Text is null || snapshot.BaselineText is null
            || !Enum.IsDefined(snapshot.Codec) || !Enum.IsDefined(snapshot.BaselineCodec)
            || !Enum.IsDefined(snapshot.LineEnding) || !Enum.IsDefined(snapshot.BaselineLineEnding)
            || snapshot.Text.Length > MaximumSize || snapshot.BaselineText.Length > MaximumSize
            || snapshot.Text.Contains('\r') || snapshot.BaselineText.Contains('\r')
            || (snapshot.BaselineDigest is not null && (snapshot.BaselineDigest.Length != 64 || !snapshot.BaselineDigest.All(Uri.IsHexDigit)))
            || (snapshot.WasSaved && (snapshot.Path is null || snapshot.BaselineDigest is null))
            || (!snapshot.WasSaved && (snapshot.Path is not null || snapshot.BaselineDigest is not null)))
            throw new InvalidDataException("文档快照版本、标识、编码或基线无效。");
        if (snapshot.SuggestedName is not null)
        {
            try { ValidateName(snapshot.SuggestedName); }
            catch (IOException ex) { throw new InvalidDataException("草稿建议文件名无效。", ex); }
        }
        if (snapshot.Path is not null && (!System.IO.Path.IsPathFullyQualified(snapshot.Path) || FullPath(snapshot.Path) != snapshot.Path))
            throw new InvalidDataException("文档快照路径必须是规范的绝对路径。");
        return new DocumentFile(snapshot.Text)
        {
            Id = snapshot.Id, Path = snapshot.Path, SuggestedName = snapshot.SuggestedName,
            Codec = snapshot.Codec, LineEnding = snapshot.LineEnding,
            _baselineText = snapshot.BaselineText, _baselineCodec = snapshot.BaselineCodec,
            _baselineLineEnding = snapshot.BaselineLineEnding, _baselineDigest = snapshot.BaselineDigest,
            _hadBom = snapshot.HadBom, _wasSaved = snapshot.WasSaved
        };
    }

    private void SetBaseline(byte[] bytes, bool bom)
    {
        _baselineText = Text;
        _baselineCodec = Codec;
        _baselineLineEnding = LineEnding;
        _baselineDigest = Convert.ToHexString(SHA256.HashData(bytes));
        _hadBom = bom;
        _wasSaved = true;
        _originalMixedBytes = LineEnding == LineEnding.Mixed ? bytes : null;
        ChangeVersion++;
    }

    private byte[] EncodeForSave(out bool bom)
    {
        if (!Enum.IsDefined(Codec) || !Enum.IsDefined(LineEnding)) throw new InvalidDataException("编码或换行选项无效。");
        bom = Codec == TextCodec.Utf8Bom || (Codec is TextCodec.Utf16LE or TextCodec.Utf16BE
            && (!_wasSaved || Codec != _baselineCodec || _hadBom));
        if (LineEnding == LineEnding.Mixed)
        {
            if (IsDirty || _originalMixedBytes is null) throw new MixedLineEndingsException();
            bom = _hadBom;
            return _originalMixedBytes;
        }
        var text = LineEnding switch
        {
            LineEnding.CrLf => Text.Replace("\n", "\r\n", StringComparison.Ordinal),
            LineEnding.Cr => Text.Replace('\n', '\r'),
            _ => Text
        };
        RejectBinary(text);
        var encoding = EncodingFor(Codec);
        try
        {
            int count = encoding.GetByteCount(text);
            var prefix = bom ? Preamble(Codec) : [];
            if ((long)count + prefix.Length > MaximumSize) throw new InvalidDataException("文件超过 50 MiB 上限，无法保存。");
            var result = new byte[count + prefix.Length];
            prefix.CopyTo(result, 0);
            encoding.GetBytes(text, result.AsSpan(prefix.Length));
            if (!string.Equals(encoding.GetString(result, prefix.Length, count), text, StringComparison.Ordinal))
                throw new InvalidDataException("目标编码不能无损表示正文，请选择 UTF-8。");
            return result;
        }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("目标编码不能无损表示正文，请选择 UTF-8。", ex); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("编码往返校验失败。", ex); }
    }

    private static (string Text, TextCodec Codec, bool Bom) Decode(byte[] bytes, TextCodec? requested)
    {
        if (requested is not null && !Enum.IsDefined(requested.Value)) throw new InvalidDataException("指定的文件编码无效。");
        var span = bytes.AsSpan();
        if (span.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || span.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            throw new InvalidDataException("暂不支持 UTF-32 编码，请转换为 UTF-8 或 UTF-16。");
        TextCodec? marked = span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? TextCodec.Utf8Bom
            : span.StartsWith(new byte[] { 0xff, 0xfe }) ? TextCodec.Utf16LE
            : span.StartsWith(new byte[] { 0xfe, 0xff }) ? TextCodec.Utf16BE : null;
        if (marked is not null && requested is not null && requested != marked
            && !(marked == TextCodec.Utf8Bom && requested is TextCodec.Utf8 or TextCodec.Utf8Bom))
            throw new InvalidDataException("指定编码与文件 BOM 不一致。");
        int skip = marked is null ? 0 : Preamble(marked.Value).Length;
        var codec = marked ?? requested ?? TextCodec.Utf8;
        try { return (Read(codec), codec, marked is not null); }
        catch (DecoderFallbackException) when (marked is null && requested is null)
        {
            try { return (Read(TextCodec.Gb18030), TextCodec.Gb18030, false); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("文件不是有效的 UTF-8 或 GB18030 文本，请明确指定编码。", ex); }
        }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("文件包含无效编码字节，已拒绝有损解码。", ex); }

        string Read(TextCodec selected)
        {
            var encoding = EncodingFor(selected);
            var text = encoding.GetString(bytes, skip, bytes.Length - skip);
            RejectBinary(text);
            if (!encoding.GetBytes(text).AsSpan().SequenceEqual(bytes.AsSpan(skip)))
                throw new InvalidDataException("文件编码不能逐字节无损往返，已拒绝打开。");
            return text;
        }
    }

    private static Encoding EncodingFor(TextCodec codec) => codec switch
    {
        TextCodec.Utf8 or TextCodec.Utf8Bom => new UTF8Encoding(false, true),
        TextCodec.Utf16LE => new UnicodeEncoding(false, false, true),
        TextCodec.Utf16BE => new UnicodeEncoding(true, false, true),
        TextCodec.Gb18030 => Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => throw new InvalidDataException("不支持的文本编码。")
    };
    private static byte[] Preamble(TextCodec codec) => codec switch
    {
        TextCodec.Utf8Bom => [0xef, 0xbb, 0xbf],
        TextCodec.Utf16LE => [0xff, 0xfe],
        TextCodec.Utf16BE => [0xfe, 0xff],
        _ => []
    };
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static LineEnding DetectLineEnding(string text)
    {
        bool crlf = false, lf = false, cr = false;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf = true; i++; }
                else cr = true;
            }
            else if (text[i] == '\n') lf = true;
        return (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0) > 1 ? LineEnding.Mixed
            : crlf ? LineEnding.CrLf : cr ? LineEnding.Cr : LineEnding.Lf;
    }
    private static void RejectBinary(string text)
    {
        foreach (var c in text)
            if (char.IsControl(c) && c is not '\t' and not '\r' and not '\n' and not '\f')
                throw new InvalidDataException("文件包含二进制控制字符，已拒绝作为文本打开或保存。");
    }
    private static byte[] ReadLimited(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumSize) throw new InvalidDataException("文件超过 50 MiB 上限。");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("读取中文件大小发生变化。");
        return bytes;
    }
    private static string FullPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)
                || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new IOException("请使用普通的 Windows 文件路径。");
            var full = System.IO.Path.GetFullPath(path);
            ValidateName(System.IO.Path.GetFileName(full));
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { throw new IOException("文件路径无效。", ex); }
    }
    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".."
            || name.EndsWith(' ') || name.EndsWith('.') || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            throw new IOException("文件名无效：不能包含路径、控制字符、Windows 非法字符或尾随空格/句点。");
        var stem = name.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
            throw new IOException("文件名是 Windows 保留设备名，请选择其他名称。");
    }
    private static void RejectReparseComponents(string path)
    {
        for (string? current = path; current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("路径包含软链接或重解析点，请另存为到普通目录。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static SafeFileHandle OpenSafeTarget(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new IOException("文件是只读文件，请另存为。");
        if (!OperatingSystem.IsWindows()) throw new IOException("无法验证 Windows 文件链接计数，请另存为。");
        var handle = CreateFile(path, 0x80000000, 1 | 4, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("无法取得文件安全句柄，文件可能被占用：" + new Win32Exception(error).Message);
        }
        if (!GetFileInformationByHandle(handle, out var information))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("无法验证文件链接计数，拒绝原位保存：" + new Win32Exception(error).Message);
        }
        if ((information.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || information.NumberOfLinks != 1)
        {
            handle.Dispose();
            throw new IOException("文件为链接或存在多个硬链接，请另存为。");
        }
        return handle;
    }
    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static void ApplyAccessSecurity(string path, string descriptor)
    {
        if (!OperatingSystem.IsWindows()) return;
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(descriptor, AccessControlSections.Access);
        new FileInfo(path).SetAccessControl(security);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
}
