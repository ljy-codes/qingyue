namespace Qingyue.Core;

public sealed record ReadingPresentation(
    string FontFamily,
    double FontSize,
    double LineSpacing,
    double ParagraphSpacing)
{
    public const double NaturalLineHeightFactor = 1.33;

    public static ReadingPresentation Default { get; } =
        new("Microsoft YaHei UI", 14, 6, 13);

    // Compatibility alias for callers compiled against the multiplier-era
    // property. New code must use LineSpacing / LineHeightPixels.
    public double LineHeight => LineSpacing;

    public double LineHeightPixels => FontSize * NaturalLineHeightFactor + LineSpacing;

    public ReadingPresentation Normalize()
    {
        string family = string.IsNullOrWhiteSpace(FontFamily)
            ? Default.FontFamily
            : FontFamily.Trim();
        double size = double.IsFinite(FontSize)
            ? Math.Clamp(FontSize, 10, 30)
            : Default.FontSize;
        double lineSpacing = double.IsFinite(LineSpacing)
            ? Math.Clamp(LineSpacing, 0, 48)
            : Default.LineSpacing;
        double paragraphSpacing = double.IsFinite(ParagraphSpacing)
            ? Math.Clamp(ParagraphSpacing, 0, 48)
            : Default.ParagraphSpacing;
        return new(family, size, lineSpacing, paragraphSpacing);
    }

    public double LineHeightFor(double fontSize) =>
        Math.Max(fontSize, fontSize * NaturalLineHeightFactor + LineSpacing);
}
