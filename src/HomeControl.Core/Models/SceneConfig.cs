using HomeControl.Core.Hotkeys;

namespace HomeControl.Core.Models;

/// <summary>
/// A scene: devices grouped under one toggle (Settings › Scenes). Turning it on turns all of
/// them on, turning it off turns them all off.
/// </summary>
public sealed class SceneConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>The icon.</summary>
    public DeviceKind Kind { get; set; } = DeviceKind.Scene;

    /// <summary>The devices it switches (<see cref="DeviceConfig.Id"/>s), in order.</summary>
    public List<string> DeviceIds { get; set; } = [];

    /// <summary>Shown in the tray flyout. The shortcut works either way.</summary>
    public bool ShowInTray { get; set; } = true;

    /// <summary>Global shortcut that toggles the scene.</summary>
    public Hotkey? Hotkey { get; set; }

    public SceneConfig Clone()
    {
        var copy = (SceneConfig)MemberwiseClone();
        copy.DeviceIds = [.. DeviceIds];
        return copy;
    }

    /// <summary>
    /// A scene is on while any of its devices is on (as a room is in Google Home), off when all
    /// the devices it knows about are off, and unknown when no device state is known.
    /// </summary>
    public static bool? StateOf(IEnumerable<bool?> deviceStates)
    {
        var known = deviceStates.Where(s => s.HasValue).ToList();
        return known.Count == 0 ? null : known.Any(s => s == true);
    }
}
