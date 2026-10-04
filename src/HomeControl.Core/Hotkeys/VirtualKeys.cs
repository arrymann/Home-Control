using System.Globalization;

namespace HomeControl.Core.Hotkeys;

/// <summary>
/// Names for the Win32 virtual-key codes that can be used as the main key of a shortcut.
/// Each key has a canonical token (used in settings files, never contains '+') and a
/// display name (shown in the UI).
/// </summary>
public static class VirtualKeys
{
    private static readonly Dictionary<int, (string Token, string Display)> Keys = Build();
    private static readonly Dictionary<string, int> ByName = BuildLookup();

    public const int F13 = 0x7C;
    public const int F24 = 0x87;

    /// <summary>Returns true if <paramref name="vk"/> can be the main key of a shortcut.</summary>
    public static bool IsAssignable(int vk) => Keys.ContainsKey(vk);

    /// <summary>Keys that may be used without any modifier (they are not used for typing).</summary>
    public static bool IsStandaloneAllowed(int vk) => vk is >= F13 and <= F24;

    /// <summary>Returns true for Shift, Ctrl, Alt and Windows keys (either side).</summary>
    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    public static string GetToken(int vk) =>
        Keys.TryGetValue(vk, out var names) ? names.Token : "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);

    public static string GetDisplayName(int vk) =>
        Keys.TryGetValue(vk, out var names) ? names.Display : "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);

    public static bool TryParse(string text, out int vk)
    {
        text = text.Trim();
        if (ByName.TryGetValue(text, out vk))
        {
            return true;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vk) &&
            IsAssignable(vk))
        {
            return true;
        }

        vk = 0;
        return false;
    }

    private static Dictionary<int, (string, string)> Build()
    {
        var keys = new Dictionary<int, (string, string)>();

        for (var c = 'A'; c <= 'Z'; c++)
        {
            keys[c] = (c.ToString(), c.ToString());
        }

        for (var d = '0'; d <= '9'; d++)
        {
            keys[d] = (d.ToString(), d.ToString());
        }

        for (var i = 1; i <= 24; i++)
        {
            keys[0x70 + i - 1] = ("F" + i, "F" + i);
        }

        for (var i = 0; i <= 9; i++)
        {
            keys[0x60 + i] = ("Num" + i, "Num " + i);
        }

        keys[0x6A] = ("NumMultiply", "Num *");
        keys[0x6B] = ("NumAdd", "Num +");
        keys[0x6D] = ("NumSubtract", "Num -");
        keys[0x6E] = ("NumDecimal", "Num .");
        keys[0x6F] = ("NumDivide", "Num /");

        keys[0x08] = ("Backspace", "Backspace");
        keys[0x0D] = ("Enter", "Enter");
        keys[0x13] = ("Pause", "Pause");
        keys[0x20] = ("Space", "Space");
        keys[0x21] = ("PageUp", "Page Up");
        keys[0x22] = ("PageDown", "Page Down");
        keys[0x23] = ("End", "End");
        keys[0x24] = ("Home", "Home");
        keys[0x25] = ("Left", "Left");
        keys[0x26] = ("Up", "Up");
        keys[0x27] = ("Right", "Right");
        keys[0x28] = ("Down", "Down");
        keys[0x2C] = ("PrintScreen", "Print Screen");
        keys[0x2D] = ("Insert", "Insert");
        keys[0x2E] = ("Delete", "Delete");

        keys[0xBA] = ("Semicolon", ";");
        keys[0xBB] = ("Equals", "=");
        keys[0xBC] = ("Comma", ",");
        keys[0xBD] = ("Minus", "-");
        keys[0xBE] = ("Period", ".");
        keys[0xBF] = ("Slash", "/");
        keys[0xC0] = ("Backtick", "`");
        keys[0xDB] = ("LeftBracket", "[");
        keys[0xDC] = ("Backslash", "\\");
        keys[0xDD] = ("RightBracket", "]");
        keys[0xDE] = ("Quote", "'");

        return keys;
    }

    private static Dictionary<string, int> BuildLookup()
    {
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vk, (token, display)) in Keys)
        {
            lookup[token] = vk;
            lookup.TryAdd(display, vk);
            lookup.TryAdd(display.Replace(" ", string.Empty, StringComparison.Ordinal), vk);
        }

        lookup["Return"] = 0x0D;
        lookup["Del"] = 0x2E;
        lookup["Ins"] = 0x2D;
        lookup["PgUp"] = 0x21;
        lookup["PgDn"] = 0x22;
        lookup["PrtScn"] = 0x2C;
        return lookup;
    }
}
