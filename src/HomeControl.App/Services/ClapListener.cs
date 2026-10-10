using System.Runtime.InteropServices;
using HomeControl.Core.Automations;
using HomeControl.Core.Settings;
using Microsoft.UI.Dispatching;

namespace HomeControl.Services;

/// <summary>Why the microphone is open or closed, for the settings page, the node and the tray.</summary>
internal enum ClapListenerState
{
    /// <summary>Clap listening is turned off.</summary>
    Off,

    /// <summary>On, but no enabled automation has a (wired) Claps trigger: the microphone stays closed.</summary>
    NotNeeded,

    /// <summary>Asleep, locked (when asked) or in a Remote Desktop session.</summary>
    Paused,
    Starting,
    Listening,
    Blocked,
    NoMicrophone,
    Retrying,
}

/// <summary>
/// Listens for claps while it is wanted: clap listening is on, an enabled automation has a Claps
/// trigger, and the PC isn't asleep, locked (if the user asked) or used over Remote Desktop.
/// Heard patterns go to the automations. Only the number of claps leaves the microphone code.
/// All members are used on the UI thread.
/// </summary>
internal sealed class ClapListener : IDisposable
{
    private const int SmRemoteSession = 0x1000;
    private const string RemoteDesktopPause = "Paused during Remote Desktop, where the microphone would be the remote one.";

    private readonly DispatcherQueue _dispatcher;
    private readonly Func<ClapSettings> _settings;
    private readonly AutomationService _automations;
    private readonly bool _simulated;
    private MicrophoneCapture? _capture;
    private bool _running;
    private string? _deviceId;
    private bool _sleeping;
    private bool _disposed;
    private string? _detail;
    private DateTimeOffset? _lastClap;

