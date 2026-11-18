using System.IO;
using System.Windows.Media.Imaging;
using Qingyue.Windows;

internal static class IconThemeChecks
{
    public static void Run(Action<bool, string> check, MainWindow window, string root)
    {
        check(AppIconTheme.Options.Count == 4, "应用图标提供四套可切换主题");
        check(AppIconTheme.Parse(null) == AppIconThemeKind.Sky
            && AppIconTheme.Parse("invalid") == AppIconThemeKind.Sky,
            "未知图标主题默认回退天空蓝");
        check(AppIconTheme.NormalizeName("sakura") == "Sakura"
            && AppIconTheme.NormalizeName("mint") == "Mint"
            && AppIconTheme.NormalizeName("peach") == "Peach",
            "图标主题名称可规范化");

        string settingsRoot = Path.Combine(root, "icon-settings");
        var settings = new ReaderSettings { IconTheme = "Peach" };
        settings.Save(settingsRoot);
        var restored = ReaderSettings.Load(settingsRoot);
        check(restored.IconTheme == "Peach", "图标主题可以保存并恢复");

        File.WriteAllText(
            Path.Combine(settingsRoot, "Settings.json"),
            """{"Dark":true,"Theme":"Dark"}""");
        check(ReaderSettings.Load(settingsRoot).IconTheme == "Sky",
            "旧版缺失图标字段时默认天空蓝");

        check(window.Icon is BitmapImage image
            && image.UriSource?.AbsolutePath.EndsWith("/Assets/Qingyue-sky.png", StringComparison.OrdinalIgnoreCase) == true,
            "主窗口启动加载天空蓝图标");

        foreach (var option in AppIconTheme.Options)
        {
            window.ApplyAppIconTheme(option.Kind);
            check(window.Icon is BitmapImage themeImage
                && themeImage.UriSource?.AbsolutePath.EndsWith(
                    "/Assets/" + option.FileName, StringComparison.OrdinalIgnoreCase) == true,
                $"主窗口可以即时切换{option.Label}图标");
        }
        window.ApplyAppIconTheme(AppIconThemeKind.Sky);
    }
}
