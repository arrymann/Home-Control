using HomeControl.Core.Hotkeys;
using HomeControl.Core.Models;
using HomeControl.Core.Settings;
using HomeControl.Helpers;
using HomeControl.Interop;
using HomeControl.Views;
using Microsoft.UI.Xaml;

namespace HomeControl;

/// <summary>
/// <c>HomeControl.exe --smoke-test result.txt [screenshot folder]</c>: opens the flyout
/// (with sample devices) in light and dark theme, every settings page and the device dialog,
/// then writes "OK" or the errors to the result file and exits. CI runs it to catch XAML
/// problems that only show up at runtime. Nothing is saved to the user's settings.
/// </summary>
internal static class SmokeTest
{
    public const string Argument = "--smoke-test";

    public static async Task RunAsync(AppHost host, string resultPath, string? screenshotFolder)
    {
        var errors = new List<string>();
        Application.Current.UnhandledException += (_, e) => errors.Add("Unhandled: " + e.Exception);
        if (screenshotFolder is not null)
        {
            Directory.CreateDirectory(screenshotFolder);
        }

        async Task Step(string name, Action action, Func<IntPtr>? capture = null, int waitMs = 1500)
        {
            try
            {
                action();
                await Task.Delay(waitMs);
                if (capture is not null && screenshotFolder is not null)
                {
                    var file = Path.Combine(screenshotFolder, $"{name.Replace(' ', '-')}.jpg");
                    await ScreenCapture.SaveWindowAsync(capture(), file);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex}");
            }
        }

        var kinds = Enum.GetValues<DeviceKind>();
        host.Settings.Devices.AddRange(kinds.Take(6).Select((kind, i) => new DeviceConfig
        {
            Name = $"{Ui.KindName(kind).Split(' ')[0]} {i + 1}",
            Kind = kind,
            Hotkey = i < 3 ? new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, '1' + i) : null,
        }));
        host.Home.LoadDevices(host.Settings.Devices);
        host.Home.SimulateSignedIn();

        foreach (var kind in kinds)
        {
            await Step($"icon {kind}", () =>
            {
                Ui.DeviceIcon(kind);
                Ui.DeviceIconOn(kind);
            }, waitMs: 0);
        }

        void SetTheme(ThemePreference theme)
        {
            host.Settings.Theme = theme;
            host.ApplyAppearance();
        }

        await Step("flyout light", () =>
        {
            SetTheme(ThemePreference.Light);
            host.ShowPopup();
            host.Home.Devices[0].SimulateState(true);
            host.Home.Devices[2].SimulateState(false);
        }, () => host.PopupWindowHandle, waitMs: 2500);
        await Step("flyout dark", () => SetTheme(ThemePreference.Dark), () => host.PopupWindowHandle);

        await Step("settings devices dark", () => host.OpenSettings("devices"), () => host.SettingsWindowHandle, waitMs: 2500);
        await Step("settings account light", () =>
        {
            SetTheme(ThemePreference.Light);
            host.OpenSettings("account");
        }, () => host.SettingsWindowHandle);
        await Step("settings general light", () => host.OpenSettings("general"), () => host.SettingsWindowHandle);

        DeviceEditorDialog? dialog = null;
        await Step("device dialog", () =>
        {
            dialog = new DeviceEditorDialog(host.Settings.Devices[0])
            {
                XamlRoot = host.SettingsXamlRoot,
                RequestedTheme = host.EffectiveTheme,
            };
            _ = dialog.ShowAsync();
        }, () => host.SettingsWindowHandle);
        await Step("close device dialog", () => dialog?.Hide(), waitMs: 500);

        try
        {
            await File.WriteAllTextAsync(resultPath, errors.Count == 0 ? "OK" : string.Join(Environment.NewLine, errors));
        }
        finally
        {
            host.Exit();
        }
    }
}
