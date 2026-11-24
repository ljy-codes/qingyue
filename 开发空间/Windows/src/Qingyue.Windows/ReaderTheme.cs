using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Qingyue.Windows;

public enum ReaderThemeKind
{
    Classic,
    Coffee,
    Celadon,
    SeaSalt,
    MistPurple,
    InkBlue,
    Paper,

    // Source-compatible legacy values. Canonical() maps these to the shared
    // seven-theme model, while explicit values remain available to old callers.
    Dark,
    Light,
    WarmPaper
}

public sealed record ThemePalette(
    ReaderThemeKind Theme,
    bool Dark,
    string Window,
    string Panel,
    string Main,
    string Editor,
    string Reading,
    string ReadingPanel,
    string Text,
    string Muted,
    string Border,
    string Accent,
    string Selection,
    string String,
    string Number,
    string Key,
    string Comment,
    string Hover,
    string AccentText,
    string Unsaved,
    string Saved,
    string AccentForeground,
    string SearchHighlight,
    string SearchHighlightBorder,
    string AnnotationAmber,
    string AnnotationBlue,
    string AnnotationGreen,
    string AnnotationRose,
    string AnnotationViolet);

public static class ReaderTheme
{
    public static IReadOnlyList<ReaderThemeKind> All { get; } =
    [
        ReaderThemeKind.Classic,
        ReaderThemeKind.Coffee,
        ReaderThemeKind.Celadon,
        ReaderThemeKind.SeaSalt,
        ReaderThemeKind.MistPurple,
        ReaderThemeKind.InkBlue,
        ReaderThemeKind.Paper
    ];

    private sealed record Definition(
        uint Window,
        uint Panel,
        uint Editor,
        uint Text,
        uint Muted,
        uint Border,
        uint Accent,
        uint Selection,
        uint String,
        uint Number,
        uint Key,
        uint Comment,
        uint? Main = null,
        uint? Hover = null,
        uint? AccentText = null,
        uint? Unsaved = null,
        uint? Saved = null);

    public static string Label(ReaderThemeKind theme) => Canonical(theme) switch
    {
        ReaderThemeKind.Coffee => "咖色暖读",
        ReaderThemeKind.Celadon => "青瓷古色",
        ReaderThemeKind.SeaSalt => "海盐蓝",
        ReaderThemeKind.MistPurple => "烟雨紫",
        ReaderThemeKind.InkBlue => "墨蓝深色",
        ReaderThemeKind.Paper => "纸白极简",
        _ => "经典"
    };

    public static string Detail(ReaderThemeKind theme) => Canonical(theme) switch
    {
        ReaderThemeKind.Coffee => "暖纸底 · 褐橘",
        ReaderThemeKind.Celadon => "玉色底 · 碧山绿",
        ReaderThemeKind.SeaSalt => "湖水蓝 · 釉蓝",
        ReaderThemeKind.MistPurple => "烟雨白 · 紫幽兰",
        ReaderThemeKind.InkBlue => "深蓝黑 · 珊瑚红",
        ReaderThemeKind.Paper => "白卡 · 深紫 / 青绿",
        _ => "原有蓝灰 / 暖纸"
    };

    public static IReadOnlyList<string> Swatches(ReaderThemeKind theme) => Canonical(theme) switch
    {
        ReaderThemeKind.Coffee => ["#EDE3D1", "#FFF8E9", "#3C3026", "#A75832", "#E8D2B6"],
        ReaderThemeKind.Celadon => ["#EDF1E9", "#FBFCFB", "#2E3427", "#719847", "#DFE8D7"],
        ReaderThemeKind.SeaSalt => ["#EAF4F7", "#FFFFFF", "#1B2B33", "#1477A8", "#CDEAF7"],
        ReaderThemeKind.MistPurple => ["#EFF0F5", "#FCFCFE", "#2B2A35", "#686F8F", "#E4E2F2"],
        ReaderThemeKind.InkBlue => ["#E9ECF1", "#FBFBFC", "#1E3B7A", "#CA462F", "#F1D7D3"],
        ReaderThemeKind.Paper => ["#F3F4F6", "#FFFFFF", "#1F2937", "#6D5BD0", "#EDE9FE"],
        _ => ["#F6F4EF", "#EFEAE2", "#262A2F", "#2F6FDB", "#D7E5FA"]
    };

