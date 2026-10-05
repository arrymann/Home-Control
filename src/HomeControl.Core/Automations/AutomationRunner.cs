using HomeControl.Core.Devices;
using HomeControl.Core.GoogleHome;

namespace HomeControl.Core.Automations;

/// <summary>What the PC is doing right now (for PC state conditions).</summary>
public sealed record PcStatus(bool Locked, TimeSpan Idle, bool? OnBattery, bool? DisplayOn);

/// <summary>Everything an automation run needs from the app. Implemented by the app; faked in tests.</summary>
public interface IAutomationHost
{
    DateTimeOffset Now { get; }

    TimeZoneInfo TimeZone { get; }

    GeoLocation? Location { get; }

    PcStatus GetPcStatus();

    /// <summary>The device's label, or null when there is no such device.</summary>
    string? GetDeviceName(string? deviceId);

    /// <summary>Whether a device is on (reading it if needed); null when unknown.</summary>
    Task<bool?> GetDeviceStateAsync(string deviceId, CancellationToken cancellationToken);

    Task<DeviceCommandResult> SetDeviceAsync(string deviceId, DeviceCommand command, CancellationToken cancellationToken);

    Task TurnAllOffAsync(CancellationToken cancellationToken);

    void Notify(string message);

    Task RunPcPowerAsync(PcPowerCommand command, CancellationToken cancellationToken);

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>How a run behaves.</summary>
public sealed record RunOptions
{
    public static readonly RunOptions Normal = new();

    /// <summary>For the PC shutdown trigger: Windows only waits a few seconds.</summary>
    public static readonly RunOptions Shutdown = new() { SkipDelays = true, AllowPcPower = false, RetryDevices = false };

    /// <summary>"Run now" from the editor: everything for real, but waits are skipped.</summary>
    public static readonly RunOptions Test = new() { SkipDelays = true };

    public bool SkipDelays { get; init; }

    public bool AllowPcPower { get; init; } = true;

    /// <summary>Retry a device command that failed for lack of a connection (e.g. right after waking up).</summary>
    public bool RetryDevices { get; init; } = true;
}

/// <summary>One node that ran.</summary>
public sealed record AutomationStep(string NodeId, NodeCategory Category, string Text, bool Success);

/// <summary>What a run did.</summary>
public sealed class AutomationRunResult
{
    private readonly List<AutomationStep> _steps = [];

    public IReadOnlyList<AutomationStep> Steps
    {
        get
        {
            lock (_steps)
            {
                return _steps.ToList();
            }
        }
    }

    public bool Cancelled { get; internal set; }

    public bool Failed => Steps.Any(s => !s.Success);

    /// <summary>"Turned on Backlight; Couldn't turn off Desk: The device is offline."</summary>
    public string Summary
    {
        get
        {
            var actions = Steps.Where(s => s.Category == NodeCategory.Action).Select(s => s.Text).ToList();
            var text = actions.Count > 0
                ? string.Join("; ", actions)
                : Steps.LastOrDefault(s => s.Category == NodeCategory.Condition)?.Text is { } condition
                    ? $"Nothing to do ({condition})"
                    : "Nothing to do";
            return Cancelled ? $"{text} (stopped)" : text;
        }
    }

    internal void Add(AutomationStep step)
    {
        lock (_steps)
        {
            _steps.Add(step);
        }
    }
}

/// <summary>
/// Runs an automation from one of its triggers: follows the wires through conditions (taking
/// their "yes" or "no" output) and actions. Branches run side by side; a failed action is
/// recorded and the run carries on.
/// </summary>
public sealed class AutomationRunner
{
    private const int MaxSteps = 500;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    private readonly IAutomationHost _host;

    public AutomationRunner(IAutomationHost host)
    {
        _host = host;
    }

    public async Task<AutomationRunResult> RunAsync(Automation automation, AutomationNode trigger, RunOptions options, CancellationToken cancellationToken)
    {
        var result = new AutomationRunResult();
        var steps = 0;
        try
        {
            await FollowAsync(automation, trigger.Id, Ports.Then, options, result, () => ++steps, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }

        return result;
    }

    private Task FollowAsync(
        Automation automation, string nodeId, string port, RunOptions options, AutomationRunResult result, Func<int> countStep, CancellationToken cancellationToken)
    {
        var targets = AutomationGraph.Targets(automation, nodeId, port);
        return targets.Count switch
        {
            0 => Task.CompletedTask,
            1 => ExecuteAsync(automation, targets[0], options, result, countStep, cancellationToken),
            _ => Task.WhenAll(targets.Select(t => ExecuteAsync(automation, t, options, result, countStep, cancellationToken))),
        };
    }

    private async Task ExecuteAsync(
        Automation automation, AutomationNode node, RunOptions options, AutomationRunResult result, Func<int> countStep, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (countStep() > MaxSteps)
        {
            result.Add(new(node.Id, node.Category, "Stopped: too many steps", false));
            return;
        }

        string next;
        if (node.Category == NodeCategory.Condition)
        {
            var (met, text) = await EvaluateAsync(node, cancellationToken);
            result.Add(new(node.Id, node.Category, text, true));
            next = met ? Ports.Yes : Ports.No;
        }
        else
        {
            result.Add(await RunActionAsync(node, options, cancellationToken));
            next = Ports.Then;
        }

        await FollowAsync(automation, node.Id, next, options, result, countStep, cancellationToken);
    }

    private async Task<(bool Met, string Text)> EvaluateAsync(AutomationNode node, CancellationToken cancellationToken)
    {
        var description = NodeText.Describe(node, _host.GetDeviceName);
        bool met;
        switch (node)
        {
            case TimeWindowConditionNode window:
                met = TimeSchedule.IsWithin(_host.Now, window.From, window.To, _host.Location, _host.TimeZone);
                break;
            case DaysConditionNode days:
                met = TimeSchedule.Includes(days.Days, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_host.Now, _host.TimeZone).DateTime));
                break;
            case DeviceStateConditionNode device when device.DeviceId is { } id:
                met = await _host.GetDeviceStateAsync(id, cancellationToken) == device.IsOn;
                break;
            case PcStateConditionNode pc:
                met = IsPcState(_host.GetPcStatus(), pc.State, pc.IdleMinutes);
                break;
            default:
                met = false;
                break;
        }

