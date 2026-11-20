using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Qingyue.Core;

namespace Qingyue.Windows;

public sealed record WorkspaceTypeOption(string Name, WorkspaceFileFilter Filter)
{
    public static IReadOnlyList<WorkspaceTypeOption> All { get; } =
    [
        new("全部", WorkspaceFileFilter.All),
        new("文本", WorkspaceFileFilter.Text),
        new("Markdown", WorkspaceFileFilter.Markdown),
        new("代码", WorkspaceFileFilter.Code),
        new("数据", WorkspaceFileFilter.Data),
        new("配置", WorkspaceFileFilter.Configuration)
    ];
}

public sealed class WorkspaceNode : INotifyPropertyChanged
{
    public WorkspaceNode(string fullPath, bool isDirectory, TextKind? kind, long length, bool isRoot = false)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Kind = kind;
        Length = length;
        IsRoot = isRoot;
        Name = isRoot ? "工作区 · " + fullPath : Path.GetFileName(fullPath);
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public bool IsRoot { get; }
    public TextKind? Kind { get; }
    public long Length { get; }
    public ObservableCollection<WorkspaceNode> Children { get; } = [];
    public bool IsFavorite { get; private set; }
    public string DisplayName => (IsFavorite ? "★ " : "") + Name;
    public string MetaText => IsRoot ? "默认忽略 bin、obj、.git、.vs"
        : IsDirectory ? "目录" : $"{(Kind is { } value ? LanguageCatalog.DisplayName(value) : "文件")} · {FormatSize(Length)}";
    public string FavoriteMenuText => IsFavorite ? "取消收藏目录" : "收藏目录";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetFavorite(bool value)
    {
        if (!IsDirectory || IsFavorite == value) return;
        IsFavorite = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteMenuText)));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.#} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024d / 1024d:0.#} MB";
        return $"{bytes / 1024d / 1024d / 1024d:0.#} GB";
    }
}

public partial class MainWindow
{
    private long workspaceGeneration;
    private CancellationTokenSource? workspaceScanCancellation;

    public async Task OpenWorkspaceAsync()
    {
        var dialog = new OpenFolderDialog { Title = "打开本地工作区" };
        if (Modal(() => dialog.ShowDialog()) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;
        string root = Path.GetFullPath(dialog.FolderName);
        settings.WorkspaceRoot = root;
        await ScanWorkspaceAsync(root);
        SchedulePersist();
    }

    private async Task ScanWorkspaceAsync(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        long generation = ++workspaceGeneration;
        workspaceScanCancellation?.Cancel();
        workspaceScanCancellation?.Dispose();
        workspaceScanCancellation = new CancellationTokenSource();
        try
        {
            var filter = WorkspaceTypeFilter.SelectedItem as WorkspaceTypeOption;
            var result = await workspaceScanner.ScanAsync(fullRoot, new WorkspaceScanOptions(
                WorkspaceFilter.Text, filter?.Filter ?? WorkspaceFileFilter.All), workspaceScanCancellation.Token);
            if (generation != workspaceGeneration) return;
            WorkspaceTree.ItemsSource = BuildWorkspaceRoots(result);
            StatusText.Text = result.Warnings.Count == 0
                ? $"工作区已加载：{result.Entries.Count(item => !item.IsDirectory):N0} 个文件"
                : $"工作区已加载；{result.Warnings[0]}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == workspaceGeneration)
            {
                WorkspaceTree.ItemsSource = null;
                StatusText.Text = "工作区读取失败：" + ex.Message;
            }
        }
    }

    private IReadOnlyList<WorkspaceNode> BuildWorkspaceRoots(WorkspaceScanResult result)
    {
        var root = new WorkspaceNode(result.Root, true, null, 0, isRoot: true);
        var nodes = new Dictionary<string, WorkspaceNode>(StringComparer.OrdinalIgnoreCase)
        {
            [result.Root] = root
        };
        foreach (var entry in result.Entries.OrderBy(item => item.RelativePath.Count(value => value == '/'))
                     .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            string parentPath = Path.GetDirectoryName(entry.Path) ?? result.Root;
            if (!nodes.TryGetValue(parentPath, out var parent))
            {
                parent = EnsureDirectoryNode(parentPath, result.Root, nodes);
            }
            var node = new WorkspaceNode(entry.Path, entry.IsDirectory, entry.Kind, entry.Length);
            node.SetFavorite(entry.IsDirectory && IsFavoriteDirectory(entry.Path));
            parent.Children.Add(node);
            nodes[entry.Path] = node;
        }
        SortWorkspaceTree(root);
        return [root];
    }

    private static WorkspaceNode EnsureDirectoryNode(
        string path, string rootPath, Dictionary<string, WorkspaceNode> nodes)
    {
        if (nodes.TryGetValue(path, out var existing)) return existing;
        string parentPath = Path.GetDirectoryName(path) ?? rootPath;
        var parent = EnsureDirectoryNode(parentPath, rootPath, nodes);
        var node = new WorkspaceNode(path, true, null, 0);
        parent.Children.Add(node);
        nodes[path] = node;
        return node;
    }

    private static void SortWorkspaceTree(WorkspaceNode node)
    {
        var sorted = node.Children.OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        node.Children.Clear();
        foreach (var child in sorted)
        {
            node.Children.Add(child);
            SortWorkspaceTree(child);
        }
    }

    private bool IsFavoriteDirectory(string path) =>
        settings.FavoriteDirectories.Any(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));

