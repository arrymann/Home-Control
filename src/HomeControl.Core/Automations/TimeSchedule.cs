namespace HomeControl.Core.Automations;

/// <summary>Turns <see cref="TimePoint"/>s into actual times on actual days.</summary>
public static class TimeSchedule
{
    /// <summary>
    /// The time point on a local calendar day: the clock time (moved past a daylight-saving gap),
    /// or the sun event plus the offset. Null when the sun event doesn't happen that day or no
    /// location is set.
    /// </summary>
    public static DateTimeOffset? Resolve(TimePoint point, DateOnly date, GeoLocation? location, TimeZoneInfo zone) =>
        ResolveAll(point, date, location, zone).Select(t => (DateTimeOffset?)t).FirstOrDefault();

    /// <summary>Like <see cref="Resolve"/>, but a sun event can happen twice on one local day (twilight near midnight).</summary>
    public static IEnumerable<DateTimeOffset> ResolveAll(TimePoint point, DateOnly date, GeoLocation? location, TimeZoneInfo zone)
    {
        if (point.Reference == TimeReference.Clock)
        {
            return [AtLocalTime(date.ToDateTime(point.Time), zone)];
        }

        if (location is not { IsValid: true })
        {
            return [];
        }

        return SolarCalculator.GetEvents(date, location, SolarCalculator.ToSunEvent(point.Reference), zone)
            .Select(sun => TimeZoneInfo.ConvertTime(sun.AddMinutes(point.OffsetMinutes), zone));
    }

    /// <summary>The first time after <paramref name="after"/> on one of <paramref name="days"/>, or null.</summary>
    public static DateTimeOffset? NextOccurrence(
        TimePoint point, Weekdays days, DateTimeOffset after, GeoLocation? location, TimeZoneInfo zone, int maxDays = 400)
    {
        if (days == Weekdays.None)
        {
            return null;
        }

        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime).AddDays(-1);
        for (var i = 0; i <= maxDays; i++)
        {
            var date = start.AddDays(i);
            if (!Includes(days, date))
            {
                continue;
            }

            foreach (var time in ResolveAll(point, date, location, zone).OrderBy(t => t))
            {
                if (time > after)
                {
                    return time;
                }
            }
        }

        return null;
    }

    /// <summary>Occurrences with <paramref name="fromExclusive"/> &lt; time &lt;= <paramref name="toInclusive"/>.</summary>
    public static IEnumerable<DateTimeOffset> OccurrencesBetween(
        TimePoint point, Weekdays days, DateTimeOffset fromExclusive, DateTimeOffset toInclusive, GeoLocation? location, TimeZoneInfo zone)
    {
        if (toInclusive <= fromExclusive)
        {
            yield break;
        }

        // Offsets of up to a day can move an occurrence onto the neighbouring date.
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fromExclusive, zone).DateTime).AddDays(-1);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(toInclusive, zone).DateTime).AddDays(1);
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            if (!Includes(days, date))
            {
                continue;
            }

            foreach (var time in ResolveAll(point, date, location, zone))
            {
                if (time > fromExclusive && time <= toInclusive)
                {
                    yield return time;
                }
            }
        }
    }

    /// <summary>
    /// Is <paramref name="now"/> between the last <paramref name="from"/> and the next <paramref name="to"/>?
    /// The window can wrap past midnight (e.g. sunset to sunrise). When sun events don't happen
    /// (polar day or night), a sunset-to-sunrise style window is decided by where the sun is.
    /// False when the times can't be worked out otherwise (e.g. sun events without a location).
    /// </summary>
    public static bool IsWithin(DateTimeOffset now, TimePoint from, TimePoint to, GeoLocation? location, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        // A negative offset can move tomorrow's event into today, so look one day ahead too.
        var lastFrom = Enumerable.Range(-1, 3)
            .SelectMany(shift => ResolveAll(from, today.AddDays(shift), location, zone))
            .Where(time => time <= now)
            .Select(time => (DateTimeOffset?)time)
            .Max();

        if (lastFrom is { } start)
        {
            var end = Enumerable.Range(-1, 4)
                .SelectMany(shift => ResolveAll(to, today.AddDays(shift), location, zone))
                .Where(time => time > start)
                .Select(time => (DateTimeOffset?)time)
                .Min();
            if (end is { } stop)
            {
                return now < stop;
            }
        }

        return BySunPosition(now, from, to, location);
    }

    /// <summary>For windows between an evening and a morning sun event (or the reverse) when one of them doesn't happen.</summary>
    private static bool BySunPosition(DateTimeOffset now, TimePoint from, TimePoint to, GeoLocation? location)
    {
        if (!from.IsSolar || !to.IsSolar || location is not { IsValid: true })
        {
            return false;
        }

        var start = SolarCalculator.ToSunEvent(from.Reference);
        var end = SolarCalculator.ToSunEvent(to.Reference);
        var elevation = SolarCalculator.ElevationDegrees(now, location);
        if (SolarCalculator.IsEvening(start) && SolarCalculator.IsMorning(end))
        {
            return elevation < SolarCalculator.EventAltitude(start); // e.g. polar night: dark all day
        }

        if (SolarCalculator.IsMorning(start) && SolarCalculator.IsEvening(end))
        {
            return elevation > SolarCalculator.EventAltitude(start); // e.g. midnight sun
        }

        return false;
    }

    public static Weekdays ToWeekdays(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Weekdays.Monday,
        DayOfWeek.Tuesday => Weekdays.Tuesday,
        DayOfWeek.Wednesday => Weekdays.Wednesday,
        DayOfWeek.Thursday => Weekdays.Thursday,
        DayOfWeek.Friday => Weekdays.Friday,
        DayOfWeek.Saturday => Weekdays.Saturday,
        _ => Weekdays.Sunday,
    };

    public static bool Includes(Weekdays days, DateOnly date) => (days & ToWeekdays(date.DayOfWeek)) != 0;

    /// <summary>A local wall-clock time as a moment: times skipped by a DST change move forward, repeated times take the first.</summary>
    private static DateTimeOffset AtLocalTime(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var guard = 0;
        while (zone.IsInvalidTime(local) && guard++ < 24 * 4)
        {
            local = local.AddMinutes(15);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
