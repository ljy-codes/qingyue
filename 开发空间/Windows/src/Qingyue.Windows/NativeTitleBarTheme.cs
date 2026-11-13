using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private const int DwmBorderColorAttribute = 34;
    private const int DwmCaptionColorAttribute = 35;
    private const int DwmTextColorAttribute = 36;
    private const int DwmUseImmersiveDarkModeAttribute = 20;
    private const int DwmUseImmersiveDarkModeLegacyAttribute = 19;

    private void ApplyNativeTitleBarTheme(bool dark)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            int captionColor = ToColorRef((Color)ColorConverter.ConvertFromString(
                dark ? "#23262B" : "#EFEAE2"));
            int textColor = ToColorRef((Color)ColorConverter.ConvertFromString(
                dark ? "#E8E2D8" : "#262A2F"));

            int immersiveDarkMode = dark ? 1 : 0;
            SetDwmColor(handle, DwmUseImmersiveDarkModeAttribute, immersiveDarkMode);
            SetDwmColor(handle, DwmUseImmersiveDarkModeLegacyAttribute, immersiveDarkMode);
            SetDwmColor(handle, DwmCaptionColorAttribute, captionColor);
            SetDwmColor(handle, DwmTextColorAttribute, textColor);
            SetDwmColor(handle, DwmBorderColorAttribute, captionColor);
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

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeValueSize);
}
