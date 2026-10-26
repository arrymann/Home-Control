using Windows.Graphics;
using static HomeControl.Interop.NativeMethods;

namespace HomeControl.Interop;

/// <summary>The tray icon's right-click menu (a native popup menu, like other tray apps).</summary>
internal sealed class ContextMenu
{
    private readonly List<Item> _items = [];

    public ContextMenu Add(string text, Action action, bool isDefault = false, bool enabled = true)
    {
        _items.Add(new Item(text, action, isDefault, enabled, null));
        return this;
    }

    /// <summary>Adds the item only when <paramref name="condition"/> is true.</summary>
    public ContextMenu AddIf(bool condition, string text, Action action) => condition ? Add(text, action) : this;

    /// <summary>Adds an item that opens <paramref name="submenu"/>.</summary>
    public ContextMenu AddSubmenu(string text, ContextMenu submenu, bool enabled = true)
    {
        _items.Add(new Item(text, null, false, enabled, submenu));
        return this;
    }

    public ContextMenu AddSeparator()
    {
        _items.Add(new Item(null, null, false, true, null));
        return this;
    }

    /// <summary>The items' texts, a submenu's indented under its item, separators as "-" (for the smoke test).</summary>
    public IEnumerable<string> Texts(string indent = "")
    {
        foreach (var item in _items)
        {
            yield return indent + (item.Text ?? "-") + (item.Enabled ? string.Empty : " (unavailable)");
            if (item.Submenu is { } submenu)
            {
                foreach (var text in submenu.Texts(indent + "  "))
                {
                    yield return text;
                }
            }
        }
    }

    /// <summary>Shows the menu at a screen point and runs the chosen action.</summary>
    public void Show(IntPtr owner, PointInt32 point, bool openUpwards)
    {
        var actions = new List<Action>();
        var menu = Build(actions);
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            // Required so the menu closes when the user clicks elsewhere (KB135788).
            SetForegroundWindow(owner);
            var flags = TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY | TPM_WORKAREA | (openUpwards ? TPM_BOTTOMALIGN : 0);
            var command = TrackPopupMenuEx(menu, flags, point.X, point.Y, owner, IntPtr.Zero);
            PostMessageW(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (command > 0 && command <= actions.Count)
            {
                actions[command - 1].Invoke();
            }
        }
        finally
        {
            DestroyMenu(menu); // and its submenus
        }
    }

    /// <summary>Makes the native menu; each action's command is its place in <paramref name="actions"/> plus one.</summary>
    private IntPtr Build(List<Action> actions)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        foreach (var item in _items)
        {
            if (item.Text is null)
            {
                AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                continue;
            }

            var grayed = item.Enabled ? 0 : MF_GRAYED;
            if (item.Submenu is { } submenu)
            {
                var child = submenu.Build(actions);
                if (child != IntPtr.Zero && !AppendMenuW(menu, MF_POPUP | grayed, unchecked((UIntPtr)(nuint)child), item.Text))
                {
                    DestroyMenu(child);
                }

                continue;
            }

            actions.Add(item.Action!);
            var command = (uint)actions.Count;
            AppendMenuW(menu, MF_STRING | grayed, (UIntPtr)command, item.Text);
            if (item.IsDefault)
            {
                SetMenuDefaultItem(menu, command, 0);
            }
        }

        return menu;
    }

    private sealed record Item(string? Text, Action? Action, bool IsDefault, bool Enabled, ContextMenu? Submenu);
}
