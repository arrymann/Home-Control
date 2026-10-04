using System.Diagnostics;
using HomeControl.Core.Assistant;
using HomeControl.Core.Auth;
using HomeControl.Core.Devices;
using HomeControl.Core.GoogleHome;
using HomeControl.Core.Models;
using HomeControl.Services;

namespace HomeControl.ViewModels;

/// <summary>One device row in the tray popup.</summary>
public sealed class DeviceViewModel : BindableBase
{
    private readonly HomeViewModel _owner;
    private DeviceConfig _config;
    private bool? _state;
    private bool _isOn;
    private bool _isOnline = true;
    private bool _isBusy;
    private bool _syncingToggle;
    private string? _busyText;
    private string? _error;
    private DateTimeOffset _lastUpdated = DateTimeOffset.MinValue;
    private long _lastCommandEnded;

    internal DeviceViewModel(HomeViewModel owner, DeviceConfig config)
    {
        _owner = owner;
        _config = config;
    }

    public string Id => _config.Id;

    public DeviceConfig Config => _config;

    public string Label => _config.Label.Trim();

    public DeviceKind Kind => _config.Kind;

    public string HotkeyText => _config.Hotkey is { IsValid: true } hotkey ? hotkey.ToString() : string.Empty;

    /// <summary>Last known state; null until the service has told us or we changed it.</summary>
    public bool? State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(Subtitle));
                _owner.OnDeviceStateChanged();
            }
        }
    }

    /// <summary>False when Google Home reports the device as unreachable.</summary>
    public bool IsOnline
    {
        get => _isOnline;
        private set
        {
            if (SetProperty(ref _isOnline, value))
            {
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    /// <summary>Bound two-way to the toggle switch: the user flipping it sends the command.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!SetProperty(ref _isOn, value) || _syncingToggle)
            {
                return;
            }

            _ = SetPowerAsync(value);
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanToggle));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public bool CanToggle => !_isBusy;

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    /// <summary>Second line of the row: room, progress/error/state, and the shortcut.</summary>
    public string Subtitle
    {
        get
        {
            var status = _isBusy ? _busyText
                : HasError ? _error
                : !_isOnline ? "Offline"
                : _state switch { true => "On", false => "Off", null => null };
            var room = _owner.ShowRooms ? _config.Room : null;

            return string.Join("  ·  ", new[] { room, status, HotkeyText }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }

    /// <summary>True when the state is older than <paramref name="maxAge"/> (or unknown).</summary>
    internal bool IsStale(TimeSpan maxAge) => DateTimeOffset.UtcNow - _lastUpdated > maxAge;

    internal void Update(DeviceConfig config)
    {
        _config = config;
        OnPropertyChanged(nameof(Config));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(Subtitle));
    }

    internal void RefreshSubtitle() => OnPropertyChanged(nameof(Subtitle));

    /// <summary>
    /// Applies a state read in a batch. Ignored while a command is in flight, and when the read
    /// was sent before the last command finished (it would undo the command's newer state).
    /// </summary>
    /// <param name="readStarted">Stopwatch timestamp taken just before the read was sent.</param>
    internal void ApplyStatus(DeviceStatus status, long readStarted)
    {
        if (_isBusy || readStarted <= _lastCommandEnded)
        {
            return;
        }

        _lastUpdated = DateTimeOffset.UtcNow;
        IsOnline = status.Online;
        if (status.Online && _error is not null)
        {
            Error = null;
        }

        State = status.IsOn;
        SyncToggle(_state ?? false);
    }

    /// <summary>Flips the device (unknown state counts as off). Used by the global shortcut.</summary>
    public Task<DeviceCommandResult?> ToggleAsync() => SetPowerAsync(!(_state ?? false));

    public async Task<DeviceCommandResult?> SetPowerAsync(bool turnOn)
    {
        if (_isBusy)
        {
            return null;
        }

        _busyText = turnOn ? "Turning on…" : "Turning off…";
        IsBusy = true;
        Error = null;
        SyncToggle(turnOn);

        DeviceCommandResult? result = null;
        try
        {
            result = await _owner.Controller.SetPowerAsync(_config, turnOn, CancellationToken.None);
            if (result.Success)
            {
                State = result.IsOn ?? turnOn;
                IsOnline = true;
                _lastUpdated = DateTimeOffset.UtcNow;
            }
            else
            {
                if (result.IsOn is { } actual)
                {
                    State = actual;
                }

                Error = Shorten(result.Message);
            }
        }
        catch (Exception ex)
        {
            Error = Describe(ex);
            result = new DeviceCommandResult(false, null, Error);
        }
        finally
        {
            _lastCommandEnded = Stopwatch.GetTimestamp();
            IsBusy = false;
            SyncToggle(_state ?? false);
        }

        return result;
    }

    /// <summary>Asks the device's service whether it is on.</summary>
    public async Task RefreshStateAsync(CancellationToken cancellationToken)
    {
        if (_isBusy)
        {
            return;
        }

        _busyText = "Checking…";
        IsBusy = true;
        Error = null;
        try
        {
            var result = await _owner.Controller.QueryPowerAsync(_config, cancellationToken);
            if (result.Success)
            {
                State = result.IsOn;
                _lastUpdated = DateTimeOffset.UtcNow;
            }
            else
            {
                Error = Shorten(result.Message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error = Describe(ex);
        }
        finally
        {
            IsBusy = false;
            SyncToggle(_state ?? false);
        }
    }

    /// <summary>Smoke test only: set a state without contacting Google.</summary>
    internal void SimulateState(bool isOn, bool online = true)
    {
        State = isOn;
        IsOnline = online;
        SyncToggle(isOn);
    }

    private void SyncToggle(bool value)
    {
        _syncingToggle = true;
        try
        {
            IsOn = value;
        }
        finally
        {
            _syncingToggle = false;
        }
    }

    private string Describe(Exception ex)
    {
        switch (ex)
        {
            case GoogleHomeSignInRequiredException:
                _owner.OnGoogleHomeSignInRequired(ex.Message);
                return "Sign in to Google Home";
            case GoogleHomeException:
                _owner.ReportError(ex.Message);
                return Shorten(ex.Message);
            case AuthenticationRequiredException:
            case AssistantException { IsAuthenticationError: true }:
                _owner.OnAuthenticationFailed(ex.Message);
                return "Sign in again";
            case AssistantException or OAuthException:
                _owner.ReportError(ex.Message);
                return Shorten(ex.Message);
            case HttpRequestException:
                _owner.ReportError("Could not reach Google. Check your internet connection.");
                return "No connection";
            default:
                Log.Error($"Controlling {Label}", ex);
                _owner.ReportError(ex.Message);
                return "Something went wrong";
        }
    }

    private static string Shorten(string message) =>
        string.IsNullOrWhiteSpace(message) ? "Failed" : message.Length <= 80 ? message : message[..79] + "…";
}
