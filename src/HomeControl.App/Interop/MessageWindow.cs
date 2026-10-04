using System.ComponentModel;
using System.Runtime.InteropServices;
using HomeControl.Services;
using static HomeControl.Interop.NativeMethods;

namespace HomeControl.Interop;

/// <summary>
/// A hidden top-level Win32 window on the UI thread. It owns the tray icon and the global
/// hotkeys and receives broadcasts such as WM_SETTINGCHANGE (theme changes) and
/// "TaskbarCreated" (Explorer restarted). Top-level rather than message-only, because
/// message-only windows do not receive broadcasts.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    public const string ClassName = "HomeControl.TrayHost";

    /// <summary>Posted by a second instance of the app to open the popup.</summary>
    public const uint WM_SHOW_POPUP = WM_APP + 2;

    private readonly WndProc _wndProc; // must stay referenced while the window exists
    private readonly IntPtr _instance;

    public delegate void MessageHandler(uint message, IntPtr wParam, IntPtr lParam, ref bool handled);

    public MessageWindow()
    {
        _wndProc = WindowProcedure;
        _instance = GetModuleHandleW(null);

        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _instance,
            lpszClassName = ClassName,
        };

        if (RegisterClassExW(ref windowClass) == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the tray window class.");
        }

        Handle = CreateWindowExW(0, ClassName, "Home Control", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the tray window.");
        }
    }

    public event MessageHandler? MessageReceived;

    public IntPtr Handle { get; private set; }

    /// <summary>Asks an already running instance to show its popup.</summary>
    public static void SignalRunningInstance()
    {
        var window = FindWindowW(ClassName, null);
        if (window == IntPtr.Zero)
        {
            return;
        }

        // Let the running instance take the foreground when it opens the popup.
        GetWindowThreadProcessId(window, out var processId);
        AllowSetForegroundWindow(processId);
        PostMessageW(window, WM_SHOW_POPUP, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var handled = false;
            MessageReceived?.Invoke(message, wParam, lParam, ref handled);
            if (handled)
            {
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            // An exception must never cross back into native code.
            Log.Error($"Window message 0x{message:X4}", ex);
        }

        return DefWindowProcW(hWnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
            UnregisterClassW(ClassName, _instance);
        }
    }
}
