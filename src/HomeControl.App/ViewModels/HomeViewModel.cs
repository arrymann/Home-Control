using System.Collections.ObjectModel;
using System.Diagnostics;
using HomeControl.Core.Auth;
using HomeControl.Core.Devices;
using HomeControl.Core.GoogleHome;
using HomeControl.Core.Models;
using HomeControl.Services;

namespace HomeControl.ViewModels;

/// <summary>State of the tray popup: the device list plus connection and error status.</summary>
public sealed class HomeViewModel : BindableBase
{
    private const int MaxParallelRequests = 3;

    private readonly GoogleAccount _account;
    private bool _assistantSignedIn;
    private bool _hasClient;
    private bool _googleHomeConnected;
    private bool _isRefreshing;
    private bool _batchRefreshRunning;
    private bool _isSyncing;
    private bool _showRooms;
    private string? _errorMessage;
    private bool _isErrorOpen;
    private CancellationTokenSource? _refreshCts;

    // Devices hidden from the popup still work with their shortcuts.
    private Dictionary<string, DeviceViewModel> _hidden = [];

    internal HomeViewModel(IDeviceController controller, GoogleAccount account)
    {
        Controller = controller;
        _account = account;
        Devices.CollectionChanged += (_, _) => RaiseListChanged();
        UpdateAccountState();
    }

    internal IDeviceController Controller { get; }

    private IBatchStateReader? BatchReader => Controller as IBatchStateReader;

    /// <summary>Devices shown in the popup (hidden and missing devices are left out).</summary>
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    /// <summary>Google Assistant is signed in.</summary>
    public bool IsAssistantSignedIn
    {
        get => _assistantSignedIn;
        private set
        {
            if (SetProperty(ref _assistantSignedIn, value))
            {
                RaiseListChanged();
            }
        }
    }

    /// <summary>The Google Home web session works.</summary>
    public bool IsGoogleHomeConnected
    {
        get => _googleHomeConnected;
        private set
        {
            if (SetProperty(ref _googleHomeConnected, value))
            {
                RaiseListChanged();
            }
        }
    }

    public bool HasClient
    {
        get => _hasClient;
        private set => SetProperty(ref _hasClient, value);
    }

    /// <summary>At least one way of switching devices is set up.</summary>
    public bool IsConnected => _googleHomeConnected || _assistantSignedIn;

    public bool HasDevices => Devices.Count > 0;

    /// <summary>The device list is being loaded from Google Home (set by the host).</summary>
    public bool IsSyncing
    {
        get => _isSyncing;
        internal set
        {
            if (SetProperty(ref _isSyncing, value))
            {
                RaiseListChanged();
            }
        }
    }

    public bool NeedsSignIn => !IsConnected;

    public bool ShowLoading => IsConnected && Devices.Count == 0 && _isSyncing;

    public bool ShowNoDevices => IsConnected && Devices.Count == 0 && !_isSyncing;

    public bool ShowDevices => IsConnected && Devices.Count > 0;

