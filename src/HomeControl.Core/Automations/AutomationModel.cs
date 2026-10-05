using System.Text.Json.Serialization;

namespace HomeControl.Core.Automations;

/// <summary>Everything stored in automations.json: the home's location and the automations.</summary>
public sealed class AutomationDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Where sunrise, sunset and twilight are calculated for. Null until the user sets it.</summary>
    public GeoLocation? Location { get; set; }

    public List<Automation> Automations { get; set; } = [];
}

/// <summary>A place on Earth (degrees; north and east are positive).</summary>
public sealed record GeoLocation(double Latitude, double Longitude, string? Name = null)
{
    public bool IsValid => Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name)
            ? FormattableString.Invariant($"{Latitude:0.####}, {Longitude:0.####}")
            : Name!;
}

/// <summary>
/// One automation: a graph of nodes. Triggers start a run; the run follows the links from the
/// trigger's output through conditions (which pick their "yes" or "no" output) and actions.
/// </summary>
public sealed class Automation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New automation";

    public bool Enabled { get; set; } = true;

    public List<AutomationNode> Nodes { get; set; } = [];

    public List<AutomationLink> Links { get; set; } = [];

    /// <summary>When the automation last ran (null: never).</summary>
    public DateTimeOffset? LastRun { get; set; }

    /// <summary>One line about the last run, e.g. "Turned on Backlight".</summary>
    public string? LastResult { get; set; }

    public AutomationNode? FindNode(string id) => Nodes.FirstOrDefault(n => n.Id == id);
}

/// <summary>A wire from an output port of one node to the input of another.</summary>
public sealed record AutomationLink(string FromNode, string FromPort, string ToNode);

public enum NodeCategory
{
    Trigger,
    Condition,
    Action,
}

/// <summary>Output port names.</summary>
public static class Ports
{
    /// <summary>The single output of triggers and actions.</summary>
    public const string Then = "then";

    /// <summary>Taken when a condition is true.</summary>
    public const string Yes = "yes";

    /// <summary>Taken when a condition is false.</summary>
    public const string No = "no";
}

/// <summary>A node in the graph. X/Y are its position in the editor.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TimeTriggerNode), "trigger.time")]
[JsonDerivedType(typeof(PcEventTriggerNode), "trigger.pc")]
[JsonDerivedType(typeof(ShutdownTriggerNode), "trigger.shutdown")]
[JsonDerivedType(typeof(TimeWindowConditionNode), "condition.time")]
[JsonDerivedType(typeof(DaysConditionNode), "condition.days")]
[JsonDerivedType(typeof(DeviceStateConditionNode), "condition.device")]
[JsonDerivedType(typeof(PcStateConditionNode), "condition.pc")]
[JsonDerivedType(typeof(DeviceActionNode), "action.device")]
[JsonDerivedType(typeof(AllOffActionNode), "action.alloff")]
[JsonDerivedType(typeof(DelayActionNode), "action.delay")]
[JsonDerivedType(typeof(NotifyActionNode), "action.notify")]
[JsonDerivedType(typeof(PcPowerActionNode), "action.pcpower")]
public abstract class AutomationNode
{
    private static readonly string[] SingleOutput = [Ports.Then];
    private static readonly string[] ConditionOutputs = [Ports.Yes, Ports.No];

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public double X { get; set; }

    public double Y { get; set; }

    [JsonIgnore]
    public abstract NodeCategory Category { get; }

    /// <summary>Triggers have no input: they start a run.</summary>
    [JsonIgnore]
    public bool HasInput => Category != NodeCategory.Trigger;

    [JsonIgnore]
    public IReadOnlyList<string> Outputs => Category == NodeCategory.Condition ? ConditionOutputs : SingleOutput;

