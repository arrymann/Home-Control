using HomeControl.Core.Hotkeys;
using HomeControl.Core.Models;
using HomeControl.Core.Security;
using HomeControl.Core.Settings;

namespace HomeControl.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var dir = new TempDirectory();
        var settings = new SettingsStore(dir.File("settings.json")).Load();

        Assert.Equal(ThemePreference.System, settings.Theme);
        Assert.Equal(BackdropKind.Mica, settings.Backdrop);
        Assert.Empty(settings.Devices);
        Assert.Equal("en-US", settings.Assistant.LanguageCode);
    }

    [Fact]
    public void Round_trips_all_settings()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(dir.File("sub/settings.json"));
        var original = new AppSettings
        {
            Theme = ThemePreference.Dark,
            Backdrop = BackdropKind.MicaAlt,
            OpenPopupHotkey = Hotkey.Parse("Win+Alt+H"),
            NotifyOnHotkey = false,
            RefreshStatesOnOpen = true,
            Assistant = { LanguageCode = "de-DE", OnCommandTemplate = "schalte {name} ein" },
            Devices =
            {
                new DeviceConfig { Name = "Kitchen light", Kind = DeviceKind.Light, Hotkey = Hotkey.Parse("Ctrl+Alt+1") },
                new DeviceConfig { Name = "Coffee", DisplayName = "Espresso", Kind = DeviceKind.Coffee, OnCommand = "start the coffee machine" },
            },
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Null(store.LoadError);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
        Assert.Equal(BackdropKind.MicaAlt, loaded.Backdrop);
        Assert.Equal(Hotkey.Parse("Win+Alt+H"), loaded.OpenPopupHotkey);
        Assert.False(loaded.NotifyOnHotkey);
        Assert.True(loaded.RefreshStatesOnOpen);
        Assert.Equal("de-DE", loaded.Assistant.LanguageCode);
        Assert.Equal("schalte {name} ein", loaded.Assistant.OnCommandTemplate);
        Assert.Equal(2, loaded.Devices.Count);
        Assert.Equal(original.Devices[0].Id, loaded.Devices[0].Id);
        Assert.Equal(Hotkey.Parse("Ctrl+Alt+1"), loaded.Devices[0].Hotkey);
        Assert.Equal("Espresso", loaded.Devices[1].Label);
        Assert.Equal("start the coffee machine", loaded.Devices[1].OnCommand);

        var json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"theme\": \"Dark\"", json);
        Assert.Contains("\"hotkey\": \"Ctrl+Alt+1\"", json);
        Assert.DoesNotContain("label", json);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_backed_up()
    {
        using var dir = new TempDirectory();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ this is not json");

        var store = new SettingsStore(path);
        var settings = store.Load();

        Assert.NotNull(store.LoadError);
        Assert.Empty(settings.Devices);
        Assert.Equal("{ this is not json", File.ReadAllText(path + ".corrupt"));
    }

    [Fact]
    public void Normalizes_duplicate_ids_invalid_hotkeys_and_blank_templates()
    {
        using var dir = new TempDirectory();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """
            {
              // comments are allowed
              "devices": [
                { "id": "same", "name": "A", "hotkey": "garbage" },
                { "id": "same", "name": "B", "hotkey": "Ctrl+Alt+B" },
                { "name": "C" },
              ],
              "openPopupHotkey": "K",
              "assistant": { "onCommandTemplate": "", "languageCode": null }
            }
            """);

        var settings = new SettingsStore(path).Load();

        Assert.Equal(3, settings.Devices.Select(d => d.Id).Distinct().Count());
        Assert.Null(settings.Devices[0].Hotkey);
        Assert.Equal(Hotkey.Parse("Ctrl+Alt+B"), settings.Devices[1].Hotkey);
        Assert.Null(settings.OpenPopupHotkey);
        Assert.Equal(AssistantSettings.DefaultOnTemplate, settings.Assistant.OnCommandTemplate);
        Assert.Equal("en-US", settings.Assistant.LanguageCode);
    }

    [Fact]
    public void Scenes_round_trip_and_are_repaired()
    {
        using var dir = new TempDirectory();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """
            {
              "devices": [ { "id": "d1", "name": "A" } ],
              "scenes": [
                { "id": "s", "name": "Evening", "kind": "Light", "deviceIds": [ "d1", "d1", "", "d2" ], "showInTray": false, "hotkey": "Ctrl+Alt+E" },
                { "id": "s", "hotkey": "nonsense" },
                null
              ]
            }
            """);

        var store = new SettingsStore(path);
        var settings = store.Load();

        Assert.Equal(2, settings.Scenes.Count);
        var evening = settings.Scenes[0];
        Assert.Equal("Evening", evening.Name);
        Assert.Equal(DeviceKind.Light, evening.Kind);
        Assert.Equal(["d1", "d2"], evening.DeviceIds);
        Assert.False(evening.ShowInTray);
        Assert.Equal(Hotkey.Parse("Ctrl+Alt+E"), evening.Hotkey);
        Assert.NotEqual("s", settings.Scenes[1].Id);
        Assert.Equal(string.Empty, settings.Scenes[1].Name);
        Assert.Null(settings.Scenes[1].Hotkey);
        Assert.True(settings.Scenes[1].ShowInTray);

        store.Save(settings);
        var again = store.Load();
        Assert.Equal(["d1", "d2"], again.Scenes[0].DeviceIds);
        Assert.Equal(settings.Scenes[1].Id, again.Scenes[1].Id);
    }

    [Theory]
    [InlineData(new[] { 0, 0 }, false)]
    [InlineData(new[] { 0, 1 }, true)]   // on while any device is on
    [InlineData(new[] { 0, 2 }, false)]  // unknown devices don't count
    [InlineData(new[] { 2, 2 }, null)]   // nothing known
    [InlineData(new int[0], null)]
    public void A_scene_is_on_while_any_device_is_on(int[] states, bool? expected)
    {
        bool? State(int s) => s switch { 0 => false, 1 => true, _ => null };
        Assert.Equal(expected, SceneConfig.StateOf(states.Select(State)));
    }
}

public class SecretStoreTests
{
    [Fact]
    public void Persists_encrypted_values()
    {
        using var dir = new TempDirectory();
        var path = dir.File("secrets.dat");
        var store = new SecretStore(path, new XorProtector());

        store.Set(SecretStore.RefreshTokenKey, "1//refresh-token");
        store.Set(SecretStore.ClientSecretKey, "GOCSPX-secret");

        Assert.DoesNotContain("refresh-token", File.ReadAllText(path));

        var reopened = new SecretStore(path, new XorProtector());
        Assert.Equal("1//refresh-token", reopened.Get(SecretStore.RefreshTokenKey));
        Assert.Equal("GOCSPX-secret", reopened.Get(SecretStore.ClientSecretKey));
        Assert.Null(reopened.Get("missing"));
    }

    [Fact]
    public void Setting_null_removes_value()
    {
        using var dir = new TempDirectory();
        var path = dir.File("secrets.dat");
        var store = new SecretStore(path, new XorProtector());
        store.Set("k", "v");
        store.Set("k", null);

        Assert.Null(new SecretStore(path, new XorProtector()).Get("k"));
    }

    [Fact]
    public void Unreadable_file_starts_empty()
    {
        using var dir = new TempDirectory();
        var path = dir.File("secrets.dat");
        File.WriteAllBytes(path, [1, 2, 3]);

        var store = new SecretStore(path, new XorProtector());
        Assert.Null(store.Get("anything"));
        store.Set("k", "v");
        Assert.Equal("v", new SecretStore(path, new XorProtector()).Get("k"));
    }
}
