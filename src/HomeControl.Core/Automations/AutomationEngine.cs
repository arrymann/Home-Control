namespace HomeControl.Core.Automations;

/// <summary>A finished (or stopped) run.</summary>
public sealed class AutomationRunEventArgs(Automation automation, AutomationNode trigger, AutomationRunResult result) : EventArgs
{
    public Automation Automation { get; } = automation;

    public AutomationNode Trigger { get; } = trigger;

    public AutomationRunResult Result { get; } = result;
}

/// <summary>
/// Decides when triggers fire and starts the runs. The app reports PC events and calls
/// <see cref="Tick"/> every few seconds; time-of-day and idle triggers are checked there.
/// Starting an automation again while it is still running (e.g. inside a wait) restarts it.
/// Not thread-safe: call it from one thread (the app's UI thread).
/// </summary>
public sealed class AutomationEngine
{
    /// <summary>Time triggers missed by more than this (PC asleep, app not running) are skipped.</summary>
    public static readonly TimeSpan MissedGrace = TimeSpan.FromMinutes(2);

    private readonly IAutomationHost _host;
    private readonly Func<AutomationDocument> _document;
    private readonly AutomationRunner _runner;
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    private DateTimeOffset? _lastTick;
    private TimeSpan _lastIdle;

    public AutomationEngine(IAutomationHost host, Func<AutomationDocument> document)
    {
        _host = host;
        _document = document;
        _runner = new AutomationRunner(host);
    }

    /// <summary>Raised when a run finished, after <see cref="Automation.LastRun"/> was updated.</summary>
    public event EventHandler<AutomationRunEventArgs>? RunCompleted;

    /// <summary>Automations that are running right now (e.g. waiting).</summary>
    public IReadOnlyCollection<string> RunningAutomations => _running.Keys;

    /// <summary>Starts the clock and fires the "Home Control started" triggers.</summary>
    public void Start()
    {
        _lastTick = _host.Now;
        _lastIdle = _host.GetPcStatus().Idle;
        HandlePcEvent(PcEvent.Started);
    }

    /// <summary>Fires the triggers for something that happened to the PC. Idle/Active come from <see cref="Tick"/>.</summary>
    public void HandlePcEvent(PcEvent pcEvent)
    {
        foreach (var (automation, trigger) in Triggers<PcEventTriggerNode>().Where(t => t.Node.Event == pcEvent).ToList())
        {
            StartRun(automation, trigger, RunOptions.Normal);
        }
    }

    /// <summary>Checks time-of-day and idle triggers. Call every few seconds.</summary>
    public void Tick()
    {
        var now = _host.Now;
        var last = _lastTick ?? now;
        _lastTick = now;

        if (now > last)
        {
            foreach (var (automation, trigger) in Triggers<TimeTriggerNode>().ToList())
            {
                var due = TimeSchedule
                    .OccurrencesBetween(trigger.At, trigger.Days, last, now, _host.Location, _host.TimeZone)
                    .Any(time => now - time <= MissedGrace);
                if (due)
                {
                    StartRun(automation, trigger, RunOptions.Normal);
                }
            }
        }

        var idle = _host.GetPcStatus().Idle;
        var previous = _lastIdle;
        _lastIdle = idle;
        foreach (var (automation, trigger) in Triggers<PcEventTriggerNode>().ToList())
        {
            var threshold = TimeSpan.FromMinutes(Math.Max(1, trigger.IdleMinutes));
            var fire = trigger.Event switch
            {
                PcEvent.Idle => previous < threshold && idle >= threshold,
                PcEvent.Active => previous >= threshold && idle < previous,
                _ => false,
            };
            if (fire)
            {
                StartRun(automation, trigger, RunOptions.Normal);
            }
        }
    }

    /// <summary>
    /// Runs the PC shutdown triggers and waits for them (delays skipped, no PC power actions).
    /// The caller bounds the wait: Windows only gives a few seconds.
    /// </summary>
    public Task RunShutdownTriggersAsync(bool signingOut, CancellationToken cancellationToken)
    {
        var runs = Triggers<ShutdownTriggerNode>()
            .Where(t => t.Node.Kind == ShutdownKind.Any || (t.Node.Kind == ShutdownKind.SignOut) == signingOut)
            .Select(t => StartRun(t.Automation, t.Node, RunOptions.Shutdown, cancellationToken))
            .ToList();
        return Task.WhenAll(runs);
    }

    /// <summary>Runs an automation from one of its triggers right away (the editor's "Run now").</summary>
    public Task<AutomationRunResult> RunNowAsync(Automation automation, AutomationNode trigger) =>
        StartRun(automation, trigger, RunOptions.Test);

    /// <summary>Stops every running automation (e.g. when the app exits).</summary>
    public void StopAll()
    {
        foreach (var cts in _running.Values.ToList())
        {
            TryCancel(cts);
        }
    }

    /// <summary>Next time a time trigger fires, or null (no location for a sun event, no days…).</summary>
    public DateTimeOffset? NextOccurrence(TimeTriggerNode trigger) =>
        TimeSchedule.NextOccurrence(trigger.At, trigger.Days, _host.Now, _host.Location, _host.TimeZone);

    private IEnumerable<(Automation Automation, T Node)> Triggers<T>() where T : AutomationNode =>
        _document().Automations
            .Where(a => a.Enabled)
            .SelectMany(a => a.Nodes.OfType<T>().Select(n => (a, n)));

    private Task<AutomationRunResult> StartRun(Automation automation, AutomationNode trigger, RunOptions options, CancellationToken outer = default)
    {
        if (_running.Remove(automation.Id, out var previous))
        {
            TryCancel(previous); // restart
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _running[automation.Id] = cts;
        return RunAsync(automation, trigger, options, cts);
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // That run just finished.
        }
    }

    private async Task<AutomationRunResult> RunAsync(Automation automation, AutomationNode trigger, RunOptions options, CancellationTokenSource cts)
    {
        AutomationRunResult result;
        try
        {
            result = await _runner.RunAsync(automation, trigger, options, cts.Token);
        }
        finally
        {
            if (_running.TryGetValue(automation.Id, out var current) && current == cts)
            {
                _running.Remove(automation.Id);
            }

            cts.Dispose();
        }

        // A run replaced by a newer one only reports what it did before it stopped.
        if (result.Steps.Count > 0 || !result.Cancelled)
        {
            automation.LastRun = _host.Now;
            automation.LastResult = result.Summary;
        }

        RunCompleted?.Invoke(this, new AutomationRunEventArgs(automation, trigger, result));
        return result;
    }
}
