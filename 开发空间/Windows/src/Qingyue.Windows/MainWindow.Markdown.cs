using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private readonly DispatcherTimer markdownTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private EditorTab? markdownTab;
    private Grid? markdownSplit;
    private ContentControl? markdownEditorHost;
    private ContentControl? markdownPreviewHost;
    private MarkdownPreview? markdownPreview;
    private string? markdownText;
    private double markdownFont, markdownWidth;
    private CancellationTokenSource? markdownPreviewCancellation;
    private long markdownPreviewGeneration;

    private void InitializeMarkdownPreview()
    {
        markdownTimer.Tick += (_, _) =>
        {
            markdownTimer.Stop();
            if (CurrentTab is { } tab && tab == markdownTab && !tab.IsReading &&
                tab.Kind == TextKind.Markdown && PreviewLimits.CanRender(tab.File.Text, tab.Kind))
                StartMarkdownPreviewUpdate(tab);
        };
        Closed += (_, _) =>
        {
            markdownTimer.Stop();
            CancelMarkdownPreviewUpdate();
        };
    }

    private void ToggleMarkdownPreview()
    {
        if (CurrentTab is not { Kind: TextKind.Markdown } tab) return;
        if (markdownTab == tab && !tab.IsReading && ReferenceEquals(DocumentHost.Content, markdownSplit))
        {
            markdownTab = null;
            markdownTimer.Stop();
        }
        else
        {
            if (!PreviewLimits.CanRender(tab.File.Text, tab.Kind))
            {
                Notify("MD 分屏预览支持 50 万正文字符，内嵌图片数据单独计限。当前文档可继续查看完整原文。");
                return;
            }
            if (tab.IsReading) ToggleReading();
            markdownTab = tab;
            markdownText = null;
        }
        RefreshCurrent();
        tab.Editor.Focus();
    }

    private void DetachMarkdownEditor()
    {
        if (markdownEditorHost is not null) markdownEditorHost.Content = null;
    }

    private bool RefreshMarkdownPresentation(EditorTab tab)
    {
        bool enabled = markdownTab == tab && !tab.IsReading && tab.Kind == TextKind.Markdown &&
            PreviewLimits.CanRender(tab.File.Text, tab.Kind);
        if (!enabled)
        {
            markdownTimer.Stop();
            if (ReferenceEquals(DocumentHost.Content, markdownSplit) && markdownSplit is not null)
            {
                DetachMarkdownEditor();
                DocumentHost.Content = tab.IsReading && tab.Rendered is not null ? tab.Preview : tab.EditorSurface;
            }
            return false;
        }
        if (markdownSplit is null) CreateMarkdownSplit();
        if (!ReferenceEquals(DocumentHost.Content, markdownSplit))
        {
            // A WPF element can have only one logical parent.
            DocumentHost.Content = null;
            markdownEditorHost!.Content = tab.EditorSurface;
            DocumentHost.Content = markdownSplit;
        }
        if (markdownPreview is null || markdownText is null)
        {
            markdownFont = settings.FontSize;
            markdownWidth = settings.ReadingWidth;
            // Keep the control lightweight while the real document is built in the
            // cancellable async pipeline. An empty document also gives the user an
            // immediate, stable host while a large Markdown file is being prepared.
            markdownPreview = new MarkdownPreview("", settings.FontSize + 1, settings.ReadingWidth, tab.File.Path,
                includeDiagramSource: true);
            ReadingClipboard.Attach(markdownPreview, () => true,
                reason => StatusText.Text = "复制已降级：" + reason);
            markdownPreviewHost!.Content = markdownPreview;
            if (MarkdownPreview.CanBuildSynchronously(tab.File.Text))
            {
                markdownPreview.Update(tab.File.Text);
                markdownText = tab.File.Text;
            }
            else
            {
                markdownText = null;
                StartMarkdownPreviewUpdate(tab);
            }
        }
        else
        {
            if (markdownFont != settings.FontSize || markdownWidth != settings.ReadingWidth)
            {
                markdownPreview.UpdatePresentation(settings.FontSize + 1, settings.ReadingWidth);
                markdownFont = settings.FontSize;
                markdownWidth = settings.ReadingWidth;
            }
            if (markdownText != tab.File.Text)
            {
                markdownTimer.Stop();
                CancelMarkdownPreviewUpdate();
                markdownTimer.Start();
            }
        }
        return true;
    }

    private void UpdateMarkdownPreview(EditorTab tab)
    {
        // Synchronous compatibility boundary for the in-memory copy factory and
        // existing test hosts. Interactive preview updates use the async method.
        if (markdownPreview is null || markdownText == tab.File.Text) return;
        CancelMarkdownPreviewUpdate();
        double offset = markdownPreview.VerticalOffset;
        markdownPreview.Update(tab.File.Text);
        markdownText = tab.File.Text;
        markdownPreview.ScrollToVerticalOffset(offset);
    }

    private void StartMarkdownPreviewUpdate(EditorTab tab)
    {
        if (markdownPreview is null || markdownTab != tab || tab.IsReading ||
            tab.Kind != TextKind.Markdown || !PreviewLimits.CanRender(tab.File.Text, tab.Kind))
            return;
        string text = tab.File.Text;
        if (markdownText == text) return;
        _ = UpdateMarkdownPreviewAsync(tab, text, CancellationToken.None);
    }

    private async Task<bool> EnsureMarkdownPreviewAsync(EditorTab tab, string text,
        CancellationToken cancellationToken)
    {
        VerifyAccess();
        if (markdownPreview is null || markdownTab != tab || tab.IsReading ||
            tab.Kind != TextKind.Markdown || !PreviewLimits.CanRender(text, tab.Kind))
            return false;
        if (markdownText == text) return true;
        markdownTimer.Stop();
        return await UpdateMarkdownPreviewAsync(tab, text, cancellationToken);
    }

    private async Task<bool> UpdateMarkdownPreviewAsync(EditorTab tab, string text,
        CancellationToken externalCancellation)
    {
        if (markdownPreview is null) return false;
        markdownPreviewCancellation?.Cancel();
        markdownPreviewCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        markdownPreviewCancellation = cancellation;
        long generation = ++markdownPreviewGeneration;
        double offset = markdownPreview.VerticalOffset;
        try
        {
            if (CurrentTab != tab || markdownTab != tab || tab.IsReading ||
                tab.Kind != TextKind.Markdown || tab.File.Text != text || exitInProgress)
                return false;
            var model = await Task.Run(() => ReadingModel.Build(text, TextKind.Markdown),
                cancellation.Token).WaitAsync(cancellation.Token);
            var document = await MarkdownPreview.BuildDocumentAsync(model, text, tab.File.Path,
                settings.FontSize + 1, settings.ReadingWidth, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (markdownPreviewCancellation != cancellation || generation != markdownPreviewGeneration ||
                CurrentTab != tab || markdownTab != tab || tab.IsReading ||
                tab.Kind != TextKind.Markdown || tab.File.Text != text || exitInProgress)
                return false;
            markdownPreview.Document = document;
            markdownText = text;
            markdownPreview.ScrollToVerticalOffset(Math.Max(0, offset));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException or
            FormatException or OverflowException or InvalidOperationException or IndexOutOfRangeException)
        {
            if (CurrentTab == tab && markdownTab == tab && tab.File.Text == text)
                StatusText.Text = "Markdown 预览生成失败，已保留当前编辑内容。";
            return false;
        }
        finally
        {
            if (ReferenceEquals(markdownPreviewCancellation, cancellation))
                markdownPreviewCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelMarkdownPreviewUpdate()
    {
        markdownPreviewGeneration++;
        markdownPreviewCancellation?.Cancel();
        markdownPreviewCancellation?.Dispose();
        markdownPreviewCancellation = null;
    }

    private void ReleaseClosedMarkdownPreview()
    {
        if (markdownTab is null || tabs.Contains(markdownTab)) return;
        markdownTimer.Stop();
        CancelMarkdownPreviewUpdate();
        DetachMarkdownEditor();
        markdownTab = null;
        markdownPreview = null;
        markdownText = null;
        if (markdownPreviewHost is not null) markdownPreviewHost.Content = null;
    }

    private void CreateMarkdownSplit()
    {
        markdownSplit = new Grid();
        markdownSplit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
        markdownSplit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        markdownSplit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
        markdownEditorHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        markdownPreviewHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid Pane(string title, ContentControl content)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            var label = new TextBlock { Text = title, FontSize = 11, Margin = new Thickness(16, 8, 12, 8) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            grid.Children.Add(label);
            Grid.SetRow(content, 1);
            grid.Children.Add(content);
            return grid;
        }
        markdownSplit.Children.Add(Pane("MARKDOWN  /  编辑原文", markdownEditorHost));
        var splitter = new GridSplitter { Width = 7, HorizontalAlignment = HorizontalAlignment.Stretch };
        splitter.SetResourceReference(Control.BackgroundProperty, "BorderBrush");
        Grid.SetColumn(splitter, 1);
        markdownSplit.Children.Add(splitter);
        var right = Pane("预览  /  标题 · 表格 · Mermaid 时序图", markdownPreviewHost);
        Grid.SetColumn(right, 2);
        markdownSplit.Children.Add(right);
    }
}
