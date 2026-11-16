using System.Text;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private sealed record GlobalSearchListItem(GlobalSearchMatch Match)
    {
        public string DisplayText =>
            $"{System.IO.Path.GetFileName(Match.Path)}  ·  第 {Match.LineNumber} 行，第 {Match.ColumnNumber} 列  ·  {Match.LineText.Trim()}";
        public string Path => Match.Path;
    }

    private async Task ShowGlobalSearchAsync()
    {
        string root = settings.WorkspaceRoot ?? "";
        if (!Directory.Exists(root))
        {
            await OpenWorkspaceAsync();
            root = settings.WorkspaceRoot ?? "";
        }
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Notify("请先打开一个本地工作区。");
            return;
        }

        var dialog = Dialogs.Shell(this, "全局搜索", 920);
        var query = new TextBox { MinWidth = 420, ToolTip = "搜索内容" };
        var filePattern = new TextBox { Text = "*", Width = 180, ToolTip = "文件名过滤，例如 *.md;*.txt" };
        var replacement = new TextBox { Width = 240, ToolTip = "替换内容；正则捕获组使用 $1" };
        var caseOption = new CheckBox { Content = "区分大小写", Margin = new Thickness(12, 0, 0, 0) };
        var regexOption = new CheckBox { Content = "正则", Margin = new Thickness(12, 0, 0, 0) };
        var wordOption = new CheckBox { Content = "全词", Margin = new Thickness(12, 0, 0, 0) };
        var searchButton = new Button { Content = "搜索", IsDefault = true, Padding = new Thickness(14, 4, 14, 4) };
        var previewButton = new Button { Content = "预览替换", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        var applyButton = new Button { Content = "应用替换", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        var closeButton = new Button { Content = "关闭", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var status = new TextBlock { Foreground = FindResource("MutedBrush") as System.Windows.Media.Brush };
        var results = new ListBox { Height = 270, Margin = new Thickness(0, 10, 0, 10) };
        results.DisplayMemberPath = nameof(GlobalSearchListItem.DisplayText);
        var preview = new TextBox
        {
            Height = 150, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        GlobalSearchResult? current = null;
        string currentQuery = "";
        string currentReplacement = "";
        SearchOptions currentOptions = new();

        var panel = new Grid { Margin = new Thickness(22) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var queryRow = new Grid();
        queryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        queryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        queryRow.Children.Add(new TextBlock { Text = "搜索", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        Grid.SetColumn(query, 1);
        queryRow.Children.Add(query);
        panel.Children.Add(queryRow);

        var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        filterRow.Children.Add(new TextBlock { Text = "文件名", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        filterRow.Children.Add(filePattern);
        filterRow.Children.Add(caseOption);
        filterRow.Children.Add(regexOption);
        filterRow.Children.Add(wordOption);
        Grid.SetRow(filterRow, 1);
        panel.Children.Add(filterRow);

        var replacementRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        replacementRow.Children.Add(new TextBlock { Text = "替换为", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        replacementRow.Children.Add(replacement);
        replacementRow.Children.Add(searchButton);
        replacementRow.Children.Add(previewButton);
        replacementRow.Children.Add(applyButton);
        replacementRow.Children.Add(closeButton);
        Grid.SetRow(replacementRow, 2);
        panel.Children.Add(replacementRow);

        Grid.SetRow(status, 3);
        status.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(status);
        Grid.SetRow(results, 4);
        panel.Children.Add(results);
        var contextLabel = new TextBlock
        {
            Text = "匹配项（双击打开并跳到行）", Foreground = FindResource("MutedBrush") as System.Windows.Media.Brush,
            Margin = new Thickness(0, 10, 0, 0)
        };
        Grid.SetRow(contextLabel, 5);
        panel.Children.Add(contextLabel);
        Grid.SetRow(preview, 6);
        preview.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(preview);
        var hint = new TextBlock
        {
            Text = "替换会在重新读取磁盘文件后再次搜索，文件发生变化时不会使用旧结果。",
            FontSize = 10, Foreground = FindResource("MutedBrush") as System.Windows.Media.Brush,
            Margin = new Thickness(0, 8, 0, 0)
        };
        Grid.SetRow(hint, 7);
        panel.Children.Add(hint);
        dialog.Content = panel;

        async Task RunSearch()
        {
            if (string.IsNullOrEmpty(query.Text))
            {
                current = null;
                results.ItemsSource = null;
                preview.Text = "";
                status.Text = "请输入搜索内容。";
                previewButton.IsEnabled = applyButton.IsEnabled = false;
                return;
            }
            searchButton.IsEnabled = previewButton.IsEnabled = applyButton.IsEnabled = false;
            status.Text = "正在搜索…";
            try
            {
                currentQuery = query.Text;
                currentReplacement = replacement.Text;
                currentOptions = new SearchOptions(caseOption.IsChecked == true, regexOption.IsChecked == true, wordOption.IsChecked == true);
                current = await new GlobalSearchEngine().SearchAsync(root, currentQuery, new GlobalSearchOptions(
                    FileNamePattern: filePattern.Text, Search: currentOptions, Replacement: currentReplacement));
                results.ItemsSource = current.Matches.Select(item => new GlobalSearchListItem(item)).ToArray();
                status.Text = $"找到 {current.Matches.Count:N0} 个匹配，扫描 {current.FilesScanned:N0} 个文件" +
                    (current.Warnings.Count == 0 ? "" : $"；{current.Warnings.Count:N0} 个文件被跳过");
                preview.Text = currentReplacement.Length == 0
                    ? "已生成搜索结果。输入替换内容后点击“预览替换”。"
                    : BuildReplacementPreview(current);
                previewButton.IsEnabled = current.Files.Count > 0;
                applyButton.IsEnabled = current.Files.Count > 0;
            }
            catch (Exception ex)
            {
                current = null;
                results.ItemsSource = null;
                preview.Text = "";
                status.Text = "搜索失败：" + ex.Message;
            }
            finally { searchButton.IsEnabled = true; }
        }

        async Task ApplyReplacementAsync()
        {
            if (current is null || current.Files.Count == 0) return;
            var dirtyFiles = current.Files
                .Select(file => tabs.FirstOrDefault(tab =>
                    string.Equals(tab.File.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                .Where(tab => tab is not null && tab.File.IsDirty)
                .Select(tab => tab!.File.Path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (dirtyFiles.Length > 0)
            {
                status.Text = "全局替换已停止：请先保存当前标签中的未保存编辑。" +
                    (dirtyFiles.Length == 1 ? $"\n{dirtyFiles[0]}" : $"\n共 {dirtyFiles.Length:N0} 个文件");
                return;
            }
            if (Modal(() => MessageBox.Show(this,
                $"将修改 {current.Files.Count:N0} 个文件，是否继续？\n\n应用前会重新读取并重新匹配。",
                "确认全局替换", MessageBoxButton.YesNo, MessageBoxImage.Warning)) != MessageBoxResult.Yes) return;
            var errors = new List<string>();
            int changed = 0;
            foreach (var file in current.Files)
            {
                try
                {
                    var document = DocumentFile.Open(file.Path);
                    var found = SearchEngine.Find(document.Text, currentQuery, currentReplacement, currentOptions);
                    string after = SearchEngine.ReplaceAll(document.Text, found);
                    if (after == document.Text) continue;
                    document.Text = after;
                    document.Save();
                    changed++;
                }
                catch (Exception ex) { errors.Add($"{file.Path}：{ex.Message}"); }
            }
            status.Text = errors.Count == 0
                ? $"已替换 {changed:N0} 个文件。"
                : $"已替换 {changed:N0} 个文件，{errors.Count:N0} 个文件失败。";
            if (errors.Count > 0) preview.Text = string.Join("\n", errors.Take(20));
            await RunSearch();
        }

        previewButton.Click += async (_, _) =>
        {
            await RunSearch();
            if (current is not null) preview.Text = BuildReplacementPreview(current);
        };
        applyButton.Click += async (_, _) => await ApplyReplacementAsync();
        searchButton.Click += async (_, _) => await RunSearch();
        replacement.TextChanged += (_, _) =>
        {
            applyButton.IsEnabled = false;
            previewButton.IsEnabled = current?.Files.Count > 0;
        };
        replacement.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await RunSearch(); }
        };
        closeButton.Click += (_, _) => dialog.DialogResult = true;
        results.MouseDoubleClick += async (_, _) =>
        {
            if (results.SelectedItem is GlobalSearchListItem item)
                await OpenGlobalSearchMatchAsync(item.Match);
        };
        dialog.Loaded += (_, _) => query.Focus();
        Modal(() => dialog.ShowDialog());
    }

    private string BuildReplacementPreview(GlobalSearchResult result)
    {
        var builder = new StringBuilder();
        foreach (var file in result.Files)
        {
            builder.AppendLine(file.Path);
            builder.AppendLine($"  命中 {file.MatchCount:N0} 处");
            if (file.ReplacementText is { } text)
            {
                builder.AppendLine("  替换后预览：");
                builder.AppendLine(text.Length <= 1200 ? text : text[..1200] + "\n…");
            }
            builder.AppendLine();
            if (builder.Length > 120_000)
            {
                builder.AppendLine("…预览已截断");
                break;
            }
        }
        return builder.ToString();
    }

    private async Task OpenGlobalSearchMatchAsync(GlobalSearchMatch match)
    {
        await OpenPathsAsync([match.Path]);
        if (CurrentTab is not { } tab) return;
        if (tab.IsReading) ToggleReading();
        int line = 1, offset = 0;
        while (offset < tab.File.Text.Length && line < match.LineNumber)
            if (tab.File.Text[offset++] == '\n') line++;
        offset = Math.Clamp(offset + match.ColumnNumber - 1, 0, tab.File.Text.Length);
        tab.Editor.CaretIndex = EditorOffset(tab, offset);
        tab.Editor.Select(tab.Editor.CaretIndex, Math.Min(match.Value.Length, tab.Editor.Text.Length - tab.Editor.CaretIndex));
        ScrollToCaret(tab);
        tab.Editor.Focus();
    }
}
