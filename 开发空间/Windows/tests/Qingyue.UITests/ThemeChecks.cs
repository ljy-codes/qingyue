using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
using Qingyue.Windows;

internal static class ThemeChecks
{
    private static readonly Dictionary<(ReaderThemeKind Theme, bool Dark), SyntaxTokens> ExpectedSyntax = new()
    {
        [(ReaderThemeKind.Classic, false)] = new("#A64A0A", "#087563", "#175BB5", "#5C6D59"),
        [(ReaderThemeKind.Classic, true)] = new("#FFD39A", "#82DFC9", "#8DC5FF", "#C3C9BF"),
        [(ReaderThemeKind.Coffee, false)] = new("#A64A0A", "#087563", "#A75832", "#5C6D59"),
        [(ReaderThemeKind.Coffee, true)] = new("#9FCB9B", "#D9A05B", "#85B9EF", "#8C7667"),
        [(ReaderThemeKind.Celadon, false)] = new("#5B8C43", "#A96A1E", "#3E7C86", "#8A968B"),
        [(ReaderThemeKind.Celadon, true)] = new("#9FCB9B", "#D9B26A", "#85B9EF", "#8C9E92"),
        [(ReaderThemeKind.SeaSalt, false)] = new("#2F7D64", "#A96A1E", "#1660AB", "#7C929B"),
        [(ReaderThemeKind.SeaSalt, true)] = new("#9FCB9B", "#D9B26A", "#85B9EF", "#8198A4"),
        [(ReaderThemeKind.MistPurple, false)] = new("#5E7F6A", "#A96A1E", "#5B6FA8", "#8B8AA0"),
        [(ReaderThemeKind.MistPurple, true)] = new("#9FCB9B", "#D9B26A", "#9AB3FF", "#8D8CA3"),
        [(ReaderThemeKind.InkBlue, false)] = new("#5E7F6A", "#A96A1E", "#2A5FA8", "#8A93A6"),
        [(ReaderThemeKind.InkBlue, true)] = new("#9FCB9B", "#D9B26A", "#85B9EF", "#8D93A0"),
        [(ReaderThemeKind.Paper, false)] = new("#0F9F8F", "#B45309", "#6D5BD0", "#9CA3AF"),
        [(ReaderThemeKind.Paper, true)] = new("#4FD1C5", "#E3A15A", "#B3A6FF", "#8A919C")
    };

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

                var expected = ExpectedSyntax[(theme, dark)];
                check(palette.String == expected.String
                    && palette.Number == expected.Number
                    && palette.Key == expected.Key
                    && palette.Comment == expected.Comment,
                    $"{theme} {mode} 四个语法 token 与 macOS 固定 sRGB 完全一致" +
                    $"（实际 {palette.String}/{palette.Number}/{palette.Key}/{palette.Comment}）");

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
                check(ResourceColor(resources, "SyntaxStringBrush") == expected.String
                    && ResourceColor(resources, "SyntaxValueBrush") == expected.Number
                    && ResourceColor(resources, "SyntaxKeyBrush") == expected.Key
                    && ResourceColor(resources, "SyntaxCommentBrush") == expected.Comment,
                    $"{theme} {mode} 语法资源画刷直接使用 macOS 原始 token");
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

    private sealed record SyntaxTokens(string String, string Number, string Key, string Comment);
}
