using System.Text.Json;
using HomeControl.Core.Hotkeys;

namespace HomeControl.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+K", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4B)]
    [InlineData("win + shift + 1", HotkeyModifiers.Windows | HotkeyModifiers.Shift, 0x31)]
    [InlineData("Control+F5", HotkeyModifiers.Control, 0x74)]
    [InlineData("F13", HotkeyModifiers.None, 0x7C)]
    [InlineData("Ctrl+Alt+Equals", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0xBB)]
    [InlineData("Ctrl+Alt+=", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0xBB)]
    [InlineData("Ctrl+Shift+NumAdd", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x6B)]
    [InlineData("Alt+PageUp", HotkeyModifiers.Alt, 0x21)]
    [InlineData("Alt+0x41", HotkeyModifiers.Alt, 0x41)]
    public void Parses_valid_shortcuts(string text, HotkeyModifiers modifiers, int key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("K")] // letters need a modifier
    [InlineData("F5")] // F1-F12 need a modifier
    [InlineData("Ctrl+Alt")] // no key
    [InlineData("Ctrl+K+L")] // two keys
    [InlineData("K+Ctrl")] // modifier after key
    [InlineData("Ctrl+Tab")] // reserved for navigation
    [InlineData("Ctrl+Escape")]
    [InlineData("Ctrl++")]
    [InlineData("Hyper+K")]
    public void Rejects_invalid_shortcuts(string text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Fact]
    public void Formats_in_windows_order()
    {
        var hotkey = new Hotkey(HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Windows, 0x4B);
        Assert.Equal("Win + Ctrl + Alt + Shift + K", hotkey.ToString());
        Assert.Equal("Win+Ctrl+Alt+Shift+K", hotkey.ToCanonicalString());
        Assert.Equal(["Win", "Ctrl", "Alt", "Shift", "K"], hotkey.GetDisplayParts());
    }

    [Fact]
    public void Display_name_differs_from_token_for_symbol_keys()
    {
        var hotkey = new Hotkey(HotkeyModifiers.Control, 0xBF);
        Assert.Equal("Ctrl + /", hotkey.ToString());
        Assert.Equal("Ctrl+Slash", hotkey.ToCanonicalString());
    }

    [Fact]
    public void Every_assignable_key_round_trips_through_its_canonical_string()
    {
        for (var vk = 0; vk < 256; vk++)
        {
            if (!VirtualKeys.IsAssignable(vk))
            {
                continue;
            }

            var hotkey = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, vk);
            Assert.True(Hotkey.TryParse(hotkey.ToCanonicalString(), out var parsed), hotkey.ToCanonicalString());
            Assert.Equal(hotkey, parsed);
        }
    }

    [Fact]
    public void Modifier_keys_are_not_assignable()
    {
        foreach (var vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA5 })
        {
            Assert.True(VirtualKeys.IsModifierKey(vk));
            Assert.False(VirtualKeys.IsAssignable(vk));
        }
    }

    private sealed class Holder
    {
        public Hotkey? Shortcut { get; set; }
    }

    [Fact]
    public void Serializes_as_string_and_tolerates_garbage()
    {
        var json = JsonSerializer.Serialize(new Holder { Shortcut = Hotkey.Parse("Ctrl+Alt+K") }, Settings.SettingsStore.JsonOptions);
        Assert.Contains("\"shortcut\": \"Ctrl+Alt+K\"", json);
        Assert.Equal(Hotkey.Parse("Ctrl+Alt+K"), JsonSerializer.Deserialize<Holder>(json, Settings.SettingsStore.JsonOptions)!.Shortcut);

        Assert.Null(JsonSerializer.Deserialize<Holder>("""{"Shortcut":null}""")!.Shortcut);
        var garbage = JsonSerializer.Deserialize<Holder>("""{"Shortcut":"nonsense"}""")!.Shortcut;
        Assert.False(garbage!.Value.IsValid);
        var number = JsonSerializer.Deserialize<Holder>("""{"Shortcut":42}""")!.Shortcut;
        Assert.False(number!.Value.IsValid);
    }
}
