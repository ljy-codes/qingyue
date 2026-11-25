using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private CancellationTokenSource? markdownImageCancellation;
    private Task? markdownImageTask;

    private void AttachMarkdownAssets(EditorTab tab)
    {
        DataObject.AddPastingHandler(tab.Editor, (_, e) =>
        {
            if (!tab.MarkdownWriter.CanEdit || CurrentTab != tab ||
                !e.SourceDataObject.GetDataPresent(DataFormats.Bitmap, false) ||
                HasTextualClipboardData(e.SourceDataObject)) return;
            // Keep native text, HTML and rich-text paste when an application exposes
            // both a rendered bitmap and the source text in the same data object.
            e.CancelCommand();
            try
            {
                if (e.SourceDataObject.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
                    markdownImageTask = InsertMarkdownImageAsync(bitmap);
                else StatusText.Text = "无法读取剪贴板图片，请使用“插入图片”。";
            }
            catch (Exception ex) when (SharingFailure(ex))
            { StatusText.Text = "图片粘贴未完成：" + ex.Message; }
        });
        tab.Editor.PreviewDragOver += (sender, e) =>
        {
            if (!ImageDrop(tab, e.Data, out _)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        tab.Editor.PreviewDrop += async (sender, e) =>
        {
            if (!ImageDrop(tab, e.Data, out string[] paths)) return;
            e.Handled = true;
            await InsertMarkdownImageFilesAsync(paths);
        };
    }

    private bool ImageDrop(EditorTab tab, IDataObject data, out string[] paths)
    {
        paths = [];
        if (CurrentTab != tab || !tab.MarkdownWriter.CanEdit ||
            data.GetData(DataFormats.FileDrop) is not string[] dropped) return false;
        paths = dropped.Where(IsSupportedImage).Take(8).ToArray();
        return paths.Length > 0;
    }

    public Task<bool> InsertMarkdownImageAsync(BitmapSource image, CancellationToken cancellationToken = default) =>
        TrackMarkdownImageTask(InsertMarkdownImagesCoreAsync(
            _ => Task.FromResult<IReadOnlyList<(BitmapSource Image, string SuggestedName)>>(
                [(image, "pasted-image")]), cancellationToken));

    public Task<bool> InsertMarkdownImageFileAsync(string path, CancellationToken cancellationToken = default) =>
        InsertMarkdownImageFilesAsync([path], cancellationToken);

    private Task<bool> InsertMarkdownImageFilesAsync(
        IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        TrackMarkdownImageTask(InsertMarkdownImagesCoreAsync(async token =>
        {
            var images = new List<(BitmapSource Image, string SuggestedName)>();
            foreach (string path in paths.Take(8))
                images.Add((await MarkdownImageInsertion.ReadAsync(path, token),
                    Path.GetFileNameWithoutExtension(path)));
            return images;
        }, cancellationToken));

    private Task<bool> TrackMarkdownImageTask(Task<bool> task)
    {
        markdownImageTask = task;
        return task;
    }

    private static bool HasTextualClipboardData(IDataObject data) =>
        data.GetDataPresent(DataFormats.UnicodeText, false) ||
        data.GetDataPresent(DataFormats.Text, false) ||
        data.GetDataPresent(DataFormats.Html, false) ||
        data.GetDataPresent(DataFormats.Rtf, false);

    private async Task<bool> InsertMarkdownImagesCoreAsync(
        Func<CancellationToken, Task<IReadOnlyList<(BitmapSource Image, string SuggestedName)>>> readImages,
        CancellationToken cancellationToken)
    {
        VerifyAccess();
        if (exitInProgress || modalDepth > 0 || markdownImageCancellation is not null ||
            CurrentTab is not { } tab || !tab.MarkdownWriter.CanEdit || cancellationToken.IsCancellationRequested)
            return false;
        if (tab.File.Path is null && !SaveDocument(tab)) return false;
        if (tab.File.Path is not { } path || CurrentTab != tab || !tab.MarkdownWriter.CanEdit) return false;
        string source = tab.Editor.Text;
        int start = tab.Editor.SelectionStart, length = tab.Editor.SelectionLength;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        markdownImageCancellation = request;
        var assets = new List<MarkdownImageAsset>();
        bool applied = false;
        try
        {
            StatusText.Text = "正在保存图片附件…";
            var images = await readImages(request.Token);
            if (images.Count == 0) return false;
            foreach (var (image, suggestedName) in images)
            {
                request.Token.ThrowIfCancellationRequested();
                assets.Add(await MarkdownImageInsertion.WriteAsync(
                    image, path, suggestedName, request.Token));
            }
            request.Token.ThrowIfCancellationRequested();
            if (exitInProgress || CurrentTab != tab || !tabs.Contains(tab) || tab.File.Path != path ||
                tab.Editor.Text != source || tab.Editor.SelectionStart != start ||
                tab.Editor.SelectionLength != length || !tab.MarkdownWriter.CanEdit)
            {
                StatusText.Text = "文档或光标位置已变化，本次插图已取消，请重新插入。";
                return false;
            }
            string markdown = string.Join(Environment.NewLine, assets.Select(asset => asset.Markdown));
            applied = tab.MarkdownWriter.InsertImage(markdown);
            if (applied)
                StatusText.Text = assets.Count == 1
                    ? "已插入本地图片；请保存文档。撤销会保留附件以便重做。"
                    : $"已插入 {assets.Count} 张图片；请保存文档。撤销会保留附件以便重做。";
            return applied;
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消插图。"; return false; }
        catch (Exception ex) when (SharingFailure(ex) || ex is System.IO.FileFormatException)
        { StatusText.Text = "插图未完成：" + ex.Message; return false; }
        finally
        {
            if (!applied)
                foreach (var asset in assets) MarkdownImageInsertion.DeleteUnapplied(asset.FilePath);
            markdownImageCancellation = null;
        }
    }

    internal static bool IsSupportedImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".tif" or ".tiff" or ".bmp" or ".heic";
}
