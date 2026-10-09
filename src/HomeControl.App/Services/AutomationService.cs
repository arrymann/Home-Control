using System.Diagnostics;
using HomeControl.Core.Automations;
using HomeControl.Core.Devices;
using HomeControl.Interop;
using Microsoft.UI.Dispatching;

namespace HomeControl.Services;

/// <summary>
/// Runs the automations: loads automations.json, feeds PC events and a 5-second clock tick to
/// the engine, holds Windows at shutdown while the shutdown triggers run, and saves the
/// document after edits and runs.
/// </summary>
internal sealed class AutomationService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    private const int MaxParallelRequests = 4;

    private readonly AppHost _app;
    private readonly bool _readOnly;
    private readonly AutomationStore _store = new(AppPaths.AutomationsFile);
    private readonly PcMonitor _pc;
    private readonly DispatcherQueueTimer _tickTimer;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly HashSet<string> _testRuns = [];
    private bool _started;
    private long _zoneRefreshed = Environment.TickCount64;

    /// <param name="readOnly">Smoke test: nothing runs on its own and nothing is saved.</param>
    public AutomationService(AppHost app, MessageWindow window, HttpClient http, DispatcherQueue dispatcher, bool readOnly)
    {
        _app = app;
        _readOnly = readOnly;
        Document = _store.Load();
        if (_store.LoadError is not null)
        {
            Log.Info($"automations.json could not be read and was set aside: {_store.LoadError}");
        }

        Geocoding = new GeocodingClient(http);
        _pc = new PcMonitor(window);
        Engine = new AutomationEngine(new Host(this), () => Document);
        Engine.RunCompleted += OnRunCompleted;

        _pc.PcEventOccurred = OnPcEvent;
        _pc.HasSessionEndingWork = signingOut => _started && Document.Automations.Any(a =>
            a.Enabled && a.Nodes.OfType<ShutdownTriggerNode>().Any(t => t.Kind == ShutdownKind.Any || (t.Kind == ShutdownKind.SignOut) == signingOut));
        _pc.SessionEnding = signingOut => Engine.RunShutdownTriggersAsync(signingOut, CancellationToken.None);

        _tickTimer = dispatcher.CreateTimer();
        _tickTimer.Interval = TickInterval;
        _tickTimer.IsRepeating = true;
        _tickTimer.Tick += (_, _) => OnTick();

        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromSeconds(1);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveNow();
    }

    public AutomationDocument Document { get; }

    public AutomationEngine Engine { get; }

    public GeocodingClient Geocoding { get; }

    public PcStatus PcStatus => _pc.Status;

    /// <summary>Raised on the UI thread when automations changed (edited, or a run finished).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread for every PC event, even before automations start (e.g. for the clap listener).</summary>
    public event Action<PcEvent>? PcEventOccurred;

    /// <summary>Starts the clock and fires the "Home Control started" triggers.</summary>
    public void Start()
    {
        if (_readOnly || _started)
        {
            return;
        }

        _started = true;
        Engine.Start();
        _tickTimer.Start();
    }

    /// <summary>Fires the clap triggers for a pattern the clap listener heard.</summary>
    public void HandleClaps(int claps)
    {
        if (_started)
        {
            _ = Engine.HandleClaps(claps);
        }
    }

    private Task OnPcEvent(PcEvent pcEvent)
    {
        try
        {
            PcEventOccurred?.Invoke(pcEvent);
        }
        catch (Exception ex)
        {
            Log.Error($"Handling PC event {pcEvent}", ex);
        }

        if (!_started)
        {
            return Task.CompletedTask;
        }

        if (pcEvent != PcEvent.Sleeping)
        {
            return Engine.HandlePcEvent(pcEvent);
        }

        // Windows waits only briefly: what isn't done by then is dropped, not done after waking.
        var cts = new CancellationTokenSource(PcMonitor.SleepMilliseconds);
        var work = Engine.HandlePcEvent(pcEvent, cts.Token);
        _ = work.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
        return work;
    }

    private void OnTick()
    {
        // In case a time zone change wasn't announced (WM_TIMECHANGE also clears it).
        if (Environment.TickCount64 - _zoneRefreshed > 60_000)
        {
            _zoneRefreshed = Environment.TickCount64;
            TimeZoneInfo.ClearCachedData();
        }

        Engine.Tick();
    }

    /// <summary>Saves soon (edits come in bursts, e.g. while dragging a node).</summary>
    public void Save()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        if (_readOnly)
        {
            return;
        }

        try
        {
            _store.Save(Document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving automations", ex);
        }
    }

    /// <summary>Runs an automation from one of its triggers now (waits are skipped).</summary>
    public async Task<AutomationRunResult> RunNowAsync(Automation automation, AutomationNode trigger)
    {
        _testRuns.Add(automation.Id);
        try
        {
            return await Engine.RunNowAsync(automation, trigger);
        }
        finally
        {
            _testRuns.Remove(automation.Id);
        }
    }

    /// <summary>Today's sun times at the home's location, or null without a location.</summary>
    public (DateTimeOffset? Dawn, DateTimeOffset? Sunrise, DateTimeOffset? Sunset, DateTimeOffset? Dusk)? TodaysSun()
    {
        if (Document.Location is not { IsValid: true } location)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var zone = TimeZoneInfo.Local;
        return (SolarCalculator.GetEvent(today, location, SunEvent.CivilDawn, zone),
                SolarCalculator.GetEvent(today, location, SunEvent.Sunrise, zone),
                SolarCalculator.GetEvent(today, location, SunEvent.Sunset, zone),
                SolarCalculator.GetEvent(today, location, SunEvent.CivilDusk, zone));
    }

    private void OnRunCompleted(object? sender, AutomationRunEventArgs e)
    {
        Log.Info($"Automation “{e.Automation.Name}” ({NodeText.Title(e.Trigger)}): {e.Result.Summary}");
        var test = _testRuns.Contains(e.Automation.Id);
        if (e.Result.Failed && !test && !e.Result.Cancelled)
        {
            _app.ShowNotification(e.Automation.Name, e.Result.Summary);
        }

        SaveNow(); // runs are rare, and the PC may be shutting down
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _tickTimer.Stop();
        Engine.StopAll();
        if (_saveTimer.IsRunning)
        {
            SaveNow();
        }

        _pc.Dispose();
    }

    /// <summary>What automations can do in the app: devices through the tray's view models and controller.</summary>
    private sealed class Host(AutomationService service) : IAutomationHost
    {
        private static readonly TimeSpan FreshEnough = TimeSpan.FromSeconds(30);

        private AppHost App => service._app;

        public DateTimeOffset Now => DateTimeOffset.Now;

        public TimeZoneInfo TimeZone => TimeZoneInfo.Local;

        public GeoLocation? Location => service.Document.Location;

        public PcStatus GetPcStatus() => service._pc.Status;

        public string? GetDeviceName(string? deviceId) =>
            deviceId is null ? null : App.Settings.Devices.FirstOrDefault(d => d.Id == deviceId && !d.Missing)?.Label.Trim();

        public async Task<bool?> GetDeviceStateAsync(string deviceId, CancellationToken cancellationToken)
        {
            if (App.Settings.Devices.FirstOrDefault(d => d.Id == deviceId && !d.Missing) is not { } config)
            {
                return null;
            }

            var device = App.Home.Find(deviceId);
            if (device is { State: { } known } && !device.IsStale(FreshEnough))
            {
                return known;
            }

            var started = Stopwatch.GetTimestamp();
            if (App.Controller is IBatchStateReader reader && reader.CanReadInBatch(config))
            {
                var states = await reader.ReadStatesAsync([config], cancellationToken);
                if (states.TryGetValue(deviceId, out var status))
                {
                    device?.ApplyStatus(status, started);
                    return status.IsOn;
                }

                return null;
            }

            var result = await App.Controller.QueryPowerAsync(config, cancellationToken);
            if (result.Success)
            {
                device?.ApplyStatus(new DeviceStatus(result.IsOn, true), started);
                return result.IsOn;
            }

            return null;
        }

        public async Task<DeviceCommandResult> SetDeviceAsync(string deviceId, DeviceCommand command, CancellationToken cancellationToken)
        {
            if (App.Settings.Devices.FirstOrDefault(d => d.Id == deviceId && !d.Missing) is not { } config)
            {
                return new DeviceCommandResult(false, null, "The device was removed.");
            }

            var turnOn = command switch
            {
                DeviceCommand.TurnOn => true,
                DeviceCommand.TurnOff => false,
                _ => !(await GetDeviceStateAsync(deviceId, cancellationToken) ?? false),
            };

            // Through the controller rather than the row, so connection errors reach the runner (which retries).
            var result = await App.Controller.SetPowerAsync(config, turnOn, cancellationToken);
            if (result.Success)
            {
                App.Home.Find(deviceId)?.ApplyCommandResult(turnOn, result.IsOn);
            }

            return result;
        }

        /// <summary>Turns off every device in the tray, whatever its last known state (which may be old).</summary>
        public async Task TurnAllOffAsync(CancellationToken cancellationToken)
        {
            var failures = new List<string>();
            using var gate = new SemaphoreSlim(MaxParallelRequests);
            await Task.WhenAll(App.Home.Devices.ToList().Select(async device =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var result = await App.Controller.SetPowerAsync(device.Config, false, cancellationToken);
                    if (result.Success)
                    {
                        device.ApplyCommandResult(false, result.IsOn);
                    }
                    else
                    {
                        failures.Add($"{device.Label}: {result.Message}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failures.Add($"{device.Label}: {ex.Message}");
                }
                finally
                {
                    gate.Release();
                }
            }));

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(string.Join("; ", failures));
            }
        }

        public void Notify(string message) => App.ShowNotification("Home Control", message);

        public Task RunPcPowerAsync(PcPowerCommand command, CancellationToken cancellationToken)
        {
            PcPower.Run(command);
            if (command is PcPowerCommand.ShutDown or PcPowerCommand.Restart)
            {
                App.ShowNotification("Home Control", $"An automation will {(command == PcPowerCommand.Restart ? "restart" : "shut down")} the PC in one minute. To stop it, choose “Cancel shutdown” in the tray menu.");
            }

            return Task.CompletedTask;
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }
}
