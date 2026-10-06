using System.Diagnostics;
using System.Reflection;
using HomeControl.Core.Settings;
using HomeControl.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HomeControl.Views;

public sealed partial class GeneralPage : Page
{
    private bool _loading;

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

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutExpander.Description = $"Version {version?.ToString(3) ?? "1.0.0"}";
        _loading = false;
    }

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
