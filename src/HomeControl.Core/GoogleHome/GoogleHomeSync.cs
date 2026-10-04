using HomeControl.Core.Models;

namespace HomeControl.Core.GoogleHome;

/// <summary>What a sync changed.</summary>
public sealed record GoogleHomeSyncResult(int Added, int Updated, int Missing, int Linked)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Added > 0) parts.Add($"{Added} added");
        if (Linked > 0) parts.Add($"{Linked} linked");
        if (Updated > 0) parts.Add($"{Updated} updated");
        if (Missing > 0) parts.Add($"{Missing} no longer in Google Home");
        return parts.Count == 0 ? "No changes" : string.Join(", ", parts);
    }
}

/// <summary>Merges the devices discovered in Google Home into the configured device list.</summary>
public static class GoogleHomeSync
{
    /// <summary>
    /// Adds new on/off devices, refreshes names, rooms and ids of known ones, links devices
    /// that were added by name for Google Assistant, and flags devices that disappeared.
    /// The user's own choices (label, icon, shortcut, hidden, order) are kept.
    /// </summary>
    public static GoogleHomeSyncResult Merge(List<DeviceConfig> devices, GoogleHomeGraph graph)
    {
        int added = 0, updated = 0, linked = 0;
        var discovered = graph.Devices
            .Where(d => d.SupportsOnOff)
            .OrderBy(d => d.Room ?? "￿", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var found in discovered)
        {
            seen.Add(found.Id);
            var existing = devices.FirstOrDefault(d => d.Source == DeviceSource.GoogleHome && d.GoogleHomeId == found.Id);

            if (existing is null)
            {
                // A device the user added by name for Google Assistant: link it instead of duplicating it.
                existing = devices.FirstOrDefault(d =>
                    d.Source == DeviceSource.Assistant &&
                    string.Equals(d.Name.Trim(), found.Name, StringComparison.CurrentCultureIgnoreCase));
                if (existing is not null)
                {
                    existing.Source = DeviceSource.GoogleHome;
                    Apply(existing, found);
                    linked++;
                    continue;
                }

                var device = new DeviceConfig
                {
                    Source = DeviceSource.GoogleHome,
                    Kind = GoogleHomeTypes.ToKind(found.EffectiveType),
                };
                Apply(device, found);
                devices.Add(device);
                added++;
                continue;
            }

            if (Apply(existing, found))
            {
                updated++;
            }
        }

        var missing = 0;
        foreach (var device in devices.Where(d => d.Source == DeviceSource.GoogleHome))
        {
            var isMissing = device.GoogleHomeId is null || !seen.Contains(device.GoogleHomeId);
            if (isMissing)
            {
                missing++;
            }

            device.Missing = isMissing;
        }

        return new GoogleHomeSyncResult(added, updated, missing, linked);
    }

    /// <summary>Copies Google's data onto a configured device; returns true if anything changed.</summary>
    private static bool Apply(DeviceConfig device, GoogleHomeDevice found)
    {
        var changed =
            device.GoogleHomeId != found.Id ||
            device.Name != found.Name ||
            device.AgentId != found.AgentId ||
            device.PartnerDeviceId != found.PartnerDeviceId ||
            device.Room != found.Room ||
            device.GoogleHomeType != found.EffectiveType ||
            device.Missing;

        device.GoogleHomeId = found.Id;
        device.Name = found.Name;
        device.AgentId = found.AgentId;
        device.PartnerDeviceId = found.PartnerDeviceId;
        device.Room = found.Room;
        device.GoogleHomeType = found.EffectiveType;
        device.Missing = false;
        return changed;
    }
}

/// <summary>Maps Google Home device types to the app's icons.</summary>
public static class GoogleHomeTypes
{
    public static DeviceKind ToKind(string? type)
    {
        var name = type?.Split('.').LastOrDefault()?.ToUpperInvariant() ?? string.Empty;
        return name switch
        {
            "LIGHT" or "LAMP" => DeviceKind.Light,
            "OUTLET" or "SMARTPLUG" => DeviceKind.Outlet,
            "SWITCH" => DeviceKind.Switch,
            "TV" or "SETTOP" or "STREAMING_BOX" or "STREAMING_STICK" or "STREAMING_SOUNDBAR" or "REMOTECONTROL" => DeviceKind.Tv,
            "SPEAKER" or "SOUNDBAR" or "AUDIO_VIDEO_RECEIVER" => DeviceKind.Speaker,
            "FAN" or "AC_UNIT" or "AIRPURIFIER" or "AIRCOOLER" or "DEHUMIDIFIER" or "HUMIDIFIER" => DeviceKind.Climate,
            "HEATER" or "THERMOSTAT" or "RADIATOR" or "BOILER" or "WATERHEATER" => DeviceKind.Heater,
            "COFFEE_MAKER" or "KETTLE" => DeviceKind.Coffee,
            "SCENE" => DeviceKind.Scene,
            _ => DeviceKind.Other,
        };
    }
}