    public AutomationNode Clone()
    {
        var copy = (AutomationNode)MemberwiseClone();
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>Deep-copies reference-type settings (MemberwiseClone copies the rest).</summary>
    protected virtual void CopyFrom(AutomationNode source)
    {
    }
}

// ---------------------------------------------------------------- triggers

/// <summary>Days of the week a trigger or condition applies to.</summary>
[Flags]
public enum Weekdays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    WorkDays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    All = WorkDays | Weekend,
}

/// <summary>What a <see cref="TimePoint"/> is measured from.</summary>
public enum TimeReference
{
    /// <summary>A fixed time on the clock.</summary>
    Clock,
    Sunrise,
    Sunset,

    /// <summary>Civil dawn: morning twilight begins (sun 6° below the horizon).</summary>
    Dawn,

    /// <summary>Civil dusk: evening twilight ends (sun 6° below the horizon).</summary>
    Dusk,
    NauticalDawn,
    NauticalDusk,
    AstronomicalDawn,
    AstronomicalDusk,
    SolarNoon,
}

/// <summary>A time of day: a clock time, or a sun event plus an offset (e.g. 15 minutes before sunset).</summary>
public sealed class TimePoint
{
    public TimeReference Reference { get; set; } = TimeReference.Clock;

    /// <summary>The clock time (used when <see cref="Reference"/> is <see cref="TimeReference.Clock"/>).</summary>
    public TimeOnly Time { get; set; } = new(18, 0);

    /// <summary>Minutes after (positive) or before (negative) the sun event.</summary>
    public int OffsetMinutes { get; set; }

    [JsonIgnore]
    public bool IsSolar => Reference != TimeReference.Clock;

    public TimePoint Clone() => (TimePoint)MemberwiseClone();
}

/// <summary>Fires at a time of day: a clock time, or sunrise, sunset or twilight at the home's location.</summary>
public sealed class TimeTriggerNode : AutomationNode
{
    public override NodeCategory Category => NodeCategory.Trigger;

    public TimePoint At { get; set; } = new() { Reference = TimeReference.Sunset };

    public Weekdays Days { get; set; } = Weekdays.All;

    protected override void CopyFrom(AutomationNode source) => At = ((TimeTriggerNode)source).At.Clone();
}

/// <summary>Things that happen to the PC.</summary>
public enum PcEvent
{
    /// <summary>Home Control started (e.g. after signing in to Windows).</summary>
    Started,
    Locked,
    Unlocked,

    /// <summary>The PC is going to sleep or hibernate.</summary>
    Sleeping,

    /// <summary>The PC woke up from sleep or hibernation.</summary>
    Resumed,

    /// <summary>No mouse or keyboard input for <see cref="PcEventTriggerNode.IdleMinutes"/>.</summary>
    Idle,

    /// <summary>Input again after being idle for at least <see cref="PcEventTriggerNode.IdleMinutes"/>.</summary>
    Active,
    DisplayOff,
    DisplayOn,
    OnBattery,
    PluggedIn,
}

/// <summary>Fires when something happens to the PC (locked, unlocked, sleep, idle, display…).</summary>
public sealed class PcEventTriggerNode : AutomationNode
{
    public override NodeCategory Category => NodeCategory.Trigger;

    public PcEvent Event { get; set; } = PcEvent.Locked;

    /// <summary>For <see cref="PcEvent.Idle"/> and <see cref="PcEvent.Active"/>.</summary>
    public int IdleMinutes { get; set; } = 10;
}

/// <summary>Which end-of-session commands fire a <see cref="ShutdownTriggerNode"/>.</summary>
public enum ShutdownKind
{
    /// <summary>Shut down or restart.</summary>
    ShutdownOrRestart,

    /// <summary>Signing out of Windows.</summary>
    SignOut,

    /// <summary>Any of them.</summary>
    Any,
}

/// <summary>
/// Fires when Windows is told to shut down, restart or sign out. Windows waits for the actions
/// (up to a few seconds); delays are skipped and the PC power action is ignored.
/// </summary>
public sealed class ShutdownTriggerNode : AutomationNode
{
    public override NodeCategory Category => NodeCategory.Trigger;