    /// <param name="simulated">Smoke test: never opens the microphone, only shows what it would do.</param>
    public ClapListener(DispatcherQueue dispatcher, Func<ClapSettings> settings, AutomationService automations, bool simulated)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _automations = automations;
        _simulated = simulated;
        _automations.Changed += (_, _) => Update();
        _automations.PcEventOccurred += OnPcEvent;
        _automations.SessionMoved += OnSessionMoved;
    }

    public ClapListenerState State { get; private set; } = ClapListenerState.Off;

    /// <summary>True while the microphone is open.</summary>
    public bool IsListening => State == ClapListenerState.Listening;

    /// <summary>The last pattern heard (claps, when).</summary>
    public (int Count, DateTimeOffset Time)? LastPattern { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="State"/>, <see cref="StatusText"/> or what was heard changed.</summary>
    public event EventHandler? Changed;

    /// <summary>One line about what it is doing, e.g. "Listening with Microphone Array · heard 2 claps at 21:04".</summary>
    public string StatusText => State switch
    {
        ClapListenerState.Off => "Off: the microphone isn't used.",
        ClapListenerState.NotNeeded => "On, but no automation uses a Claps trigger, so the microphone stays closed.",
        ClapListenerState.Paused => _detail ?? "Paused.",
        ClapListenerState.Starting => "Opening the microphone…",
        ClapListenerState.Listening => $"Listening with {_detail ?? "the microphone"}{HeardText()}.",
        ClapListenerState.Blocked => "Windows doesn't let desktop apps use the microphone. Turn it on in Windows' microphone privacy settings.",
        ClapListenerState.NoMicrophone => $"{_detail ?? "No microphone is connected"}. Listening starts when one is.",
        ClapListenerState.Retrying => $"{_detail ?? "The microphone isn't available"}. Trying again…",
        _ => State.ToString(),
    };

    /// <summary>Opens or closes the microphone to match the settings, the automations and the PC. Cheap; call it freely.</summary>
    public void Update()
    {
        if (_disposed)
        {
            return;
        }

        var settings = _settings();
        var counts = _automations.Engine.ClapCounts();
        if (WhyClosed(settings, counts) is { } closed)
        {
            if (_running)
            {
                _running = false;
                _capture?.Stop();
            }

            SetState(closed.State, closed.Detail);
            return;
        }

        if (_simulated)
        {
            _running = true;
            SetState(ClapListenerState.Listening, "Microphone (simulated)");
            return;
        }

        _capture ??= CreateCapture();
        if (!_running || _deviceId != settings.MicrophoneId)
        {
            _running = true;
            _deviceId = settings.MicrophoneId;
            _capture.Start(_deviceId, settings.Sensitivity, counts);
            if (State is not (ClapListenerState.Listening or ClapListenerState.Starting))
            {
                SetState(ClapListenerState.Starting, null);
            }
        }
        else
        {
            _capture.Update(settings.Sensitivity, counts);
        }
    }

    /// <summary>Why the microphone should be closed now, or null when it should be open.</summary>
    private (ClapListenerState State, string? Detail)? WhyClosed(ClapSettings settings, IReadOnlyList<int> counts)
    {
        if (!settings.Enabled)
        {
            return (ClapListenerState.Off, null);
        }

        if (counts.Count == 0)
        {
            return (ClapListenerState.NotNeeded, null);
        }

        if (_sleeping)
        {
            return (ClapListenerState.Paused, "Paused while the PC sleeps.");
        }

        if (settings.PauseWhileLocked && _automations.PcStatus.Locked)
        {
            return (ClapListenerState.Paused, "Paused while the PC is locked.");
        }

        if (IsRemoteSession())
        {
            return (ClapListenerState.Paused, RemoteDesktopPause);
        }

        return null;
    }

    /// <summary>Tries the microphone again now (e.g. after changing Windows' privacy settings).</summary>
    public void Retry()
    {
        if (_running)
        {
            _capture?.Retry();
        }
        else
        {
            Update();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _automations.PcEventOccurred -= OnPcEvent;
        _automations.SessionMoved -= OnSessionMoved;
        _capture?.Dispose();
        _capture = null;
    }

    private MicrophoneCapture CreateCapture()
    {
        // Only the clap count crosses to the UI thread.
        var capture = new MicrophoneCapture(
            claps => _dispatcher.TryEnqueue(() => OnPattern(claps)),
            () => _dispatcher.TryEnqueue(OnClapHeard));
        capture.StateChanged += (state, detail) => _dispatcher.TryEnqueue(() => OnCaptureState(state, detail));
        return capture;
    }

    private void OnCaptureState(MicrophoneState state, string? detail)
    {
        if (_disposed || !_running || state == MicrophoneState.Off)
        {
            return; // closed on purpose: Update set the reason
        }

        SetState(state switch
        {
            MicrophoneState.Starting => ClapListenerState.Starting,
            MicrophoneState.Listening => ClapListenerState.Listening,
            MicrophoneState.Blocked => ClapListenerState.Blocked,
            MicrophoneState.NoMicrophone => ClapListenerState.NoMicrophone,
            MicrophoneState.RemoteSession => ClapListenerState.Paused,
            _ => ClapListenerState.Retrying,
        }, state == MicrophoneState.RemoteSession ? RemoteDesktopPause : detail);
    }

    private void OnPattern(int claps)
    {
        if (_disposed || !_running)
        {
            return;
        }

        LastPattern = (claps, DateTimeOffset.Now);
        Log.Info($"Heard {claps} claps.");
        _automations.HandleClaps(claps);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnClapHeard()
    {
        if (_disposed || !_running)
        {
            return;
        }

        _lastClap = DateTimeOffset.Now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPcEvent(PcEvent pcEvent)
    {
        switch (pcEvent)
        {
            case PcEvent.Sleeping:
                _sleeping = true;
                Update();
                break;
            case PcEvent.Resumed:
            {
                var stillOpen = _running; // sleep came without notice: the stream rarely survives it
                _sleeping = false;
                Update();
                if (stillOpen)
                {
                    Retry();
                }

                break;
            }
            case PcEvent.Locked:
            case PcEvent.Unlocked:
                Update();
                break;
        }
    }

    /// <summary>Remote Desktop connected or disconnected: close the microphone, or open it again.</summary>
    private void OnSessionMoved()
    {
        var wasOpen = _running;
        Update();
        if (wasOpen && _running)
        {
            _capture?.Retry(); // it may have held off while the session was remote
        }
    }

    private void SetState(ClapListenerState state, string? detail)
    {
        if (State == state && _detail == detail)
        {
            return;
        }

        var wasListening = IsListening;
        State = state;
        _detail = detail;
        if (wasListening != IsListening)
        {
            _lastClap = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string HeardText()
    {
        if (LastPattern is { } pattern && DateTimeOffset.Now - pattern.Time < TimeSpan.FromHours(12))
        {
            return $" · heard {pattern.Count} claps at {pattern.Time:t}";
        }

        return _lastClap is { } clap && DateTimeOffset.Now - clap < TimeSpan.FromMinutes(1) ? $" · heard a clap at {clap:T}" : string.Empty;
    }

    private static bool IsRemoteSession() => GetSystemMetrics(SmRemoteSession) != 0;

    // ------------------------------------------------------------------ interop

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
