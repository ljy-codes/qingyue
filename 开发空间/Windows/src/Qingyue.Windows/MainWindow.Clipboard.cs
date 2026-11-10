using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private CancellationTokenSource? readingSharingCancellation;

    // Compatibility entry point for the existing copyReading action.
    private async void CopyReadingDocument() => await CopyReadingDocumentAsync(CancellationToken.None);

    public Task CopyReadingDocumentAsync(CancellationToken cancellationToken = default) =>
        RunReadingSharingAsync(false, null, false, cancellationToken);

    public void CancelReadingSharing()
    {
        VerifyAccess();
        readingSharingCancellation?.Cancel();
    }

    public void CopyReadingPlainText() => CopyReadingText(markdown: false);
    public void CopyReadingMarkdown() => CopyReadingText(markdown: true);
    public void CopyReadingCodeBlock() => CopyReadingBlock(code: true);
    public void CopyReadingTable() => CopyReadingBlock(code: false);

    private void CopyReadingText(bool markdown)
    {
        VerifyAccess();
        try
        {
            if (CurrentTab is not { Kind: TextKind.Markdown } tab)
                throw new InvalidOperationException("请先打开 Markdown 文档。");
            DataObject data = markdown ? ReadingClipboard.CreateMarkdownData(tab.File.Text)
                : ReadingClipboard.CreatePlainTextData(ReadingSharingPreview(tab));
            Clipboard.SetDataObject(data, true);
            StatusText.Text = markdown ? "已复制原始 Markdown 全文。" : "已复制阅读纯文本全文。";
        }
        catch (Exception ex) when (SharingFailure(ex)) { StatusText.Text = "复制未完成：" + ex.Message; }
    }

    private void CopyReadingBlock(bool code)
    {
        VerifyAccess();
        try
        {
            if (CurrentTab is not { Kind: TextKind.Markdown } tab)
                throw new InvalidOperationException("请先打开 Markdown 文档。");
            var preview = ReadingSharingPreview(tab);
            var result = code ? ReadingClipboard.CreateCurrentCodeBlockData(preview) : ReadingClipboard.CreateCurrentTableData(preview);
            if (result is null) throw new IOException("请将光标或选区放在同一个目标块内；内容仍须满足复制上限。");
            Clipboard.SetDataObject(result.Data, true);
            StatusText.Text = SharingComplete(code ? "已复制当前代码块" : "已复制当前表格", result.Images);
        }
        catch (Exception ex) when (SharingFailure(ex)) { StatusText.Text = "复制未完成：" + ex.Message; }
    }

    public async Task ExportReadingHtmlAsync(CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        if (cancellationToken.IsCancellationRequested)
        { StatusText.Text = "已取消 HTML 导出。"; return; }
        if (CurrentTab is not { Kind: TextKind.Markdown } tab)
        { StatusText.Text = "请先打开 Markdown 文档。"; return; }
        if (readingSharingCancellation is not null || exitInProgress || modalDepth > 0)
        { StatusText.Text = "请等待当前操作完成后再导出。"; return; }
        var dialog = new SaveFileDialog
        {
            Title = "导出自包含 HTML", Filter = "HTML 文档|*.html", DefaultExt = ".html",
            FileName = Path.GetFileNameWithoutExtension(tab.File.Title) + ".html",
            AddExtension = true, OverwritePrompt = true, CheckPathExists = true
        };
        if (Modal(() => dialog.ShowDialog(this)) != true) return;
        await ExportReadingHtmlToPathAsync(dialog.FileName, File.Exists(dialog.FileName), cancellationToken);
    }

    /// <summary>For hosts that already selected a path and obtained overwrite consent.</summary>
    public Task ExportReadingHtmlToPathAsync(string targetPath, bool overwrite = false, CancellationToken cancellationToken = default) =>
        RunReadingSharingAsync(true, targetPath, overwrite, cancellationToken);

    private async Task RunReadingSharingAsync(bool export, string? targetPath, bool overwrite, CancellationToken cancellationToken)
    {
        VerifyAccess();
        if (readingSharingCancellation is not null || exitInProgress || modalDepth > 0)
        { StatusText.Text = "请等待当前操作完成后再复制或导出。"; return; }
        var tab = CurrentTab;
        if (tab is not { Kind: TextKind.Markdown })
        { StatusText.Text = "请先打开 Markdown 文档。"; return; }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = request.Token;
        readingSharingCancellation = request;
        try
        {
            token.ThrowIfCancellationRequested();
            string source = tab.File.Text;
            // Flush only pending split edits before capturing the document.
            // A stable preview keeps its document, selection and image controls.
            if (!tab.IsReading && markdownTab == tab && markdownText != source &&
                PreviewLimits.CanRender(source, tab.Kind))
            {
                markdownTimer.Stop();
                UpdateMarkdownPreview(tab);
            }
            var preview = ReadingSharingPreview(tab);
            var document = preview.Document;
            StatusText.Text = "正在等待已生成图片加载（最多 5 秒，可取消）…";
            var progress = new Progress<ReadingImageWaitProgress>(state =>
            {
                if (!ReferenceEquals(readingSharingCancellation, request) || token.IsCancellationRequested) return;
                string stage = state.PendingImages > 0 ? $"正在等待 {state.PendingImages} 张图片（最多 5 秒）"
                    : export ? "正在生成自包含 HTML" : "正在生成富文本复制内容";
                StatusText.Text = stage + "，可取消。";
            });
            await ReadingClipboard.WaitForImagesAsync(preview, token, progress);
            token.ThrowIfCancellationRequested();
            if (CurrentTab != tab || !tabs.Contains(tab) || tab.File.Text != source || preview.Document != document
                || tab.Kind != TextKind.Markdown || !ReferenceEquals(ReadingSharingPreview(tab), preview) || exitInProgress)
                throw new InvalidOperationException("阅读内容或当前标签已变化，请重新复制或导出。");
            if (export)
            {
                var result = ReadingHtmlExport.CreateDocument(preview, tab.File.Title)
                    ?? throw new IOException("内容超过安全导出预算（50 万正文字符、2 万元素或 20 MiB HTML），未写入目标。");
                token.ThrowIfCancellationRequested();
                await ReadingHtmlExport.WriteAtomicAsync(result, targetPath!,
                    tabs.Where(t => t.File.Path is not null).Select(t => t.File.Path!).ToArray(), overwrite, token);
                StatusText.Text = SharingComplete("已导出自包含 UTF-8 HTML", result.Images);
            }
            else
            {
                var result = ReadingClipboard.CreateFullDocumentData(preview)
                    ?? throw new IOException("内容超过安全复制预算或无法序列化。可选择部分内容复制。");
                token.ThrowIfCancellationRequested();
                Clipboard.SetDataObject(result.Data, true);
                StatusText.Text = SharingComplete("已复制全文，可粘贴到支持富文本的应用", result.Images);
            }
        }
        catch (OperationCanceledException) { StatusText.Text = export ? "已取消 HTML 导出。" : "已取消全文复制。"; }
        catch (Exception ex) when (SharingFailure(ex))
        { StatusText.Text = (export ? "导出未完成：" : "复制未完成：") + ex.Message; }
        finally { readingSharingCancellation = null; }
    }

    private static string SharingComplete(string action, ReadingImageReport images) =>
        $"{action}，含 {images.Embedded} 张图片。" + (images.Incomplete
            ? $" 图片未完整：{images.Description}；已保留明确占位符，可加载完成后重试。" : "");

    private static bool SharingFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or NotSupportedException
        or System.Runtime.InteropServices.COMException or System.Security.SecurityException;

    private RichTextBox ReadingSharingPreview(EditorTab tab)
    {
        if (tab.IsReading && tab.Rendered is not null && CanReuseReading(tab)) return tab.Preview;
        if (markdownTab == tab && markdownPreview is not null && markdownText == tab.File.Text) return markdownPreview;
        // Validation never rebuilds the preview while a sharing operation waits.
        // Pending split edits are flushed once before capturing the document.
        throw new InvalidOperationException("阅读预览尚未就绪或正在更新，请等待预览完成后重试。");
    }

    // Keep the synchronous, in-memory entry point used by existing integrations.
    private ReadingClipboardResult? CreateReadingDocumentData()
    {
        if (CurrentTab is not { Kind: TextKind.Markdown } tab) return null;
        if (!tab.IsReading && markdownTab == tab)
        {
            markdownTimer.Stop();
            UpdateMarkdownPreview(tab);
        }
        try { return ReadingClipboard.CreateFullDocumentData(ReadingSharingPreview(tab)); }
        catch (InvalidOperationException) { return null; }
    }
}
