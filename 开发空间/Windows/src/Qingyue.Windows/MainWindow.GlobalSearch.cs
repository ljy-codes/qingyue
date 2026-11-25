using System.IO;
using System.Windows;
using Microsoft.Win32;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private readonly List<GlobalSearchWindow> globalSearchWindows = [];

    private async Task ShowGlobalSearchAsync()
    {
        string root = settings.WorkspaceRoot ?? "";
        if (!Directory.Exists(root))
        {
            var picker = new OpenFolderDialog { Title = "选择工作区" };
            if (Modal(() => picker.ShowDialog()) != true ||
                string.IsNullOrWhiteSpace(picker.FolderName)) return;
            root = Path.GetFullPath(picker.FolderName);
            settings.WorkspaceRoot = root;
            SchedulePersist();
        }
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Notify("请先打开一个本地工作区。");
            return;
        }

        globalSearchWindows.RemoveAll(window => !window.IsVisible);
        var search = new GlobalSearchWindow(
            this,
            root,
            match => _ = OpenGlobalSearchMatchAsync(match),
            path => !tabs.Any(tab => tab.File.Path is { } openPath &&
                string.Equals(CanonicalPath(openPath), CanonicalPath(path),
                    StringComparison.OrdinalIgnoreCase)));
        search.Closed += (_, _) => globalSearchWindows.Remove(search);
        globalSearchWindows.Add(search);
        search.Show();
        search.Activate();
        await Task.CompletedTask;
    }

    private static string CanonicalPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full;
        }
        catch { return Path.GetFullPath(path); }
    }

    private async Task OpenGlobalSearchMatchAsync(GlobalSearchMatch match)
    {
        await OpenPathsAsync([match.Path]);
        if (CurrentTab is not { } tab) return;
        if (tab.IsReading) ToggleReading();
        int sourceStart = match.SourceStart >= 0
            ? match.SourceStart
            : Math.Clamp(match.LineNumber > 0 ? match.LineNumber - 1 : 0, 0, tab.File.Text.Length);
        sourceStart = Math.Clamp(sourceStart, 0, tab.File.Text.Length);
        int start = EditorOffset(tab, sourceStart);
        tab.Editor.CaretIndex = start;
        tab.Editor.Select(start, 0);
        ScrollToCaret(tab);
        tab.Editor.Focus();
    }
}