    private static Definition DefinitionFor(ReaderThemeKind theme, bool dark)
    {
        return Canonical(theme) switch
        {
            ReaderThemeKind.Coffee => dark ? CoffeeDark : CoffeeLight,
            ReaderThemeKind.Celadon => dark ? CeladonDark : CeladonLight,
            ReaderThemeKind.SeaSalt => dark ? SeaSaltDark : SeaSaltLight,
            ReaderThemeKind.MistPurple => dark ? MistPurpleDark : MistPurpleLight,
            ReaderThemeKind.InkBlue => dark ? InkBlueDark : InkBlueLight,
            ReaderThemeKind.Paper => dark ? PaperDark : PaperLight,
            _ => dark ? ClassicDark : ClassicLight
        };
    }

    private static readonly Definition ClassicDark = new(
        0x191B1F, 0x23262B, 0x30343A, 0xE8E2D8, 0xBEB8AE, 0x4B4D52, 0x4F86F7, 0x355A91,
        0xFFD39A, 0x82DFC9, 0x8DC5FF, 0xC3C9BF, 0x24354A, 0x34383F, 0x8DC5FF, 0xFFC078, 0x7BDEB3);

    private static readonly Definition ClassicLight = new(
        0xF6F4EF, 0xEFEAE2, 0xF8FAFC, 0x262A2F, 0x6B6A66, 0xD8D1C5, 0x2F6FDB, 0xD7E5FA,
        0xA64A0A, 0x087563, 0x175BB5, 0x5C6D59, 0xE6F0FA, 0xF3EDE4, 0x175BB5, 0xA95A00, 0x19764E);

    private static readonly Definition CoffeeDark = new(
        0x1A1310, 0x241A15, 0x1E1612, 0xEFDFD2, 0xA58F7E, 0x3A2B23, 0xE17A84, 0x4A2D31,
        0x9FCB9B, 0xD9A05B, 0x85B9EF, 0x8C7667, 0x2A1F18, 0x2F231B, 0xF0A0AA, 0xFFC078, 0x7BDEB3);

    private static readonly Definition CoffeeLight = new(
        0xEDE3D1, 0xE5D6C0, 0xFFF8E9, 0x3C3026, 0x6A5C4D, 0xCDB99C, 0xA75832, 0xE8D2B6,
        0xA64A0A, 0x087563, 0xA75832, 0x5C6D59, 0xE7EEF0, 0xF1E4D1, 0xA75832, 0xA95A00, 0x19764E);

    private static readonly Definition CeladonDark = new(
        0x101612, 0x18201B, 0x141A16, 0xE4EEE5, 0x94A799, 0x2B3A31, 0x8FBF6B, 0x31452F,
        0x9FCB9B, 0xD9B26A, 0x85B9EF, 0x8C9E92);

    private static readonly Definition CeladonLight = new(
        0xEDF1E9, 0xF6F8F4, 0xFBFCFB, 0x2E3427, 0x647267, 0xDDE3D8, 0x719847, 0xDFE8D7,
        0x5B8C43, 0xA96A1E, 0x3E7C86, 0x8A968B);

    private static readonly Definition SeaSaltDark = new(
        0x0B1419, 0x121D24, 0x0F181E, 0xE3EEF4, 0x8FA7B2, 0x23343D, 0x4FB3E8, 0x1E3E52,
        0x9FCB9B, 0xD9B26A, 0x85B9EF, 0x8198A4);

    private static readonly Definition SeaSaltLight = new(
        0xEAF4F7, 0xF5FAFB, 0xFFFFFF, 0x1B2B33, 0x62757E, 0xD5E4E8, 0x1477A8, 0xCDEAF7,
        0x2F7D64, 0xA96A1E, 0x1660AB, 0x7C929B);

    private static readonly Definition MistPurpleDark = new(
        0x111118, 0x191923, 0x15151E, 0xE8E7F0, 0x9C9BB2, 0x2D2D3D, 0x9AA2D6, 0x35354F,
        0x9FCB9B, 0xD9B26A, 0x9AB3FF, 0x8D8CA3);

    private static readonly Definition MistPurpleLight = new(
        0xEFF0F5, 0xF7F7FA, 0xFCFCFE, 0x2B2A35, 0x706F86, 0xDEDEE6, 0x686F8F, 0xE4E2F2,
        0x5E7F6A, 0xA96A1E, 0x5B6FA8, 0x8B8AA0);

    private static readonly Definition InkBlueDark = new(
        0x13151B, 0x1C1F26, 0x101114, 0xECE9E9, 0x9AA0AD, 0x2B2F3A, 0xCA462F, 0x642920,
        0x9FCB9B, 0xD9B26A, 0x85B9EF, 0x8D93A0);

    private static readonly Definition InkBlueLight = new(
        0xE9ECF1, 0xF4F5F8, 0xFBFBFC, 0x1E3B7A, 0x626E87, 0xD8DEE8, 0xCA462F, 0xF1D7D3,
        0x5E7F6A, 0xA96A1E, 0x2A5FA8, 0x8A93A6);

