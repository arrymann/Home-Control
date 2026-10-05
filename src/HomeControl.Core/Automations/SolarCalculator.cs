namespace HomeControl.Core.Automations;

/// <summary>Sun events a <see cref="TimePoint"/> can refer to.</summary>
public enum SunEvent
{
    Sunrise,
    Sunset,
    CivilDawn,
    CivilDusk,
    NauticalDawn,
    NauticalDusk,
    AstronomicalDawn,
    AstronomicalDusk,
    SolarNoon,
}

/// <summary>
/// Sunrise, sunset, twilight and solar noon, using NOAA's solar calculator equations
/// (accurate to about a minute between the polar circles). Events that don't happen on a
/// day (midnight sun, polar night) are null.
/// </summary>
public static class SolarCalculator
{
    /// <summary>Sun 50' below the horizon: its upper edge, with atmospheric refraction.</summary>
    private const double SunriseZenith = 90.833;
    private const double CivilZenith = 96;
    private const double NauticalZenith = 102;
    private const double AstronomicalZenith = 108;

    /// <summary>The event on a local calendar day in the given time zone (the first, if there are two).</summary>
    public static DateTimeOffset? GetEvent(DateOnly localDate, GeoLocation location, SunEvent sunEvent, TimeZoneInfo zone) =>
        GetEvents(localDate, location, sunEvent, zone).Select(e => (DateTimeOffset?)e).FirstOrDefault();

