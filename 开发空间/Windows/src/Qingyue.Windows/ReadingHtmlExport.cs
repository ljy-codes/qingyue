using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;

namespace Qingyue.Windows;

public sealed class ReadingHtmlExportResult
{
    internal ReadingHtmlExportResult(string html, ReadingImageReport images)
    { Html = html; Images = images; }

    public string Html { get; }
    public ReadingImageReport Images { get; }
    public int EmbeddedImages => Images.Embedded;
    public int OmittedImages => Images.Omitted;
}

/// <summary>
/// Uses the clipboard's whitelist and cached pixels. This class never loads a URI,
/// reads the source file, or serializes live controls on a worker thread.
/// </summary>
public static class ReadingHtmlExport
{
    private const int MaximumHtmlBytes = 20 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string Style = """
        body{margin:2rem auto;padding:0 1.5rem;max-width:900px;color:#222;background:#fff;
        font:16px/1.75 "Segoe UI","Microsoft YaHei",sans-serif;overflow-wrap:anywhere}
        h1,h2,h3,h4,h5,h6{line-height:1.35}pre{padding:1rem;background:#f4f4f4;overflow:auto;white-space:pre}
        code{font-family:Consolas,monospace}blockquote{margin-left:0;padding-left:1rem;border-left:3px solid #bbb}
        table{border-collapse:collapse;max-width:100%;display:block;overflow:auto}
        th,td{border:1px solid #999;padding:6px 10px;text-align:left}img{max-width:100%;height:auto}
        .export-warning{padding:.75rem;border:1px solid #ad6500;color:#754200;background:#fff6e7}
        """;

    public static ReadingHtmlExportResult? CreateDocument(RichTextBox preview, string title)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(title);
        preview.VerifyAccess();
        if (ReadingClipboard.CreateHtmlSnapshot(preview) is not { } snapshot) return null;
        // A host may pass a source path as a title. Keep just its final component.
        title = title.Replace('\\', '/').TrimEnd('/');
        title = title[(title.LastIndexOf('/') + 1)..];
        if (title.Contains("file:", StringComparison.OrdinalIgnoreCase) || title.Contains(':')) title = "阅读导出";
        if (string.IsNullOrWhiteSpace(title)) title = "阅读导出";
        if (title.Length > 256) title = title[..256];
        var head = new XElement("head",
            new XElement("meta", new XAttribute("charset", "utf-8")),
            new XElement("meta", new XAttribute("http-equiv", "Content-Security-Policy"),
                new XAttribute("content", "default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'")),
            new XElement("meta", new XAttribute("name", "viewport"), new XAttribute("content", "width=device-width, initial-scale=1")),
            new XElement("title", title), new XElement("style", Style));
        string warning = snapshot.Images.Incomplete ? new XElement("p",
            new XAttribute("class", "export-warning"),
            $"导出含 {snapshot.Images.Embedded} 张图片；{snapshot.Images.Description}。未包含的图片已保留占位符。")
            .ToString(SaveOptions.DisableFormatting) : "";
        string html = "<!DOCTYPE html>\n<html lang=\"zh-CN\">" + head.ToString(SaveOptions.DisableFormatting)
            + "<body>" + warning + snapshot.Fragment + "</body></html>";
        if (Utf8.GetByteCount(html) > MaximumHtmlBytes) return null;
        return new(html, snapshot.Images);
    }

    public static async Task<ReadingHtmlExportResult?> ExportAsync(RichTextBox preview, string title,
        string targetPath, IEnumerable<string> protectedSourcePaths, bool overwrite = false,
        CancellationToken cancellationToken = default, IProgress<ReadingImageWaitProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(protectedSourcePaths);
        preview.VerifyAccess();
        var sources = protectedSourcePaths.ToArray();
        // Validate before waiting and again immediately before publishing the file.
        ValidateTarget(targetPath, sources);
        var guard = new ReadingClipboard.DocumentGuard(preview);
        try
        {
            await ReadingClipboard.WaitForImagesAsync(preview, cancellationToken, progress).ConfigureAwait(false);
            var result = await preview.Dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                guard.Verify();
                return CreateDocument(preview, title);
            }, DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
            if (result is not null)
                await WriteAtomicAsync(result, targetPath, sources, overwrite, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally { await preview.Dispatcher.InvokeAsync(guard.Dispose).Task.ConfigureAwait(false); }
    }

    public static async Task WriteAtomicAsync(ReadingHtmlExportResult document, string targetPath,
        IEnumerable<string> protectedSourcePaths, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(protectedSourcePaths);
        var sources = protectedSourcePaths.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        string target = ValidateTarget(targetPath, sources);
        byte[] bytes = Utf8.GetBytes(document.Html);
        if (bytes.Length > MaximumHtmlBytes) throw new IOException("HTML 超过 20 MiB 输出上限。");
        // Only immutable text/bytes cross this boundary, never WPF objects.
        await Task.Run(async () =>
        {
            string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".qingyue-html-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                ValidateTarget(target, sources);
                // Same-directory rename is the commit point. No delete-then-create gap.
                File.Move(temporary, target, overwrite);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateTarget(string path, IEnumerable<string> protectedSourcePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal))
            throw new IOException("请选择本机磁盘上的 HTML 文件。");
        string target = Path.GetFullPath(path);
        string root = Path.GetPathRoot(target)!;
        if (new DriveInfo(root).DriveType == DriveType.Network)
            throw new IOException("导出目标必须是本机磁盘文件。");
        if (target.AsSpan(root.Length).Contains(':')) throw new IOException("不支持导出到附加数据流。");
        foreach (string source in protectedSourcePaths)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            string fullSource = Path.GetFullPath(source);
            if (string.Equals(target, fullSource, StringComparison.OrdinalIgnoreCase))
                throw new IOException("不能覆盖当前已打开的原文文件，请选择其他导出路径。");
            // The opened source may itself use an alias while the target uses
            // its real spelling. Fail closed rather than compare these strings.
            RejectReparsePath(fullSource, "已打开原文的路径经过符号链接或目录联接，无法安全确认文件身份；请从实际路径重新打开原文后导出。");
        }
        RejectReparsePath(target, "请选择不经过符号链接或目录联接的本机路径。");
        return target;
    }

    private static void RejectReparsePath(string path, string message)
    {
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(message);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(message);
    }
}
