using HomeControl.Core.Automations;
using HomeControl.Core.Devices;
using HomeControl.Core.GoogleHome;
using DeviceConfig = HomeControl.Core.Models.DeviceConfig;

namespace HomeControl.Core.Tests;

/// <summary>A scriptable host: a fake clock, PC state and devices.</summary>
internal sealed class FakeAutomationHost : IAutomationHost
{
    public DateTimeOffset Now { get; set; } = new(2025, 6, 2, 12, 0, 0, TimeSpan.FromHours(1)); // a Monday

    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public GeoLocation? Location { get; set; } = new(51.5074, -0.1278, "London");

    public PcStatus Status { get; set; } = new(false, TimeSpan.Zero, false, true);

    public Dictionary<string, (string Name, bool? On)> Devices { get; } = new()
    {
        ["lamp"] = ("Lamp", false),
        ["desk"] = ("Desk", true),
    };

    /// <summary>Exceptions to throw from the next SetDeviceAsync calls, in order.</summary>
    public Queue<Exception> Failures { get; } = new();

    public Func<string, DeviceCommand, DeviceCommandResult>? Respond { get; set; }

    public List<string> Commands { get; } = [];

    public List<string> Notifications { get; } = [];

    public List<PcPowerCommand> PowerCommands { get; } = [];

    public List<TimeSpan> Delays { get; } = [];

    /// <summary>When set, DelayAsync waits for it (so a test can cancel a run inside a wait).</summary>
    public TaskCompletionSource? DelayGate { get; set; }

    public int AllOffCount { get; private set; }

    public PcStatus GetPcStatus() => Status;

    public string? GetDeviceName(string? deviceId) => deviceId is not null && Devices.TryGetValue(deviceId, out var d) ? d.Name : null;

    public Task<bool?> GetDeviceStateAsync(string deviceId, CancellationToken cancellationToken) =>
        Task.FromResult(Devices.TryGetValue(deviceId, out var d) ? d.On : null);

    public Task<DeviceCommandResult> SetDeviceAsync(string deviceId, DeviceCommand command, CancellationToken cancellationToken)
    {
        lock (Commands)
        {
            Commands.Add($"{command} {deviceId}");
        }

        if (Failures.TryDequeue(out var failure))
        {
            throw failure;
        }

        if (Respond is not null)
        {
            return Task.FromResult(Respond(deviceId, command));
        }

        var (name, on) = Devices[deviceId];
        var newState = command switch { DeviceCommand.TurnOn => true, DeviceCommand.TurnOff => false, _ => !(on ?? false) };
        Devices[deviceId] = (name, newState);
        return Task.FromResult(new DeviceCommandResult(true, newState, "ok"));
    }

    public Task TurnAllOffAsync(CancellationToken cancellationToken)
    {
        AllOffCount++;
        return Task.CompletedTask;
    }

    public void Notify(string message) => Notifications.Add(message);

    public Task RunPcPowerAsync(PcPowerCommand command, CancellationToken cancellationToken)
    {
        PowerCommands.Add(command);
        return Task.CompletedTask;
    }

    public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        lock (Delays)
        {
            Delays.Add(delay);
        }

        if (DelayGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }
    }
}

internal static class Graph
{
    public static T Add<T>(this Automation automation, T node, double y = 0) where T : AutomationNode
    {
        node.Y = y;
        automation.Nodes.Add(node);
        return node;
    }

    public static void Wire(this Automation automation, AutomationNode from, AutomationNode to, string port = Ports.Then) =>
        Assert.True(AutomationGraph.Connect(automation, from.Id, port, to.Id), $"could not connect {from} -> {to}");
}