    /// <summary>
    /// Every time the event happens on a local calendar day. Usually one; none on polar days or
    /// nights; rarely two, when a twilight time drifts across midnight.
    /// </summary>
    public static IEnumerable<DateTimeOffset> GetEvents(DateOnly localDate, GeoLocation location, SunEvent sunEvent, TimeZoneInfo zone)
    {
        // The event of a UTC day can fall on the local day before or after; try all three.
        for (var shift = -1; shift <= 1; shift++)
        {
            var utcDate = localDate.AddDays(shift);
            if (GetEventUtcMinutes(utcDate, location.Latitude, location.Longitude, sunEvent) is not { } minutes)
            {
                continue;
            }

            var utc = new DateTimeOffset(utcDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddMinutes(minutes);
            var local = TimeZoneInfo.ConvertTime(utc, zone);
            if (DateOnly.FromDateTime(local.DateTime) == localDate)
            {
                yield return local;
            }
        }
    }

    /// <summary>The sun's altitude above the horizon in degrees (without refraction) at a moment.</summary>
    public static double ElevationDegrees(DateTimeOffset time, GeoLocation location)
    {
        var utc = time.UtcDateTime;
        var minutes = utc.TimeOfDay.TotalMinutes;
        var t = JulianCentury(JulianDay(DateOnly.FromDateTime(utc)) + minutes / 1440.0);
        var declination = Radians(SunDeclination(t));
        var trueSolarMinutes = minutes + EquationOfTime(t) + 4 * location.Longitude;
        var hourAngle = Radians(trueSolarMinutes / 4 - 180);
        var lat = Radians(location.Latitude);
        var cosZenith = Math.Sin(lat) * Math.Sin(declination) + Math.Cos(lat) * Math.Cos(declination) * Math.Cos(hourAngle);
        return 90 - Degrees(Math.Acos(Math.Clamp(cosZenith, -1, 1)));
    }

    /// <summary>The sun's altitude (degrees) at which an event happens.</summary>
    public static double EventAltitude(SunEvent sunEvent) => sunEvent switch
    {
        SunEvent.Sunrise or SunEvent.Sunset => 90 - SunriseZenith,
        SunEvent.CivilDawn or SunEvent.CivilDusk => 90 - CivilZenith,
        SunEvent.NauticalDawn or SunEvent.NauticalDusk => 90 - NauticalZenith,
        SunEvent.AstronomicalDawn or SunEvent.AstronomicalDusk => 90 - AstronomicalZenith,
        _ => 0,
    };

    /// <summary>Events when the sun goes down past an altitude (as opposed to coming up).</summary>
    public static bool IsEvening(SunEvent sunEvent) =>
        sunEvent is SunEvent.Sunset or SunEvent.CivilDusk or SunEvent.NauticalDusk or SunEvent.AstronomicalDusk;

    public static bool IsMorning(SunEvent sunEvent) =>
        sunEvent is SunEvent.Sunrise or SunEvent.CivilDawn or SunEvent.NauticalDawn or SunEvent.AstronomicalDawn;

    public static SunEvent ToSunEvent(TimeReference reference) => reference switch
    {
        TimeReference.Sunrise => SunEvent.Sunrise,
        TimeReference.Sunset => SunEvent.Sunset,
        TimeReference.Dawn => SunEvent.CivilDawn,
        TimeReference.Dusk => SunEvent.CivilDusk,
        TimeReference.NauticalDawn => SunEvent.NauticalDawn,
        TimeReference.NauticalDusk => SunEvent.NauticalDusk,
        TimeReference.AstronomicalDawn => SunEvent.AstronomicalDawn,
        TimeReference.AstronomicalDusk => SunEvent.AstronomicalDusk,
        TimeReference.SolarNoon => SunEvent.SolarNoon,
        _ => throw new ArgumentOutOfRangeException(nameof(reference), reference, "Not a sun event."),
    };

    /// <summary>Minutes after 00:00 UTC of <paramref name="utcDate"/> (may be negative or past 1440), or null.</summary>
    internal static double? GetEventUtcMinutes(DateOnly utcDate, double latitude, double longitude, SunEvent sunEvent)
    {
        var julianDay = JulianDay(utcDate);
        if (sunEvent == SunEvent.SolarNoon)
        {
            // Refine once with the equation of time at the estimated noon.
            var estimate = SolarNoonUtc(julianDay - longitude / 360.0, longitude);
            return SolarNoonUtc(julianDay + estimate / 1440.0, longitude);
        }

        var (zenith, rising) = sunEvent switch
        {
            SunEvent.Sunrise => (SunriseZenith, true),
            SunEvent.Sunset => (SunriseZenith, false),
            SunEvent.CivilDawn => (CivilZenith, true),
            SunEvent.CivilDusk => (CivilZenith, false),
            SunEvent.NauticalDawn => (NauticalZenith, true),
            SunEvent.NauticalDusk => (NauticalZenith, false),
            SunEvent.AstronomicalDawn => (AstronomicalZenith, true),
            SunEvent.AstronomicalDusk => (AstronomicalZenith, false),
            _ => throw new ArgumentOutOfRangeException(nameof(sunEvent)),
        };

        // First estimate from the sun's position at noon, then recompute at the estimated time.
        var first = RiseSetUtc(julianDay + (720 - 4 * longitude) / 1440.0, latitude, longitude, zenith, rising);
        if (first is null)
        {
            return null;
        }

        return RiseSetUtc(julianDay + first.Value / 1440.0, latitude, longitude, zenith, rising) ?? first;
    }

    /// <summary>NOAA's sunrise/sunset formula for the sun's position at Julian day <paramref name="jd"/>.</summary>
    private static double? RiseSetUtc(double jd, double latitude, double longitude, double zenith, bool rising)
    {
        var t = JulianCentury(jd);
        var equationOfTime = EquationOfTime(t);
        var declination = Radians(SunDeclination(t));
        var lat = Radians(latitude);

        var cosHourAngle = Math.Cos(Radians(zenith)) / (Math.Cos(lat) * Math.Cos(declination)) - Math.Tan(lat) * Math.Tan(declination);
        if (cosHourAngle is < -1 or > 1)
        {
            return null; // the sun never reaches this altitude today
        }

        var hourAngle = Degrees(Math.Acos(cosHourAngle));
        if (!rising)
        {
            hourAngle = -hourAngle;
        }

        // Minutes after 00:00 UTC of the date being calculated (jd only sets the sun's position).
        return 720 - 4 * (longitude + hourAngle) - equationOfTime;
    }

    private static double SolarNoonUtc(double jd, double longitude) =>
        720 - 4 * longitude - EquationOfTime(JulianCentury(jd));

    /// <summary>Julian day at 00:00 UTC.</summary>
    private static double JulianDay(DateOnly date)
    {
        var year = date.Year;
        var month = date.Month;
        if (month <= 2)
        {
            year -= 1;
            month += 12;
        }

        var a = Math.Floor(year / 100.0);
        var b = 2 - a + Math.Floor(a / 4);
        return Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + date.Day + b - 1524.5;
    }

    private static double JulianCentury(double jd) => (jd - 2451545.0) / 36525.0;

    private static double GeomMeanLongSun(double t)
    {
        var l0 = 280.46646 + t * (36000.76983 + t * 0.0003032);
        l0 %= 360;
        return l0 < 0 ? l0 + 360 : l0;
    }

    private static double GeomMeanAnomalySun(double t) => 357.52911 + t * (35999.05029 - 0.0001537 * t);

    private static double EccentricityEarthOrbit(double t) => 0.016708634 - t * (0.000042037 + 0.0000001267 * t);

    private static double SunEquationOfCenter(double t)
    {
        var m = Radians(GeomMeanAnomalySun(t));
        return Math.Sin(m) * (1.914602 - t * (0.004817 + 0.000014 * t))
               + Math.Sin(2 * m) * (0.019993 - 0.000101 * t)
               + Math.Sin(3 * m) * 0.000289;
    }

    private static double SunApparentLong(double t)
    {
        var trueLong = GeomMeanLongSun(t) + SunEquationOfCenter(t);
        var omega = 125.04 - 1934.136 * t;
        return trueLong - 0.00569 - 0.00478 * Math.Sin(Radians(omega));
    }

    private static double ObliquityCorrection(double t)
    {
        var seconds = 21.448 - t * (46.8150 + t * (0.00059 - t * 0.001813));
        var meanObliquity = 23.0 + (26.0 + seconds / 60.0) / 60.0;
        var omega = 125.04 - 1934.136 * t;
        return meanObliquity + 0.00256 * Math.Cos(Radians(omega));
    }

    private static double SunDeclination(double t) =>
        Degrees(Math.Asin(Math.Sin(Radians(ObliquityCorrection(t))) * Math.Sin(Radians(SunApparentLong(t)))));

    /// <summary>Equation of time in minutes.</summary>
    private static double EquationOfTime(double t)
    {
        var epsilon = Radians(ObliquityCorrection(t));
        var l0 = Radians(GeomMeanLongSun(t));
        var e = EccentricityEarthOrbit(t);
        var m = Radians(GeomMeanAnomalySun(t));

        var y = Math.Tan(epsilon / 2);
        y *= y;

        var value = y * Math.Sin(2 * l0)
                    - 2 * e * Math.Sin(m)
                    + 4 * e * y * Math.Sin(m) * Math.Cos(2 * l0)
                    - 0.5 * y * y * Math.Sin(4 * l0)
                    - 1.25 * e * e * Math.Sin(2 * m);
        return Degrees(value) * 4;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180.0;

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;
}
