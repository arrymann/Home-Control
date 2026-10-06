using HomeControl.Core.Models;
using HomeControl.Services;
using HomeControl.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HomeControl.Views;

/// <summary>A row on the Devices page.</summary>
public sealed class DeviceListItem
{
    internal DeviceListItem(DeviceConfig config, bool canMoveUp, bool canMoveDown)
    {
        Id = config.Id;
        Label = config.Label.Trim();
        Kind = config.Kind;
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
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

/// <summary>A home on the Devices page, with its rooms. Collapsing it hides the rooms.</summary>
public sealed class DeviceHomeGroup : BindableBase
{
    private readonly Action<string, bool> _remember;
    private bool _isExpanded;

    internal DeviceHomeGroup(string key, string title, int deviceCount, IReadOnlyList<DeviceRoomGroup> rooms, bool isExpanded, Action<string, bool> remember)
    {
        Key = key;
        Title = title;
        Count = deviceCount == 1 ? "1 device" : $"{deviceCount} devices";
        Rooms = rooms;
        _isExpanded = isExpanded;
        _remember = remember;
    }

    internal string Key { get; }

    public string Title { get; }

    public string Count { get; }

    public IReadOnlyList<DeviceRoomGroup> Rooms { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Chevron));
                OnPropertyChanged(nameof(RoomsVisibility));
                OnPropertyChanged(nameof(ToggleText));
                OnPropertyChanged(nameof(AccessibleName));
                _remember(Key, value);
            }
        }
    }

    public string Chevron => _isExpanded ? "\uE70D" : "\uE76C";

    public Visibility RoomsVisibility => _isExpanded ? Visibility.Visible : Visibility.Collapsed;

    public string ToggleText => _isExpanded ? "Collapse" : "Expand";

    public string AccessibleName => $"{Title}, {Count}, {(_isExpanded ? "expanded" : "collapsed")}";
}

/// <summary>A room on the Devices page: a collapsible group of devices.</summary>
public sealed class DeviceRoomGroup : BindableBase
{
    private readonly Action<string, bool> _remember;
    private bool _isExpanded;

    internal DeviceRoomGroup(string key, string title, IReadOnlyList<DeviceListItem> devices, bool isExpanded, Action<string, bool> remember)
    {
        Key = key;
        Title = title;
        Devices = devices;
        var hidden = devices.Count(d => !d.ShowInTray);
        Description = (devices.Count == 1 ? "1 device" : $"{devices.Count} devices") + (hidden > 0 ? $"  ·  {hidden} not in the tray" : string.Empty);
        _isExpanded = isExpanded;
        _remember = remember;
    }

    internal string Key { get; }

    public string Title { get; }

    public string Description { get; }

    public IReadOnlyList<DeviceListItem> Devices { get; }

    /// <summary>Bound two-way to the expander.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                _remember(Key, value);
            }
        }
    }
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

    /// <summary>
    /// Devices are grouped by home and room here (homes and rooms by name, unnamed ones last);
    /// within a room they keep the tray's order, which Move up/down changes.
    /// </summary>
    private static string GroupKey(DeviceConfig device) => RoomKey(Clean(device.Home), Clean(device.Room));

    private static string HomeKey(string? home) => "home:" + home;

    private static string RoomKey(string? home, string? room) => "room:" + home + "\u001f" + room;

    private static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private List<DeviceHomeGroup> BuildGroups(IReadOnlyList<DeviceConfig> devices)
    {
        var collapsed = App.Host.Settings.CollapsedDeviceGroups.ToHashSet();
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var hasHomes = devices.Any(d => Clean(d.Home) is not null);

        return devices
            .GroupBy(d => Clean(d.Home), comparer)
            .OrderBy(g => g.Key is null)
            .ThenBy(g => g.Key, comparer)
            .Select(home =>
            {
                var rooms = home
                    .GroupBy(d => Clean(d.Room), comparer)
                    .OrderBy(g => g.Key is null)
                    .ThenBy(g => g.Key, comparer)
                    .Select(room =>
                    {
                        var members = room.ToList();
                        var items = members.Select((d, i) => new DeviceListItem(d, i > 0, i < members.Count - 1)).ToList();
                        var key = RoomKey(home.Key, room.Key);
                        var title = room.Key ?? "No room";
                        return new DeviceRoomGroup(key, title, items, !collapsed.Contains(key), RememberGroup);
                    })
                    .ToList();
                var homeKey = HomeKey(home.Key);
                var homeTitle = home.Key ?? (hasHomes ? "Other devices" : "Devices");
                return new DeviceHomeGroup(homeKey, homeTitle, home.Count(), rooms, !collapsed.Contains(homeKey), RememberGroup);
            })
            .ToList();
    }

    /// <summary>Saves which groups are collapsed (all start expanded).</summary>
    private static void RememberGroup(string key, bool isExpanded)
    {
        var collapsed = App.Host.Settings.CollapsedDeviceGroups;
        var changed = isExpanded ? collapsed.Remove(key) : !collapsed.Contains(key);
        if (!isExpanded && changed)
        {
            collapsed.Add(key);
        }

        if (changed)
        {
            App.Host.SaveSettingsQuietly();
        }
    }

    private void OnHomeHeaderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceHomeGroup home)
        {
            home.IsExpanded = !home.IsExpanded;
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        var devices = App.Host.Settings.Devices;
        HomeList.ItemsSource = BuildGroups(devices);
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

    /// <summary>Swaps the device with its neighbour in the same room (the tray shows that order).</summary>
    private void Move(object sender, int offset)
    {
        var devices = App.Host.Settings.Devices;
        var index = IndexOf(sender);
        if (index < 0)
        {
            return;
        }

        var group = GroupKey(devices[index]);
        var target = index + offset;
        while (target >= 0 && target < devices.Count &&
               !string.Equals(GroupKey(devices[target]), group, StringComparison.CurrentCultureIgnoreCase))
        {
            target += offset;
        }

        if (target < 0 || target >= devices.Count)
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
