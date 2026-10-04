using HomeControl.Core.Assistant;
using HomeControl.Core.Auth;
using HomeControl.Core.Security;
using HomeControl.Core.Settings;
using HomeControl.Interop;
using HomeControl.Services;
using HomeControl.ViewModels;
using HomeControl.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace HomeControl;

/// <summary>
/// Wires everything together and owns the app's lifetime: tray icon, popup, settings
/// window, hotkeys and the Google services.
/// </summary>
internal sealed class AppHost
{
    private readonly SettingsStore _settingsStore = new(AppPaths.SettingsFile);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private DispatcherQueue _dispatcher = null!;
    private MessageWindow _messageWindow = null!;
    private TrayIcon _trayIcon = null!;
    private ThemeService _theme = null!;
    private HotkeyService _hotkeys = null!;
    private TrayPopupWindow _popup = null!;
    private SettingsWindow? _settingsWindow;
    private AssistantClient _assistant = null!;
    private IntPtr _trayIconHandle;
    private int _trayIconDpi;
    private bool _trayIconLight;

    public AppSettings Settings { get; private set; } = new();

    public GoogleAccount Account { get; private set; } = null!;

    public IDeviceController Controller { get; private set; } = null!;

    public IAssistantClient Assistant => _assistant;

    public HomeViewModel Home { get; private set; } = null!;

    public HotkeyService Hotkeys => _hotkeys;

    public ThemeService Theme => _theme;

    /// <summary>Owner window for file pickers opened from the settings pages.</summary>
    public IntPtr SettingsWindowHandle => _settingsWindow is null ? IntPtr.Zero : WindowHelpers.GetHandle(_settingsWindow);

    /// <summary>Raised after settings were saved and applied.</summary>
    public event EventHandler? SettingsApplied;

    public void Start(bool background)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Settings = _settingsStore.Load();
        if (_settingsStore.LoadError is not null)
        {
            Log.Info($"Settings could not be read and were reset: {_settingsStore.LoadError}");
        }

        var secrets = new SecretStore(AppPaths.SecretsFile, new DpapiProtector());
        Account = new GoogleAccount(_http, secrets);
        Account.StateChanged += (_, _) => _dispatcher.TryEnqueue(() =>
        {
            Home.UpdateAccountState();
            UpdateToolTip();
        });

        _assistant = new AssistantClient(Account, () => Settings.Assistant);
        Controller = new AssistantDeviceController(_assistant, () => Settings.Assistant);

