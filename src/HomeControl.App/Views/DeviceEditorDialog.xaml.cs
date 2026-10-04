using HomeControl.Core.Assistant;
using HomeControl.Core.Models;
using HomeControl.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HomeControl.Views;

/// <summary>An entry of the icon picker.</summary>
public sealed class KindOption(DeviceKind kind)
{
    public DeviceKind Kind { get; } = kind;

    public string Name => Ui.KindName(Kind);

    /// <summary>A fresh geometry per read: the same item is shown in the list and the selection box.</summary>
    public Geometry Icon => Ui.DeviceIcon(Kind);
}

/// <summary>Adds or edits a device.</summary>
public sealed partial class DeviceEditorDialog : ContentDialog
{
    private readonly DeviceConfig _device;
    private readonly List<KindOption> _kinds = Enum.GetValues<DeviceKind>().Select(k => new KindOption(k)).ToList();

    /// <param name="device">The device to edit, or null to add a new one.</param>
    public DeviceEditorDialog(DeviceConfig? device)
    {
        InitializeComponent();
        _device = device?.Clone() ?? new DeviceConfig();
        Title = device is null ? "Add a device" : "Edit device";

        NameBox.Text = _device.Name;
        LabelBox.Text = _device.DisplayName ?? string.Empty;
        KindBox.ItemsSource = _kinds;
        KindBox.SelectedItem = _kinds.First(k => k.Kind == _device.Kind);
        ShortcutBox.Hotkey = _device.Hotkey;
        ShortcutBox.Validate = hotkey => App.Host.ValidateHotkey(hotkey, _device.Id, isPopupHotkey: false);
        OnCommandBox.Text = _device.OnCommand ?? string.Empty;
        OffCommandBox.Text = _device.OffCommand ?? string.Empty;
        StateQueryBox.Text = _device.StateQuery ?? string.Empty;

        var assistant = App.Host.Settings.Assistant;
        OnCommandBox.PlaceholderText = assistant.OnCommandTemplate;
        OffCommandBox.PlaceholderText = assistant.OffCommandTemplate;
        StateQueryBox.PlaceholderText = assistant.StateQueryTemplate;

        UpdateTestButtons();
    }

    /// <summary>The edited device (valid after the dialog returned Primary).</summary>
    public DeviceConfig Result => _device;

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateTestButtons();
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ErrorText.Text = "Enter the device name as it appears in the Google Home app.";
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        Apply(_device);
    }

    private void Apply(DeviceConfig target)
    {
        target.Name = NameBox.Text.Trim();
        target.DisplayName = NullIfEmpty(LabelBox.Text);
        target.Kind = (KindBox.SelectedItem as KindOption)?.Kind ?? DeviceKind.Other;
        target.Hotkey = ShortcutBox.Hotkey;
        target.OnCommand = NullIfEmpty(OnCommandBox.Text);
        target.OffCommand = NullIfEmpty(OffCommandBox.Text);
        target.StateQuery = NullIfEmpty(StateQueryBox.Text);
    }

    private void OnTestOnClick(object sender, RoutedEventArgs e) =>
        _ = RunTestAsync((controller, device) => controller.SetPowerAsync(device, true, CancellationToken.None));

    private void OnTestOffClick(object sender, RoutedEventArgs e) =>
        _ = RunTestAsync((controller, device) => controller.SetPowerAsync(device, false, CancellationToken.None));

    private void OnTestStateClick(object sender, RoutedEventArgs e) =>
        _ = RunTestAsync((controller, device) => controller.QueryPowerAsync(device, CancellationToken.None), isQuery: true);

    private async Task RunTestAsync(Func<IDeviceController, DeviceConfig, Task<DeviceCommandResult>> test, bool isQuery = false)
    {
        var device = _device.Clone();
        Apply(device);

        SetTesting(true);
        try
        {
            var result = await test(App.Host.Controller, device);
            var state = result.IsOn switch { true => "on", false => "off", null => "unknown" };
            TestResult.Text = (result.Success, isQuery) switch
            {
                (false, _) => $"Didn't work. Google Assistant said: “{result.Message}”",
                (true, true) => $"State: {state}. Google Assistant said: “{result.Message}”",
                _ => $"Done. Google Assistant said: “{result.Message}”",
            };
        }
        catch (Exception ex)
        {
            TestResult.Text = ex.Message;
        }
        finally
        {
            SetTesting(false);
        }
    }

    private void SetTesting(bool testing)
    {
        TestProgress.IsActive = testing;
        TestOnButton.IsEnabled = TestOffButton.IsEnabled = TestStateButton.IsEnabled = !testing && CanTest;
        if (testing)
        {
            TestResult.Text = "Asking Google Assistant…";
        }
    }

    private bool CanTest => App.Host.Account.IsSignedIn && !string.IsNullOrWhiteSpace(NameBox.Text);

    private void UpdateTestButtons()
    {
        if (TestProgress.IsActive)
        {
            return;
        }

        TestOnButton.IsEnabled = TestOffButton.IsEnabled = TestStateButton.IsEnabled = CanTest;
        if (!App.Host.Account.IsSignedIn)
        {
            TestResult.Text = "Sign in on the Account page to try commands.";
        }
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
