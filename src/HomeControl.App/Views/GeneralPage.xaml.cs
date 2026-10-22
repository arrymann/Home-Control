using System.Diagnostics;
using System.Reflection;
using HomeControl.Core.Audio;
using HomeControl.Core.Settings;
using HomeControl.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace HomeControl.Views;

/// <summary>A row in Settings › General › Clap events.</summary>
public sealed class ClapEventItem
{
    internal ClapEventItem(ClapEventSummary summary)
    {
        Id = summary.Id;
        var time = summary.Time.ToLocalTime();
        Title = time.Date == DateTimeOffset.Now.Date ? $"Today {time:T}" : $"{time:d} {time:T}";
        Description = summary.Description;
        FlagIndex = (int)summary.Flag;
        Note = summary.Note ?? "";
    }

    public string Id { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>The <see cref="ClapEventFlag"/>, as the index of its entry in the list.</summary>
    public int FlagIndex { get; internal set; }

    public string Note { get; internal set; }
}

public sealed partial class GeneralPage : Page
{
    private const int EventsShown = 25;

    private static GeneralPage? _current;
    private bool _loading;
    private int _microphoneLoads;
    private string? _eventsShown;

    public GeneralPage()
    {
        InitializeComponent();
        PopupShortcut.Validate = hotkey => App.Host.ValidateHotkey(hotkey, ownerId: null, isPopupHotkey: true);
        PopupShortcut.HotkeyChanged += (_, _) =>
        {
            App.Host.Settings.OpenPopupHotkey = PopupShortcut.Hotkey;
            App.Host.SaveSettings();
        };
    }

    private static AppSettings Settings => App.Host.Settings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loading = true;
        Select(ThemeBox, Settings.Theme.ToString());
        Select(BackdropBox, Settings.Backdrop.ToString());
        PopupShortcut.Hotkey = Settings.OpenPopupHotkey;
        NotifySwitch.IsOn = Settings.NotifyOnHotkey;
        RefreshSwitch.IsOn = Settings.RefreshStatesOnOpen;
        StartupSwitch.IsOn = StartupService.IsEnabled;

        ClapSwitch.IsOn = Settings.Claps.Enabled;
        Select(ClapSensitivityBox, Settings.Claps.Sensitivity.ToString());
        ClapLockSwitch.IsOn = Settings.Claps.PauseWhileLocked;
        ClapEventsSwitch.IsOn = Settings.Claps.SaveEvents;
        ShowMicrophones([], listed: false);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutExpander.Description = $"Version {version?.ToString(3) ?? "1.0.0"}";
        _loading = false;

        _current = this;
        App.Host.Claps.Changed += OnClapsChanged;
        App.Host.Claps.Heard += OnClapsHeard;
        App.Host.Claps.Events.Changed += OnClapEventsChanged;
        App.Host.SettingsApplied += OnClapsChanged;
        UpdateClapStatus();
        ShowHeard();
        _ = LoadMicrophonesAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Host.Claps.Changed -= OnClapsChanged;
        App.Host.Claps.Heard -= OnClapsHeard;
        App.Host.Claps.Events.Changed -= OnClapEventsChanged;
        App.Host.SettingsApplied -= OnClapsChanged;
        if (_current == this)
        {
            _current = null;
        }
    }

    /// <summary>Scrolls to the clap listening section (used by the smoke test).</summary>
    internal static void ShowClapListening()
    {
        if (_current is { } page)
        {
            page.ClapExpander.IsExpanded = true;
            page.ClapHeader.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
        }
    }

    /// <summary>Scrolls to the clap events (used by the smoke test).</summary>
    internal static void ShowClapEvents()
    {
        if (_current is { } page)
        {
            page.ClapExpander.IsExpanded = true;
            page.ClapEventsCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
        }
    }

    private void OnClapsChanged(object? sender, EventArgs e) => UpdateClapStatus();

    private void OnClapEventsChanged(object? sender, EventArgs e) => ShowClapEvents(force: false);

