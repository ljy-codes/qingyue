using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Qingyue.Core;

namespace Qingyue.Windows;

/// <summary>
/// Native, non-modal workspace browser matching the macOS WorkspaceBrowserWindow.
/// A scan is cached until the user asks to rescan; filtering only rebuilds the tree.
/// </summary>
public sealed class WorkspaceBrowserWindow : Window
{
    private const int MaximumFavorites = 50;
    private readonly ReaderSettings settings;
    private readonly Action<string> rootChanged;
    private readonly Action saveSettings;
    private readonly Action<string> openFile;
    private readonly LocalWorkspaceScanner scanner = new();
    private readonly TreeView tree = new();
    private readonly TextBox nameFilter = new();
    private readonly TextBox typeFilter = new();
    private readonly ComboBox favorites = new();
    private readonly Button favoriteButton = new() { Content = "收藏目录" };
    private readonly TextBlock rootLabel = new();
    private readonly TextBlock status = new();
    private readonly DispatcherTimer filterTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly CancellationTokenSource lifetime = new();
    private IReadOnlyList<WorkspaceFileEntry> files = [];
    private string root;
    private long generation;
    private bool updatingFavorites;

    public WorkspaceBrowserWindow(
        Window owner,
        ReaderSettings settings,
        string root,
        Action<string> rootChanged,
        Action saveSettings,
        Action<string> openFile)
    {
        this.settings = settings;
        this.rootChanged = rootChanged;
        this.saveSettings = saveSettings;
        this.openFile = openFile;
        this.root = Path.GetFullPath(root);

        Owner = owner;
        Title = "本地工作区";
        Width = 720;
        Height = 560;
        MinWidth = 540;
        MinHeight = 360;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = owner.Icon;
        Name = "WorkspaceBrowser";
        AutomationProperties.SetName(this, "本地工作区窗口");
        foreach (var key in owner.Resources.Keys) Resources[key] = owner.Resources[key];

        Build();
        filterTimer.Tick += (_, _) =>
        {
            filterTimer.Stop();
            ApplyFilters();
        };
        Closed += (_, _) =>
        {
            filterTimer.Stop();
            lifetime.Cancel();
            lifetime.Dispose();
        };
        RefreshFavorites();
        _ = ScanAsync(this.root, notifyRootChange: false);
    }

    public string CurrentRoot => root;
    public string StatusText => status.Text;

