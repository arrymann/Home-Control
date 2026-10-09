using HomeControl.Core.Assistant;
using HomeControl.Core.Auth;
using HomeControl.Core.Devices;
using HomeControl.Core.GoogleHome;
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
    private bool _exited;
    private MessageWindow _messageWindow = null!;
    private TrayIcon _trayIcon = null!;
    private ThemeService _theme = null!;
    private HotkeyService _hotkeys = null!;
    private TrayPopupWindow _popup = null!;
    private SettingsWindow? _settingsWindow;
    private GoogleSignInWindow? _signInWindow;
    private AssistantClient _assistant = null!;
    private GoogleHomeSession _googleHomeSession = null!;
    private GoogleHomeClient _googleHome = null!;
    private DispatcherQueueTimer _pollTimer = null!;
    private AutomationService _automations = null!;
    private ClapListener _claps = null!;
    private Task<GoogleHomeSyncResult>? _syncTask;
    private int _googleHomeSignOuts;
    private bool _syncFailed;
    private IntPtr _trayIconHandle;
    private int _trayIconDpi;
    private bool _trayIconLight;

    public AppSettings Settings { get; private set; } = new();

    /// <summary>
    /// Running the smoke test: settings are never written and nothing contacts Google on its
    /// own (the test calls the Google Home code itself).
    /// </summary>
    internal bool IsSmokeTest { get; init; }

    public GoogleAccount Account { get; private set; } = null!;

    public IDeviceController Controller { get; private set; } = null!;

    public IAssistantClient Assistant => _assistant;

    /// <summary>State of the Google Home web session.</summary>
    public GoogleHomeConnection GoogleHomeState => _googleHomeSession.State;

    /// <summary>A Google Home sync is running.</summary>
    public bool IsSyncingGoogleHome => _syncTask is not null;

    /// <summary>Outcome of the last Google Home sync or sign-in problem, for the settings pages.</summary>
    public string? GoogleHomeMessage { get; private set; }

    internal GoogleHomeSession GoogleHomeSession => _googleHomeSession;

    internal AutomationService Automations => _automations;

    internal ClapListener Claps => _claps;

    public HomeViewModel Home { get; private set; } = null!;

    public HotkeyService Hotkeys => _hotkeys;

    public ThemeService Theme => _theme;

    public IntPtr PopupWindowHandle => WindowHelpers.GetHandle(_popup);

    /// <summary>XAML root of the settings window, for dialogs.</summary>
    public XamlRoot? SettingsXamlRoot => _settingsWindow?.Content.XamlRoot;

    /// <summary>Owner window for file pickers opened from the settings pages.</summary>
    public IntPtr SettingsWindowHandle => _settingsWindow is null ? IntPtr.Zero : WindowHelpers.GetHandle(_settingsWindow);

    internal IntPtr SignInWindowHandle => _signInWindow is null ? IntPtr.Zero : WindowHelpers.GetHandle(_signInWindow);

    internal string DescribeSignInWindow() => _signInWindow?.DescribeForTest() ?? "closed";

    /// <summary>Raised after settings were saved and applied.</summary>
    public event EventHandler? SettingsApplied;

    /// <summary>Raised on the UI thread when the Google Home session or a sync changed state.</summary>
    public event EventHandler? GoogleHomeChanged;

    public void Start(bool background)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Settings = _settingsStore.Load();
        if (_settingsStore.LoadError is not null)
        {
            Log.Info($"Settings could not be read and were reset: {_settingsStore.LoadError}");
        }

        // Hosts the tray icon, the hotkeys and the hidden Google Home page.
        _messageWindow = new MessageWindow();
        _messageWindow.MessageReceived += OnMessage;

        var secrets = new SecretStore(AppPaths.SecretsFile, new DpapiProtector());
        Account = new GoogleAccount(_http, secrets);
        Account.StateChanged += (_, _) => _dispatcher.TryEnqueue(() =>
        {
            Home.UpdateAccountState();
            UpdateToolTip();
        });

        _googleHomeSession = new GoogleHomeSession(_dispatcher, _messageWindow.Handle, () => Settings.GoogleHome);
        _googleHomeSession.StateChanged += (_, _) => OnGoogleHomeStateChanged();
        _googleHome = new GoogleHomeClient(_googleHomeSession) { Trace = Log.Info };

        _assistant = new AssistantClient(Account, () => Settings.Assistant);
        Controller = new CompositeDeviceController(
            new GoogleHomeDeviceController(_googleHome),
            new AssistantDeviceController(_assistant, () => Settings.Assistant),
            () => Settings.UseAssistantFallback && Account.IsSignedIn,
            () => Settings.GoogleHome.Enabled);

        Home = new HomeViewModel(Controller, Account);
        Home.LoadDevices(Settings.Devices);
        Home.LoadScenes(Settings.Scenes);

        // Until the first request says otherwise, assume an earlier Google Home sign-in still works.
        Home.SetGoogleHomeConnected(Settings.GoogleHome.Enabled);
        Home.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.Summary))
            {
                UpdateToolTip();
            }
        };

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
        _popup.GoogleSignInRequested += (_, _) => OpenGoogleSignIn();
        _popup.Shown += (_, _) => OnPopupOpened();
        _popup.Hidden += (_, _) => _pollTimer.Stop();

        // Keeps the toggles current while the popup is open (one request for all Google Home devices).
        _pollTimer = _dispatcher.CreateTimer();
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += (_, _) =>
        {
            if (_popup.IsOpen && Home.IsGoogleHomeConnected && !Home.IsRefreshing)
            {
                _ = Home.PollStatesAsync();
            }
        };

        _automations = new AutomationService(this, _messageWindow, _http, _dispatcher, readOnly: IsSmokeTest);
        _claps = new ClapListener(_dispatcher, () => Settings.Claps, _automations, simulated: IsSmokeTest);
        _claps.Changed += (_, _) => UpdateToolTip();
        PcPower.ShutdownFailed += (_, message) => _trayIcon.ShowNotification("Home Control", message);

        ApplyAppearance();
        ApplyHotkeys();
        StartupService.RefreshPath();

        if (Settings.GoogleHome.Enabled && !IsSmokeTest)
        {
            _ = SyncQuietlyAsync();
        }

        // Last, so "when Home Control starts" automations find everything ready.
        _automations.Start();
        _claps.Update();

        if (!background)
        {
            ShowPopupOrSetup();
        }
    }

    /// <summary>A notification from the tray icon (respects Focus/quiet hours).</summary>
    public void ShowNotification(string title, string text) => _trayIcon.ShowNotification(title, text);

    /// <summary>Saves settings and applies them everywhere.</summary>
    public void SaveSettings()
    {
        SaveSettingsQuietly();
        Home.LoadDevices(Settings.Devices);
        Home.LoadScenes(Settings.Scenes);
        ApplyHotkeys();
        ApplyAppearance();
        _claps?.Update();
        SettingsApplied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves settings that only remember how a page looks (nothing to apply).</summary>
    public void SaveSettingsQuietly()
    {
        try
        {
            if (!IsSmokeTest)
            {
                _settingsStore.Save(Settings);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving settings", ex);
        }
    }

    /// <summary>Opens the popup next to the tray icon (or the taskbar corner when the icon is hidden).</summary>
    public void ShowPopup()
    {
        if (_popup.IsOpen)
        {
            return;
        }

        _popup.ShowAt(GetPopupAnchor());
    }

    /// <summary>Shows the popup, or the settings page that is needed first.</summary>
    public void ShowPopupOrSetup()
    {
        if (!Settings.GoogleHome.Enabled && !Account.IsSignedIn)
        {
            OpenSettings("account");
        }
        else if (Settings.Devices.Count == 0 && !Settings.GoogleHome.Enabled)
        {
            OpenSettings("devices");
        }
        else
        {
            ShowPopup();
        }
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

    /// <summary>Opens Settings › Automations with an automation in the editor.</summary>
    internal void OpenAutomation(string automationId)
    {
        OpenSettings("automations");
        AutomationsPage.Edit(automationId);
    }

    /// <summary>Opens the window where the user signs in to Google Home.</summary>
    public void OpenGoogleSignIn()
    {
        _popup.Hide();
        if (_signInWindow is null)
        {
            var window = new GoogleSignInWindow(_googleHomeSession);
            window.SignedIn += (_, _) => _ = OnGoogleSignedInAsync(window);
            window.Closed += (_, _) =>
            {
                if (_signInWindow == window)
                {
                    _signInWindow = null;
                }
            };
            window.ApplyAppearance(EffectiveTheme, Settings.Backdrop);
            _signInWindow = window;
        }

        _signInWindow.Activate();
        NativeMethods.SetForegroundWindow(WindowHelpers.GetHandle(_signInWindow));
    }

    internal void CloseGoogleSignIn() => _signInWindow?.Close();

    /// <summary>
    /// Reads the devices from Google Home and merges them into the device list (new devices
    /// are added, renamed ones updated, removed ones flagged). Concurrent calls share one sync.
    /// </summary>
    public async Task<GoogleHomeSyncResult> SyncGoogleHomeAsync()
    {
        if (_syncTask is { } running)
        {
            return await running;
        }

        var task = RunSyncAsync();
        _syncTask = task;
        Home.IsSyncing = true;
        GoogleHomeChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var result = await task;
            GoogleHomeMessage = null;
            _syncFailed = false;
            return result;
        }
        catch (Exception ex)
        {
            if (Settings.GoogleHome.Enabled)
            {
                GoogleHomeMessage = ex is GoogleHomeException ? ex.Message : "Couldn't load your devices: " + ex.Message;
            }

            // Not signed in needs the user; anything else (no network yet, a page that didn't load)
            // is retried when Google Home answers again or the popup opens.
            _syncFailed = ex is not GoogleHomeSignInRequiredException;
            throw;
        }
        finally
        {
            if (_syncTask == task)
            {
                _syncTask = null;
            }

            Home.IsSyncing = false;
            GoogleHomeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Signs out of Google Home: deletes the private browser profile's cookies and data.</summary>
    public async Task SignOutGoogleHomeAsync()
    {
        _googleHomeSignOuts++; // a sync that is still running must not sign back in
        _signInWindow?.Close();
        Settings.GoogleHome.Enabled = false;
        Settings.GoogleHome.AuthUser = 0; // the profile is emptied: the next sign-in is account 0
        _syncFailed = false;
        SaveSettings();
        Home.SetGoogleHomeConnected(false);
        try
        {
            await _googleHomeSession.SignOutAsync();
            GoogleHomeMessage = null;
        }
        catch (Exception ex)
        {
            Log.Error("Signing out of Google Home", ex);
            GoogleHomeMessage = "Couldn't delete the sign-in data: " + ex.Message;
        }

        GoogleHomeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Exit()
    {
        if (_exited)
        {
            return; // e.g. "Exit" and an exit request at the same time
        }

        _exited = true;
        _pollTimer.Stop();
        _claps.Dispose(); // closes the microphone
        _automations.Dispose();
        _signInWindow?.Close();
        _settingsWindow?.Close();
        _hotkeys.Dispose();
        _trayIcon.Dispose();
        _popup.CloseForExit();
        if (_trayIconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_trayIconHandle);
        }

        _googleHomeSession.Dispose(); // its hidden browser lives in the message window
        _messageWindow.Dispose();
        _assistant.Dispose();
        Application.Current.Exit();
    }

    /// <summary>
    /// Checks a shortcut against the app's other shortcuts and against other applications.
    /// Returns an error message, or null when the shortcut can be used.
    /// </summary>
    /// <param name="ownerId">The device or scene the shortcut is for (its own shortcut is no conflict).</param>
    public string? ValidateHotkey(Core.Hotkeys.Hotkey hotkey, string? ownerId, bool isPopupHotkey)
    {
        if (!isPopupHotkey && Settings.OpenPopupHotkey == hotkey)
        {
            return "already opens Home Control.";
        }

        var other = Settings.Devices.FirstOrDefault(d => d.Id != ownerId && d.Hotkey == hotkey);
        if (other is not null)
        {
            return $"already used for {other.Label}.";
        }

        var scene = Settings.Scenes.FirstOrDefault(s => s.Id != ownerId && s.Hotkey == hotkey);
        if (scene is not null)
        {
            return $"already used for the scene {scene.Name.Trim()}.";
        }

        return _hotkeys.IsAvailable(hotkey) ? null : "another app is using this shortcut.";
    }

    /// <summary>The theme the windows should use right now.</summary>
    public ElementTheme EffectiveTheme => _theme.Resolve(Settings.Theme);

    private void TogglePopup(PointInt32 clickPoint) => _popup.Toggle(GetPopupAnchor(clickPoint));

    private void OnPopupOpened()
    {
        if (IsSmokeTest)
        {
            return;
        }

        // Google Home states come in one cheap request; Assistant devices need one query each.
        _ = Home.RefreshStatesAsync(includeAssistant: Settings.RefreshStatesOnOpen);
        _pollTimer.Interval = TimeSpan.FromSeconds(Settings.GoogleHome.RefreshSeconds);
        _pollTimer.Start();

        // The last sync failed (e.g. no network at startup), or the first one never finished.
        if (Settings.GoogleHome.Enabled && (_syncFailed || Settings.GoogleHome.LastSync is null))
        {
            _ = SyncQuietlyAsync();
        }
    }

    /// <summary>
    /// The sign-in window found a Google session on home.google.com. Cookies alone don't prove
    /// Google Home accepts it, so sync now and tell the window how that went: a rejected session
    /// sends the user back to Google's sign-in page.
    /// </summary>
    private async Task OnGoogleSignedInAsync(GoogleSignInWindow window)
    {
        // A sync that is already running may use the previous account: let it finish, then sync again.
        var signOuts = _googleHomeSignOuts;
        if (_syncTask is { } running)
        {
            try
            {
                await running;
            }
            catch (Exception)
            {
                // reported by that sync
            }
        }

        if (signOuts != _googleHomeSignOuts)
        {
            return; // the user signed out meanwhile
        }

        if (window.AuthUser is { } authUser && authUser != Settings.GoogleHome.AuthUser)
        {
            Settings.GoogleHome.AuthUser = authUser;
        }

        Settings.GoogleHome.Enabled = true;
        SaveSettings();
        Home.SetGoogleHomeConnected(true);
        try
        {
            var result = await SyncGoogleHomeAsync();
            var count = Settings.Devices.Count(d => d.IsGoogleHome && !d.Missing);
            window.ReportSignInResult(GoogleSignInOutcome.Synced, count == 1 ? "1 device" : $"{count} devices");
        }
        catch (GoogleHomeSignInRequiredException)
        {
            // The session may already have been SignedOut, so no state change would tell the popup.
            Home.SetGoogleHomeConnected(false);
            window.ReportSignInResult(GoogleSignInOutcome.Rejected, null);
        }
        catch (Exception ex)
        {
            Log.Error("Syncing Google Home after signing in", ex);
            window.ReportSignInResult(GoogleSignInOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Syncs in the background; problems show on the settings pages and in the popup.</summary>
    private async Task SyncQuietlyAsync()
    {
        try
        {
            await SyncGoogleHomeAsync();
        }
        catch (GoogleHomeSignInRequiredException ex)
        {
            Log.Info("Google Home sync: " + ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("Syncing Google Home", ex);
            if (Home.Devices.Count == 0)
            {
                // Nothing in the popup to show the problem on otherwise.
                Home.ReportError(GoogleHomeMessage ?? ex.Message);
            }
        }
    }

    private async Task<GoogleHomeSyncResult> RunSyncAsync()
    {
        var signOuts = _googleHomeSignOuts;
        var graph = await _googleHome.GetHomeGraphAsync(CancellationToken.None);
        if (signOuts != _googleHomeSignOuts)
        {
            throw new GoogleHomeSignInRequiredException("You signed out of Google Home.");
        }

        var result = GoogleHomeSync.Merge(Settings.Devices, graph);
        Log.Info($"Google Home sync: {graph.Devices.Count} devices in {graph.Homes.Count} home(s); {result}");

        Settings.GoogleHome.Enabled = true;
        Settings.GoogleHome.LastSync = DateTimeOffset.Now;
        SaveSettings();
        Home.SetGoogleHomeConnected(true);
        _ = Home.RefreshStatesAsync(includeAssistant: false);
        return result;
    }

    private void OnGoogleHomeStateChanged()
    {
        switch (_googleHomeSession.State)
        {
            case GoogleHomeConnection.Connected:
                Home.SetGoogleHomeConnected(true);
                if (_syncFailed && _syncTask is null && Settings.GoogleHome.Enabled && !IsSmokeTest)
                {
                    _syncFailed = false;
                    _ = SyncQuietlyAsync(); // the connection is back: catch up on the failed sync
                }

                break;
            case GoogleHomeConnection.SignedOut:
            case GoogleHomeConnection.Unavailable:
                Home.SetGoogleHomeConnected(false);
                break;
        }

        UpdateToolTip();
        GoogleHomeChanged?.Invoke(this, EventArgs.Empty);
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

        // Icon hidden in the overflow area: the tray end of the taskbar, wherever it is.
        if (Taskbar.GetTrayCorner() is { } corner)
        {
            return corner;
        }

        var work = DisplayArea.Primary.WorkArea;
        return new PointInt32(work.X + work.Width - 1, work.Y + work.Height - 1);
    }

    private void ShowContextMenu(PointInt32 point)
    {
        _popup.Hide();
        var taskbarAtBottom = Taskbar.GetPlacement(point).Edge == TaskbarEdge.Bottom;

        new ContextMenu()
            .Add("Open Home Control", ShowPopup, isDefault: true)
            .Add("Refresh device states", () => _ = Home.RefreshStatesAsync(), enabled: Home.ShowDevices)
            .Add("Turn all off", () => _ = Home.TurnAllOffAsync(), enabled: Home.ShowDevices)
            .AddSeparator()
            .Add(Settings.GoogleHome.Enabled ? "Sync devices from Google Home" : "Sign in to Google Home",
                () =>
                {
                    if (Settings.GoogleHome.Enabled && Home.IsGoogleHomeConnected)
                    {
                        _ = SyncQuietlyAsync();
                    }
                    else
                    {
                        OpenGoogleSignIn();
                    }
                },
                enabled: _syncTask is null)
            .AddSeparator()
            .AddIf(PcPower.ShutdownScheduled, "Cancel shutdown", CancelScheduledShutdown)
            .AddIf(_automations.Engine.ClapCounts().Count > 0,
                Settings.Claps.Enabled ? "Stop listening for claps" : "Listen for claps",
                () =>
                {
                    Settings.Claps.Enabled = !Settings.Claps.Enabled;
                    SaveSettings();
                })
            .Add("Settings", () => OpenSettings())
            .AddSeparator()
            .Add("Exit", Exit)
            .Show(_messageWindow.Handle, point, openUpwards: taskbarAtBottom);
    }

    private void CancelScheduledShutdown()
    {
        PcPower.CancelShutdown();
        _trayIcon.ShowNotification("Home Control", "The shutdown was cancelled.");
    }

    private void ApplyHotkeys()
    {
        var bindings = new List<HotkeyBinding>();
        if (Settings.OpenPopupHotkey is { IsValid: true } openHotkey)
        {
            bindings.Add(new HotkeyBinding(openHotkey, "Open Home Control", () =>
            {
                if (_popup.IsOpen)
                {
                    _popup.Hide();
                }
                else
                {
                    ShowPopup();
                }
            }));
        }

        foreach (var device in Settings.Devices.Where(d => !d.Missing))
        {
            if (device.Hotkey is { IsValid: true } hotkey)
            {
                var id = device.Id;
                bindings.Add(new HotkeyBinding(hotkey, device.Label, () => _ = ToggleFromHotkeyAsync(id)));
            }
        }

        foreach (var scene in Settings.Scenes)
        {
            if (scene.Hotkey is { IsValid: true } hotkey)
            {
                var id = scene.Id;
                bindings.Add(new HotkeyBinding(hotkey, $"the scene {scene.Name.Trim()}", () => _ = ToggleSceneFromHotkeyAsync(id)));
            }
        }

        _hotkeys.Apply(bindings);
    }

    private async Task ToggleSceneFromHotkeyAsync(string sceneId)
    {
        if (Home.FindScene(sceneId) is not { } scene)
        {
            return;
        }

        if (!Home.IsConnected)
        {
            _trayIcon.ShowNotification("Home Control", "Sign in to Google Home in Settings › Account to control your devices.");
            return;
        }

        var turnOn = !(scene.State ?? false);
        var failed = await scene.SetPowerAsync(turnOn);
        if (Settings.NotifyOnHotkey || failed is not null)
        {
            _trayIcon.ShowNotification(scene.Label, failed is null ? $"Turned {(turnOn ? "on" : "off")}" : $"Not everything switched: {failed}");
        }
    }

    private async Task ToggleFromHotkeyAsync(string deviceId)
    {
        var device = Home.Find(deviceId);
        if (device is null)
        {
            return;
        }

        if (!Home.IsConnected)
        {
            _trayIcon.ShowNotification("Home Control", "Sign in to Google Home in Settings › Account to control your devices.");
            return;
        }

        var result = await Home.ToggleFromShortcutAsync(device);
        if (result is null || !Settings.NotifyOnHotkey)
        {
            return;
        }

        var text = !result.Success ? $"Couldn't change it: {result.Message}"
            : device.IsPending ? $"Turning {(device.EffectiveState == true ? "on" : "off")}…"
            : $"Turned {(device.State == true ? "on" : "off")}";
        _trayIcon.ShowNotification(device.Label, text);
    }

    /// <summary>Applies theme and window material to every window and the tray icon.</summary>
    public void ApplyAppearance()
    {
        var theme = EffectiveTheme;
        WindowHelpers.SetMenuTheme(Settings.Theme);
        _popup.ApplyAppearance(theme, Settings.Backdrop);
        _settingsWindow?.ApplyAppearance(theme, Settings.Backdrop);
        _signInWindow?.ApplyAppearance(theme, Settings.Backdrop);
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

    private void UpdateToolTip() =>
        _trayIcon?.SetToolTip($"Home Control – {Home.Summary}{(_claps?.IsListening == true ? " · listening for claps" : "")}");

    private void OnMessage(uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case MessageWindow.WM_SHOW_POPUP:
                handled = true;
                ShowPopupOrSetup();
                break;
            case MessageWindow.WM_EXIT_APP:
                handled = true;
                Log.Info("Asked to exit by another program.");
                _dispatcher.TryEnqueue(Exit); // not while inside the message window's own procedure
                break;
            case NativeMethods.WM_DPICHANGED:
            case NativeMethods.WM_DISPLAYCHANGE:
                UpdateTrayIcon();
                break;
        }
    }
}
