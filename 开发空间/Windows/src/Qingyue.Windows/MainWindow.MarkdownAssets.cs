using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
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
        tab.Editor.PreviewDragOver += (_, e) =>
        {
            if (!ImageDrop(tab, e.Data, out var ignoredPath)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        tab.Editor.PreviewDrop += async (_, e) =>
        {
            if (!ImageDrop(tab, e.Data, out string? path)) return;
            e.Handled = true;
            await InsertMarkdownImageFileAsync(path!);
        };
    }

    private bool ImageDrop(EditorTab tab, IDataObject data, out string? path)
    {
        path = null;
        if (CurrentTab != tab || !tab.MarkdownWriter.CanEdit ||
            data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths) return false;
        if (Path.GetExtension(paths[0]).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff"))
            return false;
        path = paths[0];
        return true;
    }

    private async void MarkdownImageClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (exitInProgress || modalDepth > 0 || CurrentTab is not { } tab || !tab.MarkdownWriter.CanEdit) return;
        var dialog = new OpenFileDialog
        {
            Title = "插入本地图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff",
            CheckFileExists = true, Multiselect = false
        };
        if (Modal(() => dialog.ShowDialog(this)) == true)
            await InsertMarkdownImageFileAsync(dialog.FileName);
    }

    public Task<bool> InsertMarkdownImageAsync(BitmapSource image, CancellationToken cancellationToken = default) =>
        TrackMarkdownImageTask(InsertMarkdownImageCoreAsync(_ => Task.FromResult(image), cancellationToken));

    public Task<bool> InsertMarkdownImageFileAsync(string path, CancellationToken cancellationToken = default) =>
        TrackMarkdownImageTask(InsertMarkdownImageCoreAsync(token => MarkdownImageInsertion.ReadAsync(path, token), cancellationToken));

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

    private async Task<bool> InsertMarkdownImageCoreAsync(
        Func<CancellationToken, Task<BitmapSource>> readImage, CancellationToken cancellationToken)
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
        MarkdownImageAsset? asset = null;
        bool applied = false;
        try
        {
            StatusText.Text = "正在保存图片附件…";
            var image = await readImage(request.Token);
            asset = await MarkdownImageInsertion.WriteAsync(image, path, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (exitInProgress || CurrentTab != tab || !tabs.Contains(tab) || tab.File.Path != path ||
                tab.Editor.Text != source || tab.Editor.SelectionStart != start ||
                tab.Editor.SelectionLength != length || !tab.MarkdownWriter.CanEdit)
            {
                StatusText.Text = "文档或光标位置已变化，本次插图已取消，请重新插入。";
                return false;
            }
            applied = tab.MarkdownWriter.InsertImage(asset.Markdown);
            if (applied) StatusText.Text = "已插入本地图片；请保存文档。撤销会保留附件以便重做。";
            return applied;
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消插图。"; return false; }
        catch (Exception ex) when (SharingFailure(ex) || ex is System.IO.FileFormatException)
        { StatusText.Text = "插图未完成：" + ex.Message; return false; }
        finally
        {
            if (!applied && asset is not null) MarkdownImageInsertion.DeleteUnapplied(asset.FilePath);
            markdownImageCancellation = null;
        }
    }
}
