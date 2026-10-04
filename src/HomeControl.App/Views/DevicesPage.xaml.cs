using HomeControl.Core.Models;
using HomeControl.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HomeControl.Views;

/// <summary>A row on the Devices page.</summary>
public sealed class DeviceListItem
{
    internal DeviceListItem(DeviceConfig config, int index, int count)
    {
        Id = config.Id;
        Label = config.Label.Trim();
        Kind = config.Kind;
        CanMoveUp = index > 0;
        CanMoveDown = index < count - 1;
        ShowInTray = !config.Hidden;

        // Discovered devices come back at the next sync, so they are hidden rather than removed.
        CanRemove = !config.IsGoogleHome || config.Missing;
        RemoveToolTip = CanRemove ? "Remove" : "Devices from Google Home can't be removed – turn off “Show in the tray” instead";

        var parts = new List<string>();
        if (config.Missing)
        {
            parts.Add("No longer in Google Home");
        }
        else if (config.IsGoogleHome)
        {
            if (!string.IsNullOrWhiteSpace(config.Room))
            {
                parts.Add(config.Room.Trim());
            }

            if (!string.IsNullOrWhiteSpace(config.DisplayName))
            {
                parts.Add($"Google Home name: {config.Name.Trim()}");
            }
        }
        else
        {
            parts.Add($"Google Assistant: “{config.Name.Trim()}”");
        }

        parts.Add(config.Hotkey is { IsValid: true } hotkey ? $"Shortcut: {hotkey}" : "No shortcut");
        if (config.Hidden)
        {
            parts.Add("Not in the tray");
        }

        Description = string.Join("  ·  ", parts);
    }

    public string Id { get; }

    public string Label { get; }

    public string Description { get; }

    public DeviceKind Kind { get; }

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }

    public bool ShowInTray { get; }

    public bool CanRemove { get; }

    public string RemoveToolTip { get; }
}

public sealed partial class DevicesPage : Page
{
    public DevicesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.Host.SettingsApplied += OnSettingsApplied;
        App.Host.Account.StateChanged += OnAccountChanged;
        App.Host.GoogleHomeChanged += OnAccountChanged;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Host.SettingsApplied -= OnSettingsApplied;
        App.Host.Account.StateChanged -= OnAccountChanged;
        App.Host.GoogleHomeChanged -= OnAccountChanged;
    }

    private void OnSettingsApplied(object? sender, EventArgs e) => Refresh();

    private void OnAccountChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        var devices = App.Host.Settings.Devices;
        DeviceList.ItemsSource = devices.Select((d, i) => new DeviceListItem(d, i, devices.Count)).ToList();
        ListHeader.Visibility = devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SignInInfo.IsOpen = !App.Host.Home.IsConnected;
        UpdateSyncCard();

        var failed = App.Host.Hotkeys.Failed;
        HotkeyInfo.IsOpen = failed.Count > 0;
        HotkeyInfo.Message = failed.Count == 0
            ? string.Empty
            : "Another app already uses: " + string.Join(", ", failed.Select(b => $"{b.Hotkey} ({b.Description})")) + ". Pick different shortcuts.";
    }

    private void UpdateSyncCard()
    {
        var host = App.Host;
        var settings = host.Settings.GoogleHome;
        var syncing = host.IsSyncingGoogleHome;
        SyncCard.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        SyncProgress.IsActive = syncing;
        SyncButton.IsEnabled = !syncing;
        SyncCard.Description = syncing
            ? "Loading your devices…"
            : host.GoogleHomeMessage
              ?? (settings.LastSync is { } lastSync
                  ? $"Last synced {lastSync.ToLocalTime():g}. New devices are added when the app starts and when you sync."
                  : "New devices are added when the app starts and when you sync.");
    }

    private async void OnSyncClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.Host.SyncGoogleHomeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Syncing Google Home", ex);
        }

        Refresh();
    }

    private void OnSignInClick(object sender, RoutedEventArgs e) => App.Host.OpenGoogleSignIn();

    private void OnShowInTrayToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not DeviceListItem item)
        {
            return;
        }

        var device = App.Host.Settings.Devices.FirstOrDefault(d => d.Id == item.Id);
        if (device is null || device.Hidden == !toggle.IsOn)
        {
            return; // initial binding, or nothing changed
        }

        device.Hidden = !toggle.IsOn;
        App.Host.SaveSettings();
    }

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new DeviceEditorDialog(null) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            App.Host.Settings.Devices.Add(dialog.Result);
            App.Host.SaveSettings();
        }
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        var index = IndexOf(sender);
        if (index < 0)
        {
            return;
        }

        var devices = App.Host.Settings.Devices;
        var dialog = new DeviceEditorDialog(devices[index]) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            // The list may have changed while the dialog was open (a sync, for example).
            if (devices.FirstOrDefault(d => d.Id == dialog.Result.Id) is { } current)
            {
                dialog.ApplyTo(current);
                App.Host.SaveSettings();
            }
        }
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        var index = IndexOf(sender);
        if (index < 0)
        {
            return;
        }

        var device = App.Host.Settings.Devices[index];
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"Remove {device.Label.Trim()}?",
            Content = device.Missing
                ? "It was removed from Google Home, so it can't be switched any more."
                : "It is only removed from Home Control, not from Google Home.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            App.Host.Settings.Devices.RemoveAll(d => d.Id == device.Id);
            App.Host.SaveSettings();
        }
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int offset)
    {
        var devices = App.Host.Settings.Devices;
        var index = IndexOf(sender);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= devices.Count)
        {
            return;
        }

        (devices[index], devices[target]) = (devices[target], devices[index]);
        App.Host.SaveSettings();
    }

    private static int IndexOf(object sender) =>
        (sender as FrameworkElement)?.DataContext is DeviceListItem item
            ? App.Host.Settings.Devices.FindIndex(d => d.Id == item.Id)
            : -1;
}
