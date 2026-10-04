using HomeControl.Core.Assistant;
using HomeControl.Core.Auth;
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
    private bool _isBusy;
    private bool _syncingToggle;
    private string? _busyText;
    private string? _error;

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

    /// <summary>Last known state; null until the Assistant has told us or we changed it.</summary>
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

    /// <summary>Second line of the row: progress, error or state, plus the shortcut.</summary>
    public string Subtitle
    {
        get
        {
            var status = _isBusy ? _busyText
                : HasError ? _error
                : _state switch { true => "On", false => "Off", null => null };

            return string.Join("  ·  ", new[] { status, HotkeyText }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }

    internal void Update(DeviceConfig config)
    {
        _config = config;
        OnPropertyChanged(nameof(Config));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(Subtitle));
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
            }
            else
            {
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
            IsBusy = false;
            SyncToggle(_state ?? false);
        }

        return result;
    }

    /// <summary>Asks the Assistant whether the device is on.</summary>
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
    internal void SimulateState(bool isOn)
    {
        State = isOn;
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
