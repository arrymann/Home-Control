using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeControl.Core.Hotkeys;

/// <summary>
/// A global keyboard shortcut: a set of modifiers plus one Win32 virtual key.
/// Serialized in settings as a canonical string such as <c>"Ctrl+Alt+K"</c>.
/// </summary>
[JsonConverter(typeof(HotkeyJsonConverter))]
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int Key)
{
    /// <summary>
    /// True when the shortcut can be registered: the key is supported and either a modifier
    /// is present or the key is one that does not interfere with typing (F13-F24).
    /// </summary>
    public bool IsValid =>
        VirtualKeys.IsAssignable(Key) &&
        (Modifiers != HotkeyModifiers.None || VirtualKeys.IsStandaloneAllowed(Key));

    /// <summary>Human-readable form, e.g. <c>"Ctrl + Alt + K"</c>.</summary>
    public override string ToString() => Format(" + ", VirtualKeys.GetDisplayName(Key));

    /// <summary>Canonical form used for persistence, e.g. <c>"Ctrl+Alt+K"</c>.</summary>
    public string ToCanonicalString() => Format("+", VirtualKeys.GetToken(Key));

    /// <summary>The individual parts for rendering as key caps, e.g. ["Ctrl", "Alt", "K"].</summary>
    public IReadOnlyList<string> GetDisplayParts()
    {
        var parts = ModifierNames().ToList();
        parts.Add(VirtualKeys.GetDisplayName(Key));
        return parts;
    }

    public static Hotkey Parse(string text) =>
        TryParse(text, out var hotkey) ? hotkey : throw new FormatException($"'{text}' is not a valid shortcut.");

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        int? key = null;

        foreach (var rawPart in text.Split('+'))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
            {
                return false;
            }

            var modifier = ParseModifier(part);
            if (modifier != HotkeyModifiers.None)
            {
                if (key is not null)
                {
                    return false; // modifiers must come before the key
                }

                modifiers |= modifier;
                continue;
            }

            if (key is not null || !VirtualKeys.TryParse(part, out var vk))
            {
                return false;
            }

            key = vk;
        }

        if (key is null)
        {
            return false;
        }

        hotkey = new Hotkey(modifiers, key.Value);
        return hotkey.IsValid;
    }

    private string Format(string separator, string keyName)
    {
        var parts = ModifierNames().ToList();
        parts.Add(keyName);
        return string.Join(separator, parts);
    }

    private IEnumerable<string> ModifierNames()
    {
        // Same order as Windows/PowerToys: Win, Ctrl, Alt, Shift.
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) yield return "Win";
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) yield return "Ctrl";
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) yield return "Alt";
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) yield return "Shift";
    }

    private static HotkeyModifiers ParseModifier(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HotkeyModifiers.Control,
        "alt" => HotkeyModifiers.Alt,
        "shift" => HotkeyModifiers.Shift,
        "win" or "windows" or "meta" => HotkeyModifiers.Windows,
        _ => HotkeyModifiers.None,
    };
}

/// <summary>
/// Reads and writes <see cref="Hotkey"/> as its canonical string. Unknown or invalid values
/// are read as <c>default</c> (an invalid hotkey) rather than failing the whole settings file.
/// </summary>
public sealed class HotkeyJsonConverter : JsonConverter<Hotkey>
{
    public override Hotkey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return default;
        }

        return Hotkey.TryParse(reader.GetString(), out var hotkey) ? hotkey : default;
    }

    public override void Write(Utf8JsonWriter writer, Hotkey value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.IsValid ? value.ToCanonicalString() : string.Empty);
}
