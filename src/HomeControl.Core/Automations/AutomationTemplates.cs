using HomeControl.Core.Models;

namespace HomeControl.Core.Automations;

/// <summary>Ready-made automations to start from.</summary>
public enum AutomationTemplate
{
    Blank,

    /// <summary>A light turns on 15 minutes before sunset.</summary>
    LightsAtSunset,

    /// <summary>Everything turns off when the PC shuts down.</summary>
    OffAtShutdown,

    /// <summary>A device turns off when the PC locks, and back on when it is unlocked after dark.</summary>
    FollowLock,

    /// <summary>A device turns off after the PC has been idle for 15 minutes.</summary>
    OffWhenIdle,
}

public static class AutomationTemplates
{
    // Column positions in the editor: triggers, conditions, actions.
    private const double TriggerX = 40;
    private const double ConditionX = 380;
    private const double ActionX = 720;

    public static string Title(AutomationTemplate template) => template switch
    {
        AutomationTemplate.LightsAtSunset => "Lights on at sunset",
        AutomationTemplate.OffAtShutdown => "Everything off when the PC shuts down",
        AutomationTemplate.FollowLock => "Off when the PC locks, on when it unlocks after dark",
        AutomationTemplate.OffWhenIdle => "Off when the PC is idle",
        _ => "Blank automation",
    };

    /// <summary>Creates the automation; <paramref name="devices"/> picks a sensible device (a light if there is one).</summary>
    public static Automation Create(AutomationTemplate template, IReadOnlyList<DeviceConfig> devices)
    {
        var device = devices.FirstOrDefault(d => !d.Missing && d.Kind == DeviceKind.Light) ?? devices.FirstOrDefault(d => !d.Missing);
        var automation = new Automation { Name = template == AutomationTemplate.Blank ? "New automation" : Title(template) };

        switch (template)
        {
            case AutomationTemplate.LightsAtSunset:
            {
                var trigger = Add(automation, new TimeTriggerNode { At = new TimePoint { Reference = TimeReference.Sunset, OffsetMinutes = -15 } }, TriggerX, 60);
                var action = Add(automation, new DeviceActionNode { DeviceId = device?.Id, Command = DeviceCommand.TurnOn }, ConditionX, 60);
                AutomationGraph.Connect(automation, trigger.Id, Ports.Then, action.Id);
                break;
            }

            case AutomationTemplate.OffAtShutdown:
            {
                var trigger = Add(automation, new ShutdownTriggerNode(), TriggerX, 60);
                var action = Add(automation, new AllOffActionNode(), ConditionX, 60);
                AutomationGraph.Connect(automation, trigger.Id, Ports.Then, action.Id);
                break;
            }

            case AutomationTemplate.FollowLock:
            {
                var locked = Add(automation, new PcEventTriggerNode { Event = PcEvent.Locked }, TriggerX, 60);
                var off = Add(automation, new DeviceActionNode { DeviceId = device?.Id, Command = DeviceCommand.TurnOff }, ActionX, 60);
                var unlocked = Add(automation, new PcEventTriggerNode { Event = PcEvent.Unlocked }, TriggerX, 300);
                var dark = Add(automation, new TimeWindowConditionNode(), ConditionX, 300);
                var on = Add(automation, new DeviceActionNode { DeviceId = device?.Id, Command = DeviceCommand.TurnOn }, ActionX, 300);
                AutomationGraph.Connect(automation, locked.Id, Ports.Then, off.Id);
                AutomationGraph.Connect(automation, unlocked.Id, Ports.Then, dark.Id);
                AutomationGraph.Connect(automation, dark.Id, Ports.Yes, on.Id);
                break;
            }

            case AutomationTemplate.OffWhenIdle:
            {
                var trigger = Add(automation, new PcEventTriggerNode { Event = PcEvent.Idle, IdleMinutes = 15 }, TriggerX, 60);
                var action = Add(automation, new DeviceActionNode { DeviceId = device?.Id, Command = DeviceCommand.TurnOff }, ConditionX, 60);
                AutomationGraph.Connect(automation, trigger.Id, Ports.Then, action.Id);
                break;
            }
        }

        return automation;
    }

    private static T Add<T>(Automation automation, T node, double x, double y) where T : AutomationNode
    {
        node.X = x;
        node.Y = y;
        automation.Nodes.Add(node);
        return node;
    }
}