    private void WorkspaceFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (settings.WorkspaceRoot is { } root && initialized) _ = ScanWorkspaceAsync(root);
    }

    private void WorkspaceTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (settings.WorkspaceRoot is { } root && initialized) _ = ScanWorkspaceAsync(root);
    }

    private async void WorkspaceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WorkspaceTree.SelectedItem is WorkspaceNode { IsDirectory: false } node)
            await OpenPathsAsync([node.FullPath]);
        e.Handled = true;
    }

    private void WorkspaceFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Parent is not ContextMenu menu ||
            menu.PlacementTarget is not FrameworkElement target ||
            target.DataContext is not WorkspaceNode { IsDirectory: true } node) return;
        bool favorite = !IsFavoriteDirectory(node.FullPath);
        settings.FavoriteDirectories.RemoveAll(path =>
            string.Equals(path, node.FullPath, StringComparison.OrdinalIgnoreCase));
        if (favorite) settings.FavoriteDirectories.Insert(0, node.FullPath);
        node.SetFavorite(favorite);
        SchedulePersist();
        e.Handled = true;
    }

    public sealed record ReadingListItem(ReadingListEntry Entry)
    {
        public string Path => Entry.Path;
        public string Title => System.IO.Path.GetFileName(Entry.Path);
        public string Folder => System.IO.Path.GetDirectoryName(Entry.Path) ?? "";
        public string StatusText => $"{(Entry.IsPinned ? "已固定 · " : "")}{(Entry.IsRead ? "已读" : "未读")} · 进度 {Entry.ProgressPercent}%";
        public string PinMenuText => Entry.IsPinned ? "取消固定" : "固定到顶部";
        public string ReadMenuText => Entry.IsRead ? "标为未读" : "标为已读";
    }

    public void RefreshReadingList()
    {
        if (ReadingList is null) return;
        ReadingList.ItemsSource = readingList.Items.Select(item => new ReadingListItem(item)).ToArray();
    }

    private async void ReadingListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ReadingList.SelectedItem is ReadingListItem item)
        {
            await OpenPathsAsync([item.Path]);
            e.Handled = true;
        }
    }

    private async void ReadingListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ReadingList.SelectedItem is ReadingListItem item)
        {
            e.Handled = true;
            await OpenPathsAsync([item.Path]);
        }
    }

    private void ReadingListAction(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Parent is not ContextMenu menu ||
            menu.PlacementTarget is not FrameworkElement target ||
            target.DataContext is not ReadingListItem listItem ||
            item.Tag is not string action) return;
        bool changed = action switch
        {
            "pin" => readingList.SetPinned(listItem.Path, !listItem.Entry.IsPinned),
            "read" => readingList.MarkRead(listItem.Path, !listItem.Entry.IsRead),
            "remove" => readingList.Remove(listItem.Path),
            _ => false
        };
        if (changed)
        {
            readingListDirty = true;
            RefreshReadingList();
            SchedulePersist();
        }
        e.Handled = true;
    }

    private void UpdateReadingListProgress(EditorTab tab)
    {
        if (!initialized || tab.File.Path is not { } path || readingList.Find(path) is not { } item) return;
        int anchor = Math.Clamp(CurrentSourceAnchor(tab), 0, tab.File.Text.Length);
        int progress = ProgressPercent(tab);
        bool read = item.IsRead || progress >= 95;
        if (!readingList.UpdateProgress(path, progress, anchor, read)) return;
        readingListDirty = true;
        if (CurrentTab == tab) RefreshReadingList();
    }

    private async Task RestoreReadingListPositionAsync(EditorTab tab)
    {
        if (tab.File.Path is not { } path || readingList.Find(path) is not { } item) return;
        int anchor = Math.Clamp(item.SourceAnchor, 0, tab.File.Text.Length);
        if (tab.Kind == TextKind.Markdown && !tab.IsReading)
        {
            await EnterReadingAsync(tab, anchor, focus: false);
        }
        else if (tab.IsReading)
        {
            RestoreReadingViewport(tab, anchor, 0);
        }
        else if (anchor > 0)
        {
            tab.Editor.CaretIndex = EditorOffset(tab, anchor);
            ScrollToCaret(tab);
        }
    }
}
