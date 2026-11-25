using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private readonly List<ReaderAction> shortcutCommands = [];
    private bool savingAll;

    private void InstallShortcuts()
    {
        void Bind(Key key, ModifierKeys modifiers, string action) =>
            BindAction(new KeyBinding(new ReaderAction(
                () => CanExecuteAction(action), () => ExecuteAction(action)), key, modifiers));
        Bind(Key.N, ModifierKeys.Control, "new");
        Bind(Key.O, ModifierKeys.Control, "open");
        Bind(Key.S, ModifierKeys.Control, "save");
        Bind(Key.S, ModifierKeys.Control | ModifierKeys.Shift, "saveAs");
        Bind(Key.S, ModifierKeys.Control | ModifierKeys.Alt, "saveAll");
        Bind(Key.Q, ModifierKeys.Control, "quit");
        Bind(Key.W, ModifierKeys.Control, "close");
        Bind(Key.W, ModifierKeys.Control | ModifierKeys.Shift, "closeAll");
        Bind(Key.F, ModifierKeys.Control, "find");
        Bind(Key.H, ModifierKeys.Control | ModifierKeys.Shift, "replace");
        Bind(Key.Z, ModifierKeys.Control | ModifierKeys.Shift, "redo");
        Bind(Key.R, ModifierKeys.Control, "reading");
        Bind(Key.R, ModifierKeys.Control | ModifierKeys.Shift, "reload");
        Bind(Key.J, ModifierKeys.Control | ModifierKeys.Shift, "formatJson");
        Bind(Key.T, ModifierKeys.Control | ModifierKeys.Shift, "theme");
        Bind(Key.OemComma, ModifierKeys.Control, "appearance");
        Bind(Key.D, ModifierKeys.Control, "bookmark");
        Bind(Key.B, ModifierKeys.Control, "sidebar");
        Bind(Key.L, ModifierKeys.Control, "goto");
        Bind(Key.F, ModifierKeys.Control | ModifierKeys.Shift, "focus");
        Bind(Key.F, ModifierKeys.Control | ModifierKeys.Alt, "format");
        Bind(Key.O, ModifierKeys.Control | ModifierKeys.Shift, "outline");
        Bind(Key.P, ModifierKeys.Control | ModifierKeys.Shift, "quick");
        Bind(Key.G, ModifierKeys.Control, "next");
        Bind(Key.G, ModifierKeys.Control | ModifierKeys.Shift, "previous");
        Bind(Key.Left, ModifierKeys.Alt, "returnPosition");
        Bind(Key.OemCloseBrackets, ModifierKeys.Control | ModifierKeys.Shift, "nextTab");
        Bind(Key.OemOpenBrackets, ModifierKeys.Control | ModifierKeys.Shift, "previousTab");
        Bind(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift, "fontUp");
        Bind(Key.Add, ModifierKeys.Control, "fontUp");
        Bind(Key.OemMinus, ModifierKeys.Control, "fontDown");
        Bind(Key.Subtract, ModifierKeys.Control, "fontDown");
        for (int i = 1; i <= 9; i++) Bind(Key.D0 + i, ModifierKeys.Control, "tab:" + i);
    }

    private void BindAction(KeyBinding binding)
    {
        shortcutCommands.Add((ReaderAction)binding.Command);
        InputBindings.Add(binding);
    }

    private void MenuAction(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action }) ExecuteAction(action);
        e.Handled = true;
    }

    private async void ExecuteAction(string action)
    {
        if (!CanExecuteAction(action)) return;
        var tab = CurrentTab;
        try
        {
            switch (action)
            {
                case "undo":
                    if (FocusedTextCommandTarget() is { } undoTarget)
                        ApplicationCommands.Undo.Execute(null, undoTarget);
                    break;
                case "redo":
                    if (FocusedTextCommandTarget() is { } redoTarget)
                        ApplicationCommands.Redo.Execute(null, redoTarget);
                    break;
                case "new": NewDocument().Editor.Focus(); break;
                case "open": await ChooseOpenAsync(); break;
                case "openWorkspace": await OpenWorkspaceAsync(); break;
                case "globalSearch": await ShowGlobalSearchAsync(); break;
                case "reload": if (tab is not null) await ReloadDocumentAsync(tab); break;
                case "save": if (tab is not null) SaveDocument(tab); break;
                case "saveAs": if (tab is not null) SaveDocument(tab, true); break;
                case "saveAll": SaveAllDocuments(); break;
                case "rename": if (tab is not null) RenamePrompt(tab); break;
                case "close": if (tab is not null) CloseTab(tab, ensureDocument: false, closeWindowIfEmpty: true); break;
                case "closeAll":
                    foreach (var item in tabs.ToArray().Reverse())
                        if (!CloseTab(item, false, closeWindowIfEmpty: false)) break;
                    if (tabs.Count == 0 && !exitInProgress) Close();
                    break;
                case "closeOthers": CloseOtherTabs(tab); break;
                case "quit": await RequestQuitAsync(); break;
                case "reading": await ToggleReadingAsync(); break;
                case "annotate": CaptureAnnotation(); break;
                case "bookmark": ToggleBookmark(); break;
                case "cancelLoading": CancelLoading(); break;
                case "diagnostics": ShowOperationDiagnostics(); break;
                case "returnPosition": ReturnToReadingPosition(); break;
                case "sidebar": ToggleSidebar(); break;
                case "focus": ToggleFocus(); break;
                case "wrap": settings.Wrap = !settings.Wrap; ApplyEditorSettings(); break;
                case "lines": settings.LineNumbers = !settings.LineNumbers; ApplyEditorSettings(); break;
                case "theme": ToggleTheme(); break;
                case "appearance": ShowReadingAppearance(); break;
                case "fontUp": settings.FontSize = Math.Min(30, settings.FontSize + 1); ApplyEditorSettings(); break;
                case "fontDown": settings.FontSize = Math.Max(10, settings.FontSize - 1); ApplyEditorSettings(); break;
                case "find": ShowSearch(false); break;
                case "replace": ShowSearch(true); break;
                case "cancelSearch": CancelSearch(); break;
                case "hideFind": HideSearch(); break;
                case "next": MoveSearch(1); break;
                case "previous": MoveSearch(-1); break;
                case "replaceOne": ReplaceOne(); break;
                case "replaceAll": ReplaceAll(); break;
                case "goto": if (tab is not null) GoToLine(tab); break;
                case "outline":
                    settings.Sidebar = true;
                    if (focusMode) ToggleFocus();
                    ApplySidebar(); SidebarTabs.SelectedItem = OutlineTab; OutlineList.Focus();
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
                case "clearRecent": ClearRecentHistory(); break;
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
                case "formatJson":
                    if (tab is { Kind: TextKind.Json or TextKind.Jsonc, IsReading: false })
                        FormatCurrentDocument(tab);
                    break;
                case "insertMarkdownImage":
                    await ChooseMarkdownImagesAsync();
                    break;
                case "showAnnotations":
                    if (tab is not null) ShowAnnotationPanel();
                    break;
                case "currentBookmarks":
                    ShowCurrentBookmarks();
                    break;
                case "readingList":
                    ShowReadingList();
                    break;
                case "addReadingList":
                    await AddCurrentToReadingListAsync();
                    break;
                case "markRead":
                    await MarkCurrentReadAsync();
                    break;
                case "toggleCurrentDraft":
                    await ToggleCurrentDraftAsync();
                    break;
                case "recoveryDetails":
                    ShowRecoveryDetails();
                    break;
                case "structure": ToggleStructure(); break;
                case "markdownPreview": ToggleMarkdownPreview(); break;
                case "copyReading": await ShareReadingWithProgressAsync(false); break;
                case "copyPlain": CopyReadingPlainText(); break;
                case "copyMarkdown": CopyReadingMarkdown(); break;
                case "copyCodeBlock": CopyReadingCodeBlock(); break;
                case "copyTable": CopyReadingTable(); break;
                case "exportHtml": await ShareReadingWithProgressAsync(true); break;
                case "exportBriefMarkdown": await ExportReadingBriefAsync(false); break;
                case "exportBriefHtml": await ExportReadingBriefAsync(true); break;
                case "drafts": await ShowDraftSettingsAsync(); break;
                case "externalAccept": await AcceptExternalVersionAsync(); break;
                case "externalKeep": await KeepExternalEditAsync(); break;
                case "externalRetry": RetryExternalRefresh(); break;
                case "help":
                    Modal(() => Dialogs.CreateHelp(this, false).ShowDialog());
                    break;
                case "about": Modal(() => Dialogs.CreateHelp(this, true).ShowDialog()); break;
                case "update": await CheckForUpdatesAsync(manual: true); break;
                default:
                    if (action.StartsWith("width:") && double.TryParse(action[6..], out double width))
                    {
                        settings.ReadingWidth = ReaderSettings.NormalizeMenuReadingWidth(width);
                        UpdateReadingWidths();
                        SchedulePersist();
                    }
                    else if (action.StartsWith("tab:") && int.TryParse(action[4..], out int number) && tabs.Count > 0)
                        SelectTab(tabs[number == 9 ? tabs.Count - 1 : Math.Min(tabs.Count - 1, number - 1)]);
                    else if (action.StartsWith("reopen:") && tab is not null &&
                        Enum.TryParse<TextCodec>(action[7..], out var codec))
                        await ReopenWithCodecAsync(tab, codec);
                    else if (action.StartsWith("bookmark:", StringComparison.Ordinal) &&
                        Guid.TryParse(action[9..], out var bookmarkId) &&
                        bookmarks.Items.FirstOrDefault(item => item.Id == bookmarkId) is { } bookmark)
                        NavigateToBookmark(bookmark);
                    break;
            }
        }
        catch (Exception ex) { Notify(ex.Message, "操作未完成"); }
    }

    private bool CanExecuteAction(string action)
    {
        if (exitInProgress) return false;
        if (modalDepth > 0)
        {
            return action is "clearRecent" or "help" or "recoveryDetails";
        }
        if (restoringWorkspace) return false;
        if (action is "clearRecent" or "help" or "recoveryDetails") return true;

        var tab = CurrentTab;
        if (action is "undo" or "redo")
        {
            if (tab is { IsReading: true } && FocusedTextCommandTarget() is null) return false;
            if (FocusedTextCommandTarget() is not { } target) return false;
            return action == "undo"
                ? ApplicationCommands.Undo.CanExecute(null, target)
                : ApplicationCommands.Redo.CanExecute(null, target);
        }

        if (action == "formatJson")
            return tab is { Kind: TextKind.Json, IsReading: false };
        if (action is "copyPath" or "reveal" or "reload" ||
            action.StartsWith("reopen:", StringComparison.Ordinal))
            return tab?.File.Path is not null;
        if (action.StartsWith("tab:", StringComparison.Ordinal) &&
            int.TryParse(action[4..], out int number))
            return tabs.Count > 0 && (number == 9 || number - 1 < tabs.Count);
        if (action == "moveLeft")
            return tab is not null && tabs.IndexOf(tab) > 0;
        if (action == "moveRight")
            return tab is not null && tabs.IndexOf(tab) >= 0 &&
                tabs.IndexOf(tab) < tabs.Count - 1;
        return tab is not null;
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

    private void SaveAllDocuments()
    {
        if (savingAll || restoringWorkspace) return;
        var selected = CurrentTab;
        savingAll = true;
        int saved = 0;
        try
        {
            var ids = tabs.Where(item => item.File.IsDirty && !item.IsEmpty)
                .Select(item => item.File.Id).ToArray();
            foreach (var id in ids)
            {
                var item = tabs.FirstOrDefault(candidate => candidate.File.Id == id);
                if (item is null) continue;
                // macOS activates each document so save/cancel feedback and
                // Save As panels always refer to the document being handled.
                SelectTab(item);
                if (!SaveDocument(item))
                {
                    StatusText.Text = $"保存全部已停止 · 已保存 {saved} 份，其余内容仍保留";
                    return;
                }
                saved++;
            }
            StatusText.Text = $"保存全部完成 · {saved} 份";
        }
        finally
        {
            savingAll = false;
            if (selected is not null && tabs.Contains(selected)) SelectTab(selected);
        }
    }

    private void CloseOtherTabs(EditorTab? keep)
    {
        if (keep is null || !tabs.Contains(keep)) return;
        var candidates = tabs.Where(item => item != keep).Select(item => item.File.Id).Reverse().ToArray();
        foreach (var id in candidates)
        {
            var item = tabs.FirstOrDefault(candidate => candidate.File.Id == id);
            if (item is not null && !CloseTab(item, false)) break;
        }
        if (tabs.Contains(keep)) SelectTab(keep);
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

    private async Task ReopenWithCodecAsync(EditorTab tab, TextCodec codec)
    {
        if (tab.File.Path is null) { Notify("未命名文档没有可重新读取的磁盘文件。"); return; }
        await ReloadDocumentAsync(tab, codec);
    }

    private async void KindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || CurrentTab is not { } tab || KindCombo.SelectedItem is not TextKind kind) return;
        int anchor = CurrentSourceAnchor(tab);
        bool wasReading = tab.IsReading;
        tab.AutomaticKind = false;
        tab.Kind = kind;
        tab.ReleaseReadingResources();
        tab.IsReading = false;
        if (wasReading)
        {
            if (kind != TextKind.Markdown && tab.File.Text.Length <= 200_000 &&
                PreviewLimits.CanRender(tab.File.Text, kind))
            {
                EnterReading(tab, anchor, focus: false, snapToBlockStart: false);
            }
            else
            {
                RefreshCurrent();
                await EnterReadingAsync(tab, anchor, focus: false, snapToBlockStart: false);
            }
        }
        if (CurrentTab == tab) RefreshCurrent();
        SchedulePersist();
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
        if (settings.Recent.Count > 15) settings.Recent.RemoveRange(15, settings.Recent.Count - 15);
        RefreshRecent();
    }

    private void ClearRecentHistory()
    {
        settings.ClearRecentHistory();
        RefreshRecent();
        SchedulePersist();
    }

    public void RefreshRecent()
    {
        // The sidebar mirrors macOS refreshRecent(): stale entries remain in
        // the File → Recent menu, but the visible sidebar only lists existing files.
        var existing = settings.Recent.Where(File.Exists).Select(p => new RecentItem(p)).ToArray();
        RecentList.ItemsSource = existing;
        RecentEmptyHint.Visibility = existing.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshRecentMenu();
    }

    private void RecentMenuOpened(object sender, RoutedEventArgs e) => RefreshRecentMenu();

    private void RefreshRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var path in settings.Recent)
        {
            string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            string label = Path.GetFileName(path).Replace("_", "__");
            if (!string.IsNullOrWhiteSpace(folder)) label += " — " + folder.Replace("_", "__");
            var item = new MenuItem { Header = label, ToolTip = path };
            item.Click += async (_, _) =>
            {
                if (!File.Exists(path)) { Notify("最近文件当前不存在或无法访问：\n" + path); return; }
                await OpenPathsAsync([path]);
            };
            RecentMenu.Items.Add(item);
        }
        if (RecentMenu.Items.Count == 0) RecentMenu.Items.Add(new MenuItem { Header = "暂无最近文件", IsEnabled = false });
        RecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "清除最近文件与位置", Tag = "clearRecent" };
        clear.Click += (_, _) => ClearRecentHistory();
        RecentMenu.Items.Add(clear);
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

    private async void TabContextAction(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Parent is not ContextMenu menu
            || menu.PlacementTarget is not FrameworkElement target
            || target.DataContext is not EditorTab tab
            || item.Tag is not string action) return;
        e.Handled = true;
        switch (action)
        {
            case "rename":
                SelectTab(tab);
                RenamePrompt(tab);
                break;
            case "closeTab":
                CloseTab(tab, ensureDocument: false, closeWindowIfEmpty: true);
                break;
            case "closeOthers":
                CloseOtherTabs(tab);
                break;
            case "closeAll":
                foreach (var other in tabs.ToArray().Reverse())
                    if (!CloseTab(other, ensureDocument: false, closeWindowIfEmpty: false)) break;
                if (tabs.Count == 0 && !exitInProgress) Close();
                break;
            case "moveLeft":
                SelectTab(tab); MoveTab(-1);
                break;
            case "moveRight":
                SelectTab(tab); MoveTab(1);
                break;
            case "toggleCurrentDraft":
                SelectTab(tab);
                await ToggleCurrentDraftAsync();
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
        int index = tabs.IndexOf(tab);
        bool hasPath = tab.File.Path is not null;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            switch (item.Tag)
            {
                case "moveLeft": item.IsEnabled = index > 0; break;
                case "moveRight": item.IsEnabled = index >= 0 && index < tabs.Count - 1; break;
                case "closeOthers": item.IsEnabled = tabs.Count > 1; break;
                case "copyPath":
                case "revealPath": item.IsEnabled = hasPath; break;
                case "toggleCurrentDraft":
                    item.IsCheckable = true;
                    item.IsChecked = tab.RecoverDraft;
                    break;
            }
        }
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
        var opened = tabs.Select(tab => new QuickOpenItem(
            "tab:" + tab.File.Id,
            tab.File.Title,
            tab.File.Path ?? "未保存的文本",
            true)).ToArray();
        var openedPaths = tabs.Where(tab => tab.File.Path is not null)
            .Select(tab => tab.File.Path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recent = settings.Recent
            .Where(path => !openedPaths.Contains(path))
            .Select(path => new QuickOpenItem(
                "path:" + path,
                Path.GetFileName(path),
                path,
                false));
        var selected = await QuickOpenDialog.ShowAsync(this, opened.Concat(recent).ToArray());
        if (selected is null) return;
        if (selected.Id.StartsWith("tab:", StringComparison.Ordinal)
            && Guid.TryParse(selected.Id[4..], out var id)
            && tabs.FirstOrDefault(tab => tab.File.Id == id) is { } tabToSelect)
            SelectTab(tabToSelect);
        else if (selected.Id.StartsWith("path:", StringComparison.Ordinal))
            await OpenPathsAsync([selected.Id[5..]]);
    }
}
