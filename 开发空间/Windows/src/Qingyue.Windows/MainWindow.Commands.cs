using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private void InstallShortcuts()
    {
        void Bind(Key key, ModifierKeys modifiers, string action) =>
            InputBindings.Add(new KeyBinding(new ReaderAction(() => ExecuteAction(action)), key, modifiers));
        Bind(Key.N, ModifierKeys.Control, "new");
        Bind(Key.O, ModifierKeys.Control, "open");
        Bind(Key.S, ModifierKeys.Control, "save");
        Bind(Key.S, ModifierKeys.Control | ModifierKeys.Shift, "saveAs");
        Bind(Key.S, ModifierKeys.Control | ModifierKeys.Alt, "saveAll");
        Bind(Key.W, ModifierKeys.Control, "close");
        Bind(Key.F, ModifierKeys.Control, "find");
        Bind(Key.H, ModifierKeys.Control, "replace");
        Bind(Key.R, ModifierKeys.Control, "reading");
        Bind(Key.D, ModifierKeys.Control, "bookmark");
        Bind(Key.B, ModifierKeys.Control, "sidebar");
        Bind(Key.G, ModifierKeys.Control, "goto");
        Bind(Key.F, ModifierKeys.Control | ModifierKeys.Shift, "focus");
        Bind(Key.F, ModifierKeys.Control | ModifierKeys.Alt, "format");
        Bind(Key.O, ModifierKeys.Control | ModifierKeys.Shift, "outline");
        Bind(Key.P, ModifierKeys.Control | ModifierKeys.Shift, "quick");
        Bind(Key.F2, ModifierKeys.None, "rename");
        Bind(Key.F3, ModifierKeys.None, "next");
        Bind(Key.F3, ModifierKeys.Shift, "previous");
        Bind(Key.Left, ModifierKeys.Alt, "returnPosition");
        Bind(Key.Tab, ModifierKeys.Control, "nextTab");
        Bind(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, "previousTab");
        Bind(Key.OemPlus, ModifierKeys.Control, "fontUp");
        Bind(Key.Add, ModifierKeys.Control, "fontUp");
        Bind(Key.OemMinus, ModifierKeys.Control, "fontDown");
        Bind(Key.Subtract, ModifierKeys.Control, "fontDown");
        for (int i = 1; i <= 9; i++) Bind(Key.D0 + i, ModifierKeys.Control, "tab:" + i);
    }

    private void MenuAction(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action }) ExecuteAction(action);
        e.Handled = true;
    }

    private async void ExecuteAction(string action)
    {
        if (exitInProgress || modalDepth > 0) return;
        var tab = CurrentTab;
        try
        {
            switch (action)
            {
                case "new": NewDocument().Editor.Focus(); break;
                case "open": await ChooseOpenAsync(); break;
                case "save": if (tab is not null) SaveDocument(tab); break;
                case "saveAs": if (tab is not null) SaveDocument(tab, true); break;
                case "saveAll":
                    foreach (var item in tabs.ToArray().Where(t => t.File.IsDirty && !t.IsEmpty))
                        if (!SaveDocument(item)) break;
                    break;
                case "rename": if (tab is not null) RenamePrompt(tab); break;
                case "close": if (tab is not null) CloseTab(tab); break;
                case "closeAll":
                case "closeOthers":
                    foreach (var item in tabs.ToArray().Where(t => action != "closeOthers" || t != tab))
                        if (!CloseTab(item, false)) break;
                    if (tabs.Count == 0) NewDocument();
                    break;
                case "quit": Close(); break;
                case "reading": await ToggleReadingAsync(); break;
                case "bookmark": ToggleBookmark(); break;
                case "cancelLoading": CancelLoading(); break;
                case "diagnostics": ShowOperationDiagnostics(); break;
                case "returnPosition": ReturnToReadingPosition(); break;
                case "sidebar": ToggleSidebar(); break;
                case "focus": ToggleFocus(); break;
                case "wrap": settings.Wrap = !settings.Wrap; ApplyEditorSettings(); break;
                case "lines": settings.LineNumbers = !settings.LineNumbers; ApplyEditorSettings(); break;
                case "theme": ApplyTheme(!settings.Dark); break;
                case "fontUp": settings.FontSize = Math.Min(30, settings.FontSize + 1); ApplyEditorSettings(); break;
                case "fontDown": settings.FontSize = Math.Max(10, settings.FontSize - 1); ApplyEditorSettings(); break;
                case "find": ShowSearch(false); break;
                case "replace": ShowSearch(true); break;
                case "hideFind": HideSearch(); break;
                case "next": MoveSearch(1); break;
                case "previous": MoveSearch(-1); break;
                case "replaceOne": ReplaceOne(); break;
                case "replaceAll": ReplaceAll(); break;
                case "goto": if (tab is not null) GoToLine(tab); break;
                case "outline":
                    settings.Sidebar = true;
                    if (focusMode) ToggleFocus();
                    ApplySidebar(); SidebarTabs.SelectedIndex = 1; OutlineList.Focus();
                    break;
                case "nextTab": CycleTab(1); break;
                case "previousTab": CycleTab(-1); break;
                case "moveLeft": MoveTab(-1); break;
                case "moveRight": MoveTab(1); break;
                case "copyPath": if (tab?.File.Path is not null) Clipboard.SetText(tab.File.Path); break;
                case "reveal":
                    if (tab?.File.Path is not null)
                    {
                        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                        start.ArgumentList.Add("/select,");
                        start.ArgumentList.Add(tab.File.Path);
                        Process.Start(start);
                    }
                    break;
                case "clearRecent": settings.Recent.Clear(); RefreshRecent(); SchedulePersist(); break;
                case "quick": await QuickOpenAsync(); break;
                case "validate":
                    if (tab is not null)
                        Notify(TextServices.Diagnostic(tab.File.Text, tab.Kind) ??
                            (LanguageCatalog.Get(tab.Kind).SupportsValidation ? "语法检查通过。"
                                : LanguageCatalog.Get(tab.Kind).ValidationDescription), "文档检查");
                    break;
                case "format":
                    if (tab is not null) FormatCurrentDocument(tab);
                    break;
                case "structure": ToggleStructure(); break;
                case "markdownPreview": ToggleMarkdownPreview(); break;
                case "copyReading": await ShareReadingWithProgressAsync(false); break;
                case "copyPlain": CopyReadingPlainText(); break;
                case "copyMarkdown": CopyReadingMarkdown(); break;
                case "copyCodeBlock": CopyReadingCodeBlock(); break;
                case "copyTable": CopyReadingTable(); break;
                case "exportHtml": await ShareReadingWithProgressAsync(true); break;
                case "drafts": await ShowDraftSettingsAsync(); break;
                case "help":
                    Modal(() => Dialogs.CreateHelp(this, false).ShowDialog());
                    break;
                case "about": Modal(() => Dialogs.CreateHelp(this, true).ShowDialog()); break;
                default:
                    if (action.StartsWith("width:") && double.TryParse(action[6..], out double width))
                    { settings.ReadingWidth = width; UpdateReadingWidths(); SchedulePersist(); }
                    else if (action.StartsWith("tab:") && int.TryParse(action[4..], out int number) && tabs.Count > 0)
                        SelectTab(tabs[number == 9 ? tabs.Count - 1 : Math.Min(tabs.Count - 1, number - 1)]);
                    else if (action.StartsWith("reopen:") && tab is not null &&
                        Enum.TryParse<TextCodec>(action[7..], out var codec)) ReopenWithCodec(tab, codec);
                    break;
            }
        }
        catch (Exception ex) { Notify(ex.Message, "操作未完成"); }
    }

    private void ApplyEditorSettings()
    {
        foreach (var item in tabs) ConfigureEditor(item);
        UpdateReadingWidths();
        if (CurrentTab is { } tab) RefreshPresentation(tab);
        SchedulePersist();
    }

    private void FocusDocument()
    {
        if (CurrentTab is not { } tab) return;
        if (tab.IsReading && tab.Rendered is not null) tab.Preview.Focus();
        else if (structureTab == tab && structureView is not null) structureView.Focus();
        else tab.Editor.Focus();
    }

    private void CycleTab(int delta)
    {
        if (tabs.Count == 0) return;
        SelectTab(tabs[(Math.Max(0, tabs.IndexOf(CurrentTab!)) + delta + tabs.Count) % tabs.Count]);
        FocusDocument();
    }

    private void MoveTab(int delta)
    {
        if (CurrentTab is not { } tab) return;
        int from = tabs.IndexOf(tab), to = Math.Clamp(from + delta, 0, tabs.Count - 1);
        tabs.Move(from, to); SelectTab(tab); SchedulePersist();
    }

    private void GoToLine(EditorTab tab)
    {
        string? value = Modal(() => Dialogs.Prompt(this, "跳转到行", "输入源文件行号。", "1"));
        if (value is null) return;
        if (!int.TryParse(value, out int target) || target < 1) { Notify("请输入大于零的整数。"); return; }
        if (tab.IsReading) ToggleReading();
        ExitStructureForEditing(tab);
        int line = 1, offset = 0;
        while (offset < tab.File.Text.Length && line < target) if (tab.File.Text[offset++] == '\n') line++;
        tab.Editor.CaretIndex = EditorOffset(tab, offset);
        ScrollToCaret(tab); tab.Editor.Focus();
    }

    private void ReopenWithCodec(EditorTab tab, TextCodec codec)
    {
        if (tab.File.Path is null) { Notify("未命名文档没有可重新读取的磁盘文件。"); return; }
        if (tab.File.IsDirty && Modal(() => MessageBox.Show(this,
            "重新读取会放弃此标签未保存的修改，是否继续？", "按编码重新打开",
            MessageBoxButton.YesNo, MessageBoxImage.Warning)) != MessageBoxResult.Yes) return;
        var file = DocumentFile.Open(tab.File.Path, codec);
        int index = tabs.IndexOf(tab);
        var replacement = AddDocument(file);
        replacement.RecoverDraft = tab.RecoverDraft;
        tabs.Remove(tab);
        tabs.Move(tabs.IndexOf(replacement), index);
        SelectTab(replacement);
    }

    private void KindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || CurrentTab is not { } tab || KindCombo.SelectedItem is not TextKind kind) return;
        int anchor = CurrentSourceAnchor(tab);
        tab.AutomaticKind = false;
        tab.Kind = kind;
        tab.Reading = null;
        if (tab.IsReading) EnterReading(tab, anchor, snapToBlockStart: false);
        RefreshCurrent(); SchedulePersist();
    }

    private void CodecChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || CurrentTab is not { } tab || CodecCombo.SelectedItem is not TextCodec codec) return;
        tab.File.Codec = codec; tab.NotifyChanged(); RefreshStatus(); SchedulePersist();
    }

    private void EndingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || CurrentTab is not { } tab || EndingCombo.SelectedItem is not LineEnding ending) return;
        tab.File.LineEnding = ending; tab.NotifyChanged(); RefreshStatus(); SchedulePersist();
    }

    public sealed record RecentItem(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
        public bool Exists
        {
            get
            {
                try { return File.Exists(Path); }
                catch { return false; }
            }
        }
        public string StateText
        {
            get
            {
                try
                {
                    if (File.Exists(Path)) return "可打开";
                    string? folder = System.IO.Path.GetDirectoryName(Path);
                    return folder is not null && Directory.Exists(folder) ? "文件不存在" : "暂时无法访问";
                }
                catch { return "暂时无法访问"; }
            }
        }
    }

    private void AddRecent(string path)
    {
        settings.Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        settings.Recent.Insert(0, path);
        if (settings.Recent.Count > 30) settings.Recent.RemoveRange(30, settings.Recent.Count - 30);
        RefreshRecent();
    }

    public void RefreshRecent()
    {
        RecentList.ItemsSource = settings.Recent.Select(p => new RecentItem(p)).ToArray();
        RecentMenu.Items.Clear();
        foreach (var path in settings.Recent)
        {
            var item = new MenuItem { Header = Path.GetFileName(path).Replace("_", "__"), ToolTip = path };
            item.Click += async (_, _) =>
            {
                if (!File.Exists(path)) { Notify("最近文件当前不存在或无法访问：\n" + path); return; }
                await OpenPathsAsync([path]);
            };
            RecentMenu.Items.Add(item);
        }
        if (RecentMenu.Items.Count == 0) RecentMenu.Items.Add(new MenuItem { Header = "暂无最近文件", IsEnabled = false });
    }

    private async void RecentDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentList.SelectedItem is RecentItem item)
        {
            if (!item.Exists) { Notify("最近文件当前不存在或无法访问：\n" + item.Path); return; }
            await OpenPathsAsync([item.Path]);
        }
    }

    private async void RecentKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && RecentList.SelectedItem is RecentItem item)
        {
            e.Handled = true;
            if (!item.Exists) { Notify("最近文件当前不存在或无法访问：\n" + item.Path); return; }
            await OpenPathsAsync([item.Path]);
        }
    }

    private void RemoveRecentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Parent is not ContextMenu menu
            || menu.PlacementTarget is not FrameworkElement target
            || target.DataContext is not RecentItem recent) return;
        RemoveRecentPath(recent.Path);
        e.Handled = true;
    }

    public void RemoveRecentPath(string path)
    {
        settings.Recent.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        RefreshRecent();
        SchedulePersist();
    }

    public sealed record BookmarkListItem(
        DocumentBookmark Bookmark, string Group, string DocumentTitle, string Location, string StateText);

    public void RefreshBookmarks()
    {
        if (BookmarksList is null) return;
        var current = CurrentTab;
        BookmarksList.ItemsSource = bookmarks.Items.Select(bookmark =>
        {
            var tab = tabs.FirstOrDefault(item => item.File.Id == bookmark.DocumentId
                || bookmark.Path is not null && string.Equals(item.File.Path, bookmark.Path, StringComparison.OrdinalIgnoreCase));
            bool isCurrent = tab is not null && ReferenceEquals(tab, current);
            string title = tab?.File.Title
                ?? (bookmark.Path is null ? "未命名文档" : Path.GetFileName(bookmark.Path));
            string state = tab is not null ? "已打开"
                : bookmark.Path is { } path && File.Exists(path) ? "未打开" : "文件不存在";
            return new BookmarkListItem(bookmark, isCurrent ? "当前文档" : "其他文档",
                title, $"源位置 {bookmark.SourceAnchor:N0}", state);
        }).ToArray();
    }

    private void BookmarkDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BookmarksList.SelectedItem is BookmarkListItem item)
        {
            NavigateToBookmark(item.Bookmark);
            e.Handled = true;
        }
    }

    private void BookmarkJumpClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu menu }
            && menu.PlacementTarget is FrameworkElement target
            && target.DataContext is BookmarkListItem item)
            NavigateToBookmark(item.Bookmark);
        e.Handled = true;
    }

    private void BookmarkKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && BookmarksList.SelectedItem is BookmarkListItem item)
        {
            NavigateToBookmark(item.Bookmark);
            e.Handled = true;
        }
    }

    private void RemoveBookmarkClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Parent is not ContextMenu menu
            || menu.PlacementTarget is not FrameworkElement target
            || target.DataContext is not BookmarkListItem bookmark) return;
        if (bookmarks.Remove(bookmark.Bookmark.Id))
        {
            bookmarksDirty = true;
            ScheduleBookmarkSave();
            RefreshBookmarks();
        }
        e.Handled = true;
    }

    private void TabContextAction(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Parent is not ContextMenu menu
            || menu.PlacementTarget is not FrameworkElement target
            || target.DataContext is not EditorTab tab
            || item.Tag is not string action) return;
        e.Handled = true;
        switch (action)
        {
            case "closeTab":
                CloseTab(tab);
                break;
            case "closeOthers":
                SelectTab(tab);
                foreach (var other in tabs.ToArray().Where(candidate => candidate != tab))
                    if (!CloseTab(other, false)) break;
                if (tabs.Count == 0) NewDocument();
                break;
            case "closeRight":
                SelectTab(tab);
                int index = tabs.IndexOf(tab);
                foreach (var other in tabs.Skip(index + 1).Reverse().ToArray())
                    if (!CloseTab(other, false)) break;
                break;
            case "moveLeft":
                SelectTab(tab); MoveTab(-1);
                break;
            case "moveRight":
                SelectTab(tab); MoveTab(1);
                break;
            case "copyPath":
                if (tab.File.Path is { } path) Clipboard.SetText(path);
                break;
            case "revealPath":
                RevealPath(tab);
                break;
        }
    }

    private void TabContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not FrameworkElement target
            || target.DataContext is not EditorTab tab) return;
        bool hasPath = tab.File.Path is not null;
        foreach (var item in menu.Items.OfType<MenuItem>())
            if (item.Tag is "copyPath" or "revealPath") item.IsEnabled = hasPath;
    }

    private static void RevealPath(EditorTab tab)
    {
        if (tab.File.Path is not { } path) return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add("/select,");
        start.ArgumentList.Add(path);
        Process.Start(start);
    }

    private async Task QuickOpenAsync()
    {
        var dialog = Dialogs.Shell(this, "快速打开", 600);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "搜索已打开标签和最近文件", Margin = new Thickness(0, 0, 0, 12) });
        var query = new TextBox();
        var list = new ListBox { Height = 300, Margin = new Thickness(0, 12, 0, 12) };
        var open = new Button { Content = "打开所选", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var candidates = tabs.Select(t => new QuickItem(t.File.Title, t.File.Path, t))
            .Concat(settings.Recent.Where(p => !tabs.Any(t => string.Equals(t.File.Path, p, StringComparison.OrdinalIgnoreCase)))
                .Select(p => new QuickItem(Path.GetFileName(p), p, null))).ToArray();
        void Filter()
        {
            list.ItemsSource = candidates.Where(c => (c.Name + " " + c.Path).Contains(query.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.DisplayMemberPath = "Name"; list.SelectedIndex = 0;
        }
        QuickItem? selected = null;
        void Choose() { if (list.SelectedItem is QuickItem item) { selected = item; dialog.DialogResult = true; } }
        query.TextChanged += (_, _) => Filter();
        query.KeyDown += (_, e) => { if (e.Key == Key.Down) { list.Focus(); e.Handled = true; } };
        open.Click += (_, _) => Choose();
        list.MouseDoubleClick += (_, _) => Choose();
        panel.Children.Add(query); panel.Children.Add(list); panel.Children.Add(open);
        dialog.Content = panel; Filter();
        dialog.Loaded += (_, _) => query.Focus();
        Modal(() => dialog.ShowDialog());
        if (selected?.Tab is { } tab) SelectTab(tab);
        else if (selected?.Path is { } path) await OpenPathsAsync([path]);
    }
    private sealed record QuickItem(string Name, string? Path, EditorTab? Tab);
}