    /// <summary>The newest clap events, rebuilt only when one was added or marked (so a note being typed isn't lost).</summary>
    private void ShowClapEvents(bool force)
    {
        var events = App.Host.Claps.Events.Events;
        var items = events.Take(EventsShown).Select(e => new ClapEventItem(e)).ToList();
        var key = string.Join("|", items.Select(i => $"{i.Id}:{i.FlagIndex}:{i.Note}"));
        if (force || key != _eventsShown)
        {
            _eventsShown = key;
            ClapEventList.ItemsSource = items;
        }

        var marked = events.Count(e => e.Flag != ClapEventFlag.None);
        ClapEventsSummary.Text = events.Count == 0
            ? "None yet. They show up here when it hears claps, and when you say it missed some."
            : $"{events.Count} saved, {marked} marked{(events.Count > EventsShown ? $"; the newest {EventsShown} are shown" : "")}.";
        ClapExportButton.IsEnabled = ClapDeleteButton.IsEnabled = events.Count > 0;
        ClapMissedButton.IsEnabled = Settings.Claps.SaveEvents && App.Host.Claps.IsListening;
    }

    private void ShowClapEventsMessage(string text)
    {
        ClapEventsMessage.Text = text;
        ClapEventsMessage.Visibility = Visibility.Visible;
    }

