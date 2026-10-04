using HomeControl.Core.Hotkeys;
using HomeControl.Core.Models;
using HomeControl.Helpers;
using HomeControl.Views;
using Microsoft.UI.Xaml;

namespace HomeControl;

/// <summary>
/// <c>HomeControl.exe --smoke-test result.txt</c>: opens the flyout (with sample devices),
/// every settings page and the device dialog, then writes "OK" or the errors to the result
/// file and exits. CI runs it to catch XAML problems that only show up at runtime.
/// Nothing is saved to the user's settings.
/// </summary>
internal static class SmokeTest
{
    public const string Argument = "--smoke-test";

    public static async Task RunAsync(AppHost host, string resultPath)
    {
        var errors = new List<string>();
        Application.Current.UnhandledException += (_, e) => errors.Add("Unhandled: " + e.Exception);

        async Task Step(string name, Action action, int waitMs = 1500)
        {
            try
            {
                action();
                await Task.Delay(waitMs);
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex}");
            }
        }

        var kinds = Enum.GetValues<DeviceKind>();
        host.Settings.Devices.AddRange(kinds.Select((kind, i) => new DeviceConfig
        {
            Name = $"Test {kind}",
            Kind = kind,
            Hotkey = i == 0 ? Hotkey.Parse("Ctrl+Alt+Shift+F12") : null,
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

        await Step("flyout", host.ShowPopup, waitMs: 2500);
        await Step("flyout device on", () => host.Home.Devices[0].SimulateState(true));
        foreach (var page in new[] { "devices", "account", "general" })
        {
            await Step($"settings page {page}", () => host.OpenSettings(page));
        }

        DeviceEditorDialog? dialog = null;
        await Step("device dialog", () =>
        {
            dialog = new DeviceEditorDialog(host.Settings.Devices[0]) { XamlRoot = host.SettingsXamlRoot };
            _ = dialog.ShowAsync();
        });
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
