using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FSLTool.Views;

/// <summary>
/// Title bar of the app's windows (Windows 11): dark / light mode, caption and text colours of the current palette
/// (so the bar blends with the window background), rounded corners. Older Windows ignore the attributes.
/// </summary>
public static class WindowChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33,
                      DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;

    /// <summary>Hooks the window: applies the theme now (or once it has a handle). ThemeManager re-applies on change.</summary>
    public static void Attach(Window w)
    {
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Apply(w);
        else w.SourceInitialized += (_, _) => Apply(w);
    }

    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = ThemeManager.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int caption = ColorRef("Bg"), text = ColorRef("Text"), border = ColorRef("Line");
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    /// <summary>COLORREF (0x00BBGGRR) of a palette brush.</summary>
    private static int ColorRef(string key)
    {
        var c = ThemeManager.Brush(key) is SolidColorBrush b ? b.Color : Colors.Black;
        return c.R | (c.G << 8) | (c.B << 16);
    }
}
