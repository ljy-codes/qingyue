using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
using Qingyue.Windows;

internal static class ThemeChecks
{
    public static void Run(MainWindow window, Action<bool, string> check)
    {
        foreach (var theme in ReaderTheme.All)
        {
            foreach (bool dark in new[] { false, true })
            {
                var palette = ReaderTheme.GetPalette(theme, dark);
                string mode = dark ? "深色" : "浅色";
                check(palette.Theme == theme && palette.Dark == dark,
                    $"{theme} {mode} 调色板保留主题身份和明暗状态");
                CheckContrast(check, palette.Text, palette.Editor, 4.5,
                    $"{theme} {mode} 正文/编辑器对比度");
                CheckContrast(check, palette.Muted, palette.Panel, 4.5,
                    $"{theme} {mode} 辅助文字/面板对比度");
                CheckContrast(check, palette.Accent, palette.Editor, 3.0,
                    $"{theme} {mode} 强调色/编辑器对比度");
                CheckContrast(check, palette.AccentText, palette.Editor, 4.5,
                    $"{theme} {mode} 强调文字/编辑器对比度");
                CheckContrast(check, palette.AccentForeground, palette.Accent, 4.5,
                    $"{theme} {mode} 强调按钮前景/强调色对比度");
                CheckContrast(check, palette.Text, palette.Main, 4.5,
                    $"{theme} {mode} 正文/主区域对比度");
                CheckContrast(check, palette.Text, palette.Hover, 4.5,
                    $"{theme} {mode} 正文/悬停区域对比度");

                var resources = new ResourceDictionary();
                var applied = ReaderTheme.Apply(resources, theme, dark);
                check(applied == palette
                    && ResourceColor(resources, "TextBrush") == palette.Text
                    && ResourceColor(resources, "EditorBrush") == palette.Editor
                    && ResourceColor(resources, "MutedBrush") == palette.Muted
                    && ResourceColor(resources, "PanelBrush") == palette.Panel
                    && ResourceColor(resources, "AccentBrush") == palette.Accent
                    && ResourceColor(resources, "AccentTextBrush") == palette.AccentText
                    && ResourceColor(resources, "AccentForegroundBrush") == palette.AccentForeground,
                    $"{theme} {mode} 实际资源画刷注册并匹配调色板");
            }
        }

        var titleBarType = typeof(MainWindow).Assembly.GetType("Qingyue.Windows.NativeTitleBarTheme");
        var colorsMethod = titleBarType?.GetMethod(
            "GetColors", BindingFlags.Public | BindingFlags.Static, null, [typeof(ThemePalette)], null);
        check(colorsMethod is not null, "原生标题栏接口接收 ThemePalette");
        if (colorsMethod is not null)
        {
            var palette = ReaderTheme.GetPalette(ReaderThemeKind.Coffee, dark: true);
            var colors = colorsMethod.Invoke(null, [palette]);
            check(colors is not null
                && Property<string>(colors, "Caption") == palette.Panel
                && Property<string>(colors, "Text") == palette.Text
                && Property<string>(colors, "Border") == palette.Border
                && Property<bool>(colors, "Dark") == palette.Dark,
                "原生标题栏颜色实际来自传入调色板");
        }

        window.ApplyTheme(dark: false);
        window.ApplyTheme(ReaderThemeKind.Coffee);
        CheckAppliedTheme(window, check, ReaderThemeKind.Coffee, dark: false);
        window.ApplyTheme(dark: true);
        window.ApplyTheme(ReaderThemeKind.Coffee);
        CheckAppliedTheme(window, check, ReaderThemeKind.Coffee, dark: true);
    }

    private static void CheckAppliedTheme(
        MainWindow window, Action<bool, string> check, ReaderThemeKind theme, bool dark)
    {
        var expected = ReaderTheme.GetPalette(theme, dark);
        check(ResourceColor(window.Resources, "AccentBrush") == expected.Accent
            && ResourceColor(window.Resources, "AccentForegroundBrush") == expected.AccentForeground
            && ResourceColor(window.Resources, "TextBrush") == expected.Text,
            $"ApplyTheme 保留 {theme} 的{(dark ? "深色" : "浅色")}身份");
    }

    private static void CheckContrast(
        Action<bool, string> check, string foreground, string background, double minimum, string name)
    {
        var actual = ReaderTheme.Contrast(foreground, background);
        check(actual >= minimum, $"{name} >= {minimum:0.0}:1（实际 {actual:0.00}:1）");
    }

    private static string ResourceColor(ResourceDictionary resources, string key) =>
        resources[key] is SolidColorBrush brush
            ? string.Create(CultureInfo.InvariantCulture,
                $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}")
            : string.Empty;

    private static T? Property<T>(object value, string name) =>
        (T?)value.GetType().GetProperty(name)?.GetValue(value);
}