    private static readonly Definition PaperDark = new(
        0x111318, 0x171A21, 0x14171D, 0xE7EAF0, 0x9AA1AD, 0x2A2F38, 0x9B8BFF, 0x322B5A,
        0x4FD1C5, 0xE3A15A, 0xB3A6FF, 0x8A919C);

    private static readonly Definition PaperLight = new(
        0xF3F4F6, 0xFFFFFF, 0xFFFFFF, 0x1F2937, 0x6B7280, 0xE5E7EB, 0x6D5BD0, 0xEDE9FE,
        0x0F9F8F, 0xB45309, 0x6D5BD0, 0x9CA3AF);

    public static ThemePalette GetPalette(ReaderThemeKind theme, bool dark)
    {
        var id = Canonical(theme);
        var definition = DefinitionFor(id, dark);
        Color panel = ParseColor(definition.Panel);
        Color editor = ParseColor(definition.Editor);
        Color text = ParseColor(definition.Text);
        Color accent = ParseColor(definition.Accent);
        string main = Hex(definition.Main is { } mainValue
            ? ParseColor(mainValue)
            : Mix(panel, editor, 0.35));
        string hover = Hex(definition.Hover is { } hoverValue
            ? ParseColor(hoverValue)
            : Mix(panel, text, 0.06));
        string accentText = Hex(definition.AccentText is { } accentValue
            ? ParseColor(accentValue)
            : EnsureContrast(accent, editor, 4.5));
        string syntaxString = Hex(EnsureContrast(ParseColor(definition.String), editor, 4.5));
        string syntaxNumber = Hex(EnsureContrast(ParseColor(definition.Number), editor, 4.5));
        string syntaxKey = Hex(EnsureContrast(ParseColor(definition.Key), editor, 4.5));
        string syntaxComment = Hex(EnsureContrast(ParseColor(definition.Comment), editor, 4.5));
        bool darkSurface = RelativeLuminance(editor) < 0.35;
        string accentForeground = Contrast(Colors.White, accent) >= Contrast(ParseColor(0x11141A), accent)
            ? Hex(Colors.White)
            : Hex(ParseColor(0x11141A));
        string unsaved = Hex(ParseColor(definition.Unsaved ?? (dark ? 0xFFC078u : 0xA95A00u)));
        string saved = Hex(ParseColor(definition.Saved ?? (dark ? 0x7BDEB3u : 0x19764Eu)));

        return new(
            id, dark,
            Hex(definition.Window), Hex(panel), main, Hex(editor), Hex(editor), Hex(panel),
            Hex(text), Hex(definition.Muted), Hex(definition.Border), Hex(accent), Hex(definition.Selection),
            syntaxString, syntaxNumber, syntaxKey, syntaxComment,
            hover, accentText, unsaved, saved, accentForeground,
            darkSurface ? "#80F2C14D" : "#66D8A93A",
            darkSurface ? "#D5E1A128" : "#B3A46E20",
            darkSurface ? "#D6A66A" : "#A9661F",
            darkSurface ? "#78AEE8" : "#3974B4",
            darkSurface ? "#78C79A" : "#2E805D",
            darkSurface ? "#E28A96" : "#B55461",
            darkSurface ? "#A99BE0" : "#7359A9");
    }

