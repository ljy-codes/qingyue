using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private void SchedulePersist()
    {
        if (!initialized || exitInProgress) return;
        persistTimer.Stop(); persistTimer.Start();
    }

    private void ScheduleBookmarkSave()
    {
        if (!initialized || exitInProgress) return;
        bookmarkSaveTimer.Stop();
        bookmarkSaveTimer.Start();
    }

    private async Task<BookmarkSaveResult> SaveBookmarksAsync()
    {
        if (!bookmarksDirty)
            return new BookmarkSaveResult(DateTimeOffset.UtcNow, []);
        if (bookmarkSaveTask is not null)
        {
            return await bookmarkSaveTask;
        }
        var save = bookmarks.SaveAsync();
        bookmarkSaveTask = save;
        try
        {
            var result = await save;
            if (result.Errors.Count == 0) bookmarksDirty = false;
            return result;
        }
        finally { bookmarkSaveTask = null; }
    }

    private async Task<ReadingListSaveResult> SaveReadingListAsync()
    {
        if (!readingListDirty)
            return new ReadingListSaveResult(DateTimeOffset.UtcNow, []);
        if (readingListSaveTask is not null)
        {
            return await readingListSaveTask;
        }
        var save = readingList.SaveAsync();
        readingListSaveTask = save;
        try
        {
            var result = await save;
            if (result.Errors.Count == 0) readingListDirty = false;
            return result;
        }
        finally { readingListSaveTask = null; }
    }

    public async Task<WorkspaceSaveResult> PersistWorkspaceAsync()
    {
        try { await PrepareRecoveryAsync(); }
        catch (Exception ex)
        {
            return new WorkspaceSaveResult(DateTimeOffset.UtcNow, ["恢复草稿准备失败，原有数据仍保留：" + ex.Message]);
        }
        foreach (var tab in tabs.Where(item => !item.IsEmpty && item.File.Path is not null))
            RememberRecentPosition(tab);
        // Capture UI-owned state before the save await. File I/O is serialized by WorkspaceStore.
        var retained = tabs.Where(t => !discardedAtExit.Contains(t.File.Id) && !t.IsEmpty).ToArray();
        var entries = retained.Select(t => CaptureWorkspaceEntry(t) with
        {
            RecoverDraft = CanRecover(t),
            SelectionStart = SourceOffset(t, t.Editor.SelectionStart),
            SelectionLength = SourceOffset(t, t.Editor.SelectionStart + t.Editor.SelectionLength)
                - SourceOffset(t, t.Editor.SelectionStart)
        }).ToList();
        var snapshots = retained.Where(t => CanRecover(t) && (t.File.IsDirty || t.File.Path is null))
            .Select(t => t.File.CaptureSnapshot()).ToArray();
        var activeId = entries.Any(e => e.Id == CurrentTab?.File.Id) ? CurrentTab?.File.Id : entries.LastOrDefault()?.Id;
        var session = new WorkspaceSession(1, entries, activeId);
        var result = await workspace.SaveAsync(session, snapshots, openedRecoveryDocuments);
        try
        {
            if (WindowState == WindowState.Normal) { settings.Width = Width; settings.Height = Height; }
            settings.Save(dataDirectory);
        }
        catch (Exception ex) { result.Errors.Add("偏好保存失败：" + ex.Message); }
        var bookmarkResult = await SaveBookmarksAsync();
        result.Errors.AddRange(bookmarkResult.Errors);
        bool readingListWasDirty = readingListDirty;
        var readingListResult = await SaveReadingListAsync();
        result.Errors.AddRange(readingListResult.Errors);
        if (readingListWasDirty) readingListWindow?.ShowSaveError(readingListResult.Errors);
        var annotationResult = await SaveAnnotationsAsync();
        result.Errors.AddRange(annotationResult.Errors);
        return result;
    }

    private bool CanRecover(EditorTab tab) => settings.RecoveryEnabled && tab.RecoverDraft &&
        (tab.File.Path is null || !settings.ExcludedDraftPaths.Contains(tab.File.Path, StringComparer.OrdinalIgnoreCase));

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (approvedShutdown) return;
        e.Cancel = true;
        if (exitInProgress || modalDepth > 0 || closingToBackground) return;
        if (restoringWorkspace)
        {
            StatusText.Text = "正在恢复上次会话，请稍候再关闭窗口。";
            return;
        }
        _ = CloseToBackgroundAsync();
    }

    private async Task CloseToBackgroundAsync()
    {
        closingToBackground = true;
        exitInProgress = true; persistTimer.Stop();
        Action? restoreEditing = null;
        bool hidden = false;
        try
        {
            restoreEditing = FreezeRecoveryWorkspace();
            foreach (var tab in tabs.Where(t => t.File.IsDirty && !t.IsEmpty).ToArray())
            {
                // macOS window close allows save/cancel/discard, not keep-draft;
                // keep-draft belongs to the explicit Quit flow.
                SelectTab(tab);
                var choice = Modal(() => Dialogs.CloseChoice(this, tab.File.Title, allowKeep: false));
                if (choice == "cancel" || choice == "save" && !SaveBeforeClosing(tab)) return;
            }
            var result = await PersistWorkspaceAsync();
            if (result.Errors.Count > 0)
            {
                Notify("会话或草稿未能保存。窗口仍保留，请保存重要文档后重试。\n\n" +
                    string.Join("\n", result.Errors.Take(5)));
                return;
            }
            await AwaitMarkdownImageCompletionAsync();
            searchGeneration++;
            Hide();
            IsHiddenToBackground = true;
            hidden = true;
        }
        catch (Exception ex)
        {
            Notify("关闭窗口未完成，当前文档仍保留。\n" + ex.Message);
        }
        finally
        {
            restoreEditing?.Invoke();
            exitInProgress = false;
            closingToBackground = false;
            if (!hidden)
            {
                discardedAtExit.Clear();
                SchedulePersist();
            }
        }
    }

    /// <summary>
    /// Runs the full macOS-style Quit flow. Window close is deliberately a
    /// separate hide-only operation; this method is the only one that ends the
    /// application and is connected to Ctrl+Q / File → Quit.
    /// </summary>
    public async Task<bool> RequestQuitAsync()
    {
        Dispatcher.VerifyAccess();
        if (exitInProgress || modalDepth > 0 || closingToBackground) return false;
        if (restoringWorkspace)
        {
            StatusText.Text = "正在恢复上次会话，请稍候再退出。";
            return false;
        }

        exitInProgress = true; persistTimer.Stop();
        Action? restoreEditing = null;
        bool completed = false;
        try
        {
            restoreEditing = FreezeRecoveryWorkspace();
            foreach (var tab in tabs.Where(t => t.File.IsDirty && !t.IsEmpty).ToArray())
            {
                SelectTab(tab);
                var choice = Modal(() => Dialogs.CloseChoice(this, tab.File.Title, CanRecover(tab)));
                if (choice == "cancel" || choice == "save" && !SaveBeforeClosing(tab)) return false;
                if (choice == "discard") discardedAtExit.Add(tab.File.Id);
            }
            var result = await PersistWorkspaceAsync();
            if (result.Errors.Count > 0)
            {
                Notify("会话或草稿未能保存。窗口仍保留，请保存重要文档后重试。\n\n" +
                    string.Join("\n", result.Errors.Take(5)));
                return false;
            }
            await AwaitMarkdownImageCompletionAsync();
            readingListWindow?.Close();
            annotationPanelWindow?.Close();
            CloseAuxiliaryWindows();
            approvedShutdown = true; Close();
            // Another Closing handler may cancel the final close.
            if (IsVisible)
            {
                approvedShutdown = false;
                Notify("退出被取消，窗口和编辑内容仍保留。");
                return false;
            }
            completed = true;
            QuitCompleted?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            Notify("退出未完成，窗口和编辑内容仍保留。\n" + ex.Message);
            return false;
        }
        finally
        {
            if (!completed)
            {
                approvedShutdown = false;
                restoreEditing?.Invoke();
                discardedAtExit.Clear(); exitInProgress = false; SchedulePersist();
            }
        }
    }

    public void CloseForShutdown()
    {
        readingListWindow?.Close();
        annotationPanelWindow?.Close();
        CloseAuxiliaryWindows();
        approvedShutdown = true; persistTimer.Stop(); bookmarkSaveTimer.Stop(); searchGeneration++;
        Close();
    }

    private void CloseAuxiliaryWindows()
    {
        foreach (var window in workspaceBrowserWindows.Where(window => window.IsVisible).ToArray())
            window.Close();
        workspaceBrowserWindows.Clear();
        foreach (var window in globalSearchWindows.Where(window => window.IsVisible).ToArray())
            window.Close();
        globalSearchWindows.Clear();
    }

    private async Task AwaitMarkdownImageCompletionAsync()
    {
        if (markdownImageTask is not { IsCompleted: false } task) return;
        try { await task; }
        catch (Exception) { /* The image task already owns attachment cleanup. */ }
    }

    private async Task<bool> ReloadDocumentAsync(EditorTab tab, TextCodec? codec = null)
    {
        if (tab.File.Path is not { } path || !tabs.Contains(tab) || CurrentTab != tab) return false;
        bool reopening = codec is not null;
        if (tab.File.IsDirty)
        {
            string title = reopening ? "按编码重新打开？" : "重新载入磁盘文件？";
            string description = reopening
                ? "当前未保存的内容将被替换。"
                : "当前尚未保存的修改将被放弃。";
            string confirmText = reopening ? "重新打开" : "重新载入";
            if (!Modal(() => Dialogs.Confirm(this, title, description, "取消", confirmText)))
                return false;
        }

        CancelQueuedExternalRefresh();
        try
        {
            var file = await Task.Run(() => DocumentFile.Open(path, codec));
            if (!tabs.Contains(tab) || CurrentTab != tab || approvedShutdown || exitInProgress)
                return false;
            var replacement = CreateDocumentTab(file);
            int index = tabs.IndexOf(tab);
            if (index < 0) return false;
            try { await workspace.ClearDraftAsync(tab.File.Id); }
            catch (Exception ex)
            {
                StatusText.Text = "旧恢复草稿暂时无法清除，已用新会话覆盖：" + ex.Message;
            }
            tab.ReleaseReadingResources();
            tabs[index] = replacement;
            SelectTab(replacement);
            if (replacement.Kind == TextKind.Markdown)
                await EnterReadingAsync(replacement, 0, focus: false, snapToBlockStart: false);
            else
            {
                replacement.Editor.CaretIndex = 0;
                replacement.Editor.Select(0, 0);
                replacement.Editor.Focus();
            }
            RefreshCurrent();
            RefreshBookmarks();
            RefreshAnnotations();
            RefreshAnnotationHighlights();
            var result = await PersistWorkspaceAsync();
            if (result.Errors.Count > 0)
                StatusText.Text = "重新载入成功，但恢复会话保存失败：" + result.Errors[0];
            else
                StatusText.Text = reopening ? "已按所选编码重新打开。" : "已重新载入磁盘文件。";
            return true;
        }
        catch (Exception ex)
        {
            Notify((reopening ? "按编码重新打开失败" : "重新载入失败") +
                "，当前标签内容仍保留：\n" + ex.Message);
            return false;
        }
    }

    private async Task ShowDraftSettingsAsync()
    {
        var dialog = Dialogs.Shell(this, "会话与恢复草稿", 540);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "恢复草稿仅保存在本机，不会自动写回原文件。敏感文档可单独关闭恢复。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18)
        });
        var restore = new CheckBox { Content = "启动时恢复上次会话", IsChecked = settings.RestoreSession, Margin = new Thickness(0, 8, 0, 8) };
        var recovery = new CheckBox { Content = "自动保存恢复草稿", IsChecked = settings.RecoveryEnabled, Margin = new Thickness(0, 8, 0, 8) };
        var current = new CheckBox { Content = "为当前文档保留恢复草稿", IsChecked = CurrentTab is { } t && t.RecoverDraft, Margin = new Thickness(0, 8, 0, 18) };
        var clear = new CheckBox { Content = "立即清除所有已保存的恢复草稿", Margin = new Thickness(0, 8, 0, 18) };
        panel.Children.Add(restore); panel.Children.Add(recovery); panel.Children.Add(current); panel.Children.Add(clear);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = "应用", IsDefault = true };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); panel.Children.Add(buttons);
        dialog.Content = panel;
        var tab = CurrentTab;
        if (Modal(() => dialog.ShowDialog()) != true) return;
        bool disableRecovery = settings.RecoveryEnabled && recovery.IsChecked != true;
        if (disableRecovery && Modal(() => MessageBox.Show(this,
            "关闭恢复草稿并清除已保存的本机草稿？\n\n当前编辑内容仍会保留在窗口中。",
            "会话与恢复草稿", MessageBoxButton.YesNo, MessageBoxImage.Warning)) != MessageBoxResult.Yes)
            return;
        bool disableCurrent = tab?.RecoverDraft == true && current.IsChecked != true;
        if (!disableRecovery && disableCurrent && Modal(() => MessageBox.Show(this,
            "排除当前文档的恢复草稿并清除已保存内容？\n\n当前编辑内容仍会保留在窗口中。",
            "会话与恢复草稿", MessageBoxButton.YesNo, MessageBoxImage.Warning)) != MessageBoxResult.Yes)
            return;
        settings.RestoreSession = restore.IsChecked == true;
        settings.RecoveryEnabled = recovery.IsChecked == true;
        if (tab is not null)
        {
            tab.RecoverDraft = current.IsChecked == true;
            if (tab.File.Path is { } path)
            {
                settings.ExcludedDraftPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                if (!tab.RecoverDraft) settings.ExcludedDraftPaths.Add(path);
            }
        }
        if (clear.IsChecked == true || disableRecovery)
        {
            // Keep the explicit clear meaningful: these open tabs remain excluded until re-enabled.
            foreach (var item in tabs) item.RecoverDraft = false;
            await workspace.ClearDraftsAsync();
        }
        else if (disableCurrent && tab is not null)
        {
            try { await workspace.ClearDraftAsync(tab.File.Id); }
            catch (Exception ex)
            {
                tab.RecoverDraft = true;
                if (tab.File.Path is { } path)
                    settings.ExcludedDraftPaths.RemoveAll(item =>
                        string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
                Notify("当前文档草稿未能清除，设置未完整应用：\n" + ex.Message, "恢复草稿");
                return;
            }
        }
        var result = await PersistWorkspaceAsync();
        if (result.Errors.Count > 0) Notify(string.Join("\n", result.Errors));
        else StatusText.Text = clear.IsChecked == true ? "恢复草稿已清除；当前文档需要单独重新启用恢复。" : "会话与恢复偏好已更新。";
    }
}