    /// <summary>Rows show their room when the devices are spread over several rooms.</summary>
    internal bool ShowRooms => _showRooms;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                OnPropertyChanged(nameof(CanRefresh));
            }
        }
    }

    public bool CanRefresh => !_isRefreshing && ShowDevices;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>Bound two-way to the InfoBar so the user can dismiss it.</summary>
    public bool IsErrorOpen
    {
        get => _isErrorOpen;
        set => SetProperty(ref _isErrorOpen, value);
    }

    /// <summary>Header caption, e.g. "4 devices · 2 on".</summary>
    public string Summary
    {
        get
        {
            if (!IsConnected)
            {
                return "Not connected";
            }

            if (Devices.Count == 0)
            {
                return _isSyncing ? "Loading devices…" : "No devices";
            }

            var on = Devices.Count(d => d.State == true);
            var text = Devices.Count == 1 ? "1 device" : $"{Devices.Count} devices";
            return on > 0 ? $"{text}  ·  {on} on" : text;
        }
    }

    /// <summary>Raised when the Assistant rejected our credentials.</summary>
    public event EventHandler? AuthenticationRequired;

    /// <summary>Raised when the Google Home session turned out to be signed out.</summary>
    public event EventHandler? GoogleHomeSignInRequired;

    /// <summary>Raised when rows were added or removed (the popup resizes).</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Syncs the rows with the configured devices, keeping known states.</summary>
    internal void LoadDevices(IReadOnlyList<DeviceConfig> configs)
    {
        var visible = configs.Where(c => !c.Hidden && !c.Missing).ToList();
        var showRooms = visible.Select(c => c.Room).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() > 1;

        var existing = Devices.Concat(_hidden.Values).ToDictionary(d => d.Id);
        DeviceViewModel Reuse(DeviceConfig config)
        {
            if (existing.TryGetValue(config.Id, out var vm))
            {
                vm.Update(config);
                return vm;
            }

            return new DeviceViewModel(this, config);
        }

        var ordered = visible.Select(Reuse).ToList();
        _hidden = configs.Where(c => c.Hidden && !c.Missing).Select(Reuse).ToDictionary(d => d.Id);

        if (!ordered.SequenceEqual(Devices))
        {
            Devices.Clear();
            foreach (var vm in ordered)
            {
                Devices.Add(vm);
            }
        }

        if (showRooms != _showRooms)
        {
            _showRooms = showRooms;
            foreach (var device in Devices)
            {
                device.RefreshSubtitle();
            }
        }

        RaiseListChanged();
    }

    internal void UpdateAccountState()
    {
        HasClient = _account.HasClient;
        IsAssistantSignedIn = _account.IsSignedIn;
        OnPropertyChanged(nameof(Summary));
        CloseErrorIfConnected();
    }

    internal void SetGoogleHomeConnected(bool connected)
    {
        IsGoogleHomeConnected = connected;
        OnPropertyChanged(nameof(Summary));
        CloseErrorIfConnected();
    }

    /// <summary>A device by id, including devices hidden from the popup (for shortcuts).</summary>
    public DeviceViewModel? Find(string id) => Devices.FirstOrDefault(d => d.Id == id) ?? _hidden.GetValueOrDefault(id);

    /// <summary>
    /// Refreshes every device: Google Home devices in one request, Assistant devices one by one
    /// (only when <paramref name="includeAssistant"/>, as each is a separate Assistant query).
    /// </summary>
    public async Task RefreshStatesAsync(bool includeAssistant = true)
    {
        if (!IsConnected || Devices.Count == 0)
        {
            return;
        }

        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        IsRefreshing = true;
        IsErrorOpen = false;
        try
        {
            var batched = Devices.Where(d => BatchReader?.CanReadInBatch(d.Config) == true).ToList();
            var others = includeAssistant && _assistantSignedIn ? Devices.Except(batched).ToList() : [];
            await Task.WhenAll(
                RefreshBatchAsync(batched, showErrors: true, cts.Token),
                RunLimitedAsync(others, d => d.RefreshStateAsync(cts.Token)));
        }
        finally
        {
            if (_refreshCts == cts)
            {
                IsRefreshing = false;
                _refreshCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Quietly refreshes the Google Home devices (used for polling while the popup is open).</summary>
    public Task PollStatesAsync()
    {
        var batched = Devices.Where(d => BatchReader?.CanReadInBatch(d.Config) == true).ToList();
        return RefreshBatchAsync(batched, showErrors: false, CancellationToken.None);
    }

    /// <summary>Toggles a device from its shortcut, reading its current state first if it is old.</summary>
    public async Task<DeviceCommandResult?> ToggleFromShortcutAsync(DeviceViewModel device)
    {
        // Read directly rather than through RefreshBatchAsync, which skips the read while a poll is
        // running; devices hidden from the popup are never polled, so this is their only read.
        if (BatchReader is { } reader && reader.CanReadInBatch(device.Config) && device.IsStale(TimeSpan.FromSeconds(20)))
        {
            try
            {
                var started = Stopwatch.GetTimestamp();
                var states = await reader.ReadStatesAsync([device.Config], CancellationToken.None);
                if (states.TryGetValue(device.Id, out var status))
                {
                    device.ApplyStatus(status, started);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The toggle below reports the real problem (and falls back to Assistant if it can).
                Log.Error("Reading the state before a shortcut toggle", ex);
            }
        }

        return await device.ToggleAsync();
    }

    /// <summary>Turns every device off (devices already known to be off are skipped).</summary>
    public Task TurnAllOffAsync() =>
        RunLimitedAsync(Devices.Where(d => d.State != false).ToList(), d => d.SetPowerAsync(false));

    internal void OnDeviceStateChanged() => OnPropertyChanged(nameof(Summary));

    /// <summary>Smoke test only: show the device list without a Google account.</summary>
    internal void SimulateSignedIn() => IsGoogleHomeConnected = true;

    internal void ReportError(string message)
    {
        ErrorMessage = message;
        IsErrorOpen = true;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void OnAuthenticationFailed(string message)
    {
        UpdateAccountState();
        ReportError(message.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            ? message
            : message + " Please sign in again.");
        AuthenticationRequired?.Invoke(this, EventArgs.Empty);
    }

    internal void OnGoogleHomeSignInRequired(string message)
    {
        SetGoogleHomeConnected(false);
        ReportError(message);
        GoogleHomeSignInRequired?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshBatchAsync(IReadOnlyList<DeviceViewModel> devices, bool showErrors, CancellationToken cancellationToken)
    {
        if (devices.Count == 0 || BatchReader is not { } reader || _batchRefreshRunning)
        {
            return;
        }

        _batchRefreshRunning = true;
        try
        {
            var started = Stopwatch.GetTimestamp();
            var states = await reader.ReadStatesAsync(devices.Select(d => d.Config).ToList(), cancellationToken);
            foreach (var device in devices)
            {
                if (states.TryGetValue(device.Id, out var status))
                {
                    device.ApplyStatus(status, started);
                }
            }

            SetGoogleHomeConnected(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (GoogleHomeSignInRequiredException ex)
        {
            SetGoogleHomeConnected(false);
            GoogleHomeSignInRequired?.Invoke(this, EventArgs.Empty);
            if (showErrors)
            {
                ReportError(ex.Message);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing Google Home states", ex);
            if (showErrors)
            {
                ReportError(ex is GoogleHomeException ? ex.Message : "Couldn't refresh the devices: " + ex.Message);
            }
        }
        finally
        {
            _batchRefreshRunning = false;
        }
    }

    private void CloseErrorIfConnected()
    {
        if (IsConnected && _errorMessage is not null && _errorMessage.Contains("sign in", StringComparison.OrdinalIgnoreCase))
        {
            IsErrorOpen = false;
        }
    }

    private void RaiseListChanged()
    {
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(NeedsSignIn));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowNoDevices));
        OnPropertyChanged(nameof(ShowDevices));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(Summary));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task RunLimitedAsync(IReadOnlyList<DeviceViewModel> devices, Func<DeviceViewModel, Task> action)
    {
        if (devices.Count == 0)
        {
            return;
        }

        // Runs on the UI thread; the semaphore just limits how many requests are in flight.
        using var gate = new SemaphoreSlim(MaxParallelRequests);
        await Task.WhenAll(devices.Select(async device =>
        {
            await gate.WaitAsync();
            try
            {
                await action(device);
            }
            finally
            {
                gate.Release();
            }
        }));
    }
}
