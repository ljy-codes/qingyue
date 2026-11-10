using System.Windows;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private void CopyReadingDocument()
    {
        var result = CreateReadingDocumentData();
        if (result is null)
        {
            StatusText.Text = "当前内容暂无法整体复制，请选择需要的部分再按 Ctrl+C。";
            return;
        }
        Clipboard.SetDataObject(result.Data, true);
        StatusText.Text = result.OmittedImages > 0
            ? $"已复制全文，含 {result.EmbeddedImages} 张图片；{result.OmittedImages} 张图片尚未加载或超出复制上限，请加载完成后重试。"
            : $"已复制全文，含 {result.EmbeddedImages} 张图片，可粘贴到支持富文本的应用。";
    }

    private ReadingClipboardResult? CreateReadingDocumentData()
    {
        if (CurrentTab is not { Kind: TextKind.Markdown } tab) return null;
        if (!tab.IsReading && markdownTab == tab)
        {
            markdownTimer.Stop();
            UpdateMarkdownPreview(tab);
        }
        var preview = tab.IsReading && tab.Rendered is not null ? tab.Preview
            : markdownTab == tab ? markdownPreview : null;
        return preview is null ? null : ReadingClipboard.CreateFullDocumentData(preview);
    }
}
