using System.Diagnostics;
using System.Windows.Threading;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private void ScheduleStartupUpdateCheck()
    {
        if (!startupUpdateCheckEnabled || settings.LastUpdateCheckUtc is { } last &&
            DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24))
            return;

        Dispatcher.BeginInvoke(new Action(() => _ = CheckForUpdatesAsync(manual: false)),
            DispatcherPriority.ApplicationIdle);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (exitInProgress) return;
        settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        try { settings.Save(dataDirectory); }
        catch { /* Update throttling is best effort and must never block the window. */ }

        var result = await updateService.CheckAsync();
        if (exitInProgress) return;
        if (result.Error is { } error)
        {
            if (manual) StatusText.Text = error;
            return;
        }

        if (!result.IsAvailable)
        {
            if (manual) StatusText.Text = $"当前已是最新版本（{result.CurrentVersion}）。";
            return;
        }

        StatusText.Text = $"发现新版本 {result.LatestVersion}，可在帮助菜单打开发布页。";
        if (manual && result.ReleaseUrl is { } url)
            Modal(() => Dialogs.UpdateAvailable(this, result, () => OpenReleasePage(url)));
    }

    private void OpenReleasePage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Notify("无法打开发布页：\n" + error.Message);
        }
    }
}
