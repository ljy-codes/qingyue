using System.Windows.Media;

namespace Qingyue.Windows;

public static class ReadingFontCatalog
{
    public const string Fallback = "Microsoft YaHei UI";

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
        return IsInstalled(candidate) ? candidate : Fallback;
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
}
