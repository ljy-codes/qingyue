using System.IO;
using System.Text;
using Qingyue.Core;

namespace Qingyue.Windows;

public static class ReadingBriefExportService
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static ReadingBriefInput CreateInput(
        string title,
        string? sourcePath,
        TextKind kind,
        string sourceText,
        int progressPercent,
        DateTimeOffset generatedAt,
        IReadOnlyList<DocumentAnnotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(annotations);
        var model = ReadingModel.Build(sourceText, kind);
        return new ReadingBriefInput(
            string.IsNullOrWhiteSpace(title) ? "未命名文档" : title.Trim(),
            sourcePath,
            kind,
            sourceText,
            Math.Clamp(progressPercent, 0, 100),
            generatedAt,
            model.Headings,
            annotations);
    }

    public static async Task ExportAsync(
        ReadingBriefInput input,
        string targetPath,
        bool html,
        IEnumerable<string> protectedSourcePaths,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(protectedSourcePaths);
        var sources = protectedSourcePaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        string? validationError = ValidateTarget(targetPath, sources);
        if (validationError is not null) throw new IOException(validationError);

        string content = html
            ? ReadingBriefExporter.BuildHtml(input)
            : ReadingBriefExporter.BuildMarkdown(input);
        byte[] bytes = Utf8.GetBytes(content);
        string target = Path.GetFullPath(targetPath);
        string? directory = Path.GetDirectoryName(target);
        if (directory is null) throw new IOException("导出目录无效。");
        Directory.CreateDirectory(directory);
        validationError = ValidateTarget(target, sources);
        if (validationError is not null) throw new IOException(validationError);
        if (File.Exists(target) && !overwrite)
            throw new IOException("目标文件已存在，请选择其他路径或明确覆盖。");

        string temporary = Path.Combine(directory, ".qingyue-brief-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            validationError = ValidateTarget(target, sources);
            if (validationError is not null) throw new IOException(validationError);
            File.Move(temporary, target, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    /// <summary>
    /// Returns null for a safe target; otherwise returns the user-facing reason.
    /// </summary>
    public static string? ValidateTarget(string path, IEnumerable<string> protectedSourcePaths)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return "请选择本机磁盘上的简报文件。";
        if (path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal))
            return "请选择本机磁盘上的简报文件。";

        string target;
        try { target = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { return "导出路径无效：" + ex.Message; }

        string? root = Path.GetPathRoot(target);
        if (string.IsNullOrWhiteSpace(root)) return "导出路径没有有效的本机磁盘。";
        try
        {
            if (new DriveInfo(root).DriveType == DriveType.Network)
                return "导出目标必须是本机磁盘文件。";
        }
        catch (IOException ex) { return "无法确认导出磁盘：" + ex.Message; }
        if (target.AsSpan(root.Length).Contains(':'))
            return "不支持导出到 NTFS 附加数据流。";

        foreach (string source in protectedSourcePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            string fullSource;
            try { fullSource = Path.GetFullPath(source); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            { return "当前原文路径无效：" + ex.Message; }
            if (string.Equals(target, fullSource, StringComparison.OrdinalIgnoreCase))
                return "不能覆盖当前已打开的原文文件，请选择其他导出路径。";
            string? sourceError = RejectReparsePath(fullSource);
            if (sourceError is not null)
                return "当前原文路径不安全：" + sourceError;
        }

        return RejectReparsePath(target) is { } targetError
            ? "请选择不经过符号链接或目录联接的本机路径：" + targetError
            : null;
    }

    private static string? RejectReparsePath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return "路径包含软链接或重解析点。";
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { return "路径权限不足。"; }
        }
        return null;
    }
}