    public static ThemePalette Apply(ResourceDictionary resources, ReaderThemeKind theme, bool dark)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var palette = GetPalette(theme, dark);
        var values = new Dictionary<string, string>
        {
            ["WindowBrush"] = palette.Window,
            ["PanelBrush"] = palette.Panel,
            ["MainBrush"] = palette.Main,
            ["EditorBrush"] = palette.Editor,
            ["ReadingBrush"] = palette.Reading,
            ["ReadingPanelBrush"] = palette.ReadingPanel,
            ["TextBrush"] = palette.Text,
            ["MutedBrush"] = palette.Muted,
            ["BorderBrush"] = palette.Border,
            ["AccentBrush"] = palette.Accent,
            ["SelectionBrush"] = palette.Selection,
            ["AccentTextBrush"] = palette.AccentText,
            ["SyntaxKeyBrush"] = palette.Key,
            ["SyntaxStringBrush"] = palette.String,
            ["SyntaxValueBrush"] = palette.Number,
            ["SyntaxCommentBrush"] = palette.Comment,
            ["UnsavedBrush"] = palette.Unsaved,
            ["SavedBrush"] = palette.Saved,
            ["AccentForegroundBrush"] = palette.AccentForeground,
            ["HoverBrush"] = palette.Hover,
            ["SearchHighlightBrush"] = palette.SearchHighlight,
            ["SearchHighlightBorderBrush"] = palette.SearchHighlightBorder,
            ["AnnotationAmberBrush"] = palette.AnnotationAmber,
            ["AnnotationBlueBrush"] = palette.AnnotationBlue,
            ["AnnotationGreenBrush"] = palette.AnnotationGreen,
            ["AnnotationRoseBrush"] = palette.AnnotationRose,
            ["AnnotationVioletBrush"] = palette.AnnotationViolet
        };
        foreach (var (key, value) in values)
            resources[key] = new SolidColorBrush(ParseColor(value));
        resources[SystemColors.MenuBrushKey] = resources["PanelBrush"];
        resources[SystemColors.MenuTextBrushKey] = resources["TextBrush"];
        resources[SystemColors.WindowBrushKey] = resources["PanelBrush"];
        resources[SystemColors.WindowTextBrushKey] = resources["TextBrush"];
        return palette;
    }

    public static ThemePalette Apply(ResourceDictionary resources, ReaderThemeKind theme) =>
        Apply(resources, Canonical(theme), theme == ReaderThemeKind.Dark);

    public static ReaderThemeKind Parse(string? value, bool darkFallback = true) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "classic" or "dark" or "light" or "经典" => ReaderThemeKind.Classic,
            "coffee" or "warmpaper" or "warm-paper" or "暖纸" or "咖色暖读" => ReaderThemeKind.Coffee,
            "celadon" or "青瓷古色" => ReaderThemeKind.Celadon,
            "seasalt" or "sea-salt" or "海盐蓝" => ReaderThemeKind.SeaSalt,
            "mistpurple" or "mist-purple" or "烟雨紫" => ReaderThemeKind.MistPurple,
            "inkblue" or "ink-blue" or "墨蓝深色" => ReaderThemeKind.InkBlue,
            "paper" or "纸白极简" => ReaderThemeKind.Paper,
            _ => ReaderThemeKind.Classic
        };

    public static string NormalizeName(string? value, bool darkFallback = true) =>
        Canonical(Parse(value, darkFallback)) switch
        {
            ReaderThemeKind.Coffee => "coffee",
            ReaderThemeKind.Celadon => "celadon",
            ReaderThemeKind.SeaSalt => "seaSalt",
            ReaderThemeKind.MistPurple => "mistPurple",
            ReaderThemeKind.InkBlue => "inkBlue",
            ReaderThemeKind.Paper => "paper",
            _ => "classic"
        };

    public static bool IsDark(ReaderThemeKind theme, bool dark) => dark;

    public static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static ReaderThemeKind Canonical(ReaderThemeKind theme) => theme switch
    {
        ReaderThemeKind.Coffee => ReaderThemeKind.Coffee,
        ReaderThemeKind.WarmPaper => ReaderThemeKind.Coffee,
        ReaderThemeKind.Celadon => ReaderThemeKind.Celadon,
        ReaderThemeKind.SeaSalt => ReaderThemeKind.SeaSalt,
        ReaderThemeKind.MistPurple => ReaderThemeKind.MistPurple,
        ReaderThemeKind.InkBlue => ReaderThemeKind.InkBlue,
        ReaderThemeKind.Paper => ReaderThemeKind.Paper,
        _ => ReaderThemeKind.Classic
    };

    public static double Contrast(string first, string second) =>
        Contrast(ParseColor(first), ParseColor(second));

    private static double Contrast(Color first, Color second)
    {
        double one = RelativeLuminance(first), two = RelativeLuminance(second);
        return (Math.Max(one, two) + .05) / (Math.Min(one, two) + .05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            double channel = value / 255d;
            return channel <= .03928 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }

    private static Color EnsureContrast(Color source, Color background, double target)
    {
        Color result = source;
        Color toward = RelativeLuminance(background) > .45 ? ParseColor(0x11141A) : Colors.White;
        for (int guard = 0; guard < 28 && Contrast(result, background) < target; guard++)
            result = Mix(result, toward, .08);
        return result;
    }

    private static Color Mix(Color first, Color second, double fraction)
    {
        double amount = Math.Clamp(fraction, 0, 1);
        static byte Blend(byte one, byte two, double amount) =>
            (byte)Math.Round(one + (two - one) * amount);
        return Color.FromRgb(Blend(first.R, second.R, amount), Blend(first.G, second.G, amount),
            Blend(first.B, second.B, amount));
    }

    private static Color ParseColor(uint value) =>
        Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    private static string Hex(uint value) => Hex(ParseColor(value));

    private static Color ParseColor(string value) =>
        (Color)ColorConverter.ConvertFromString(value);

    private static string Hex(Color color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
}
