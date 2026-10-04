using HomeControl.Core.Settings;
using HomeControl.Interop;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using static HomeControl.Interop.NativeMethods;

namespace HomeControl.Services;

/// <summary>
/// Tracks the Windows light/dark settings. Windows has two: one for apps (used by the popup
/// and settings window) and one for the taskbar/system (used for the tray icon).
/// </summary>
internal sealed class ThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly MessageWindow _window;

    public ThemeService(MessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
        Refresh();
    }

    /// <summary>Raised on the UI thread when either Windows theme setting changes.</summary>
    public event EventHandler? Changed;

    public bool AppsUseLightTheme { get; private set; } = true;

    public bool TaskbarUsesLightTheme { get; private set; }

    public ElementTheme Resolve(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.Dark => ElementTheme.Dark,
        _ => AppsUseLightTheme ? ElementTheme.Light : ElementTheme.Dark,
    };

    private void OnMessage(uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var relevant = message switch
        {
            WM_SETTINGCHANGE => lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet",
            WM_THEMECHANGED or WM_SYSCOLORCHANGE => true,
            _ => false,
        };

        if (relevant && Refresh())
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Re-reads the registry; returns true if anything changed.</summary>
    private bool Refresh()
    {
        bool apps = true, taskbar = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            apps = key?.GetValue("AppsUseLightTheme") is not int a || a != 0;
            // Missing on old Windows 10 builds, where the taskbar was always dark.
            taskbar = key?.GetValue("SystemUsesLightTheme") is int s && s != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Reading theme settings", ex);
        }

        var changed = apps != AppsUseLightTheme || taskbar != TaskbarUsesLightTheme;
        AppsUseLightTheme = apps;
        TaskbarUsesLightTheme = taskbar;
        return changed;
    }

    public void Dispose() => _window.MessageReceived -= OnMessage;
}