    private void Build()
    {
        var panel = new Grid { Margin = new Thickness(12) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        var choose = new Button
        {
            Name = "WorkspaceChooseButton",
            Content = "打开文件夹…",
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(choose, "打开文件夹");
        choose.Click += async (_, _) => await ChooseRootAsync();
        var rescan = new Button
        {
            Name = "WorkspaceRescanButton",
            Content = "重新扫描",
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(rescan, "重新扫描工作区");
        rescan.Click += async (_, _) => await ScanAsync(root, notifyRootChange: false);
        favoriteButton.Name = "WorkspaceFavoriteButton";
        favoriteButton.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetName(favoriteButton, "收藏目录");
        favoriteButton.Click += (_, _) => ToggleFavorite();
        favorites.Name = "WorkspaceFavoritePicker";
        favorites.MinWidth = 180;
        favorites.DisplayMemberPath = nameof(WorkspaceFavoriteOption.Label);
        AutomationProperties.SetName(favorites, "收藏目录");
        favorites.SelectionChanged += (_, _) => SelectFavorite();
        toolbar.Children.Add(choose);
        toolbar.Children.Add(rescan);
        toolbar.Children.Add(favoriteButton);
        toolbar.Children.Add(favorites);
        panel.Children.Add(toolbar);

        rootLabel.Name = "WorkspaceRootLabel";
        rootLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        rootLabel.Margin = new Thickness(0, 8, 0, 0);
        rootLabel.Foreground = FindBrush("MutedBrush");
        AutomationProperties.SetName(rootLabel, "当前工作区");
        Grid.SetRow(rootLabel, 1);
        panel.Children.Add(rootLabel);

        var filters = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        filters.ColumnDefinitions.Add(new ColumnDefinition());
        filters.ColumnDefinitions.Add(new ColumnDefinition());
        nameFilter.Name = "WorkspaceNameFilter";
        nameFilter.ToolTip = "筛选文件名或路径";
        nameFilter.Margin = new Thickness(0, 0, 6, 0);
        AutomationProperties.SetName(nameFilter, "工作区文件名过滤");
        nameFilter.TextChanged += (_, _) => QueueFilter();
        typeFilter.Name = "WorkspaceTypeFilter";
        typeFilter.ToolTip = "类型，例如 md,txt,json";
        typeFilter.Margin = new Thickness(6, 0, 0, 0);
        AutomationProperties.SetName(typeFilter, "工作区类型过滤");
        typeFilter.TextChanged += (_, _) => QueueFilter();
        filters.Children.Add(nameFilter);
        Grid.SetColumn(typeFilter, 1);
        filters.Children.Add(typeFilter);
        Grid.SetRow(filters, 2);
        panel.Children.Add(filters);

        tree.Name = "WorkspaceTree";
        tree.Margin = new Thickness(0, 10, 0, 0);
        tree.BorderBrush = FindBrush("BorderBrush");
        tree.BorderThickness = new Thickness(1);
        tree.ItemTemplate = BuildNodeTemplate();
        tree.ItemContainerStyle = BuildTreeItemStyle();
        tree.MouseDoubleClick += (_, e) =>
        {
            if (tree.SelectedItem is WorkspaceNode node)
            {
                if (node.IsDirectory)
                {
                    node.IsExpanded = !node.IsExpanded;
                    if (tree.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem item)
                        item.IsExpanded = node.IsExpanded;
                }
                else
                {
                    openFile(node.FullPath);
                }
            }
            e.Handled = true;
        };
        AutomationProperties.SetName(tree, "工作区文件树");
        Grid.SetRow(tree, 3);
        panel.Children.Add(tree);

        status.Name = "WorkspaceStatus";
        status.Margin = new Thickness(0, 8, 0, 0);
        status.FontSize = 11;
        status.Foreground = FindBrush("MutedBrush");
        status.Text = "正在扫描…";
        AutomationProperties.SetName(status, "工作区状态");
        Grid.SetRow(status, 4);
        panel.Children.Add(status);
        Content = panel;
    }

    private static HierarchicalDataTemplate BuildNodeTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(WorkspaceNode.DisplayName)));
        text.SetBinding(FrameworkElement.ToolTipProperty,
            new System.Windows.Data.Binding(nameof(WorkspaceNode.FullPath)));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        return new HierarchicalDataTemplate(typeof(WorkspaceNode))
        {
            ItemsSource = new System.Windows.Data.Binding(nameof(WorkspaceNode.Children)),
            VisualTree = text
        };
    }

