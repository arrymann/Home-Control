using System.ComponentModel;
using HomeControl.Core.Models;

namespace HomeControl.ViewModels;

/// <summary>
/// A scene in the tray: one toggle for a group of devices. It is on while any of its devices is
/// on; turning it on or off sends that command to every device, through the device rows (so
/// each row shows its own progress and errors).
/// </summary>
public sealed class SceneViewModel : BindableBase
{
    private SceneConfig _config;
    private IReadOnlyList<DeviceViewModel> _members = [];
    private bool _isOn;
    private bool _isBusy;
    private bool _syncingToggle;
    private string? _error;

    internal SceneViewModel(SceneConfig config)
    {
        _config = config;
    }

    public string Id => _config.Id;

    public SceneConfig Config => _config;

    public string Label => string.IsNullOrWhiteSpace(_config.Name) ? "Scene" : _config.Name.Trim();

    public DeviceKind Kind => _config.Kind;

    /// <summary>
    /// On while any of its devices is on (unknown counts as off). A device Google lists as offline
    /// counts by its on/off value, as its row shows it (Google lists some TVs that are on as offline).
    /// </summary>
    public bool? State => SceneConfig.StateOf(_members.Select(m => m.EffectiveState));

    /// <summary>Bound two-way to the toggle switch: the user flipping it switches every device.</summary>
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

    public bool CanToggle => !_isBusy && _members.Count > 0;

    /// <summary>Second line: how many of its devices are on, a problem, and the shortcut.</summary>
    public string Subtitle
    {
        get
        {
            if (!string.IsNullOrEmpty(_error))
            {
                return _error;
            }

            var on = _members.Count(m => m.EffectiveState == true);
            var count = _members.Count switch
            {
                0 => "No devices",
                1 => on == 1 ? "1 device, on" : "1 device",
                var n => on > 0 ? $"{n} devices · {on} on" : $"{n} devices",
            };
            var hotkey = _config.Hotkey is { IsValid: true } h ? h.ToString() : null;
            return string.Join("  ·  ", new[] { count, hotkey }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }

    internal void Update(SceneConfig config)
    {
        _config = config;
        OnPropertyChanged(nameof(Config));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(Subtitle));
    }

    /// <summary>Points the scene at the device rows it switches (rows are reused across reloads).</summary>
    internal void SetMembers(IReadOnlyList<DeviceViewModel> members)
    {
        foreach (var member in _members)
        {
            member.PropertyChanged -= OnMemberChanged;
        }

        _members = members;
        foreach (var member in _members)
        {
            member.PropertyChanged += OnMemberChanged;
        }

        OnPropertyChanged(nameof(CanToggle));
        Refresh();
    }

    /// <summary>Flips the scene (unknown counts as off). Used by the global shortcut.</summary>
    public Task<string?> ToggleAsync() => SetPowerAsync(!(State ?? false));

    /// <summary>Switches every device; returns a description of what failed, or null.</summary>
    public async Task<string?> SetPowerAsync(bool turnOn)
    {
        if (_isBusy || _members.Count == 0)
        {
            Refresh();
            return null;
        }

        IsBusy = true;
        SetError(null);
        SyncToggle(turnOn);
        var failures = new List<string>();
        try
        {
            await HomeViewModel.RunLimitedAsync(_members, async member =>
            {
                var result = await member.SetPowerAsync(turnOn);
                if (result is { Success: false })
                {
                    failures.Add($"{member.Label}: {result.Message}");
                }
            });
        }
        finally
        {
            IsBusy = false;
            SetError(failures.Count switch
            {
                0 => null,
                1 => failures[0],
                var n => $"{n} devices didn't switch",
            });
            Refresh();
        }

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    private void OnMemberChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isBusy && e.PropertyName is nameof(DeviceViewModel.State) or nameof(DeviceViewModel.IsPending) or nameof(DeviceViewModel.IsOnline))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Subtitle));
        SyncToggle(State ?? false);
    }

    private void SetError(string? error)
    {
        if (_error != error)
        {
            _error = error;
            OnPropertyChanged(nameof(Subtitle));
        }
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
}