public class TimeScheduleTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly GeoLocation NewYorkCity = new(40.7128, -74.0060);

    [Fact]
    public void Clock_time_in_a_daylight_saving_gap_moves_forward()
    {
        // 2025-03-09 02:30 doesn't exist in New York (clocks jump from 02:00 to 03:00).
        var time = TimeSchedule.Resolve(new TimePoint { Time = new TimeOnly(2, 30) }, new DateOnly(2025, 3, 9), null, NewYork);

        Assert.Equal(new DateTimeOffset(2025, 3, 9, 3, 0, 0, TimeSpan.FromHours(-4)), time);
    }

    [Fact]
    public void Repeated_clock_time_takes_the_first_one()
    {
        // 2025-11-02 01:30 happens twice in New York; the first is daylight time.
        var time = TimeSchedule.Resolve(new TimePoint { Time = new TimeOnly(1, 30) }, new DateOnly(2025, 11, 2), null, NewYork);

        Assert.Equal(TimeSpan.FromHours(-4), time!.Value.Offset);
    }

    [Fact]
    public void Sun_events_need_a_location_and_apply_the_offset()
    {
        var point = new TimePoint { Reference = TimeReference.Sunset, OffsetMinutes = -30 };
        var date = new DateOnly(2024, 12, 21);

        Assert.Null(TimeSchedule.Resolve(point, date, null, NewYork));
        var time = TimeSchedule.Resolve(point, date, NewYorkCity, NewYork)!.Value;
        Assert.InRange(time, new DateTimeOffset(2024, 12, 21, 15, 59, 0, TimeSpan.FromHours(-5)), new DateTimeOffset(2024, 12, 21, 16, 3, 0, TimeSpan.FromHours(-5)));
    }

    [Fact]
    public void Next_occurrence_respects_the_days()
    {
        var after = new DateTimeOffset(2025, 6, 6, 20, 0, 0, TimeSpan.FromHours(-4)); // Friday 20:00
        var point = new TimePoint { Time = new TimeOnly(7, 0) };

        Assert.Equal(new DateTimeOffset(2025, 6, 9, 7, 0, 0, TimeSpan.FromHours(-4)), TimeSchedule.NextOccurrence(point, Weekdays.WorkDays, after, null, NewYork));
        Assert.Equal(new DateTimeOffset(2025, 6, 7, 7, 0, 0, TimeSpan.FromHours(-4)), TimeSchedule.NextOccurrence(point, Weekdays.All, after, null, NewYork));
        Assert.Null(TimeSchedule.NextOccurrence(point, Weekdays.None, after, null, NewYork));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(12, false)]
    [InlineData(20, true)]
    public void Sunset_to_sunrise_window_wraps_past_midnight(int hour, bool expected)
    {
        var now = new DateTimeOffset(2025, 6, 10, hour, 30, 0, TimeSpan.FromHours(-4));
        var from = new TimePoint { Reference = TimeReference.Sunset };
        var to = new TimePoint { Reference = TimeReference.Sunrise };

        Assert.Equal(expected, TimeSchedule.IsWithin(now, from, to, NewYorkCity, NewYork));
    }

    [Theory]
    [InlineData(21, false)]
    [InlineData(23, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void Clock_window_over_midnight(int hour, bool expected)
    {
        var now = new DateTimeOffset(2025, 6, 10, hour, 0, 0, TimeSpan.FromHours(-4));
        var from = new TimePoint { Time = new TimeOnly(22, 0) };
        var to = new TimePoint { Time = new TimeOnly(6, 0) };

        Assert.Equal(expected, TimeSchedule.IsWithin(now, from, to, null, NewYork));
    }

    [Fact]
    public void Window_with_unknown_sun_times_is_false()
    {
        var now = new DateTimeOffset(2025, 6, 10, 23, 0, 0, TimeSpan.FromHours(-4));
        Assert.False(TimeSchedule.IsWithin(now, new TimePoint { Reference = TimeReference.Sunset }, new TimePoint { Reference = TimeReference.Sunrise }, null, NewYork));
    }

    [Fact]
    public void Occurrences_between_two_ticks()
    {
        var point = new TimePoint { Time = new TimeOnly(18, 0) };
        var from = new DateTimeOffset(2025, 6, 10, 17, 59, 55, TimeSpan.FromHours(-4));

        Assert.Single(TimeSchedule.OccurrencesBetween(point, Weekdays.All, from, from.AddSeconds(5), null, NewYork));
        Assert.Empty(TimeSchedule.OccurrencesBetween(point, Weekdays.All, from.AddSeconds(5), from.AddSeconds(10), null, NewYork));
        Assert.Empty(TimeSchedule.OccurrencesBetween(point, Weekdays.Weekend, from, from.AddSeconds(5), null, NewYork)); // a Tuesday
    }
}

public class AutomationGraphTests
{
    [Fact]
    public void Connection_rules()
    {
        var automation = new Automation();
        var trigger = automation.Add(new PcEventTriggerNode());
        var condition = automation.Add(new PcStateConditionNode());
        var action = automation.Add(new DeviceActionNode { DeviceId = "lamp" });
        var second = automation.Add(new DelayActionNode());

        Assert.True(AutomationGraph.CanConnect(automation, trigger.Id, Ports.Then, condition.Id, out _));
        Assert.False(AutomationGraph.CanConnect(automation, condition.Id, Ports.Yes, trigger.Id, out var intoTrigger));
        Assert.Contains("Triggers", intoTrigger);
        Assert.False(AutomationGraph.CanConnect(automation, action.Id, Ports.Then, action.Id, out _));
        Assert.False(AutomationGraph.CanConnect(automation, trigger.Id, Ports.Yes, action.Id, out _)); // triggers have no "yes"

        automation.Wire(trigger, condition);
        automation.Wire(condition, action, Ports.Yes);
        automation.Wire(action, second);
        Assert.False(AutomationGraph.CanConnect(automation, trigger.Id, Ports.Then, condition.Id, out var duplicate));
        Assert.Contains("already", duplicate);
        Assert.False(AutomationGraph.CanConnect(automation, second.Id, Ports.Then, condition.Id, out var loop));
        Assert.Contains("loop", loop);

        AutomationGraph.RemoveNode(automation, condition.Id);
        Assert.Single(automation.Links); // action -> second
    }

    [Fact]
    public void Validation_finds_the_usual_mistakes()
    {
        var automation = new Automation();
        var devices = new HashSet<string> { "lamp" };
        Assert.Contains(AutomationGraph.Validate(automation, devices, hasLocation: true), i => i.Message.Contains("Add a trigger"));

        var trigger = automation.Add(new TimeTriggerNode { At = new TimePoint { Reference = TimeReference.Sunset } });
        var unwired = automation.Add(new DeviceActionNode { DeviceId = "lamp" });
        var issues = AutomationGraph.Validate(automation, devices, hasLocation: false);
        Assert.Contains(issues, i => i.Message.Contains("Connect an action"));
        Assert.Contains(issues, i => i.NodeId == unwired.Id && i.Severity == IssueSeverity.Warning);
        Assert.Contains(issues, i => i.NodeId == trigger.Id && i.Message.Contains("location"));

        automation.Wire(trigger, unwired);
        var gone = automation.Add(new DeviceActionNode { DeviceId = "removed" });
        automation.Wire(trigger, gone);
        var none = automation.Add(new DeviceStateConditionNode());
        automation.Wire(trigger, none);
        issues = AutomationGraph.Validate(automation, devices, hasLocation: true);
        Assert.Contains(issues, i => i.NodeId == gone.Id && i.Message.Contains("removed"));
        Assert.Contains(issues, i => i.NodeId == none.Id && i.Message.Contains("Pick a device"));
        Assert.DoesNotContain(issues, i => i.NodeId == unwired.Id);
    }

    [Fact]
    public void Summary_reads_like_a_sentence()
    {
        var automation = AutomationTemplates.Create(AutomationTemplate.LightsAtSunset, [new DeviceConfig { Id = "lamp", Name = "Lamp" }]);

        var summary = AutomationGraph.Summarize(automation, id => id == "lamp" ? "Lamp" : null);

        Assert.Equal("15 min before sunset → Turn on Lamp", summary);
    }

    [Fact]
    public void Templates_are_valid()
    {
        var devices = new List<DeviceConfig> { new() { Id = "lamp", Name = "Lamp" } };
        foreach (var template in Enum.GetValues<AutomationTemplate>().Where(t => t != AutomationTemplate.Blank))
        {
            var automation = AutomationTemplates.Create(template, devices);
            var issues = AutomationGraph.Validate(automation, new HashSet<string> { "lamp" }, hasLocation: true);
            Assert.True(issues.Count == 0, $"{template}: {string.Join("; ", issues.Select(i => i.Message))}");
        }
    }
}

public class AutomationRunnerTests
{
    private static (Automation Automation, AutomationNode Trigger) DarkLamp()
    {
        // When unlocked: if it's dark, turn the lamp on, else notify.
        var automation = new Automation();
        var trigger = automation.Add(new PcEventTriggerNode { Event = PcEvent.Unlocked });
        var dark = automation.Add(new TimeWindowConditionNode());
        var on = automation.Add(new DeviceActionNode { DeviceId = "lamp" }, y: 0);
        var notify = automation.Add(new NotifyActionNode { Message = "Daylight" }, y: 100);
        automation.Wire(trigger, dark);
        automation.Wire(dark, on, Ports.Yes);
        automation.Wire(dark, notify, Ports.No);
        return (automation, trigger);
    }

    [Fact]
    public async Task Conditions_pick_their_yes_or_no_output()
    {
        var host = new FakeAutomationHost { Now = new DateTimeOffset(2025, 6, 2, 23, 30, 0, TimeSpan.FromHours(1)) };
        var (automation, trigger) = DarkLamp();

        var night = await new AutomationRunner(host).RunAsync(automation, trigger, RunOptions.Normal, default);
        Assert.Equal(["TurnOn lamp"], host.Commands);
        Assert.Empty(host.Notifications);
        Assert.Equal("Turned on Lamp", night.Summary);

        host.Now = new DateTimeOffset(2025, 6, 2, 13, 0, 0, TimeSpan.FromHours(1));
        await new AutomationRunner(host).RunAsync(automation, trigger, RunOptions.Normal, default);
        Assert.Equal(["Daylight"], host.Notifications);
        Assert.Single(host.Commands);
    }

    [Fact]
    public async Task Failed_action_is_recorded_and_the_chain_carries_on()
    {
        var host = new FakeAutomationHost
        {
            Respond = (id, _) => id == "lamp" ? new DeviceCommandResult(false, null, "The device is offline.") : new DeviceCommandResult(true, false, "ok"),
        };
        var automation = new Automation();
        var trigger = automation.Add(new ShutdownTriggerNode());
        var lamp = automation.Add(new DeviceActionNode { DeviceId = "lamp", Command = DeviceCommand.TurnOff });
        var desk = automation.Add(new DeviceActionNode { DeviceId = "desk", Command = DeviceCommand.TurnOff });
        automation.Wire(trigger, lamp);
        automation.Wire(lamp, desk);

        var result = await new AutomationRunner(host).RunAsync(automation, trigger, RunOptions.Normal, default);

        Assert.Equal(["TurnOff lamp", "TurnOff desk"], host.Commands);
        Assert.True(result.Failed);
        Assert.Contains("offline", result.Summary);
        Assert.Contains("Turned off Desk", result.Summary);
    }

    [Fact]
    public async Task Connection_errors_are_retried_but_sign_in_errors_are_not()
    {
        var host = new FakeAutomationHost();
        host.Failures.Enqueue(new GoogleHomeException("home.google.com could not be loaded"));
        var automation = new Automation();
        var trigger = automation.Add(new PcEventTriggerNode { Event = PcEvent.Resumed });
        automation.Wire(trigger, automation.Add(new DeviceActionNode { DeviceId = "lamp" }));

        var result = await new AutomationRunner(host).RunAsync(automation, trigger, RunOptions.Normal, default);
        Assert.False(result.Failed);
        Assert.Equal(2, host.Commands.Count);
        Assert.Equal([TimeSpan.FromSeconds(5)], host.Delays);

        host.Commands.Clear();
        host.Failures.Enqueue(new GoogleHomeSignInRequiredException());
        result = await new AutomationRunner(host).RunAsync(automation, trigger, RunOptions.Normal, default);
        Assert.True(result.Failed);
        Assert.Single(host.Commands);
    }

    [Fact]
    public async Task Shutdown_runs_skip_waits_and_pc_power()
    {
        var host = new FakeAutomationHost();
        var automation = new Automation();
        var trigger = automation.Add(new ShutdownTriggerNode());
        var wait = automation.Add(new DelayActionNode { Seconds = 600 });
        var power = automation.Add(new PcPowerActionNode { Command = PcPowerCommand.ShutDown });
        var off = automation.Add(new AllOffActionNode());
        automation.Wire(trigger, wait);
        automation.Wire(wait, power);
        automation.Wire(power, off);

        await new AutomationRunner(host).RunAsync(automation, trigger, RunOptions.Shutdown, default);

        Assert.Empty(host.Delays);
        Assert.Empty(host.PowerCommands);
        Assert.Equal(1, host.AllOffCount);
    }

    [Fact]
    public async Task Pc_state_and_device_conditions()
    {
        var host = new FakeAutomationHost { Status = new PcStatus(Locked: true, Idle: TimeSpan.FromMinutes(20), OnBattery: true, DisplayOn: false) };
        var automation = new Automation();
        var trigger = automation.Add(new PcEventTriggerNode());
        var idle = automation.Add(new PcStateConditionNode { State = PcState.Idle, IdleMinutes = 15 });
        var deskOn = automation.Add(new DeviceStateConditionNode { DeviceId = "desk", IsOn = true });
        var off = automation.Add(new DeviceActionNode { DeviceId = "desk", Command = DeviceCommand.TurnOff });
        automation.Wire(trigger, idle);
        automation.Wire(idle, deskOn, Ports.Yes);
        automation.Wire(deskOn, off, Ports.Yes);

        await new AutomationRunner(host).RunAsync(automation, trigger, RunOptions.Normal, default);
        Assert.Equal(["TurnOff desk"], host.Commands);

        Assert.True(AutomationRunner.IsPcState(host.Status, PcState.OnBattery, 0));
        Assert.True(AutomationRunner.IsPcState(host.Status, PcState.DisplayOff, 0));
        Assert.False(AutomationRunner.IsPcState(host.Status, PcState.InUse, 15));
    }

    [Fact]
    public async Task Branches_from_one_output_all_run()
    {
        var host = new FakeAutomationHost();
        var automation = new Automation();
        var trigger = automation.Add(new PcEventTriggerNode());
        automation.Wire(trigger, automation.Add(new DeviceActionNode { DeviceId = "lamp" }, y: 0));
        automation.Wire(trigger, automation.Add(new DeviceActionNode { DeviceId = "desk", Command = DeviceCommand.Toggle }, y: 100));

        var result = await new AutomationRunner(host).RunAsync(automation, trigger, RunOptions.Normal, default);

        Assert.Equal(2, result.Steps.Count);
        Assert.Contains("TurnOn lamp", host.Commands);
        Assert.Contains("Toggle desk", host.Commands);
        Assert.False(host.Devices["desk"].On);
    }
}

public class AutomationEngineTests
{
    private static (AutomationEngine Engine, FakeAutomationHost Host, AutomationDocument Document) Create(params Automation[] automations)
    {
        var host = new FakeAutomationHost();
        var document = new AutomationDocument { Location = host.Location };
        document.Automations.AddRange(automations);
        return (new AutomationEngine(host, () => document), host, document);
    }

    private static Automation At(TimeOnly time, Weekdays days = Weekdays.All, string device = "lamp")
    {
        var automation = new Automation();
        var trigger = automation.Add(new TimeTriggerNode { At = new TimePoint { Time = time }, Days = days });
        automation.Wire(trigger, automation.Add(new DeviceActionNode { DeviceId = device }));
        return automation;
    }

    [Fact]
    public void Time_trigger_fires_once_when_its_time_passes()
    {
        var automation = At(new TimeOnly(18, 0));
        var (engine, host, _) = Create(automation);
        host.Now = new DateTimeOffset(2025, 6, 2, 17, 59, 50, TimeSpan.FromHours(1));
        engine.Start();

        host.Now = host.Now.AddSeconds(5);
        engine.Tick();
        Assert.Empty(host.Commands);

        host.Now = host.Now.AddSeconds(10); // 18:00:05
        engine.Tick();
        Assert.Equal(["TurnOn lamp"], host.Commands);
        Assert.NotNull(automation.LastRun);
        Assert.Equal("Turned on Lamp", automation.LastResult);

        host.Now = host.Now.AddSeconds(5);
        engine.Tick();
        Assert.Single(host.Commands);
    }

    [Fact]
    public void Missed_times_are_skipped_after_sleep_and_days_are_respected()
    {
        var (engine, host, _) = Create(At(new TimeOnly(18, 0)), At(new TimeOnly(18, 1), Weekdays.Weekend, "desk"));
        host.Now = new DateTimeOffset(2025, 6, 2, 17, 0, 0, TimeSpan.FromHours(1));
        engine.Start();

        host.Now = new DateTimeOffset(2025, 6, 2, 19, 0, 0, TimeSpan.FromHours(1)); // woke up an hour late
        engine.Tick();
        Assert.Empty(host.Commands);

        host.Now = new DateTimeOffset(2025, 6, 3, 18, 0, 30, TimeSpan.FromHours(1));
        engine.Tick(); // more than the grace after the last tick, but 18:00 was 30 s ago
        Assert.Equal(["TurnOn lamp"], host.Commands);

        host.Now = host.Now.AddMinutes(1); // 18:01:30 on a Tuesday: the weekend trigger stays quiet
        engine.Tick();
        Assert.Single(host.Commands);
    }

    [Fact]
    public void Disabled_automations_never_run()
    {
        var automation = new Automation { Enabled = false };
        var trigger = automation.Add(new PcEventTriggerNode { Event = PcEvent.Locked });
        automation.Wire(trigger, automation.Add(new AllOffActionNode()));
        var (engine, host, _) = Create(automation);

        engine.HandlePcEvent(PcEvent.Locked);

        Assert.Equal(0, host.AllOffCount);
    }

    [Fact]
    public void Pc_events_and_started()
    {
        var automation = new Automation();
        var locked = automation.Add(new PcEventTriggerNode { Event = PcEvent.Locked });
        var started = automation.Add(new PcEventTriggerNode { Event = PcEvent.Started });
        automation.Wire(locked, automation.Add(new DeviceActionNode { DeviceId = "lamp", Command = DeviceCommand.TurnOff }));
        automation.Wire(started, automation.Add(new DeviceActionNode { DeviceId = "desk" }));
        var (engine, host, _) = Create(automation);

        engine.Start();
        Assert.Equal(["TurnOn desk"], host.Commands);

        engine.HandlePcEvent(PcEvent.Unlocked);
        engine.HandlePcEvent(PcEvent.Locked);
        Assert.Equal(["TurnOn desk", "TurnOff lamp"], host.Commands);
    }

    [Fact]
    public void Idle_and_back_fire_on_crossing_the_threshold()
    {
        var automation = new Automation();
        var idle = automation.Add(new PcEventTriggerNode { Event = PcEvent.Idle, IdleMinutes = 10 });
        var back = automation.Add(new PcEventTriggerNode { Event = PcEvent.Active, IdleMinutes = 10 });
        automation.Wire(idle, automation.Add(new DeviceActionNode { DeviceId = "lamp", Command = DeviceCommand.TurnOff }));
        automation.Wire(back, automation.Add(new DeviceActionNode { DeviceId = "lamp" }));
        var (engine, host, _) = Create(automation);
        engine.Start();

        foreach (var minutes in new[] { 5.0, 9.9, 10.0, 15.0 })
        {
            host.Status = host.Status with { Idle = TimeSpan.FromMinutes(minutes) };
            engine.Tick();
        }

        Assert.Equal(["TurnOff lamp"], host.Commands);

        host.Status = host.Status with { Idle = TimeSpan.FromSeconds(1) };
        engine.Tick();
        host.Status = host.Status with { Idle = TimeSpan.FromSeconds(6) };
        engine.Tick();
        Assert.Equal(["TurnOff lamp", "TurnOn lamp"], host.Commands);
    }

    [Fact]
    public async Task Triggering_again_restarts_a_waiting_run()
    {
        var automation = new Automation();
        var trigger = automation.Add(new PcEventTriggerNode { Event = PcEvent.Locked });
        var wait = automation.Add(new DelayActionNode { Seconds = 60 });
        automation.Wire(trigger, wait);
        automation.Wire(wait, automation.Add(new AllOffActionNode()));
        var (engine, host, _) = Create(automation);
        host.DelayGate = new TaskCompletionSource();
        var completed = new List<AutomationRunResult>();
        engine.RunCompleted += (_, e) => completed.Add(e.Result);

        engine.HandlePcEvent(PcEvent.Locked);
        engine.HandlePcEvent(PcEvent.Locked);
        Assert.Single(engine.RunningAutomations);

        await WaitUntil(() => completed.Count == 1);
        Assert.True(completed[0].Cancelled);

        host.DelayGate.SetResult();
        await WaitUntil(() => completed.Count == 2);
        Assert.Equal(1, host.AllOffCount);
        Assert.Empty(engine.RunningAutomations);
    }

    [Fact]
    public async Task Shutdown_triggers_match_the_kind()
    {
        Automation Make(ShutdownKind kind, string device)
        {
            var automation = new Automation();
            var trigger = automation.Add(new ShutdownTriggerNode { Kind = kind });
            automation.Wire(trigger, automation.Add(new DeviceActionNode { DeviceId = device, Command = DeviceCommand.TurnOff }));
            return automation;
        }

        var (engine, host, _) = Create(Make(ShutdownKind.ShutdownOrRestart, "lamp"), Make(ShutdownKind.SignOut, "desk"), Make(ShutdownKind.Any, "desk"));

        await engine.RunShutdownTriggersAsync(signingOut: false, default);
        Assert.Equal(["TurnOff lamp", "TurnOff desk"], host.Commands);

        host.Commands.Clear();
        await engine.RunShutdownTriggersAsync(signingOut: true, default);
        Assert.Equal(["TurnOff desk", "TurnOff desk"], host.Commands);
    }

    [Fact]
    public void Next_occurrence_of_a_sun_trigger()
    {
        var automation = new Automation();
        var trigger = automation.Add(new TimeTriggerNode { At = new TimePoint { Reference = TimeReference.Sunset } });
        var (engine, host, _) = Create(automation);

        var next = engine.NextOccurrence(trigger);

        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2025, 6, 2), DateOnly.FromDateTime(next!.Value.DateTime)); // London sunset ~21:15
        Assert.InRange(next.Value.Hour, 21, 21);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}