    private static Style BuildTreeItemStyle()
    {
        var style = new Style(typeof(TreeViewItem));
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,
            new System.Windows.Data.Binding(nameof(WorkspaceNode.IsExpanded))
            {
                Mode = System.Windows.Data.BindingMode.TwoWay
            }));
        return style;
    }

    private System.Windows.Media.Brush FindBrush(string key) =>
        TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;

    private async Task ChooseRootAsync()
    {
        var dialog = new OpenFolderDialog { Title = "打开工作区" };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;
        await ScanAsync(dialog.FolderName, notifyRootChange: true);
    }

    private async Task ScanAsync(string path, bool notifyRootChange)
    {
        long token = Interlocked.Increment(ref generation);
        try
        {
            root = Path.GetFullPath(path);
            rootLabel.Text = root;
            rootLabel.ToolTip = root;
            status.Text = "正在扫描…";
            RefreshFavorites();
            var result = await scanner.ScanAsync(root, cancellationToken: lifetime.Token);
            if (token != Interlocked.Read(ref generation) || lifetime.IsCancellationRequested) return;
            files = result.Entries.Where(entry => !entry.IsDirectory).ToArray();
            ApplyFilters();
            if (notifyRootChange) rootChanged(root);
            if (result.Warnings.Count > 0)
                status.Text += " · " + result.Warnings[0];
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token != Interlocked.Read(ref generation) || lifetime.IsCancellationRequested) return;
            files = [];
            tree.ItemsSource = null;
            status.Text = ex.Message;
        }
    }

    private void QueueFilter()
    {
        filterTimer.Stop();
        filterTimer.Start();
    }

    private void ApplyFilters()
    {
        string query = nameFilter.Text.Trim();
        var extensions = typeFilter.Text.ToLowerInvariant()
            .Split([',', ';', ' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim('.'))
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filtered = files.Where(file =>
                (query.Length == 0 || file.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
                (extensions.Count == 0 || extensions.Contains(Path.GetExtension(file.Path).TrimStart('.'))))
            .ToArray();
        var roots = BuildRoots(filtered, expand: query.Length > 0 || extensions.Count > 0);
        tree.ItemsSource = roots;
        status.Text = $"显示 {filtered.Length} / {files.Count} 个文件";
    }

    private IReadOnlyList<WorkspaceNode> BuildRoots(
        IReadOnlyList<WorkspaceFileEntry> entries, bool expand)
    {
        var roots = new List<WorkspaceNode>();
        var directories = new Dictionary<string, WorkspaceNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OrderBy(item => item.RelativePath, NaturalOrder.Instance))
        {
            string[] parts = entry.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            WorkspaceNode? parent = null;
            string relative = "";
            for (int index = 0; index < parts.Length - 1; index++)
            {
                relative = relative.Length == 0 ? parts[index] : relative + "/" + parts[index];
                if (!directories.TryGetValue(relative, out var directory))
                {
                    directory = new WorkspaceNode(
                        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)),
                        true, null, 0) { IsExpanded = expand };
                    directories[relative] = directory;
                    if (parent is null) roots.Add(directory);
                    else parent.Children.Add(directory);
                }
                parent = directory;
            }
            var file = new WorkspaceNode(
                entry.Path, false, entry.Kind, entry.Length) { IsExpanded = false };
            if (parent is null) roots.Add(file);
            else parent.Children.Add(file);
        }
        roots.Sort((left, right) => NaturalOrder.Instance.Compare(left.Name, right.Name));
        foreach (var node in roots) Sort(node);
        return roots;
    }

    private static void Sort(WorkspaceNode node)
    {
        var children = node.Children.OrderBy(item => item.Name, NaturalOrder.Instance).ToArray();
        node.Children.Clear();
        foreach (var child in children)
        {
            node.Children.Add(child);
            Sort(child);
        }
    }

    private void RefreshFavorites()
    {
        updatingFavorites = true;
        try
        {
            var options = new List<WorkspaceFavoriteOption> { new(null, "收藏目录…") };
            options.AddRange(settings.FavoriteDirectories
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumFavorites)
                .Select(path => new WorkspaceFavoriteOption(path, path)));
            favorites.ItemsSource = options;
            favorites.SelectedItem = options.FirstOrDefault(item =>
                string.Equals(item.Path, root, StringComparison.OrdinalIgnoreCase)) ?? options[0];
            bool isFavorite = settings.FavoriteDirectories.Contains(root, StringComparer.OrdinalIgnoreCase);
            favoriteButton.Content = isFavorite ? "取消收藏" : "收藏目录";
        }
        finally { updatingFavorites = false; }
    }

    private void ToggleFavorite()
    {
        bool favorite = !settings.FavoriteDirectories.Contains(root, StringComparer.OrdinalIgnoreCase);
        settings.FavoriteDirectories.RemoveAll(path =>
            string.Equals(path, root, StringComparison.OrdinalIgnoreCase));
        if (favorite)
        {
            if (settings.FavoriteDirectories.Count >= MaximumFavorites)
            {
                status.Text = $"最多收藏 {MaximumFavorites} 个目录，请先取消一个收藏。";
                return;
            }
            settings.FavoriteDirectories.Add(root);
        }
        saveSettings();
        RefreshFavorites();
        status.Text = favorite ? "已收藏当前工作区目录。" : "已取消收藏当前工作区目录。";
    }

    private async void SelectFavorite()
    {
        if (updatingFavorites ||
            favorites.SelectedItem is not WorkspaceFavoriteOption { Path: { } path }) return;
        await ScanAsync(path, notifyRootChange: true);
    }
}
