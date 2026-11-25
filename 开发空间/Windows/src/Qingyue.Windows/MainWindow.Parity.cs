using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    internal const string MarkdownImageFileFilter =
        "图片|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.gif;*.bmp;*.heic";
    private ReadingListWindow? readingListWindow;
    private AnnotationPanelWindow? annotationPanelWindow;
    private DocumentFile? annotationPanelDocument;

    private async Task ChooseMarkdownImagesAsync()
    {
        if (exitInProgress || modalDepth > 0 || CurrentTab is not { } tab ||
            !tab.MarkdownWriter.CanEdit)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "插入图片",
            Filter = MarkdownImageFileFilter,
            CheckFileExists = true,
            Multiselect = true
        };
        if (Modal(() => dialog.ShowDialog(this)) != true) return;
        await InsertMarkdownImageFilesAsync(dialog.FileNames.Where(IsSupportedImage).Take(8).ToArray());
    }

    private void ShowAnnotationPanel()
    {
        if (CurrentTab is not { } tab) return;
        if (annotationPanelWindow is null || !ReferenceEquals(annotationPanelDocument, tab.File))
        {
            annotationPanelWindow?.Close();
            annotationPanelDocument = tab.File;
            annotationPanelWindow = new AnnotationPanelWindow(
                this,
                () => annotations.For(tab.File),
                item => NavigateToAnnotation(item),
                item => EditAnnotation(item),
                (item, values) => ApplyAnnotationValues(item, values),
                item => RemoveAnnotation(item),
                async () => await SaveAnnotationsAsync(),
                tab.File.Path,
                async () => await ReloadAnnotationsAfterFailedSaveAsync());
        }
        annotationPanelWindow.UpdateDocument(tab.File.Path);
        annotationPanelWindow.Refresh();
        if (!annotationPanelWindow.IsVisible) annotationPanelWindow.Show();
        else annotationPanelWindow.Activate();
    }

    private void ShowCurrentBookmarks()
    {
        if (CurrentTab is not { } tab) return;
        var menu = new ContextMenu
        {
            PlacementTarget = ContextToolbar,
            Placement = PlacementMode.Bottom
        };
        foreach (var bookmark in bookmarks.For(tab.File))
        {
            var item = new MenuItem { Header = bookmark.Label, Tag = bookmark };
            item.Click += (_, _) =>
            {
                if (item.Tag is DocumentBookmark selected) NavigateToBookmark(selected);
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "暂无书签 · Ctrl+D 添加", IsEnabled = false });
        ContextToolbar.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ShowReadingList()
    {
        readingListWindow ??= new ReadingListWindow(
            this,
            () => readingList.Items,
            OpenReadingListEntryAsync,
            ToggleReadingListPinnedAsync,
            ToggleReadingListReadAsync,
            RemoveReadingListEntryAsync,
            async () => await SaveReadingListAsync());
        readingListWindow.Refresh();
        if (!readingListWindow.IsVisible) readingListWindow.Show();
        else readingListWindow.Activate();
    }

    private bool TakePendingFileAnchor(string path, out int anchor)
    {
        string fullPath = Path.GetFullPath(path);
        if (!pendingFileAnchors.Remove(fullPath, out anchor)) return false;
        anchor = Math.Max(0, anchor);
        return true;
    }

    private async Task OpenReadingListEntryAsync(ReadingListEntry entry)
    {
        if (!File.Exists(entry.Path))
        {
            Notify("文件不存在或当前无法访问：\n" + entry.Path, "阅读清单");
            return;
        }
        pendingFileAnchors[Path.GetFullPath(entry.Path)] = Math.Max(0, entry.SourceAnchor);
        await OpenPathsAsync([entry.Path]);
    }

    private async Task AddCurrentToReadingListAsync()
    {
        if (CurrentTab?.File.Path is not { } path)
        {
            Notify("未命名文档不能加入阅读清单，请先保存文档。", "阅读清单");
            return;
        }
        readingList.Upsert(path, ProgressPercent(CurrentTab), CurrentSourceAnchor(CurrentTab), isPinned: true);
        readingListDirty = true;
        RefreshReadingList();
        var result = await SaveReadingListAsync();
        StatusText.Text = result.Errors.Count == 0
            ? "已加入阅读清单。"
            : "阅读清单保存失败 · " + result.Errors[0];
    }

    private async Task MarkCurrentReadAsync()
    {
        if (CurrentTab?.File.Path is not { } path) return;
        readingList.Upsert(path, 100, CurrentSourceAnchor(CurrentTab), isRead: true);
        readingListDirty = true;
        RefreshReadingList();
        var result = await SaveReadingListAsync();
        StatusText.Text = result.Errors.Count == 0
            ? "当前文档已标记为已读。"
            : "阅读清单保存失败 · " + result.Errors[0];
    }

    private async Task<ReadingListSaveResult> ToggleReadingListPinnedAsync(ReadingListEntry entry)
    {
        if (!readingList.SetPinned(entry.Path, !entry.IsPinned))
            return new ReadingListSaveResult(DateTimeOffset.UtcNow, []);
        readingListDirty = true;
        RefreshReadingList();
        return await SaveReadingListAsync();
    }

    private async Task<ReadingListSaveResult> ToggleReadingListReadAsync(ReadingListEntry entry)
    {
        if (!readingList.MarkRead(entry.Path, !entry.IsRead))
            return new ReadingListSaveResult(DateTimeOffset.UtcNow, []);
        readingListDirty = true;
        RefreshReadingList();
        return await SaveReadingListAsync();
    }

    private async Task<ReadingListSaveResult> RemoveReadingListEntryAsync(ReadingListEntry entry)
    {
        if (!readingList.Remove(entry.Path))
            return new ReadingListSaveResult(DateTimeOffset.UtcNow, []);
        readingListDirty = true;
        RefreshReadingList();
        return await SaveReadingListAsync();
    }

    private async Task ToggleCurrentDraftAsync()
    {
        if (CurrentTab is not { } tab) return;
        bool disabling = tab.RecoverDraft;
        if (disabling && Modal(() => MessageBox.Show(this,
            "排除此文档的恢复草稿？\n\n当前编辑内容仍会保留在窗口中，已保存的本机草稿会被清除。",
            "恢复草稿", MessageBoxButton.YesNo, MessageBoxImage.Warning)) != MessageBoxResult.Yes)
            return;

        bool previous = tab.RecoverDraft;
        tab.RecoverDraft = !previous;
        if (tab.File.Path is { } path)
        {
            settings.ExcludedDraftPaths.RemoveAll(item =>
                string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            if (!tab.RecoverDraft) settings.ExcludedDraftPaths.Add(path);
        }
        if (!tab.RecoverDraft)
        {
            try { await workspace.ClearDraftAsync(tab.File.Id); }
            catch (Exception ex)
            {
                tab.RecoverDraft = previous;
                if (tab.File.Path is { } failedPath)
                    settings.ExcludedDraftPaths.RemoveAll(item =>
                        string.Equals(item, failedPath, StringComparison.OrdinalIgnoreCase));
                Notify("当前文档草稿未能清除，开关未改变：\n" + ex.Message, "恢复草稿");
                return;
            }
        }
        var result = await PersistWorkspaceAsync();
        if (result.Errors.Count > 0) Notify(string.Join("\n", result.Errors), "恢复草稿");
        else
        {
            StatusText.Text = tab.RecoverDraft ? "已为当前文档开启恢复草稿。" : "已为当前文档关闭恢复草稿。";
            RefreshMenuState();
        }
    }

    private void ShowRecoveryDetails()
    {
        var messages = recoveryNotices.Count == 0
            ? ["本次启动没有可复查的恢复警告。"]
            : recoveryNotices.ToArray();
        Dialogs.Message(this, string.Join("\n\n", messages), "恢复详情");
    }

    private void RefreshCompanionWindows()
    {
        readingListWindow?.Refresh();
        annotationPanelWindow?.Refresh();
    }
}

internal sealed class ReadingListWindow : Window
{
    private readonly Func<IReadOnlyList<ReadingListEntry>> items;
    private readonly Func<ReadingListEntry, Task> open;
    private readonly Func<ReadingListEntry, Task<ReadingListSaveResult>> pin;
    private readonly Func<ReadingListEntry, Task<ReadingListSaveResult>> read;
    private readonly Func<ReadingListEntry, Task<ReadingListSaveResult>> remove;
    private readonly Func<Task<ReadingListSaveResult>> save;
    private readonly ListView list = new();
    private readonly TextBlock message = new();
    private readonly Button openButton = new() { Name = "ReadingListOpenButton", Content = "打开" };
    private readonly Button pinButton = new() { Name = "ReadingListPinButton", Content = "固定到顶部" };
    private readonly Button readButton = new() { Name = "ReadingListReadButton", Content = "标为已读" };
    private readonly Button removeButton = new() { Name = "ReadingListRemoveButton", Content = "移除记录" };
    private readonly Button retry = new() { Name = "ReadingListRetryButton", Content = "重试保存", IsEnabled = false };
    private bool unsaved;

    public ReadingListWindow(
        Window owner,
        Func<IReadOnlyList<ReadingListEntry>> items,
        Func<ReadingListEntry, Task> open,
        Func<ReadingListEntry, Task<ReadingListSaveResult>> pin,
        Func<ReadingListEntry, Task<ReadingListSaveResult>> read,
        Func<ReadingListEntry, Task<ReadingListSaveResult>> remove,
        Func<Task<ReadingListSaveResult>> save)
    {
        Owner = owner;
        Title = "阅读清单";
        Width = 820;
        Height = 520;
        MinWidth = 640;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = true;
        this.items = items;
        this.open = open;
        this.pin = pin;
        this.read = read;
        this.remove = remove;
        this.save = save;

        foreach (var key in owner.Resources.Keys) Resources[key] = owner.Resources[key];
        var root = new DockPanel { Margin = new Thickness(16) };
        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var explanation = new TextBlock
        {
            Text = "双击打开文件并继续阅读；移除记录不会删除原文件。",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var refresh = new Button { Content = "刷新", Margin = new Thickness(0, 0, 8, 0) };
        refresh.Click += (_, _) => Refresh();
        heading.Children.Add(explanation);
        heading.Children.Add(refresh);
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        openButton.Click += async (_, _) => await OpenSelectedAsync();
        pinButton.Click += async (_, _) => await ChangeSelectedAsync(pin);
        readButton.Click += async (_, _) => await ChangeSelectedAsync(read);
        removeButton.Click += async (_, _) => await ChangeSelectedAsync(remove);
        retry.Click += async (_, _) => await RetrySaveAsync();
        AutomationProperties.SetName(openButton, "打开阅读清单文件");
        AutomationProperties.SetName(pinButton, "固定或取消固定阅读清单文件");
        AutomationProperties.SetName(readButton, "切换阅读清单已读状态");
        AutomationProperties.SetName(removeButton, "移除阅读清单记录");
        AutomationProperties.SetName(retry, "重试保存阅读清单");
        foreach (Button button in new[] { openButton, pinButton, readButton, removeButton, retry })
        {
            button.Margin = new Thickness(0, 0, 8, 0);
            toolbar.Children.Add(button);
        }
        footer.Children.Add(toolbar);
        message.Margin = new Thickness(0, 8, 0, 0);
        message.VerticalAlignment = VerticalAlignment.Center;
        message.Name = "ReadingListStatus";
        message.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetName(message, "阅读清单状态");
        message.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        footer.Children.Add(message);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        list.Name = "ReadingListItems";
        list.View = new GridView();
        ((GridView)list.View).Columns.Add(new GridViewColumn { Header = "文件", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ReadingListRow.Title)) });
        ((GridView)list.View).Columns.Add(new GridViewColumn { Header = "文件夹", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ReadingListRow.Folder)) });
        ((GridView)list.View).Columns.Add(new GridViewColumn { Header = "固定", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ReadingListRow.PinnedText)) });
        ((GridView)list.View).Columns.Add(new GridViewColumn { Header = "状态", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ReadingListRow.StateText)) });
        ((GridView)list.View).Columns.Add(new GridViewColumn { Header = "阅读进度", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ReadingListRow.ProgressText)) });
        list.SelectionChanged += (_, _) => UpdateActions();
        list.MouseDoubleClick += async (_, _) => await OpenSelectedAsync();
        list.KeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await OpenSelectedAsync(); }
        };
        var menu = new ContextMenu();
        var pinItem = new MenuItem { Header = "固定 / 取消固定" };
        pinItem.Click += async (_, _) => await ChangeSelectedAsync(pin);
        var readItem = new MenuItem { Header = "标记已读 / 未读" };
        readItem.Click += async (_, _) => await ChangeSelectedAsync(read);
        var removeItem = new MenuItem { Header = "移除阅读记录" };
        removeItem.Click += async (_, _) => await ChangeSelectedAsync(remove);
        menu.Items.Add(pinItem);
        menu.Items.Add(readItem);
        menu.Items.Add(removeItem);
        list.ContextMenu = menu;
        root.Children.Add(list);
        Content = root;
        AutomationProperties.SetName(this, "阅读清单窗口");
        Refresh();
    }

    public void Refresh()
    {
        Guid? selectedId = (list.SelectedItem as ReadingListRow)?.Entry.Id;
        var rows = items().Select(entry => new ReadingListRow(entry)).ToArray();
        list.ItemsSource = rows;
        if (selectedId is { } id)
            list.SelectedItem = rows.FirstOrDefault(row => row.Entry.Id == id);
        if (list.SelectedItem is null && rows.Length > 0) list.SelectedIndex = 0;
        if (!unsaved)
        {
            message.Text = rows.Length == 0
                ? "阅读清单为空；在主窗口将当前文件加入清单。"
                : $"{rows.Length} 个文件 · {rows.Count(row => row.Entry.IsRead)} 个已读";
            message.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        }
        retry.IsEnabled = unsaved;
        UpdateActions();
    }

    public void ShowSaveError(IReadOnlyList<string> errors)
    {
        if (errors.Count == 0)
        {
            unsaved = false;
            message.ToolTip = null;
            message.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Refresh();
            return;
        }
        unsaved = true;
        message.Text = "保存失败，清单改动尚未写入磁盘：" + errors[0];
        message.ToolTip = message.Text;
        message.Foreground = System.Windows.Media.Brushes.OrangeRed;
        retry.IsEnabled = true;
    }

    private async Task RetrySaveAsync()
    {
        var result = await save();
        ShowSaveError(result.Errors);
    }

    private async Task OpenSelectedAsync()
    {
        if (list.SelectedItem is not ReadingListRow row) return;
        if (!File.Exists(row.Entry.Path) || Directory.Exists(row.Entry.Path))
        {
            message.Text = "文件不存在或已移动：" + row.Entry.Path;
            message.Foreground = System.Windows.Media.Brushes.OrangeRed;
            return;
        }
        await open(row.Entry);
    }

    private async Task ChangeSelectedAsync(Func<ReadingListEntry, Task<ReadingListSaveResult>> action)
    {
        if (list.SelectedItem is not ReadingListRow row) return;
        var result = await action(row.Entry);
        ShowSaveError(result.Errors);
    }

    private void UpdateActions()
    {
        bool selected = list.SelectedItem is ReadingListRow;
        foreach (var button in new[] { openButton, pinButton, readButton, removeButton })
            button.IsEnabled = selected;
        if (list.SelectedItem is ReadingListRow row)
        {
            pinButton.Content = row.Entry.IsPinned ? "取消固定" : "固定到顶部";
            readButton.Content = row.Entry.IsRead ? "标为未读" : "标为已读";
        }
        else
        {
            pinButton.Content = "固定到顶部";
            readButton.Content = "标为已读";
        }
    }

    private sealed record ReadingListRow(ReadingListEntry Entry)
    {
        public string Title => Path.GetFileName(Entry.Path);
        public string Folder => Path.GetDirectoryName(Entry.Path) ?? "";
        public string PinnedText => Entry.IsPinned ? "已固定" : "";
        public string StateText => Entry.IsRead ? "已读" : "未读";
        public string ProgressText => Entry.ProgressPercent + "%";
    }
}

