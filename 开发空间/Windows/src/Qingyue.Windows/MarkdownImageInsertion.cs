using System.IO;
using System.Windows.Media.Imaging;

namespace Qingyue.Windows;

internal sealed record MarkdownImageAsset(string FilePath, string Markdown);

/// <summary>Writes a new, portable attachment without changing the document itself.</summary>
internal static class MarkdownImageInsertion
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal const int MaximumSide = 8192;
    internal const long MaximumPixels = 16_000_000;

    public static async Task<MarkdownImageAsset> WriteAsync(
        BitmapSource image, string documentPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDimensions(image);
        // Capture on the caller's dispatcher, then encode the immutable image off-thread.
        var snapshot = image.IsFrozen ? image : image.CloneCurrentValue();
        if (!snapshot.IsFrozen) snapshot.Freeze();
        string document = LocalPath(documentPath);
        string directory = Path.Combine(Path.GetDirectoryName(document)!,
            Path.GetFileNameWithoutExtension(document) + ".assets");
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var encoded = new LimitedImageStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(snapshot));
            try { encoder.Save(encoded); }
            catch (Exception ex) when (encoded.Exceeded)
            { throw new IOException("PNG 图片超过 4 MiB，请缩小图片后重试。", ex); }
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePath(document);
            RejectReparsePath(directory);
            Directory.CreateDirectory(directory);
            RejectReparsePath(directory);
            string target = Path.Combine(directory, "image-" + Guid.NewGuid().ToString("N") + ".png");
            bool created = false;
            try
            {
                await using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    created = true;
                    encoded.Position = 0;
                    await encoded.CopyToAsync(stream, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Uri.EscapeDataString(Path.GetFileName(directory)) + "/" +
                    Uri.EscapeDataString(Path.GetFileName(target));
                // Parentheses are legal file-name characters, but must not close the Markdown link.
                relative = relative.Replace("(", "%28", StringComparison.Ordinal)
                    .Replace(")", "%29", StringComparison.Ordinal);
                return new MarkdownImageAsset(target, $"![图片]({relative})");
            }
            catch
            {
                if (created) DeleteUnapplied(target);
                throw;
            }
        }, cancellationToken);
    }

    public static Task<BitmapSource> ReadAsync(string path, CancellationToken cancellationToken) =>
        Task.Run<BitmapSource>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string local = LocalPath(path);
            RejectReparsePath(local);
            using var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes)
                throw new IOException("请选择不超过 4 MiB 的本地图片。");
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnDemand).Frames[0];
            ValidateDimensions(frame);
            // Materialize only after checking decoded dimensions; release the source file.
            var bitmap = new WriteableBitmap(frame);
            bitmap.Freeze();
            cancellationToken.ThrowIfCancellationRequested();
            return bitmap;
        }, cancellationToken);

    internal static void DeleteUnapplied(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void ValidateDimensions(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0 ||
            image.PixelWidth > MaximumSide || image.PixelHeight > MaximumSide ||
            (long)image.PixelWidth * image.PixelHeight > MaximumPixels)
            throw new ArgumentException("图片最长边不得超过 8192 像素，总像素不得超过 1600 万。", nameof(image));
    }

    private static string LocalPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (new Uri(full).IsUnc || full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("请先将文档和图片保存在本机磁盘。");
        return full;
    }

    private static void RejectReparsePath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("附件路径经过符号链接或目录联接，请改用本机实际路径。");
    }

    private sealed class LimitedImageStream : MemoryStream
    {
        public bool Exceeded { get; private set; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckLength(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckLength(buffer.Length);
            base.Write(buffer);
        }
        public override void SetLength(long value)
        {
            if (value > MaximumBytes) Fail();
            base.SetLength(value);
        }
        private void CheckLength(int count)
        {
            if (Position + count > MaximumBytes) Fail();
        }
        private void Fail()
        {
            Exceeded = true;
            throw new IOException("PNG 图片超过 4 MiB，请缩小图片后重试。");
        }
    }
}
