using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qingyue.Core;

namespace Qingyue.Windows;

/// <summary>
/// Non-modal workspace search with the same three result columns and explicit
/// replacement-preview detail flow as the macOS GlobalSearchWindow.
/// </summary>
public sealed class GlobalSearchWindow : Window
{
    private readonly string root;
    private readonly Action<GlobalSearchMatch> openResult;
    private readonly Func<string, bool> canReplaceFile;
    private readonly TextBox query = new();
    private readonly TextBox replacement = new();
    private readonly TextBox fileFilter = new();
    private readonly CheckBox caseSensitive = new() { Content = "区分大小写" };
    private readonly CheckBox regex = new() { Content = "正则" };
    private readonly CheckBox wholeWord = new() { Content = "全词" };
    private readonly ListView results = new();
    private readonly TextBlock status = new();
    private readonly Button searchButton = new() { Content = "搜索", IsDefault = true };
    private readonly Button previewButton = new() { Content = "预览替换" };
    private readonly Button applyButton = new() { Content = "应用预览替换…", IsEnabled = false };
    private readonly Button cancelButton = new() { Content = "取消搜索", IsEnabled = false };
    private readonly List<Window> detailWindows = [];
    private GlobalSearchResult? current;
    private CancellationTokenSource? searchCancellation;
    private bool applying;
    private long generation;

    public GlobalSearchWindow(
        Window owner,
        string root,
        Action<GlobalSearchMatch> openResult,
        Func<string, bool>? canReplaceFile = null)
    {
        this.root = Path.GetFullPath(root);
        this.openResult = openResult;
        this.canReplaceFile = canReplaceFile ?? (_ => true);
        Owner = owner;
        Title = "全局搜索 · " + new DirectoryInfo(this.root).Name;
        Width = 900;
        Height = 580;
        MinWidth = 680;
        MinHeight = 400;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = owner.Icon;
        Name = "GlobalSearchWindow";
        AutomationProperties.SetName(this, "全局搜索窗口");
        foreach (var key in owner.Resources.Keys) Resources[key] = owner.Resources[key];
        Build();
        Closed += (_, _) =>
        {
            CancelSearch();
            foreach (var detail in detailWindows.Where(window => window.IsVisible).ToArray())
                detail.Close();
            detailWindows.Clear();
        };
        Loaded += (_, _) => query.Focus();
    }

    private void Build()
    {
        var panel = new Grid { Margin = new Thickness(12) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var path = new TextBlock
        {
            Text = root,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = root,
            FontSize = 11,
            Foreground = FindBrush("MutedBrush")
        };
        AutomationProperties.SetName(path, "全局搜索工作区");
        panel.Children.Add(path);

        var searchRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        query.Name = "GlobalSearchQuery";
        query.ToolTip = "搜索内容";
        query.Margin = new Thickness(0, 0, 8, 0);
        query.TextChanged += (_, _) => Invalidate();
        query.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await RunSearchAsync(previewOnly: false);
            }
        };
        AutomationProperties.SetName(query, "工作区搜索内容");
        searchButton.Name = "GlobalSearchSearchButton";
        searchButton.Margin = new Thickness(0, 0, 8, 0);
        searchButton.Click += async (_, _) => await RunSearchAsync(previewOnly: false);
        cancelButton.Name = "GlobalSearchCancelSearchButton";
        cancelButton.Margin = new Thickness(0, 0, 8, 0);
        cancelButton.Click += (_, _) => Invalidate("已取消搜索。");
        var closeButton = new Button { Name = "GlobalSearchCloseButton", Content = "关闭", IsCancel = true };
        closeButton.Click += (_, _) => Close();
        searchRow.Children.Add(query);
        Grid.SetColumn(searchButton, 1);
        searchRow.Children.Add(searchButton);
        Grid.SetColumn(cancelButton, 2);
        searchRow.Children.Add(cancelButton);
        Grid.SetColumn(closeButton, 3);
        searchRow.Children.Add(closeButton);
        Grid.SetRow(searchRow, 1);
        panel.Children.Add(searchRow);

        var options = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        caseSensitive.Name = "GlobalSearchCaseSensitive";
        regex.Name = "GlobalSearchRegex";
        wholeWord.Name = "GlobalSearchWholeWord";
        foreach (var option in new[] { caseSensitive, regex, wholeWord })
        {
            option.Margin = new Thickness(0, 0, 12, 0);
            option.Checked += (_, _) => Invalidate();
            option.Unchecked += (_, _) => Invalidate();
        }
        fileFilter.Name = "GlobalSearchFileFilter";
        fileFilter.ToolTip = "文件名过滤（包含）";
        fileFilter.MinWidth = 150;
        fileFilter.TextChanged += (_, _) => Invalidate();
        AutomationProperties.SetName(fileFilter, "搜索文件名过滤");
        options.Children.Add(caseSensitive);
        Grid.SetColumn(regex, 1);
        options.Children.Add(regex);
        Grid.SetColumn(wholeWord, 2);
        options.Children.Add(wholeWord);
        Grid.SetColumn(fileFilter, 3);
        options.Children.Add(fileFilter);
        Grid.SetRow(options, 2);
        panel.Children.Add(options);

