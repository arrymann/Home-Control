using System.Diagnostics;
using System.Reflection;
using HomeControl.Core.Audio;
using HomeControl.Core.Settings;
using HomeControl.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HomeControl.Views;

public sealed partial class GeneralPage : Page
{
    private static GeneralPage? _current;
    private bool _loading;
    private int _microphoneLoads;

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
        ShowMicrophones([]);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutExpander.Description = $"Version {version?.ToString(3) ?? "1.0.0"}";
        _loading = false;

        _current = this;
        App.Host.Claps.Changed += OnClapsChanged;
        UpdateClapStatus();
        _ = LoadMicrophonesAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Host.Claps.Changed -= OnClapsChanged;
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

    private void OnClapsChanged(object? sender, EventArgs e) => UpdateClapStatus();

    private void UpdateClapStatus()
    {
        var claps = App.Host.Claps;
        ClapExpander.Description = claps.StatusText;
        ClapBlockedBar.IsOpen = claps.State == ClapListenerState.Blocked;
    }

    private async Task LoadMicrophonesAsync()
    {
        var load = ++_microphoneLoads;
        var microphones = await Task.Run(() => MicrophoneCapture.ListMicrophones());
        if (load == _microphoneLoads && _current == this)
        {
            ShowMicrophones(microphones);
        }
    }

    /// <summary>Fills the microphone list: the Windows default first, then the plugged-in ones.</summary>
    private void ShowMicrophones(IReadOnlyList<(string Id, string Name)> microphones)
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
            MicrophoneBox.Items.Add(new ComboBoxItem { Content = "Chosen microphone (not connected)", Tag = chosen });
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
