using System.Runtime.InteropServices;
using Windows.Graphics;
using static HomeControl.Interop.NativeMethods;

namespace HomeControl.Interop;

/// <summary>Where the tray icon was clicked, in physical screen pixels.</summary>
internal sealed class TrayIconEventArgs(PointInt32 point) : EventArgs
{
    public PointInt32 Point { get; } = point;
}

/// <summary>A notification-area icon (Shell_NotifyIcon, NOTIFYICON_VERSION_4).</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = WM_APP + 1;

    private readonly MessageWindow _window;
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _icon;
    private string _toolTip = "Home Control";
    private bool _added;
    private long _lastSelectTicks;

    public TrayIcon(MessageWindow window)
    {
        _window = window;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        _window.MessageReceived += OnMessage;
    }

    /// <summary>Left click, or Enter/Space while the icon has keyboard focus.</summary>
    public event EventHandler<TrayIconEventArgs>? Selected;

    /// <summary>Right click or the context-menu key.</summary>
    public event EventHandler<TrayIconEventArgs>? ContextMenuRequested;

    public void Show()
    {
        var data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            // Already there (e.g. after a quick Explorer restart): update it instead.
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }

        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
        _added = true;
    }

    /// <summary>Sets the icon. The caller keeps ownership of the handle.</summary>
    public void SetIcon(IntPtr icon)
    {
        _icon = icon;
        if (_added)
        {
            var data = CreateData(NIF_ICON);
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
    }

    public void SetToolTip(string text)
    {
        _toolTip = text.Length > 127 ? text[..127] : text;
        if (_added)
        {
            var data = CreateData(NIF_TIP | NIF_SHOWTIP);
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
    }

    /// <summary>Shows a notification (a toast on Windows 10/11) from the tray icon.</summary>
    public void ShowNotification(string title, string text)
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = NIIF_NONE | NIIF_NOSOUND | NIIF_RESPECT_QUIET_TIME;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>Screen rectangle of the icon, or null when it is hidden in the overflow area.</summary>
    public RectInt32? GetBounds()
    {
        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _window.Handle,
            uID = IconId,
        };

        if (Shell_NotifyIconGetRect(ref identifier, out var rect) != 0 || rect.Right <= rect.Left)
        {
            return null;
        }

        return new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private void OnMessage(uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            handled = true;
            var point = new PointInt32(LowWord(wParam), HighWord(wParam));
            switch ((uint)LowWord(lParam))
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    // Keyboard selection can be reported twice; ignore an immediate repeat.
                    var now = Environment.TickCount64;
                    if (now - _lastSelectTicks > 250)
                    {
                        _lastSelectTicks = now;
                        Selected?.Invoke(this, new TrayIconEventArgs(point));
                    }

                    break;
                case WM_CONTEXTMENU:
                    ContextMenuRequested?.Invoke(this, new TrayIconEventArgs(point));
                    break;
            }
        }
        else if (message == _taskbarCreatedMessage && _added)
        {
            // Explorer restarted and forgot all icons.
            Show();
        }
    }

    private NOTIFYICONDATAW CreateData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _toolTip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    public void Dispose()
    {
        _window.MessageReceived -= OnMessage;
        if (_added)
        {
            var data = CreateData(0);
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }
    }
}
