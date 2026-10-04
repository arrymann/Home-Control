using System.Text.Json.Serialization;
using HomeControl.Core.Hotkeys;

namespace HomeControl.Core.Models;

/// <summary>
/// A device as configured by the user. Devices are addressed by the exact name they have
/// in the Google Home app; the optional command overrides cover devices or scenes that
/// need a different phrase.
/// </summary>
public sealed class DeviceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Name of the device in the Google Home app, e.g. "Kitchen light".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional label shown in the tray popup instead of <see cref="Name"/>.</summary>
    public string? DisplayName { get; set; }

    public DeviceKind Kind { get; set; } = DeviceKind.Light;

    /// <summary>Global shortcut that toggles the device.</summary>
    public Hotkey? Hotkey { get; set; }

    /// <summary>Phrase that turns the device on. Defaults to the Assistant settings template.</summary>
    public string? OnCommand { get; set; }

    /// <summary>Phrase that turns the device off. Defaults to the Assistant settings template.</summary>
    public string? OffCommand { get; set; }

    /// <summary>Question used to read the device state. Defaults to the Assistant settings template.</summary>
    public string? StateQuery { get; set; }

    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName!;

    public DeviceConfig Clone() => (DeviceConfig)MemberwiseClone();
}
