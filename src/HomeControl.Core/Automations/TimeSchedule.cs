namespace HomeControl.Core.Automations;

/// <summary>Turns <see cref="TimePoint"/>s into actual times on actual days.</summary>
public static class TimeSchedule
{
    /// <summary>
    /// The time point on a local calendar day: the clock time (moved past a daylight-saving gap),
    /// or the sun event plus the offset. Null when the sun event doesn't happen that day or no
    /// location is set.
    /// </summary>
    public static DateTimeOffset? Resolve(TimePoint point, DateOnly date, GeoLocation? location, TimeZoneInfo zone)
    {
        if (point.Reference == TimeReference.Clock)
        {
            return AtLocalTime(date.ToDateTime(point.Time), zone);
        }

        if (location is not { IsValid: true })
        {
            return null;
        }

        var sun = SolarCalculator.GetEvent(date, location, SolarCalculator.ToSunEvent(point.Reference), zone);
        return sun is { } value ? TimeZoneInfo.ConvertTime(value.AddMinutes(point.OffsetMinutes), zone) : null;
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

            if (Resolve(point, date, location, zone) is { } time && time > after)
            {
                return time;
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
            if (Includes(days, date) &&
                Resolve(point, date, location, zone) is { } time &&
                time > fromExclusive && time <= toInclusive)
            {
                yield return time;
            }
        }
    }

    /// <summary>
    /// Is <paramref name="now"/> between the last <paramref name="from"/> and the next <paramref name="to"/>?
    /// The window can wrap past midnight (e.g. sunset to sunrise). False when the times can't be
    /// worked out (sun events without a location, or on a day without them).
    /// </summary>
    public static bool IsWithin(DateTimeOffset now, TimePoint from, TimePoint to, GeoLocation? location, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        DateTimeOffset? lastFrom = null;
        for (var shift = -1; shift <= 0; shift++)
        {
            if (Resolve(from, today.AddDays(shift), location, zone) is { } time && time <= now && (lastFrom is null || time > lastFrom))
            {
                lastFrom = time;
            }
        }

        if (lastFrom is null)
        {
            return false;
        }

        for (var shift = -1; shift <= 1; shift++)
        {
            if (Resolve(to, today.AddDays(shift), location, zone) is { } end && end > lastFrom)
            {
                return now < end;
            }
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
