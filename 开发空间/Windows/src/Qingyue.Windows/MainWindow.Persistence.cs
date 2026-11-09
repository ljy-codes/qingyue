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

    public async Task<WorkspaceSaveResult> PersistWorkspaceAsync()
    {
        // Capture UI-owned state before crossing any await. File I/O is serialized by WorkspaceStore.
        var retained = tabs.Where(t => !discardedAtExit.Contains(t.File.Id) && !t.IsEmpty).ToArray();
        var entries = retained.Select(t => new WorkspaceEntry(
            t.File.Id, t.File.Path, t.IsReading,
            t.IsReading && t.Rendered is not null && t.Reading is not null
                ? t.Reading.DisplayToSource(t.Rendered.OffsetAt(t.Preview.Selection.Start)) : SourceOffset(t, t.Editor.CaretIndex),
            SourceOffset(t, t.Editor.SelectionStart),
            SourceOffset(t, t.Editor.SelectionStart + t.Editor.SelectionLength) - SourceOffset(t, t.Editor.SelectionStart),
            CanRecover(t), t.AutomaticKind, t.Kind)).ToList();
        var snapshots = retained.Where(t => CanRecover(t) && (t.File.IsDirty || t.File.Path is null))
            .Select(t => t.File.CaptureSnapshot()).ToArray();
        var activeId = entries.Any(e => e.Id == CurrentTab?.File.Id) ? CurrentTab?.File.Id : entries.LastOrDefault()?.Id;
        var session = new WorkspaceSession(1, entries, activeId);
        var result = await workspace.SaveAsync(session, snapshots);
        try
        {
            if (WindowState == WindowState.Normal) { settings.Width = Width; settings.Height = Height; }
            settings.Save(dataDirectory);
        }
        catch (Exception ex) { result.Errors.Add("偏好保存失败：" + ex.Message); }
        return result;
    }

    private bool CanRecover(EditorTab tab) => settings.RecoveryEnabled && tab.RecoverDraft &&
        (tab.File.Path is null || !settings.ExcludedDraftPaths.Contains(tab.File.Path, StringComparer.OrdinalIgnoreCase));

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (approvedShutdown) return;
        e.Cancel = true;
        if (exitInProgress || modalDepth > 0) return;
        exitInProgress = true; persistTimer.Stop();
        try
        {
            foreach (var tab in tabs.Where(t => t.File.IsDirty && !t.IsEmpty).ToArray())
            {
                var choice = Modal(() => Dialogs.CloseChoice(this, tab.File.Title, CanRecover(tab)));
                if (choice == "cancel" || choice == "save" && !SaveDocument(tab)) return;
                if (choice == "discard") discardedAtExit.Add(tab.File.Id);
            }
            var result = await PersistWorkspaceAsync();
            if (result.Errors.Count > 0)
            {
                Notify("会话或草稿未能保存。窗口将保留，请保存重要文档后重试。\n\n" + string.Join("\n", result.Errors.Take(5)));
                return;
            }
            approvedShutdown = true; Close();
        }
        finally
        {
            if (!approvedShutdown)
            {
                discardedAtExit.Clear(); exitInProgress = false; SchedulePersist();
            }
        }
    }

    public void CloseForShutdown()
    {
        approvedShutdown = true; persistTimer.Stop(); searchGeneration++;
        Close();
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
        var current = new CheckBox { Content = "为当前文档保留恢复草稿", IsChecked = CurrentTab is { } t && CanRecover(t), Margin = new Thickness(0, 8, 0, 18) };
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
        if (clear.IsChecked == true)
        {
            // Keep the explicit clear meaningful: these open tabs remain excluded until re-enabled.
            foreach (var item in tabs) item.RecoverDraft = false;
            await workspace.ClearDraftsAsync();
        }
        var result = await PersistWorkspaceAsync();
        if (result.Errors.Count > 0) Notify(string.Join("\n", result.Errors));
        else StatusText.Text = clear.IsChecked == true ? "恢复草稿已清除；当前文档需要单独重新启用恢复。" : "会话与恢复偏好已更新。";
    }
}
