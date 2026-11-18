using System.Windows.Media.Imaging;

namespace Qingyue.Windows;

public enum AppIconThemeKind
{
    Sky,
    Sakura,
    Mint,
    Peach
}

public sealed record AppIconThemeOption(
    AppIconThemeKind Kind,
    string Label,
    string FileName);

public static class AppIconTheme
{
    public const AppIconThemeKind Default = AppIconThemeKind.Sky;

    public static IReadOnlyList<AppIconThemeOption> Options { get; } =
    [
        new(AppIconThemeKind.Sky, "天空蓝", "Qingyue-sky.png"),
        new(AppIconThemeKind.Sakura, "樱花紫", "Qingyue-sakura.png"),
        new(AppIconThemeKind.Mint, "薄荷珊瑚", "Qingyue-mint.png"),
        new(AppIconThemeKind.Peach, "蜜桃紫", "Qingyue-peach.png")
    ];

    public static AppIconThemeKind Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Default;
        if (Enum.TryParse<AppIconThemeKind>(value, ignoreCase: true, out var kind)
            && Options.Any(item => item.Kind == kind))
            return kind;
        var option = Options.FirstOrDefault(item =>
            string.Equals(item.Label, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return option?.Kind ?? Default;
    }

    public static string NormalizeName(string? value) => Parse(value).ToString();

    public static BitmapImage Load(string? value) => Load(Parse(value));

    public static BitmapImage Load(AppIconThemeKind kind)
    {
        var option = Options.FirstOrDefault(item => item.Kind == kind) ?? Options[0];
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(
            $"pack://application:,,,/Qingyue;component/Assets/{option.FileName}",
            UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