        return (met, $"{description}: {(met ? "yes" : "no")}");
    }

    internal static bool IsPcState(PcStatus status, PcState state, int idleMinutes) => state switch
    {
        PcState.Locked => status.Locked,
        PcState.Unlocked => !status.Locked,
        PcState.Idle => status.Idle >= TimeSpan.FromMinutes(idleMinutes),
        PcState.InUse => status.Idle < TimeSpan.FromMinutes(idleMinutes),
        PcState.OnBattery => status.OnBattery == true,
        PcState.PluggedIn => status.OnBattery == false,
        PcState.DisplayOn => status.DisplayOn != false,
        PcState.DisplayOff => status.DisplayOn == false,
        _ => false,
    };

    private async Task<AutomationStep> RunActionAsync(AutomationNode node, RunOptions options, CancellationToken cancellationToken)
    {
        var description = NodeText.Describe(node, _host.GetDeviceName);
        try
        {
            switch (node)
            {
                case DeviceActionNode { DeviceId: { } deviceId } device:
                    return await RunDeviceActionAsync(node, deviceId, device.Command, description, options, cancellationToken);

                case DeviceActionNode:
                    return new(node.Id, node.Category, $"{description}: no device chosen", false);

                case AllOffActionNode:
                    await _host.TurnAllOffAsync(cancellationToken);
                    return new(node.Id, node.Category, "Turned every device off", true);

                case DelayActionNode when options.SkipDelays:
                    return new(node.Id, node.Category, $"{description} (skipped)", true);

                case DelayActionNode delay:
                    await _host.DelayAsync(TimeSpan.FromSeconds(Math.Max(0, delay.Seconds)), cancellationToken);
                    return new(node.Id, node.Category, $"Waited {NodeText.Duration(TimeSpan.FromSeconds(delay.Seconds))}", true);

                case NotifyActionNode notify:
                    _host.Notify(string.IsNullOrWhiteSpace(notify.Message) ? "Automation ran" : notify.Message.Trim());
                    return new(node.Id, node.Category, "Showed a notification", true);

                case PcPowerActionNode when !options.AllowPcPower:
                    return new(node.Id, node.Category, $"{description}: skipped while the PC shuts down", true);

                case PcPowerActionNode power:
                    await _host.RunPcPowerAsync(power.Command, cancellationToken);
                    return new(node.Id, node.Category, description, true);

                default:
                    return new(node.Id, node.Category, $"{description}: not supported", false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(node.Id, node.Category, $"{description} failed: {ex.Message}", false);
        }
    }

    private async Task<AutomationStep> RunDeviceActionAsync(
        AutomationNode node, string deviceId, DeviceCommand command, string description, RunOptions options, CancellationToken cancellationToken)
    {
        if (_host.GetDeviceName(deviceId) is null)
        {
            return new(node.Id, node.Category, $"{description}: the device was removed", false);
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = await _host.SetDeviceAsync(deviceId, command, cancellationToken);
                return result.Success
                    ? new(node.Id, node.Category, Done(command, _host.GetDeviceName(deviceId)!, result.IsOn), true)
                    : new(node.Id, node.Category, $"{description}: {result.Message}", false);
            }
            catch (Exception ex) when (options.RetryDevices && attempt < RetryDelays.Length && IsTransient(ex))
            {
                // Typically right after waking up, before the network is back.
                await _host.DelayAsync(RetryDelays[attempt], cancellationToken);
            }
        }
    }

    private static string Done(DeviceCommand command, string name, bool? isOn) => command switch
    {
        DeviceCommand.Toggle => $"Toggled {name}{(isOn is { } on ? $" ({(on ? "on" : "off")})" : string.Empty)}",
        DeviceCommand.TurnOff => $"Turned off {name}",
        _ => $"Turned on {name}",
    };

    private static bool IsTransient(Exception ex) => ex switch
    {
        GoogleHomeSignInRequiredException => false,
        GoogleHomeException or HttpRequestException or TimeoutException => true,
        _ => false,
    };
}
