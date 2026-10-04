using System.Text.Json.Serialization;
using HomeControl.Core.Hotkeys;

namespace HomeControl.Core.Models;

/// <summary>Which service switches a device.</summary>
public enum DeviceSource
{
    /// <summary>Added by name; switched with Google Assistant text commands.</summary>
    Assistant,

    /// <summary>Discovered in Google Home; switched through the home.google.com session.</summary>
    GoogleHome,
}

/// <summary>
/// A device shown in the tray. Google Home devices are discovered automatically and keep
/// their Google ids; Assistant devices are added by the exact name they have in the Google
/// Home app, with optional command overrides for devices or scenes that need another phrase.
/// </summary>
public sealed class DeviceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DeviceSource Source { get; set; } = DeviceSource.Assistant;

    /// <summary>Name of the device in the Google Home app, e.g. "Kitchen light".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional label shown in the tray popup instead of <see cref="Name"/>.</summary>
    public string? DisplayName { get; set; }

    public DeviceKind Kind { get; set; } = DeviceKind.Light;

    /// <summary>Global shortcut that toggles the device.</summary>
    public Hotkey? Hotkey { get; set; }

    /// <summary>Not shown in the tray popup (discovered devices the user doesn't want there).</summary>
    public bool Hidden { get; set; }

    /// <summary>Phrase that turns the device on. Defaults to the Assistant settings template.</summary>
    public string? OnCommand { get; set; }

    /// <summary>Phrase that turns the device off. Defaults to the Assistant settings template.</summary>
    public string? OffCommand { get; set; }

    /// <summary>Question used to read the device state. Defaults to the Assistant settings template.</summary>
    public string? StateQuery { get; set; }

    /// <summary>Google Home device id (a UUID), for <see cref="DeviceSource.GoogleHome"/> devices.</summary>
    public string? GoogleHomeId { get; set; }

    /// <summary>Id of the partner integration (e.g. the plug maker's cloud) that owns the device.</summary>
    public string? AgentId { get; set; }

    /// <summary>The partner integration's own id for the device.</summary>
    public string? PartnerDeviceId { get; set; }

    /// <summary>Room in Google Home, if any.</summary>
    public string? Room { get; set; }

    /// <summary>Google Home device type, e.g. "action.devices.types.OUTLET".</summary>
    public string? GoogleHomeType { get; set; }

    /// <summary>The device disappeared from Google Home at the last sync.</summary>
    public bool Missing { get; set; }

    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName!;

    [JsonIgnore]
    public bool IsGoogleHome => Source == DeviceSource.GoogleHome && !string.IsNullOrEmpty(GoogleHomeId);

    public DeviceConfig Clone() => (DeviceConfig)MemberwiseClone();
}
