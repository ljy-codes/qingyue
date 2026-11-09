using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private const int MarkdownPreviewLimit = 200_000;
    private readonly DispatcherTimer markdownTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private EditorTab? markdownTab;
    private Grid? markdownSplit;
    private ContentControl? markdownEditorHost;
    private ContentControl? markdownPreviewHost;
    private MarkdownPreview? markdownPreview;
    private string? markdownText;
    private double markdownFont, markdownWidth;

    private void InitializeMarkdownPreview()
    {
        markdownTimer.Tick += (_, _) =>
        {
            markdownTimer.Stop();
            if (CurrentTab is { } tab && tab == markdownTab && !tab.IsReading &&
                tab.Kind == TextKind.Markdown && tab.File.Text.Length <= MarkdownPreviewLimit)
                UpdateMarkdownPreview(tab);
        };
        Closed += (_, _) => markdownTimer.Stop();
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
            if (tab.File.Text.Length > MarkdownPreviewLimit)
            {
                Notify("MD 分屏预览支持 20 万字符以内的文档。当前文档可继续在编辑区查看完整原文。");
                return;
            }
            markdownTab = tab;
            markdownText = null;
            if (tab.IsReading) ToggleReading();
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
            tab.File.Text.Length <= MarkdownPreviewLimit;
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
        if (markdownPreview is null || markdownText is null ||
            markdownFont != settings.FontSize || markdownWidth != settings.ReadingWidth)
        {
            markdownFont = settings.FontSize;
            markdownWidth = settings.ReadingWidth;
            markdownPreview = new MarkdownPreview(tab.File.Text, settings.FontSize + 1, settings.ReadingWidth);
            markdownPreviewHost!.Content = markdownPreview;
            markdownText = tab.File.Text;
        }
        else if (markdownText != tab.File.Text)
        {
            markdownTimer.Stop();
            markdownTimer.Start();
        }
        return true;
    }

    private void UpdateMarkdownPreview(EditorTab tab)
    {
        if (markdownPreview is null || markdownText == tab.File.Text) return;
        double offset = markdownPreview.VerticalOffset;
        markdownPreview.Update(tab.File.Text);
        markdownText = tab.File.Text;
        markdownPreview.ScrollToVerticalOffset(offset);
    }

    private void ReleaseClosedMarkdownPreview()
    {
        if (markdownTab is null || tabs.Contains(markdownTab)) return;
        markdownTimer.Stop();
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