        var replaceRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        replaceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        replaceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        replaceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        replacement.Name = "GlobalSearchReplacement";
        replacement.ToolTip = "替换内容（正则捕获组 $1）";
        replacement.Margin = new Thickness(0, 0, 8, 0);
        replacement.TextChanged += (_, _) => Invalidate();
        AutomationProperties.SetName(replacement, "工作区替换内容");
        previewButton.Name = "GlobalSearchPreviewButton";
        previewButton.Margin = new Thickness(0, 0, 8, 0);
        previewButton.Click += async (_, _) => await RunSearchAsync(previewOnly: true);
        applyButton.Name = "GlobalSearchApplyButton";
        applyButton.Click += async (_, _) => await ApplyReplacementAsync();
        replaceRow.Children.Add(replacement);
        Grid.SetColumn(previewButton, 1);
        replaceRow.Children.Add(previewButton);
        Grid.SetColumn(applyButton, 2);
        replaceRow.Children.Add(applyButton);
        Grid.SetRow(replaceRow, 3);
        panel.Children.Add(replaceRow);

        results.Name = "GlobalSearchResults";
        results.Margin = new Thickness(0, 10, 0, 0);
        results.BorderBrush = FindBrush("BorderBrush");
        results.BorderThickness = new Thickness(1);
        var view = new GridView();
        view.Columns.Add(new GridViewColumn
        {
            Header = "文件", Width = 260,
            DisplayMemberBinding = new System.Windows.Data.Binding(nameof(GlobalSearchRow.File))
        });
        view.Columns.Add(new GridViewColumn
        {
            Header = "行 / 列", Width = 90,
            DisplayMemberBinding = new System.Windows.Data.Binding(nameof(GlobalSearchRow.Position))
        });
        view.Columns.Add(new GridViewColumn
        {
            Header = "正文 / 替换预览", Width = 480,
            DisplayMemberBinding = new System.Windows.Data.Binding(nameof(GlobalSearchRow.Preview))
        });
        results.View = view;
        results.MouseDoubleClick += (_, _) => OpenSelected();
        AutomationProperties.SetName(results, "全局搜索结果");
        Grid.SetRow(results, 4);
        panel.Children.Add(results);

