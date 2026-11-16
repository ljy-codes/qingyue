namespace Qingyue.Core;

public sealed record ReadingPresentation(
    string FontFamily,
    double FontSize,
    double LineHeight,
    double ParagraphSpacing)
{
    public static ReadingPresentation Default { get; } =
        new("Microsoft YaHei UI", 15, 1.75, 13);

    public ReadingPresentation Normalize()
    {
        string family = string.IsNullOrWhiteSpace(FontFamily)
            ? Default.FontFamily
            : FontFamily.Trim();
        double size = double.IsFinite(FontSize)
            ? Math.Clamp(FontSize, 10, 30)
            : Default.FontSize;
        double lineHeight = double.IsFinite(LineHeight)
            ? Math.Clamp(LineHeight, 1.1, 3.2)
            : Default.LineHeight;
        double paragraphSpacing = double.IsFinite(ParagraphSpacing)
            ? Math.Clamp(ParagraphSpacing, 0, 48)
            : Default.ParagraphSpacing;
        return new(family, size, lineHeight, paragraphSpacing);
    }
}
