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
    private bool _isPending;
    private bool _pendingTarget;
    private CancellationTokenSource? _verify;
    private string? _error;
    private DateTimeOffset _lastUpdated = DateTimeOffset.MinValue;
    private long _lastCommandEnded;

    /// <summary>When a device hasn't confirmed a command, it is asked again after these waits (about a minute in all).</summary>
    private static readonly TimeSpan[] VerifyDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(25)];

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
            }
        }
    }

    /// <summary>
    /// A command was sent but the device hasn't reported its new state yet (TVs and other slow
    /// devices). The toggle shows the requested state, greyed out, until the device confirms it.
    /// </summary>
    public bool IsPending
    {
        get => _isPending;
        private set
        {
            if (SetProperty(ref _isPending, value))
            {
                OnPropertyChanged(nameof(CanToggle));
                OnPropertyChanged(nameof(PendingText));
            }
        }
    }

    /// <summary>Tooltip while <see cref="IsPending"/>.</summary>
    public string? PendingText => _isPending ? $"Waiting for {Label} to turn {(_pendingTarget ? "on" : "off")}…" : null;

    public bool CanToggle => !_isBusy && !_isPending;

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

    /// <summary>
    /// Second line of the row: where the device is (its room, and its home when there are several)
    /// and its shortcut. The toggle shows the state; problems (an error, offline) show here.
    /// </summary>
    public string Subtitle
    {
        get
        {
            if (HasError)
            {
                return _error!;
            }

            var offline = _isOnline ? null : "Offline";
            return string.Join("  ·  ", new[] { _owner.LocationOf(_config), offline, HotkeyText }.Where(s => !string.IsNullOrEmpty(s)));
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

        if (_isPending)
        {
            if (status.Online && status.IsOn != _pendingTarget)
            {
                return; // not caught up yet; VerifyAsync keeps asking
            }

            StopVerifying();
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

    /// <summary>
    /// Shows the result of a command sent without the row (by an automation): the state, or the
    /// wait for the device to confirm it.
    /// </summary>
    internal void ApplyCommandResult(bool turnOn, bool? reported)
    {
        if (_isBusy)
        {
            return; // the row's own command reports when it finishes
        }

        _lastCommandEnded = Stopwatch.GetTimestamp(); // older reads mustn't undo this
        IsOnline = true;
        Error = null;
        if (reported == turnOn)
        {
            StopVerifying();
            _lastUpdated = DateTimeOffset.UtcNow;
            State = turnOn;
        }
        else
        {
            StartVerifying(turnOn);
        }

        SyncToggle(turnOn);
    }

    /// <summary>Flips the device (unknown state counts as off). Used by the global shortcut.</summary>
    public Task<DeviceCommandResult?> ToggleAsync() => SetPowerAsync(_isPending ? !_pendingTarget : !(_state ?? false));

    public async Task<DeviceCommandResult?> SetPowerAsync(bool turnOn)
    {
        if (_isBusy)
        {
            return null;
        }

        StopVerifying(); // a new command replaces the wait for the last one
        IsBusy = true;
        Error = null;
        SyncToggle(turnOn);

        DeviceCommandResult? result = null;
        try
        {
            result = await _owner.Controller.SetPowerAsync(_config, turnOn, CancellationToken.None);
            if (result.Success)
            {
                IsOnline = true;
                if (result.Confirms(turnOn))
                {
                    State = turnOn;
                    _lastUpdated = DateTimeOffset.UtcNow;
                }
                else
                {
                    StartVerifying(turnOn); // accepted, but the device hasn't caught up yet
                }
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
            SyncToggle(_isPending ? _pendingTarget : _state ?? false);
        }

        return result;
    }

    /// <summary>Asks the device's service whether it is on.</summary>
    public async Task RefreshStateAsync(CancellationToken cancellationToken)
    {
        if (_isBusy || _isPending)
        {
            return; // a pending command is being checked already
        }

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

    /// <summary>Smoke test only: show a command waiting for the device, without checking it.</summary>
    internal void SimulatePending(bool turnOn)
    {
        _pendingTarget = turnOn;
        State = null;
        IsPending = true;
        SyncToggle(turnOn);
    }

    /// <summary>Waits for the device to report <paramref name="target"/> (the toggle stays greyed meanwhile).</summary>
    private void StartVerifying(bool target)
    {
        StopVerifying();
        _pendingTarget = target;
        State = null; // unknown until the device says
        IsPending = true;
        var verify = _verify = new CancellationTokenSource();
        _ = VerifyAsync(target, verify);
    }

    private void StopVerifying()
    {
        _verify?.Cancel();
        _verify = null;
        IsPending = false;
    }

    /// <summary>Asks the device for its state a few times until it reports the requested one.</summary>
    private async Task VerifyAsync(bool target, CancellationTokenSource verify)
    {
        bool? reported = null;
        foreach (var delay in VerifyDelays)
        {
            try
            {
                await Task.Delay(delay, verify.Token);
                var result = await _owner.Controller.QueryPowerAsync(_config, verify.Token);
                if (verify.IsCancellationRequested)
                {
                    return;
                }

                if (result.Success && result.IsOn is { } isOn)
                {
                    reported = isOn;
                    if (isOn == target)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (verify.IsCancellationRequested)
            {
                return; // a new command, or a batch read confirmed it
            }
            catch (Exception ex)
            {
                Log.Info($"Checking {Label} after a command: {ex.Message}");
            }
        }

        if (_verify != verify)
        {
            return;
        }

        // Confirmed, or it gave up: show what the device last said (still unknown: what was asked).
        _verify = null;
        IsPending = false;
        _lastCommandEnded = Stopwatch.GetTimestamp(); // reads sent meanwhile mustn't undo this
        _lastUpdated = DateTimeOffset.UtcNow;
        State = reported;
        SyncToggle(reported ?? target);
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
