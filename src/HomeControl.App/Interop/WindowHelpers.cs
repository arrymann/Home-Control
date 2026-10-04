using HomeControl.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using static HomeControl.Interop.NativeMethods;

namespace HomeControl.Interop;

internal static class WindowHelpers
{
    public static IntPtr GetHandle(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    /// <summary>Makes the DWM frame (border, shadow, caption) match the app theme.</summary>
    public static void SetDarkFrame(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    /// <summary>
    /// Colors the system caption buttons (minimize, maximize, close) of a window with a custom
    /// title bar to match the app theme; the system draws them, so they don't follow XAML.
    /// </summary>
    public static void SetCaptionButtonColors(AppWindowTitleBar titleBar, bool dark)
    {
        var foreground = dark ? Colors.White : Colors.Black;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = dark ? ColorHelper.FromArgb(0x5D, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x5C, 0x00, 0x00, 0x00);
        titleBar.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x09, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedBackgroundColor = dark ? ColorHelper.FromArgb(0x0A, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x06, 0x00, 0x00, 0x00);
    }

    /// <summary>Rounded corners on Windows 11 (ignored on Windows 10).</summary>
    public static void SetRoundedCorners(IntPtr hwnd)
    {
        var value = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
    }

    /// <summary>Scale factor (1.0 = 96 DPI) of the monitor containing a point.</summary>
    public static double GetScaleForPoint(PointInt32 point)
    {
        var monitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
    }

    public static PointInt32 GetCursorPosition() =>
        GetCursorPos(out var point) ? new PointInt32(point.X, point.Y) : new PointInt32(0, 0);

    /// <summary>
    /// Lets the tray context menu (a classic Win32 menu) use dark mode. Undocumented uxtheme
    /// export used by Explorer, Notepad and Windows Terminal; silently ignored where missing.
    /// </summary>
    public static void SetMenuTheme(ThemePreference preference)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            return;
        }

        try
        {
            // 1 = allow dark (follow the system), 2 = force dark, 3 = force light
            SetPreferredAppMode(preference switch
            {
                ThemePreference.Dark => 2,
                ThemePreference.Light => 3,
                _ => 1,
            });
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }
}
