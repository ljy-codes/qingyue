using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace Qingyue.Windows;

public readonly record struct NativeTitleBarColors(
    string Caption,
    string Text,
    string Border,
    bool Dark);

public static class NativeTitleBarTheme
{
    public static NativeTitleBarColors GetColors(ThemePalette palette) =>
        new(palette.Panel, palette.Text, palette.Border, palette.Dark);
}

public partial class MainWindow
{
    private const int DwmBorderColorAttribute = 34;
    private const int DwmCaptionColorAttribute = 35;
    private const int DwmTextColorAttribute = 36;
    private const int DwmUseImmersiveDarkModeAttribute = 20;
    private const int DwmUseImmersiveDarkModeLegacyAttribute = 19;

    private void ApplyNativeTitleBarTheme(ThemePalette palette)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            var colors = NativeTitleBarTheme.GetColors(palette);
            int captionColor = ToColorRef(ParseColor(colors.Caption));
            int textColor = ToColorRef(ParseColor(colors.Text));
            int borderColor = ToColorRef(ParseColor(colors.Border));

            int immersiveDarkMode = colors.Dark ? 1 : 0;
            SetDwmColor(handle, DwmUseImmersiveDarkModeAttribute, immersiveDarkMode);
            SetDwmColor(handle, DwmUseImmersiveDarkModeLegacyAttribute, immersiveDarkMode);
            SetDwmColor(handle, DwmCaptionColorAttribute, captionColor);
            SetDwmColor(handle, DwmTextColorAttribute, textColor);
            SetDwmColor(handle, DwmBorderColorAttribute, borderColor);
        }
        catch (DllNotFoundException)
        {
            // Keep the native default when DWM is unavailable.
        }
        catch (EntryPointNotFoundException)
        {
            // Keep the native default when the attribute is unavailable.
        }
    }

    private static void SetDwmColor(IntPtr handle, int attribute, int color)
    {
        _ = DwmSetWindowAttribute(handle, attribute, ref color, sizeof(int));
    }

    private static int ToColorRef(Color color) =>
        color.R | (color.G << 8) | (color.B << 16);

    private static Color ParseColor(string value) =>
        (Color)ColorConverter.ConvertFromString(value);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeValueSize);
}
