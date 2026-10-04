using System.Collections.ObjectModel;
using HomeControl.Core.Assistant;
using HomeControl.Core.Auth;
using HomeControl.Core.Models;

namespace HomeControl.ViewModels;

/// <summary>State of the tray popup: the device list plus sign-in and error status.</summary>
public sealed class HomeViewModel : BindableBase
{
    private const int MaxParallelRequests = 3;

    private readonly GoogleAccount _account;
    private bool _isSignedIn;
    private bool _hasClient;
    private bool _isRefreshing;
    private string? _errorMessage;
    private bool _isErrorOpen;
    private CancellationTokenSource? _refreshCts;

    internal HomeViewModel(IDeviceController controller, GoogleAccount account)
    {
        Controller = controller;
        _account = account;
        Devices.CollectionChanged += (_, _) => RaiseListChanged();
        UpdateAccountState();
    }

    internal IDeviceController Controller { get; }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    public bool IsSignedIn
    {
        get => _isSignedIn;
        private set
        {
            if (SetProperty(ref _isSignedIn, value))
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

    public bool HasDevices => Devices.Count > 0;

    public bool NeedsSignIn => !_isSignedIn;

    public bool ShowNoDevices => _isSignedIn && Devices.Count == 0;

    public bool ShowDevices => _isSignedIn && Devices.Count > 0;

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
            if (!_isSignedIn)
            {
                return _hasClient ? "Not signed in" : "Not set up yet";
            }

            if (Devices.Count == 0)
            {
                return "No devices";
            }

            var on = Devices.Count(d => d.State == true);
            var text = Devices.Count == 1 ? "1 device" : $"{Devices.Count} devices";
            return on > 0 ? $"{text}  ·  {on} on" : text;
        }
    }

    /// <summary>Raised when the Assistant rejected our credentials.</summary>
    public event EventHandler? AuthenticationRequired;

    /// <summary>Raised when rows were added or removed (the popup resizes).</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Syncs the rows with the configured devices, keeping known states.</summary>
    internal void LoadDevices(IReadOnlyList<DeviceConfig> configs)
    {
        var existing = Devices.ToDictionary(d => d.Id);
        var ordered = configs.Select(config =>
        {
            if (existing.TryGetValue(config.Id, out var vm))
            {
                vm.Update(config);
                return vm;
            }

            return new DeviceViewModel(this, config);
        }).ToList();

        if (!ordered.SequenceEqual(Devices))
        {
            Devices.Clear();
            foreach (var vm in ordered)
            {
                Devices.Add(vm);
            }
        }

        RaiseListChanged();
    }

    internal void UpdateAccountState()
    {
        HasClient = _account.HasClient;
        IsSignedIn = _account.IsSignedIn;
        OnPropertyChanged(nameof(Summary));
        if (_isSignedIn && _errorMessage is not null && _errorMessage.Contains("sign in", StringComparison.OrdinalIgnoreCase))
        {
            IsErrorOpen = false;
        }
    }

    public DeviceViewModel? Find(string id) => Devices.FirstOrDefault(d => d.Id == id);

    /// <summary>Asks the Assistant for the state of every device.</summary>
    public async Task RefreshStatesAsync()
    {
        if (!_isSignedIn || Devices.Count == 0)
        {
            return;
        }

        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        IsRefreshing = true;
        IsErrorOpen = false;
        try
        {
            await RunLimitedAsync(Devices.ToList(), d => d.RefreshStateAsync(cts.Token));
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

    /// <summary>Turns every device off (devices already known to be off are skipped).</summary>
    public Task TurnAllOffAsync() =>
        RunLimitedAsync(Devices.Where(d => d.State != false).ToList(), d => d.SetPowerAsync(false));

    internal void OnDeviceStateChanged() => OnPropertyChanged(nameof(Summary));

    /// <summary>Smoke test only: show the device list without a Google account.</summary>
    internal void SimulateSignedIn() => IsSignedIn = true;

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

    private void RaiseListChanged()
    {
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(NeedsSignIn));
        OnPropertyChanged(nameof(ShowNoDevices));
        OnPropertyChanged(nameof(ShowDevices));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(Summary));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task RunLimitedAsync(IReadOnlyList<DeviceViewModel> devices, Func<DeviceViewModel, Task> action)
    {
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
