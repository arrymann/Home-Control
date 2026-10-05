using System.Globalization;

namespace HomeControl.Core.Automations;

/// <summary>Names and one-line descriptions of nodes, for the editor, summaries and run logs.</summary>
public static class NodeText
{
    public static string Title(AutomationNode node) => node switch
    {
        TimeTriggerNode => "Time of day",
        PcEventTriggerNode => "PC event",
        ShutdownTriggerNode => "PC shutdown",
        TimeWindowConditionNode => "Time window",
        DaysConditionNode => "Day of the week",
        DeviceStateConditionNode => "Device state",
        PcStateConditionNode => "PC state",
        DeviceActionNode => "Device",
        AllOffActionNode => "Everything off",
        DelayActionNode => "Wait",
        NotifyActionNode => "Notification",
        PcPowerActionNode => "PC power",
        _ => node.GetType().Name,
    };

    /// <summary>A sentence such as "15 min before sunset" or "Turn on Backlight".</summary>
    public static string Describe(AutomationNode node, Func<string?, string?> deviceName) => node switch
    {
        TimeTriggerNode t => $"{Capitalize(TimePoint(t.At, atPrefix: true))}{DaysSuffix(t.Days)}",
        PcEventTriggerNode p => PcEventText(p.Event, p.IdleMinutes),
        ShutdownTriggerNode s => s.Kind switch
        {
            ShutdownKind.SignOut => "When you sign out of Windows",
            ShutdownKind.Any => "When the PC shuts down, restarts or you sign out",
            _ => "When the PC shuts down or restarts",
        },
        TimeWindowConditionNode w => $"Between {TimePoint(w.From, atPrefix: false)} and {TimePoint(w.To, atPrefix: false)}",
        DaysConditionNode d => $"On {Days(d.Days)}",
        DeviceStateConditionNode d => $"{DeviceLabel(d.DeviceId, deviceName)} is {(d.IsOn ? "on" : "off")}",
        PcStateConditionNode p => PcStateText(p.State, p.IdleMinutes),
        DeviceActionNode a => a.Command switch
        {
            DeviceCommand.TurnOff => $"Turn off {DeviceLabel(a.DeviceId, deviceName)}",
            DeviceCommand.Toggle => $"Toggle {DeviceLabel(a.DeviceId, deviceName)}",
            _ => $"Turn on {DeviceLabel(a.DeviceId, deviceName)}",
        },
        AllOffActionNode => "Turn every device off",
        DelayActionNode d => $"Wait {Duration(TimeSpan.FromSeconds(d.Seconds))}",
        NotifyActionNode n => string.IsNullOrWhiteSpace(n.Message) ? "Show a notification" : $"Notify “{n.Message.Trim()}”",
        PcPowerActionNode p => p.Command switch
        {
            PcPowerCommand.Sleep => "Put the PC to sleep",
            PcPowerCommand.ShutDown => "Shut down the PC (1-minute warning)",
            PcPowerCommand.Restart => "Restart the PC (1-minute warning)",
            PcPowerCommand.DisplayOff => "Turn the display off",
            _ => "Lock the PC",
        },
        _ => Title(node),
    };

    public static string PcEventText(PcEvent pcEvent, int idleMinutes) => pcEvent switch
    {
        PcEvent.Started => "When Home Control starts (e.g. after signing in)",
        PcEvent.Locked => "When the PC is locked",
        PcEvent.Unlocked => "When the PC is unlocked",
        PcEvent.Sleeping => "When the PC goes to sleep",
        PcEvent.Resumed => "When the PC wakes up",
        PcEvent.Idle => $"When the PC has been idle for {Duration(TimeSpan.FromMinutes(idleMinutes))}",
        PcEvent.Active => $"When you're back after {Duration(TimeSpan.FromMinutes(idleMinutes))} away",
        PcEvent.DisplayOff => "When the display turns off",
        PcEvent.DisplayOn => "When the display turns on",
        PcEvent.OnBattery => "When the PC switches to battery",
        PcEvent.PluggedIn => "When the PC is plugged in",
        _ => pcEvent.ToString(),
    };

    public static string PcStateText(PcState state, int idleMinutes) => state switch
    {
        PcState.Locked => "The PC is locked",
        PcState.Unlocked => "The PC is unlocked",
        PcState.Idle => $"The PC has been idle for {Duration(TimeSpan.FromMinutes(idleMinutes))}",
        PcState.InUse => $"The PC was used in the last {Duration(TimeSpan.FromMinutes(idleMinutes))}",
        PcState.OnBattery => "The PC is on battery",
        PcState.PluggedIn => "The PC is plugged in",
        PcState.DisplayOn => "The display is on",
        PcState.DisplayOff => "The display is off",
        _ => state.ToString(),
    };

    public static string ReferenceName(TimeReference reference) => reference switch
    {
        TimeReference.Clock => "time",
        TimeReference.Sunrise => "sunrise",
        TimeReference.Sunset => "sunset",
        TimeReference.Dawn => "dawn",
        TimeReference.Dusk => "dusk",
        TimeReference.NauticalDawn => "nautical dawn",
        TimeReference.NauticalDusk => "nautical dusk",
        TimeReference.AstronomicalDawn => "astronomical dawn",
        TimeReference.AstronomicalDusk => "astronomical dusk",
        TimeReference.SolarNoon => "solar noon",
        _ => reference.ToString(),
    };

    /// <summary>"18:00", "sunset", "15 min before sunset" (with <paramref name="atPrefix"/>: "at 18:00", "at sunset").</summary>
    public static string TimePoint(TimePoint point, bool atPrefix)
    {
        if (point.Reference == TimeReference.Clock)
        {
            var time = point.Time.ToString("t", CultureInfo.CurrentCulture);
            return atPrefix ? $"at {time}" : time;
        }

        var name = ReferenceName(point.Reference);
        if (point.OffsetMinutes == 0)
        {
            return atPrefix ? $"at {name}" : name;
        }

        var amount = Duration(TimeSpan.FromMinutes(Math.Abs(point.OffsetMinutes)));
        return $"{amount} {(point.OffsetMinutes < 0 ? "before" : "after")} {name}";
    }

    public static string Days(Weekdays days) => days switch
    {
        Weekdays.All => "every day",
        Weekdays.WorkDays => "weekdays",
        Weekdays.Weekend => "weekends",
        Weekdays.None => "no days",
        _ => string.Join(", ", Enum.GetValues<DayOfWeek>()
            .Select(d => (Day: d, Flag: TimeSchedule.ToWeekdays(d)))
            .OrderBy(d => ((int)d.Day + 6) % 7) // Monday first
            .Where(d => (days & d.Flag) != 0)
            .Select(d => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(d.Day))),
    };

    /// <summary>"45 s", "5 min", "1 h 30 min".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span.TotalMinutes < 1)
        {
            return $"{(int)span.TotalSeconds} s";
        }

        var hours = (int)span.TotalHours;
        var minutes = span.Minutes;
        var seconds = span.Seconds;
        var parts = new List<string>();
        if (hours > 0) parts.Add($"{hours} h");
        if (minutes > 0) parts.Add($"{minutes} min");
        if (seconds > 0 && hours == 0) parts.Add($"{seconds} s");
        return string.Join(" ", parts);
    }

    private static string DaysSuffix(Weekdays days) => days == Weekdays.All ? string.Empty : $", {Days(days)}";

    private static string DeviceLabel(string? deviceId, Func<string?, string?> deviceName) =>
        deviceName(deviceId) ?? (string.IsNullOrEmpty(deviceId) ? "(no device)" : "(missing device)");

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
}