    private void OnClapEventsToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            Settings.Claps.SaveEvents = ClapEventsSwitch.IsOn;
            App.Host.SaveSettings();
        }
    }

    private void OnClapMissedClick(object sender, RoutedEventArgs e) =>
        ShowClapEventsMessage(App.Host.Claps.SaveMissed()
            ? "Saved what it heard in the last half minute. Add a note if you like."
            : "It isn't listening right now, so there is nothing to save.");

    private async void OnClapFlagChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: ClapEventItem item } box && box.SelectedIndex >= 0 && box.SelectedIndex != item.FlagIndex)
        {
            item.FlagIndex = box.SelectedIndex;
            await App.Host.Claps.Events.SetFlagAsync(item.Id, (ClapEventFlag)box.SelectedIndex);
        }
    }

    private async void OnClapNoteLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ClapEventItem item } box && box.Text.Trim() != item.Note)
        {
            item.Note = box.Text.Trim();
            await App.Host.Claps.Events.SetNoteAsync(item.Id, item.Note);
        }
    }

    private async void OnClapExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = $"Home Control clap events {DateTime.Now:yyyy-MM-dd}",
            };
            picker.FileTypeChoices.Add("Zip file", [".zip"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Host.SettingsWindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            var count = await App.Host.Claps.Events.ExportAsync(file.Path);
            ShowClapEventsMessage($"Exported {count} clap event{(count == 1 ? "" : "s")} to {file.Path}.");
        }
        catch (Exception ex)
        {
            Log.Error("Exporting clap events", ex);
            ShowClapEventsMessage("Couldn't export them: " + ex.Message);
        }
    }

    private void OnClapEventsFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = App.Host.Claps.Events.Folder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private async void OnClapDeleteClick(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "Delete all clap events?",
            Content = "The events and what you marked are deleted. Export them first if you want to send them.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            await App.Host.Claps.Events.DeleteAllAsync();
            ShowClapEventsMessage("Deleted.");
        }
    }

    private void OnClapsHeard(object? sender, EventArgs e) => ShowHeard();

    private void ShowHeard()
    {
        var recent = App.Host.Claps.RecentSounds;
        ClapCopyButton.Content = "Copy";
        ClapHeardText.Text = recent.Count > 0
            ? string.Join("\n", recent)
            : App.Host.Claps.IsListening
                ? "Nothing yet. Clap twice, or make the sound that set it off, and it shows up here."
                : "Shows up here while it listens.";
    }

    private void OnClapCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText($"Home Control, clap sensitivity {Settings.Claps.Sensitivity}:\n{string.Join("\n", App.Host.Claps.RecentSounds)}");
            Clipboard.SetContent(package);
            ClapCopyButton.Content = "Copied";
        }
        catch (Exception ex)
        {
            // The clipboard can be busy (another app holding it).
            Log.Info($"Copying what it hears: {ex.GetType().Name} 0x{ex.HResult:X8}");
            ClapCopyButton.Content = "Couldn't copy, try again";
        }
    }

    private void UpdateClapStatus()
    {
        var claps = App.Host.Claps;
        ClapExpander.Description = claps.StatusText;
        ClapBlockedBar.IsOpen = claps.State == ClapListenerState.Blocked;
        ShowHeard();
        ShowClapEvents(force: false);

        // Also changed from the tray menu.
        var loading = _loading;
        _loading = true;
        ClapSwitch.IsOn = Settings.Claps.Enabled;
        ClapLockSwitch.IsOn = Settings.Claps.PauseWhileLocked;
        ClapEventsSwitch.IsOn = Settings.Claps.SaveEvents;
        Select(ClapSensitivityBox, Settings.Claps.Sensitivity.ToString());
        _loading = loading;
    }

    private async Task LoadMicrophonesAsync()
    {
        var load = ++_microphoneLoads;
        var microphones = await Task.Run(() => MicrophoneCapture.ListMicrophones());
        if (load == _microphoneLoads && _current == this)
        {
            ShowMicrophones(microphones, listed: true);
        }
    }

    /// <summary>Fills the microphone list: the Windows default first, then the plugged-in ones.</summary>
    /// <param name="listed">False until Windows has listed the microphones.</param>
    private void ShowMicrophones(IReadOnlyList<(string Id, string Name)> microphones, bool listed)
    {
        var loading = _loading;
        _loading = true;
        MicrophoneBox.Items.Clear();
        MicrophoneBox.Items.Add(new ComboBoxItem { Content = "Windows default", Tag = string.Empty });
        foreach (var (id, name) in microphones)
        {
            MicrophoneBox.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        }

        var chosen = Settings.Claps.MicrophoneId;
        if (chosen is not null && microphones.All(m => m.Id != chosen))
        {
            MicrophoneBox.Items.Add(new ComboBoxItem { Content = listed ? "Chosen microphone (not connected)" : "Chosen microphone", Tag = chosen });
        }

        Select(MicrophoneBox, chosen ?? string.Empty);
        _loading = loading;
    }

    private void OnClapToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            Settings.Claps.Enabled = ClapSwitch.IsOn;
            App.Host.SaveSettings();
        }
    }

    private void OnClapSensitivityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && Enum.TryParse<ClapSensitivity>(SelectedTag(ClapSensitivityBox), out var sensitivity))
        {
            Settings.Claps.Sensitivity = sensitivity;
            App.Host.SaveSettings();
        }
    }

    private void OnMicrophoneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SelectedTag(MicrophoneBox) is { } id)
        {
            Settings.Claps.MicrophoneId = id.Length == 0 ? null : id;
            App.Host.SaveSettings();
        }
    }

    private void OnClapLockToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            Settings.Claps.PauseWhileLocked = ClapLockSwitch.IsOn;
            App.Host.SaveSettings();
        }
    }

    private void OnMicrophonePrivacyClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });

    private void OnClapRetryClick(object sender, RoutedEventArgs e) => App.Host.Claps.Retry();

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && Enum.TryParse<ThemePreference>(SelectedTag(ThemeBox), out var theme))
        {
            Settings.Theme = theme;
            App.Host.SaveSettings();
        }
    }

    private void OnBackdropChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && Enum.TryParse<BackdropKind>(SelectedTag(BackdropBox), out var backdrop))
        {
            Settings.Backdrop = backdrop;
            App.Host.SaveSettings();
        }
    }

    private void OnNotifyToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            Settings.NotifyOnHotkey = NotifySwitch.IsOn;
            App.Host.SaveSettings();
        }
    }

    private void OnRefreshToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            Settings.RefreshStatesOnOpen = RefreshSwitch.IsOn;
            App.Host.SaveSettings();
        }
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            StartupService.SetEnabled(StartupSwitch.IsOn);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error("Changing startup setting", ex);
            _loading = true;
            StartupSwitch.IsOn = StartupService.IsEnabled;
            _loading = false;
        }
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });
    }

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == tag) ?? box.Items[0];

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;
}