        Home = new HomeViewModel(Controller, Account);
        Home.LoadDevices(Settings.Devices);
        Home.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.Summary))
            {
                UpdateToolTip();
            }
        };

        _messageWindow = new MessageWindow();
        _messageWindow.MessageReceived += OnMessage;
        _theme = new ThemeService(_messageWindow);
        _theme.Changed += (_, _) => ApplyAppearance();
        _hotkeys = new HotkeyService(_messageWindow);

        _trayIcon = new TrayIcon(_messageWindow);
        _trayIcon.Selected += (_, e) => TogglePopup(e.Point);
        _trayIcon.ContextMenuRequested += (_, e) => ShowContextMenu(e.Point);
        UpdateTrayIcon();
        UpdateToolTip();
        _trayIcon.Show();

        _popup = new TrayPopupWindow(Home);
        _popup.SettingsRequested += (_, page) => OpenSettings(page);

        ApplyAppearance();
        ApplyHotkeys();
        StartupService.RefreshPath();

        if (!background)
        {
            if (!Account.IsSignedIn || Settings.Devices.Count == 0)
            {
                OpenSettings(Account.IsSignedIn ? "devices" : "account");
            }
            else
            {
                ShowPopup();
            }
        }
    }

    /// <summary>Saves settings and applies them everywhere.</summary>
    public void SaveSettings()
    {
        try
        {
            _settingsStore.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving settings", ex);
        }

        Home.LoadDevices(Settings.Devices);
        ApplyHotkeys();
        ApplyAppearance();
        SettingsApplied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens the popup next to the tray icon (or the taskbar corner when the icon is hidden).</summary>
    public void ShowPopup()
    {
        if (_popup.IsOpen)
        {
            return;
        }

        _popup.ShowAt(GetPopupAnchor());
        OnPopupOpened();
    }

    public void OpenSettings(string? page = null)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                _hotkeys.Resume(); // in case a shortcut recorder was active
            };
            _settingsWindow.ApplyAppearance(_theme.Resolve(Settings.Theme), Settings.Backdrop);
        }

        _settingsWindow.Show(page);
    }

    public void Exit()
    {
        _settingsWindow?.Close();
        _hotkeys.Dispose();
        _trayIcon.Dispose();
        _popup.CloseForExit();
        if (_trayIconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_trayIconHandle);
        }

        _messageWindow.Dispose();
        _assistant.Dispose();
        Application.Current.Exit();
    }

    /// <summary>
    /// Checks a shortcut against the app's other shortcuts and against other applications.
    /// Returns an error message, or null when the shortcut can be used.
    /// </summary>
    public string? ValidateHotkey(Core.Hotkeys.Hotkey hotkey, string? deviceId, bool isPopupHotkey)
    {
        if (!isPopupHotkey && Settings.OpenPopupHotkey == hotkey)
        {
            return "already opens Home Control.";
        }

        var other = Settings.Devices.FirstOrDefault(d => d.Id != deviceId && d.Hotkey == hotkey);
        if (other is not null)
        {
            return $"already used for {other.Label}.";
        }

        return _hotkeys.IsAvailable(hotkey) ? null : "another app is using this shortcut.";
    }

    /// <summary>The theme the windows should use right now.</summary>
    public ElementTheme EffectiveTheme => _theme.Resolve(Settings.Theme);

    private void TogglePopup(PointInt32 clickPoint)
    {
        var wasOpen = _popup.IsOpen;
        _popup.Toggle(GetPopupAnchor(clickPoint));
        if (!wasOpen && _popup.IsOpen)
        {
            OnPopupOpened();
        }
    }

    private void OnPopupOpened()
    {
        if (Settings.RefreshStatesOnOpen)
        {
            _ = Home.RefreshStatesAsync();
        }
    }

    private PointInt32 GetPopupAnchor(PointInt32? clickPoint = null)
    {
        if (_trayIcon.GetBounds() is { } icon)
        {
            return new PointInt32(icon.X + icon.Width / 2, icon.Y + icon.Height / 2);
        }

        if (clickPoint is { } point)
        {
            return point;
        }

        // Icon hidden in the overflow area: use the corner of the primary work area.
        var work = DisplayArea.Primary.WorkArea;
        return new PointInt32(work.X + work.Width - 1, work.Y + work.Height - 1);
    }

    private void ShowContextMenu(PointInt32 point)
    {
        _popup.Hide();
        var taskbarAtBottom = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Nearest) is var display &&
                              display.WorkArea.Y + display.WorkArea.Height <= point.Y;

        new ContextMenu()
            .Add("Open Home Control", ShowPopup, isDefault: true)
            .Add("Refresh device states", () => _ = Home.RefreshStatesAsync(), enabled: Home.ShowDevices)
            .Add("Turn all off", () => _ = Home.TurnAllOffAsync(), enabled: Home.ShowDevices)
            .AddSeparator()
            .Add("Settings", () => OpenSettings())
            .AddSeparator()
            .Add("Exit", Exit)
            .Show(_messageWindow.Handle, point, openUpwards: taskbarAtBottom);
    }

    private void ApplyHotkeys()
    {
        var bindings = new List<HotkeyBinding>();
        if (Settings.OpenPopupHotkey is { IsValid: true } openHotkey)
        {
            bindings.Add(new HotkeyBinding(openHotkey, "Open Home Control", ShowPopup));
        }

        foreach (var device in Settings.Devices)
        {
            if (device.Hotkey is { IsValid: true } hotkey)
            {
                var id = device.Id;
                bindings.Add(new HotkeyBinding(hotkey, device.Label, () => _ = ToggleFromHotkeyAsync(id)));
            }
        }

        _hotkeys.Apply(bindings);
    }

    private async Task ToggleFromHotkeyAsync(string deviceId)
    {
        var device = Home.Find(deviceId);
        if (device is null)
        {
            return;
        }

        if (!Account.IsSignedIn)
        {
            _trayIcon.ShowNotification("Home Control", "Sign in to Google in Settings to control your devices.");
            return;
        }

        var result = await device.ToggleAsync();
        if (result is null || !Settings.NotifyOnHotkey)
        {
            return;
        }

        var text = result.Success
            ? $"Turned {(device.State == true ? "on" : "off")}"
            : $"Couldn't change it: {result.Message}";
        _trayIcon.ShowNotification(device.Label, text);
    }

    private void ApplyAppearance()
    {
        var theme = EffectiveTheme;
        WindowHelpers.SetMenuTheme(Settings.Theme);
        _popup.ApplyAppearance(theme, Settings.Backdrop);
        _settingsWindow?.ApplyAppearance(theme, Settings.Backdrop);
        UpdateTrayIcon();
    }

    /// <summary>Uses the white glyph on a dark taskbar and the black glyph on a light one.</summary>
    private void UpdateTrayIcon()
    {
        var light = _theme.TaskbarUsesLightTheme;
        var dpi = (int)NativeMethods.GetDpiForWindow(_messageWindow.Handle);
        if (dpi <= 0)
        {
            dpi = 96;
        }

        if (_trayIconHandle != IntPtr.Zero && light == _trayIconLight && dpi == _trayIconDpi)
        {
            return;
        }

        var size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, (uint)dpi);
        var file = AppPaths.Asset(light ? "TrayIcon.Light.ico" : "TrayIcon.Dark.ico");
        var icon = NativeMethods.LoadImageW(IntPtr.Zero, file, NativeMethods.IMAGE_ICON, size, size, NativeMethods.LR_LOADFROMFILE);
        if (icon == IntPtr.Zero)
        {
            Log.Info($"Could not load tray icon {file}");
            return;
        }

        _trayIcon.SetIcon(icon);
        if (_trayIconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_trayIconHandle);
        }

        _trayIconHandle = icon;
        _trayIconLight = light;
        _trayIconDpi = dpi;
    }

    private void UpdateToolTip() => _trayIcon?.SetToolTip($"Home Control – {Home.Summary}");

    private void OnMessage(uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case MessageWindow.WM_SHOW_POPUP:
                handled = true;
                if (!Account.IsSignedIn || Settings.Devices.Count == 0)
                {
                    OpenSettings();
                }
                else
                {
                    ShowPopup();
                }

                break;
            case NativeMethods.WM_DPICHANGED:
            case NativeMethods.WM_DISPLAYCHANGE:
                UpdateTrayIcon();
                break;
        }
    }
}
