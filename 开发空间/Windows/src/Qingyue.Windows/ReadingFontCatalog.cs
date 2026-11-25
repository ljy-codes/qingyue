using System.Windows.Media;

namespace Qingyue.Windows;

public static class ReadingFontCatalog
{
    public const string Fallback = "Microsoft YaHei UI";
    public const string Song = "SimSun";
    public const string SystemPreference = "system";

    public static IReadOnlyList<(string Value, string Label)> Preferences { get; } =
    [
        (Fallback, "微软雅黑（苹方替代）"),
        (Song, "宋体"),
        (SystemPreference, "系统字体")
    ];

    private static readonly string[] PreferredFonts =
    [
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "Segoe UI",
        "Noto Sans CJK SC",
        "SimSun",
        "Cascadia Mono"
    ];

    public static IReadOnlyList<string> Available =>
        PreferredFonts.Where(IsInstalled).ToArray();

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Fallback;
        string candidate = value.Trim();
        if (string.Equals(candidate, SystemPreference, StringComparison.OrdinalIgnoreCase))
            return SystemPreference;
        if (IsPingFangAlias(candidate)) return Fallback;
        if (IsSongAlias(candidate)) return IsInstalled(Song) ? Song : Fallback;
        return IsInstalled(candidate) ? candidate : Fallback;
    }

    public static string PreferenceLabel(string? value)
    {
        string normalized = Normalize(value);
        return Preferences.FirstOrDefault(item =>
                string.Equals(item.Value, normalized, StringComparison.OrdinalIgnoreCase))
            .Label ?? Preferences[0].Label;
    }

    public static string Resolve(string? value)
    {
        string normalized = Normalize(value);
        return string.Equals(normalized, SystemPreference, StringComparison.OrdinalIgnoreCase)
            ? System.Windows.SystemFonts.MessageFontFamily.Source
            : normalized;
    }

    private static bool IsInstalled(string value)
    {
        try
        {
            return Fonts.SystemFontFamilies.Any(font =>
                string.Equals(font.Source, value, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return string.Equals(value, Fallback, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsPingFangAlias(string value) =>
        value.Equals("PingFang SC", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("PingFangSC", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("PingFang", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("苹方", StringComparison.Ordinal);

    private static bool IsSongAlias(string value) =>
        value.Equals("Songti SC", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("SongtiSC", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Songti", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("宋体", StringComparison.Ordinal) ||
        value.Equals(Song, StringComparison.OrdinalIgnoreCase);
}