internal sealed class AnnotationPanelWindow : Window
{
    private readonly Func<IReadOnlyList<DocumentAnnotation>> items;
    private readonly Action<DocumentAnnotation> navigate;
    private readonly Action<DocumentAnnotation> edit;
    private readonly Action<DocumentAnnotation, AnnotationEditorResult> update;
    private readonly Action<DocumentAnnotation> remove;
    private readonly Func<Task<AnnotationSaveResult>> save;
    private readonly Func<Task>? reloadAfterFailedSave;
    private readonly ListView list = new();
    private readonly TextBlock message = new();
    private readonly Button retry = new() { Content = "重试保存", Visibility = Visibility.Collapsed };
    private readonly TextBox quote = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly TextBox note = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 76
    };
    private readonly TextBox tags = new() { ToolTip = "逗号分隔，最多 10 个标签" };
    private readonly ComboBox color = new() { Width = 116 };
    private readonly Button saveButton = new() { Name = "AnnotationSaveButton", Content = "保存批注" };
    private readonly Button deleteButton = new() { Content = "删除批注" };
    private readonly Button jumpButton = new() { Content = "回到原文" };
    private bool saving;
    private string? operationMessage;
    internal Func<bool>? ConfirmDelete { get; set; }

    public AnnotationPanelWindow(
        Window owner,
        Func<IReadOnlyList<DocumentAnnotation>> items,
        Action<DocumentAnnotation> navigate,
        Action<DocumentAnnotation> edit,
        Action<DocumentAnnotation, AnnotationEditorResult> update,
        Action<DocumentAnnotation> remove,
        Func<Task<AnnotationSaveResult>> save,
        string? path,
        Func<Task>? reloadAfterFailedSave = null)
    {
        Owner = owner;
        Title = "文档批注";
        Width = 760;
        Height = 670;
        MinWidth = 540;
        MinHeight = 670;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = true;
        Name = "AnnotationPanel";
        AutomationProperties.SetName(this, "文档批注窗口");
        this.items = items;
        this.navigate = navigate;
        this.edit = edit;
        this.update = update;
        this.remove = remove;
        this.save = save;
        this.reloadAfterFailedSave = reloadAfterFailedSave;
        ConfirmDelete = () => MessageBox.Show(this,
            "只删除本机批注，原文保持不变。",
            "删除这条批注？",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
        UpdateDocument(path);

        foreach (var key in owner.Resources.Keys) Resources[key] = owner.Resources[key];
        var root = new DockPanel { Margin = new Thickness(16) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = new Button { Content = "刷新", Margin = new Thickness(0, 0, 8, 0) };
        refresh.Click += (_, _) =>
        {
            operationMessage = null;
            Refresh();
        };
        retry.Margin = new Thickness(0, 0, 8, 0);
        retry.Click += async (_, _) => await RetrySaveAsync();
        message.VerticalAlignment = VerticalAlignment.Center;
        message.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        toolbar.Children.Add(refresh);
        toolbar.Children.Add(retry);
        toolbar.Children.Add(message);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        list.View = new GridView();
        ((GridView)list.View).Columns.Add(new GridViewColumn { Header = "摘录", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(AnnotationRow.Quote)) });
        ((GridView)list.View).Columns.Add(new GridViewColumn { Header = "位置状态", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(AnnotationRow.Location)) });
        list.SelectionChanged += (_, _) => UpdateSelection();
        list.MouseDoubleClick += (_, _) => IfSelected(navigate);
        list.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; IfSelected(navigate); }
        };
        var menu = new ContextMenu();
        var editItem = new MenuItem { Header = "编辑批注" };
        editItem.Click += (_, _) => IfSelected(edit);
        var removeItem = new MenuItem { Header = "删除批注" };
        removeItem.Click += (_, _) => IfSelected(remove);
        var jumpItem = new MenuItem { Header = "回到原文" };
        jumpItem.Click += (_, _) => IfSelected(navigate);
        menu.Items.Add(jumpItem);
        menu.Items.Add(editItem);
        menu.Items.Add(removeItem);
        list.ContextMenu = menu;
        AutomationProperties.SetName(list, "当前文档批注列表");

        quote.SetResourceReference(Control.BackgroundProperty, "EditorBrush");
        quote.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        note.SetResourceReference(Control.BackgroundProperty, "EditorBrush");
        note.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        tags.SetResourceReference(Control.BackgroundProperty, "EditorBrush");
        tags.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        color.ItemsSource = new[]
        {
            new AnnotationColorOption(AnnotationColor.Amber, "琥珀"),
            new AnnotationColorOption(AnnotationColor.Blue, "蓝色"),
            new AnnotationColorOption(AnnotationColor.Green, "绿色"),
            new AnnotationColorOption(AnnotationColor.Rose, "玫瑰"),
            new AnnotationColorOption(AnnotationColor.Violet, "紫色")
        };
        color.DisplayMemberPath = nameof(AnnotationColorOption.Label);
        color.SelectedValuePath = nameof(AnnotationColorOption.Value);
        AutomationProperties.SetName(quote, "批注摘录原文");
        AutomationProperties.SetName(note, "批注备注");
        AutomationProperties.SetName(tags, "批注标签");
        AutomationProperties.SetName(color, "批注颜色");
        AutomationProperties.SetName(message, "批注操作状态");
        saveButton.Click += async (_, _) => await SaveSelectedAsync();
        deleteButton.Click += async (_, _) => await DeleteSelectedAsync();
        jumpButton.Click += (_, _) => IfSelected(navigate);
        PreviewKeyDown += (_, e) =>
        {
            if (HandleShortcut(e.Key, Keyboard.Modifiers)) e.Handled = true;
        };

        var detail = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(86) });
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        detail.ColumnDefinitions.Add(new ColumnDefinition());
        detail.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        detail.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        AddLabel(detail, "摘录（只读）", 0, 0);
        Grid.SetRow(quote, 1); Grid.SetColumn(quote, 0); Grid.SetColumnSpan(quote, 4);
        detail.Children.Add(quote);
        AddLabel(detail, "备注", 2, 0);
        Grid.SetRow(note, 3); Grid.SetColumn(note, 0); Grid.SetColumnSpan(note, 4);
        detail.Children.Add(note);
        AddLabel(detail, "标签", 4, 0);
        Grid.SetRow(tags, 4); Grid.SetColumn(tags, 1); Grid.SetColumnSpan(tags, 3);
        tags.Margin = new Thickness(8, 0, 0, 0);
        detail.Children.Add(tags);
        AddLabel(detail, "颜色", 5, 0);
        Grid.SetRow(color, 5); Grid.SetColumn(color, 1);
        color.Margin = new Thickness(8, 6, 0, 0);
        detail.Children.Add(color);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(jumpButton);
        actions.Children.Add(deleteButton);
        actions.Children.Add(saveButton);
        foreach (Button button in actions.Children) button.Margin = new Thickness(8, 10, 0, 0);
        Grid.SetRow(actions, 6); Grid.SetColumn(actions, 0); Grid.SetColumnSpan(actions, 4);
        detail.Children.Add(actions);

        DockPanel.SetDock(list, Dock.Top);
        root.Children.Add(list);
        root.Children.Add(detail);
        Content = root;
        Refresh();
    }

    public void UpdateDocument(string? path) =>
        Title = "文档批注" + (string.IsNullOrWhiteSpace(path)
            ? ""
            : " · " + Path.GetFileName(path));

    public void Refresh()
    {
        Guid? selectedId = (list.SelectedItem as AnnotationRow)?.Annotation.Id;
        var rows = items().Select(item => new AnnotationRow(item)).ToArray();
        list.ItemsSource = rows;
        if (selectedId is { } id)
            list.SelectedItem = rows.FirstOrDefault(row => row.Annotation.Id == id);
        if (list.SelectedItem is null && rows.Length > 0) list.SelectedIndex = 0;
        message.Text = operationMessage ?? (rows.Length == 0
            ? "当前文档没有批注。先返回正文，选择文字后添加摘录。"
            : $"共 {rows.Length} 条批注 · 双击列表回到原文；修改后点击保存。");
        UpdateSelection();
    }

    public void ShowSaveError(IReadOnlyList<string> errors) =>
        ShowOperationResult(errors, "批注已保存到本机。", "批注尚未写入磁盘：");

    private void ShowOperationResult(
        IReadOnlyList<string> errors, string success, string failurePrefix)
    {
        operationMessage = errors.Count == 0
            ? success
            : failurePrefix + string.Join("；", errors);
        message.Text = operationMessage;
        retry.Visibility = errors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RetrySaveAsync()
    {
        var result = await save();
        ShowSaveError(result.Errors);
    }

    private void IfSelected(Action<DocumentAnnotation> action)
    {
        if (list.SelectedItem is AnnotationRow row) action(row.Annotation);
    }

    private async Task SaveSelectedAsync()
    {
        if (saving || list.SelectedItem is not AnnotationRow row) return;
        saving = true;
        try
        {
            var parsed = ParsePanelTags(tags.Text);
            if (parsed.Count > 10 || parsed.Any(item => item.Length > 32))
                throw new ArgumentException("标签最多 10 个，每个最多 32 个字符。");
            update(row.Annotation, new AnnotationEditorResult(
                string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim(),
                parsed,
                color.SelectedValue is AnnotationColor selected ? selected : AnnotationColor.Amber));
            var result = await save();
            Refresh();
            ShowOperationResult(result.Errors, "批注已保存到本机。", "批注尚未写入磁盘：");
        }
        catch (Exception ex)
        {
            operationMessage = "批注未保存：" + ex.Message;
            message.Text = operationMessage;
        }
        finally { saving = false; }
    }

    private async Task DeleteSelectedAsync()
    {
        if (saving || list.SelectedItem is not AnnotationRow row) return;
        if (ConfirmDelete?.Invoke() != true) return;
        saving = true;
        try
        {
            remove(row.Annotation);
            var result = await save();
            if (result.Errors.Count > 0 && reloadAfterFailedSave is not null)
                await reloadAfterFailedSave();
            Refresh();
            ShowOperationResult(result.Errors, "批注已删除。", "删除未能保存：");
        }
        catch (Exception ex)
        {
            if (reloadAfterFailedSave is not null)
            {
                try { await reloadAfterFailedSave(); }
                catch { }
            }
            operationMessage = "批注未删除：" + ex.Message;
            message.Text = operationMessage;
        }
        finally { saving = false; }
    }

    private void UpdateSelection()
    {
        if (list.SelectedItem is not AnnotationRow row)
        {
            quote.Text = "";
            note.Text = "";
            tags.Text = "";
            color.SelectedValue = AnnotationColor.Amber;
            note.IsEnabled = tags.IsEnabled = color.IsEnabled = false;
            saveButton.IsEnabled = deleteButton.IsEnabled = jumpButton.IsEnabled = false;
            return;
        }
        quote.Text = row.Annotation.Quote;
        note.Text = row.Annotation.Note ?? "";
        tags.Text = string.Join(", ", row.Annotation.Tags);
        color.SelectedValue = row.Annotation.Color;
        note.IsEnabled = tags.IsEnabled = color.IsEnabled = true;
        saveButton.IsEnabled = deleteButton.IsEnabled = jumpButton.IsEnabled = true;
    }

    private static IReadOnlyList<string> ParsePanelTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value
            .Split([',', '，'], StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .ToArray();
    }

    private bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (key != Key.S || (modifiers & ModifierKeys.Control) == 0 ||
            (modifiers & (ModifierKeys.Alt | ModifierKeys.Shift)) != 0)
            return false;
        _ = SaveSelectedAsync();
        return true;
    }

    private static void AddLabel(Grid parent, string text, int row, int column)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(label, row); Grid.SetColumn(label, column);
        parent.Children.Add(label);
    }

    private sealed record AnnotationRow(DocumentAnnotation Annotation)
    {
        public string Quote => Annotation.Quote.ReplaceLineEndings(" ");
        public string Location => ReadingBriefExporter.AnchorStateText(Annotation.AnchorState);
    }

    private sealed record AnnotationColorOption(AnnotationColor Value, string Label);
}
