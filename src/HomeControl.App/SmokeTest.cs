using HomeControl.Core.GoogleHome;
using HomeControl.Core.Hotkeys;
using HomeControl.Core.Models;
using HomeControl.Core.Settings;
using HomeControl.Helpers;
using HomeControl.Interop;
using HomeControl.Services;
using HomeControl.Views;
using Microsoft.UI.Xaml;

namespace HomeControl;

/// <summary>
/// <c>HomeControl.exe --smoke-test result.txt [screenshot folder]</c>: opens the flyout
/// (with sample devices) in light and dark theme, every settings page, the device dialog and
/// the Google sign-in window, runs a script in the hidden Google Home page and tries a sync
/// (which must fail cleanly without a sign-in), then writes "OK" or the errors to the result
/// file and exits. CI runs it to catch XAML and WebView2 problems that only show up at
/// runtime. Nothing is saved to the user's settings.
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
        Hotkey Shortcut(int n) => new(HotkeyModifiers.Control | HotkeyModifiers.Alt, '0' + n);
        DeviceConfig FromGoogleHome(string name, string room, DeviceKind kind, Hotkey? hotkey = null, bool hidden = false) => new()
        {
            Source = DeviceSource.GoogleHome,
            GoogleHomeId = Guid.NewGuid().ToString(),
            Name = name,
            Room = room,
            Kind = kind,
            Hotkey = hotkey,
            Hidden = hidden,
        };

        // A typical home: outlets, a light and a TV in two rooms, one device hidden,
        // plus a scene added by name for Google Assistant.
        List<DeviceConfig> samples =
        [
            FromGoogleHome("3d printer", "Living Room", DeviceKind.Outlet, Shortcut(1)),
            FromGoogleHome("Backlight", "Living Room", DeviceKind.Light, Shortcut(2)),
            FromGoogleHome("Desk", "Office", DeviceKind.Outlet),
            FromGoogleHome("Pc", "Office", DeviceKind.Switch),
            FromGoogleHome("Living Room TV", "Living Room", DeviceKind.Tv),
            FromGoogleHome("Hallway", "Hallway", DeviceKind.Light, hidden: true),
            new DeviceConfig { Name = "Movie night", Kind = DeviceKind.Scene, Hotkey = Shortcut(3) },
        ];
        host.Settings.Devices.AddRange(samples);
        host.Home.LoadDevices(host.Settings.Devices);
        host.Home.SimulateSignedIn();
        if (host.Home.Devices.Count != host.Settings.Devices.Count(d => !d.Hidden && !d.Missing))
        {
            errors.Add($"Hidden devices: the popup shows {host.Home.Devices.Count} devices.");
        }

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
            host.Home.Devices[1].SimulateState(false);
            host.Home.Devices[2].SimulateState(false);
            host.Home.Devices[3].SimulateState(true);
            host.Home.Devices[4].SimulateState(false, online: false);
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
        foreach (var (name, device) in new[] { ("device dialog", samples[0]), ("device dialog assistant", samples[^1]) })
        {
            await Step(name, () =>
            {
                dialog = new DeviceEditorDialog(device)
                {
                    XamlRoot = host.SettingsXamlRoot,
                    RequestedTheme = host.EffectiveTheme,
                };
                _ = dialog.ShowAsync();
            }, () => host.SettingsWindowHandle);
            await Step($"close {name}", () => dialog?.Hide(), waitMs: 500);
        }

        // Google Home: the sign-in window, a script in the hidden page, and a sync without a session.
        await Step("google sign-in", host.OpenGoogleSignIn, () => host.SignInWindowHandle, waitMs: 6000);
        await Step("close google sign-in", host.CloseGoogleSignIn, waitMs: 500);
        await RunGoogleHomeChecksAsync(host, errors);
        await Step("flyout signed out", host.ShowPopup, () => host.PopupWindowHandle, waitMs: 2000);

        try
        {
            await File.WriteAllTextAsync(resultPath, errors.Count == 0 ? "OK" : string.Join(Environment.NewLine, errors));
        }
        finally
        {
            host.Exit();
        }
    }

    private static async Task RunGoogleHomeChecksAsync(AppHost host, List<string> errors)
    {
        try
        {
            var result = await host.GoogleHomeSession.EvaluateForTestAsync("(async () => 1 + 1)()").WaitAsync(TimeSpan.FromSeconds(60));
            if (result != "2")
            {
                errors.Add($"WebView2: the test script returned {result} instead of 2.");
            }

            Log.Info($"Smoke test: the hidden Google Home page ran a script (result {result}).");
        }
        catch (GoogleHomeException ex) when (host.GoogleHomeState == GoogleHomeConnection.Unavailable)
        {
            Log.Info("Smoke test: no WebView2 Runtime, skipping the Google Home checks. " + ex.Message);
            return;
        }
        catch (Exception ex)
        {
            errors.Add("WebView2 script: " + ex);
            return;
        }

        try
        {
            var result = await host.SyncGoogleHomeAsync().WaitAsync(TimeSpan.FromSeconds(90));
            Log.Info($"Smoke test: Google Home sync worked ({result}); this PC is signed in.");
        }
        catch (GoogleHomeSignInRequiredException ex)
        {
            Log.Info($"Smoke test: sync without a Google sign-in asked to sign in, as expected ({ex.Message}).");

            // Expected without a Google sign-in: the app must now know it is signed out.
            if (host.Home.IsGoogleHomeConnected)
            {
                errors.Add("Google Home: still shown as connected after a signed-out sync.");
            }
        }
        catch (GoogleHomeException ex)
        {
            // No connection to Google from this machine: not a failure of the app.
            Log.Info("Smoke test: Google Home sync failed: " + ex.Message);
        }
        catch (Exception ex)
        {
            errors.Add("Google Home sync: " + ex);
        }
    }
}