        status.Name = "GlobalSearchStatus";
        status.Text = "搜索只读取本机文本；替换必须先预览并确认。";
        status.FontSize = 11;
        status.Foreground = FindBrush("MutedBrush");
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetName(status, "全局搜索状态");
        Grid.SetRow(status, 5);
        panel.Children.Add(status);
        Content = panel;
    }

    private Brush FindBrush(string key) =>
        TryFindResource(key) as Brush ?? Brushes.Gray;

    private void Invalidate(string message = "条件已改变，请重新搜索或预览。")
    {
        if (applying) return;
        CancelSearch();
        current = null;
        results.ItemsSource = null;
        applyButton.IsEnabled = false;
        cancelButton.IsEnabled = false;
        searchButton.IsEnabled = true;
        previewButton.IsEnabled = true;
        status.Text = message;
        status.ToolTip = null;
    }

    private void CancelSearch()
    {
        generation++;
        searchCancellation?.Cancel();
        searchCancellation?.Dispose();
        searchCancellation = null;
    }

    private async Task RunSearchAsync(bool previewOnly)
    {
        if (query.Text.Length == 0)
        {
            Invalidate("请输入搜索内容。");
            return;
        }
        if (applying) return;
        Invalidate();
        long token = generation;
        var cancellation = new CancellationTokenSource();
        searchCancellation = cancellation;
        searchButton.IsEnabled = false;
        previewButton.IsEnabled = false;
        applyButton.IsEnabled = false;
        cancelButton.IsEnabled = true;
        status.Text = previewOnly ? "正在生成替换预览…" : "正在搜索…";
        try
        {
            var result = await new GlobalSearchEngine().SearchAsync(
                root,
                query.Text,
                new GlobalSearchOptions(
                    FileNameContains: fileFilter.Text,
                    Search: new SearchOptions(
                        caseSensitive.IsChecked == true,
                        regex.IsChecked == true,
                        wholeWord.IsChecked == true),
                    Replacement: replacement.Text,
                    BuildReplacementPreview: previewOnly),
                cancellation.Token);
            if (token != generation || cancellation.IsCancellationRequested) return;
            current = result;
            if (previewOnly)
            {
                var proposals = result.Files.Where(file => file.Preview is not null).ToArray();
                results.ItemsSource = proposals.Select(file => new GlobalSearchRow(
                    file.RelativePath,
                    $"{file.MatchCount} 处",
                    "双击查看替换前后内容",
                    null,
                    file.Preview)).ToArray();
                int matches = proposals.Sum(file => file.MatchCount);
                status.Text = $"预览 {proposals.Length:N0} 个文件，{matches:N0} 处。双击查看前后内容；尚未改写磁盘。";
                applyButton.IsEnabled = proposals.Length > 0;
            }
            else
            {
                var files = result.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
                results.ItemsSource = result.Matches.Select(match =>
                {
                    files.TryGetValue(match.Path, out var file);
                    return new GlobalSearchRow(
                        file?.RelativePath ?? Path.GetFileName(match.Path),
                        $"{match.LineNumber}:{match.ColumnNumber}",
                        match.Preview,
                        match,
                        null);
                }).ToArray();
                status.Text = $"找到 {result.Matches.Count:N0} 处。双击打开并定位。";
            }
            if (result.Warnings.Count > 0)
                status.ToolTip = string.Join("\n", result.Warnings);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (token == generation) status.Text = "已取消搜索。";
        }
        catch (Exception ex)
        {
            if (token != generation) return;
            current = null;
            results.ItemsSource = null;
            applyButton.IsEnabled = false;
            status.Text = ex.Message;
        }
        finally
        {
            if (token == generation)
            {
                if (ReferenceEquals(searchCancellation, cancellation))
                    searchCancellation = null;
                cancellation.Dispose();
                searchButton.IsEnabled = true;
                previewButton.IsEnabled = true;
                cancelButton.IsEnabled = false;
            }
        }
    }

    private async Task ApplyReplacementAsync()
    {
        if (applying || current is null) return;
        var proposals = current.Files.Where(file => file.Preview is { Applied: false }).ToArray();
        if (proposals.Length == 0)
        {
            status.Text = "没有可应用的替换预览，请先点击“预览替换”。";
            return;
        }
        var permitted = proposals.Where(file => canReplaceFile(file.Path)).ToArray();
        int skipped = proposals.Length - permitted.Length;
        if (permitted.Length == 0)
        {
            status.Text = "所有文件都含有未保存正文，未执行替换。";
            return;
        }
        string detail = "将改写磁盘文本。替换前会重新检查文件版本；冲突文件会保留。" +
            (skipped > 0 ? $" 另有 {skipped:N0} 个未保存文件跳过。" : "");
        if (MessageBox.Show(this, $"应用 {permitted.Length:N0} 个文件的替换？\n\n{detail}",
            "确认全局替换", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        applying = true;
        SetControlsEnabled(false);
        status.Text = "正在应用替换…";
        int applied = 0;
        var errors = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var file in permitted)
                {
                    try
                    {
                        file.Preview!.Apply();
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{file.RelativePath}：{ex.Message}");
                    }
                }
            });
        }
        finally
        {
            applying = false;
            current = null;
            results.ItemsSource = null;
            SetControlsEnabled(true);
            Invalidate();
            status.Text = $"已替换 {applied:N0} 个文件；跳过 {skipped:N0}，失败/冲突 {errors.Count:N0}。";
            status.ToolTip = errors.Count == 0 ? null : string.Join("\n", errors);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        query.IsEnabled = replacement.IsEnabled = fileFilter.IsEnabled = enabled;
        caseSensitive.IsEnabled = regex.IsEnabled = wholeWord.IsEnabled = enabled;
        searchButton.IsEnabled = previewButton.IsEnabled = enabled;
        applyButton.IsEnabled = cancelButton.IsEnabled = enabled;
    }

    private void OpenSelected()
    {
        if (results.SelectedItem is not GlobalSearchRow row) return;
        if (row.Proposal is { } proposal)
        {
            ShowReplacementDetail(proposal);
            return;
        }
        if (row.Match is { } match) openResult(match);
    }

    private void ShowReplacementDetail(GlobalSearchReplacementPreview proposal)
    {
        detailWindows.RemoveAll(window => !window.IsVisible);
        string relative = proposal.Document.Path is { } path
            ? Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')
            : "(未保存)";
        var detail = new Window
        {
            Owner = this,
            Title = "替换预览 · " + relative,
            Width = 720,
            Height = 500,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Icon
        };
        foreach (var key in Resources.Keys) detail.Resources[key] = Resources[key];
        var view = new TextBox
        {
            Name = "GlobalSearchReplacementDetailText",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Cascadia Mono"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Text = "替换前：\n" + PrefixTextElements(proposal.OriginalText, 100_000) +
                "\n\n替换后：\n" + PrefixTextElements(proposal.ReplacementText, 100_000)
        };
        AutomationProperties.SetName(view, "替换前后内容");
        detail.Content = view;
        detail.Closed += (_, _) => detailWindows.Remove(detail);
        detailWindows.Add(detail);
        detail.Show();
        detail.Activate();
    }

    private static string PrefixTextElements(string text, int maximumElements)
    {
        if (maximumElements <= 0 || text.Length == 0) return "";
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        int elements = 0;
        int end = 0;
        while (enumerator.MoveNext())
        {
            elements++;
            end = enumerator.ElementIndex + ((string)enumerator.Current).Length;
            if (elements >= maximumElements) break;
        }
        return text[..end];
    }

    private sealed record GlobalSearchRow(
        string File,
        string Position,
        string Preview,
        GlobalSearchMatch? Match,
        GlobalSearchReplacementPreview? Proposal);
}
