namespace HomeControl.Core.Hotkeys;

/// <summary>
/// Modifier keys of a global shortcut. The values match the Win32 <c>MOD_*</c>
/// flags accepted by <c>RegisterHotKey</c>, so they can be passed through unchanged.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}
