using HomeControl.Core.Automations;
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
        DeviceConfig FromGoogleHome(string name, string room, DeviceKind kind, Hotkey? hotkey = null, bool hidden = false, string home = "Pomki") => new()
        {
            Source = DeviceSource.GoogleHome,
            GoogleHomeId = Guid.NewGuid().ToString(),
            Name = name,
            Room = room,
            Home = home,
            Kind = kind,
            Hotkey = hotkey,
            Hidden = hidden,
        };

        // A typical home: outlets, a light and a TV in three rooms, one device hidden, a heater
        // in a second home, plus a scene added by name for Google Assistant.
        List<DeviceConfig> samples =
        [
            FromGoogleHome("3d printer", "Living Room", DeviceKind.Outlet, Shortcut(1)),
            FromGoogleHome("Backlight", "Living Room", DeviceKind.Light, Shortcut(2)),
            FromGoogleHome("Desk", "Office", DeviceKind.Outlet),
            FromGoogleHome("Pc", "Office", DeviceKind.Switch),
            FromGoogleHome("Living Room TV", "Living Room", DeviceKind.Tv),
            FromGoogleHome("Hallway", "Hallway", DeviceKind.Light, hidden: true),
            new DeviceConfig { Name = "Movie night", Kind = DeviceKind.Scene, Hotkey = Shortcut(3) },
            FromGoogleHome("Heater", "Kitchen", DeviceKind.Heater, home: "Cottage"),
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
            host.Home.Devices[4].SimulatePending(true); // the TV hasn't confirmed "turn on" yet: greyed toggle
            host.Home.Devices[6].SimulateState(false, online: false);
        }, () => host.PopupWindowHandle, waitMs: 2500);
        await Step("flyout dark", () => SetTheme(ThemePreference.Dark), () => host.PopupWindowHandle);

        await Step("settings devices dark", () => host.OpenSettings("devices"), () => host.SettingsWindowHandle, waitMs: 2500);
        await Step("settings account light", () =>
        {
            SetTheme(ThemePreference.Light);
            host.OpenSettings("account");
        }, () => host.SettingsWindowHandle);
        await Step("settings general light", () => host.OpenSettings("general"), () => host.SettingsWindowHandle);

        // Automations: the list, and the editor with every kind of node.
        var everything = new Automation { Name = "Every kind of node" };
        AutomationNode[] everyNode =
        [
            new TimeTriggerNode { X = 40, Y = 40, At = new TimePoint { Reference = TimeReference.Sunset, OffsetMinutes = -15 } },
            new PcEventTriggerNode { X = 40, Y = 360, Event = PcEvent.Idle },
            new ShutdownTriggerNode { X = 40, Y = 600 },
            new TimeWindowConditionNode { X = 380, Y = 40 },
            new DaysConditionNode { X = 380, Y = 420 },
            new DeviceStateConditionNode { X = 380, Y = 620, DeviceId = samples[1].Id },
            new PcStateConditionNode { X = 720, Y = 620 },
            new DeviceActionNode { X = 720, Y = 40, DeviceId = samples[1].Id },
            new NotifyActionNode { X = 720, Y = 280, Message = "Good evening" },
            new DelayActionNode { X = 1060, Y = 40 },
            new AllOffActionNode { X = 1060, Y = 300 },
            new PcPowerActionNode { X = 1060, Y = 480, Command = PcPowerCommand.ShutDown },
        ];
        everything.Nodes.AddRange(everyNode);
        void Wire(int from, int to, string port = Ports.Then) => AutomationGraph.Connect(everything, everyNode[from].Id, port, everyNode[to].Id);
        Wire(0, 3);
        Wire(3, 7, Ports.Yes);
        Wire(3, 8, Ports.No);
        Wire(7, 9);
        Wire(1, 4);
        Wire(4, 10, Ports.Yes);
        Wire(2, 10);
        Wire(5, 11, Ports.Yes);
        var automations = host.Automations.Document;
        automations.Location = new GeoLocation(51.5074, -0.1278, "London, England, United Kingdom");
        automations.Automations.Add(AutomationTemplates.Create(AutomationTemplate.LightsAtSunset, samples));
        automations.Automations.Add(AutomationTemplates.Create(AutomationTemplate.OffAtShutdown, samples));
        automations.Automations.Add(everything);
        foreach (var automation in automations.Automations)
        {
            var issues = AutomationGraph.Validate(automation, samples.Select(d => d.Id).ToHashSet(), hasLocation: true)
                .Where(i => i.Severity == IssueSeverity.Error && automation != everything);
            errors.AddRange(issues.Select(i => $"Automation “{automation.Name}”: {i.Message}"));
        }

        await Step("settings automations dark", () =>
        {
            SetTheme(ThemePreference.Dark);
            host.OpenSettings("automations");
        }, () => host.SettingsWindowHandle, waitMs: 2000);
        await Step("automation editor dark", () => host.OpenAutomation(everything.Id), () => host.SettingsWindowHandle, waitMs: 2500);
        await Step("automation editor light", () => SetTheme(ThemePreference.Light), () => host.SettingsWindowHandle, waitMs: 1500);
        await Step("automation template editor", () => host.OpenAutomation(automations.Automations[0].Id), () => host.SettingsWindowHandle, waitMs: 2000);
        await Step("back to general", () => host.OpenSettings("general"), waitMs: 500);

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
        await Step("google sign-in", host.OpenGoogleSignIn, () => host.SignInWindowHandle, waitMs: 5000);
        Log.Info("Smoke test: sign-in window after 5 s: " + host.DescribeSignInWindow());
        await Step("google sign-in later", () => { }, () => host.SignInWindowHandle, waitMs: 7000);
        Log.Info("Smoke test: sign-in window after 12 s: " + host.DescribeSignInWindow());
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
