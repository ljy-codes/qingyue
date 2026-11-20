using System.Windows;
using System.Windows.Media;

namespace Qingyue.Windows;

public enum ReaderThemeKind
{
    Dark,
    Light,
    WarmPaper
}

public sealed record ThemePalette(
    ReaderThemeKind Theme,
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
    string Hover,
    string SearchHighlight,
    string SearchHighlightBorder,
    string AnnotationAmber,
    string AnnotationBlue,
    string AnnotationGreen,
    string AnnotationRose,
    string AnnotationViolet);

public static class ReaderTheme
{
    private static readonly IReadOnlyDictionary<ReaderThemeKind, ThemePalette> Palettes =
        new Dictionary<ReaderThemeKind, ThemePalette>
        {
            [ReaderThemeKind.Dark] = new(
                ReaderThemeKind.Dark, "#191B1F", "#23262B", "#24354A", "#30343A", "#30343A", "#23262B",
                "#E8E2D8", "#BEB8AE", "#4B4D52", "#2F6FDB", "#355A91", "#34383F",
                "#80F2C14D", "#D5E1A128", "#B8792E", "#4F8CC9", "#4C9B73", "#C56B78", "#8870C2"),
            [ReaderThemeKind.Light] = new(
                ReaderThemeKind.Light, "#F6F4EF", "#EFEAE2", "#E6F0FA", "#F8FAFC", "#F8FAFC", "#EFEAE2",
                "#262A2F", "#6B6A66", "#D8D1C5", "#2F6FDB", "#D7E5FA", "#F3EDE4",
                "#80F2C14D", "#D5E1A128", "#A9661F", "#3974B4", "#2E805D", "#B55461", "#7359A9"),
            [ReaderThemeKind.WarmPaper] = new(
                ReaderThemeKind.WarmPaper, "#EDE3D1", "#E5D6C0", "#E7EEF0", "#FFF8E9", "#FFF8E9", "#E5D6C0",
                "#3C3026", "#786A5A", "#CDB99C", "#A75832", "#E8D2B6", "#F1E4D1",
                "#80B76E2C", "#D5A87845", "#A86524", "#4A7EA5", "#4E8860", "#B65C67", "#7860A5")
        };

    public static ThemePalette Apply(ResourceDictionary resources, ReaderThemeKind theme)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var palette = Palettes[theme];
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
            resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        resources[SystemColors.MenuBrushKey] = resources["PanelBrush"];
        resources[SystemColors.MenuTextBrushKey] = resources["TextBrush"];
        resources[SystemColors.WindowBrushKey] = resources["PanelBrush"];
        resources[SystemColors.WindowTextBrushKey] = resources["TextBrush"];
        return palette;
    }

    public static ReaderThemeKind Parse(string? value, bool darkFallback = true) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "light" => ReaderThemeKind.Light,
            "warmpaper" or "warm-paper" or "暖纸" => ReaderThemeKind.WarmPaper,
            "dark" => ReaderThemeKind.Dark,
            _ => darkFallback ? ReaderThemeKind.Dark : ReaderThemeKind.Light
        };

    public static string NormalizeName(string? value, bool darkFallback = true) =>
        Parse(value, darkFallback) switch
        {
            ReaderThemeKind.Dark => "Dark",
            ReaderThemeKind.Light => "Light",
            _ => "WarmPaper"
        };
}
