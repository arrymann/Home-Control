using HomeControl.Core.Models;
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

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.DisplayName))
        {
            parts.Add($"Google Home name: {config.Name.Trim()}");
        }

        parts.Add(config.Hotkey is { IsValid: true } hotkey ? $"Shortcut: {hotkey}" : "No shortcut");
        Description = string.Join("  ·  ", parts);
    }

    public string Id { get; }

    public string Label { get; }

    public string Description { get; }

    public DeviceKind Kind { get; }

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }
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
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Host.SettingsApplied -= OnSettingsApplied;
        App.Host.Account.StateChanged -= OnAccountChanged;
    }

    private void OnSettingsApplied(object? sender, EventArgs e) => Refresh();

    private void OnAccountChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        var devices = App.Host.Settings.Devices;
        DeviceList.ItemsSource = devices.Select((d, i) => new DeviceListItem(d, i, devices.Count)).ToList();
        ListHeader.Visibility = devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SignInInfo.IsOpen = !App.Host.Account.IsSignedIn;

        var failed = App.Host.Hotkeys.Failed;
        HotkeyInfo.IsOpen = failed.Count > 0;
        HotkeyInfo.Message = failed.Count == 0
            ? string.Empty
            : "Another app already uses: " + string.Join(", ", failed.Select(b => $"{b.Hotkey} ({b.Description})")) + ". Pick different shortcuts.";
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
            // The list may have changed while the dialog was open; replace by id.
            var current = devices.FindIndex(d => d.Id == dialog.Result.Id);
            if (current >= 0)
            {
                devices[current] = dialog.Result;
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
            Content = "It is only removed from Home Control, not from Google Home.",
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

    private void OnGoToAccountClick(object sender, RoutedEventArgs e) => App.Host.OpenSettings("account");

    private static int IndexOf(object sender) =>
        (sender as FrameworkElement)?.DataContext is DeviceListItem item
            ? App.Host.Settings.Devices.FindIndex(d => d.Id == item.Id)
            : -1;
}