public class AutomationStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hc-automations-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Round_trips_every_node_type()
    {
        var store = new AutomationStore(Path.Combine(_folder, "automations.json"));
        var automation = new Automation { Name = "Everything" };
        AutomationNode[] nodes =
        [
            new TimeTriggerNode { At = new TimePoint { Reference = TimeReference.Dusk, OffsetMinutes = 10 }, Days = Weekdays.Weekend },
            new PcEventTriggerNode { Event = PcEvent.Idle, IdleMinutes = 7 },
            new ShutdownTriggerNode { Kind = ShutdownKind.Any },
            new TimeWindowConditionNode { From = new TimePoint { Time = new TimeOnly(22, 15) } },
            new DaysConditionNode { Days = Weekdays.Monday | Weekdays.Friday },
            new DeviceStateConditionNode { DeviceId = "lamp", IsOn = false },
            new PcStateConditionNode { State = PcState.OnBattery },
            new DeviceActionNode { DeviceId = "lamp", Command = DeviceCommand.Toggle },
            new AllOffActionNode(),
            new DelayActionNode { Seconds = 90 },
            new NotifyActionNode { Message = "Hi" },
            new PcPowerActionNode { Command = PcPowerCommand.Sleep },
        ];
        automation.Nodes.AddRange(nodes);
        automation.Wire(nodes[0], nodes[3]);
        automation.Wire(nodes[3], nodes[7], Ports.No);
        store.Save(new AutomationDocument { Location = new GeoLocation(1.5, 2.5, "Somewhere"), Automations = [automation] });

        var loaded = store.Load();

        Assert.Null(store.LoadError);
        Assert.Equal(new GeoLocation(1.5, 2.5, "Somewhere"), loaded.Location);
        var copy = Assert.Single(loaded.Automations);
        Assert.Equal(nodes.Select(n => n.GetType()), copy.Nodes.Select(n => n.GetType()));
        Assert.Equal(automation.Links, copy.Links);
        var dusk = Assert.IsType<TimeTriggerNode>(copy.Nodes[0]);
        Assert.Equal(TimeReference.Dusk, dusk.At.Reference);
        Assert.Equal(10, dusk.At.OffsetMinutes);
        Assert.Equal(Weekdays.Weekend, dusk.Days);
        Assert.Equal(new TimeOnly(22, 15), Assert.IsType<TimeWindowConditionNode>(copy.Nodes[3]).From.Time);
        Assert.Contains("\"type\": \"trigger.time\"", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Repairs_bad_links_and_values_and_keeps_a_corrupt_file()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "automations.json");
        File.WriteAllText(path, """
            {
              "location": { "latitude": 123, "longitude": 0 },
              "automations": [ {
                "id": "a", "name": "  ",
                "nodes": [
                  { "type": "trigger.pc", "id": "t", "event": "Idle", "idleMinutes": 0 },
                  { "type": "action.delay", "id": "d", "seconds": -5 },
                  { "type": "action.alloff", "id": "d" }
                ],
                "links": [
                  { "fromNode": "t", "fromPort": "then", "toNode": "d" },
                  { "fromNode": "d", "fromPort": "then", "toNode": "t" },
                  { "fromNode": "t", "fromPort": "yes", "toNode": "d" },
                  { "fromNode": "t", "fromPort": "then", "toNode": "missing" }
                ]
              } ]
            }
            """);
        var store = new AutomationStore(path);

        var document = store.Load();

        Assert.Null(document.Location); // latitude out of range
        var automation = Assert.Single(document.Automations);
        Assert.Equal("Automation", automation.Name);
        Assert.Equal(3, automation.Nodes.Select(n => n.Id).Distinct().Count());
        Assert.Equal(1, Assert.IsType<PcEventTriggerNode>(automation.Nodes[0]).IdleMinutes);
        Assert.Equal(0, Assert.IsType<DelayActionNode>(automation.Nodes[1]).Seconds);
        Assert.Equal([new AutomationLink("t", "then", "d")], automation.Links);

        File.WriteAllText(path, "{ not json");
        Assert.Empty(store.Load().Automations);
        Assert.NotNull(store.LoadError);
        Assert.True(File.Exists(path + ".corrupt"));
    }
}

public class GeocodingTests
{
    [Fact]
    public void Parses_open_meteo_results()
    {
        var places = GeocodingClient.Parse("""
            { "results": [
              { "id": 2950159, "name": "Berlin", "latitude": 52.52437, "longitude": 13.41053, "country": "Germany", "admin1": "Land Berlin", "timezone": "Europe/Berlin" },
              { "id": 1, "name": "Berlin", "latitude": 44.46867, "longitude": -71.18508, "country": "United States", "admin1": "New Hampshire" },
              { "id": 2, "name": "Broken" }
            ] }
            """);

        Assert.Equal(2, places.Count);
        Assert.Equal("Berlin, Land Berlin, Germany", places[0].DisplayName);
        Assert.Equal(new GeoLocation(52.52437, 13.41053, "Berlin, Land Berlin, Germany"), places[0].ToLocation());
        Assert.Empty(GeocodingClient.Parse("""{ "generationtime_ms": 0.5 }"""));
    }
}
