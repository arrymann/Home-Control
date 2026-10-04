using Windows.Graphics;
using static HomeControl.Interop.NativeMethods;

namespace HomeControl.Interop;

/// <summary>The tray icon's right-click menu (a native popup menu, like other tray apps).</summary>
internal sealed class ContextMenu
{
    private readonly List<(string? Text, Action? Action, bool IsDefault, bool Enabled)> _items = [];

    public ContextMenu Add(string text, Action action, bool isDefault = false, bool enabled = true)
    {
        _items.Add((text, action, isDefault, enabled));
        return this;
    }

    public ContextMenu AddSeparator()
    {
        _items.Add((null, null, false, true));
        return this;
    }

    /// <summary>Shows the menu at a screen point and runs the chosen action.</summary>
    public void Show(IntPtr owner, PointInt32 point, bool openUpwards)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Text is null)
                {
                    AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }

                AppendMenuW(menu, MF_STRING | (item.Enabled ? 0 : MF_GRAYED), (UIntPtr)(uint)(i + 1), item.Text);
                if (item.IsDefault)
                {
                    SetMenuDefaultItem(menu, (uint)(i + 1), 0);
                }
            }

            // Required so the menu closes when the user clicks elsewhere (KB135788).
            SetForegroundWindow(owner);
            var flags = TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY | TPM_WORKAREA | (openUpwards ? TPM_BOTTOMALIGN : 0);
            var command = TrackPopupMenuEx(menu, flags, point.X, point.Y, owner, IntPtr.Zero);
            PostMessageW(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (command > 0 && command <= _items.Count)
            {
                _items[command - 1].Action?.Invoke();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }
}