    public ShutdownKind Kind { get; set; } = ShutdownKind.ShutdownOrRestart;
}

// ---------------------------------------------------------------- conditions

/// <summary>Is it between two times of day (e.g. between sunset and sunrise)? The window may wrap past midnight.</summary>
public sealed class TimeWindowConditionNode : AutomationNode
{
    public override NodeCategory Category => NodeCategory.Condition;

    public TimePoint From { get; set; } = new() { Reference = TimeReference.Sunset };

    public TimePoint To { get; set; } = new() { Reference = TimeReference.Sunrise };

    protected override void CopyFrom(AutomationNode source)
    {
        var node = (TimeWindowConditionNode)source;
        From = node.From.Clone();
        To = node.To.Clone();
    }
}

/// <summary>Is today one of these days?</summary>
public sealed class DaysConditionNode : AutomationNode
{
    public override NodeCategory Category => NodeCategory.Condition;

    public Weekdays Days { get; set; } = Weekdays.WorkDays;
}

/// <summary>Is a device on (or off)?</summary>
public sealed class DeviceStateConditionNode : AutomationNode
{
    public override NodeCategory Category => NodeCategory.Condition;

    public string? DeviceId { get; set; }

    /// <summary>True: "is on"; false: "is off".</summary>
    public bool IsOn { get; set; } = true;
}

/// <summary>States of the PC a condition can test.</summary>
public enum PcState
{
    Locked,
    Unlocked,

    /// <summary>No input for at least <see cref="PcStateConditionNode.IdleMinutes"/>.</summary>
    Idle,

    /// <summary>Input within the last <see cref="PcStateConditionNode.IdleMinutes"/>.</summary>
    InUse,
    OnBattery,
    PluggedIn,
    DisplayOn,
    DisplayOff,
}

/// <summary>Is the PC locked, idle, on battery…?</summary>
public sealed class PcStateConditionNode : AutomationNode
{
    public override NodeCategory Category => NodeCategory.Condition;

    public PcState State { get; set; } = PcState.Unlocked;

    public int IdleMinutes { get; set; } = 10;
}

// ---------------------------------------------------------------- actions

public enum DeviceCommand
{
    TurnOn,
    TurnOff,
    Toggle,
}

/// <summary>Turns a device on or off, or toggles it.</summary>
public sealed class DeviceActionNode : AutomationNode
{
    public override NodeCategory Category => NodeCategory.Action;

    public string? DeviceId { get; set; }

    public DeviceCommand Command { get; set; } = DeviceCommand.TurnOn;
}

/// <summary>Turns every device in the tray off.</summary>
public sealed class AllOffActionNode : AutomationNode
{
    public override NodeCategory Category => NodeCategory.Action;
}

/// <summary>Waits before the next nodes run.</summary>
public sealed class DelayActionNode : AutomationNode
{
    public override NodeCategory Category => NodeCategory.Action;

    public int Seconds { get; set; } = 300;
}

/// <summary>Shows a Windows notification.</summary>
public sealed class NotifyActionNode : AutomationNode
{
    public override NodeCategory Category => NodeCategory.Action;

    public string Message { get; set; } = string.Empty;
}

public enum PcPowerCommand
{
    Lock,
    Sleep,

    /// <summary>Shuts down after a one-minute warning that can be cancelled.</summary>
    ShutDown,

    /// <summary>Restarts after a one-minute warning that can be cancelled.</summary>
    Restart,

    /// <summary>Turns the display off.</summary>
    DisplayOff,
}

/// <summary>Locks the PC, puts it to sleep, turns the display off, or shuts it down.</summary>
public sealed class PcPowerActionNode : AutomationNode
{
    public override NodeCategory Category => NodeCategory.Action;

    public PcPowerCommand Command { get; set; } = PcPowerCommand.Lock;
}
