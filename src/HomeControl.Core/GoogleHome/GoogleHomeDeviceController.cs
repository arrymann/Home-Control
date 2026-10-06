using HomeControl.Core.Devices;
using HomeControl.Core.Models;

namespace HomeControl.Core.GoogleHome;

/// <summary>Switches and reads Google Home devices through <see cref="GoogleHomeClient"/>.</summary>
public sealed class GoogleHomeDeviceController : IDeviceController, IBatchStateReader
{
    private readonly GoogleHomeClient _client;

    public GoogleHomeDeviceController(GoogleHomeClient client)
    {
        _client = client;
    }

    public async Task<DeviceCommandResult> SetPowerAsync(DeviceConfig device, bool turnOn, CancellationToken cancellationToken)
    {
        var id = RequireId(device);
        var state = await _client.SetOnOffAsync(id, device.AgentId, device.PartnerDeviceId, turnOn, cancellationToken)
            .ConfigureAwait(false);

        // An offline device's on/off value is only Google's last record (a TV that dropped off the
        // network in standby can still say "on"), so it counts as unknown.
        if (state?.Error is { } error)
        {
            return new DeviceCommandResult(false, error is "deviceOffline" or "offline" ? null : state.IsOn, DescribeError(error));
        }

        if (state is { Online: false })
        {
            return new DeviceCommandResult(false, null, $"{device.Label.Trim()} is offline.");
        }

        // What the device reports right after the command. Slow devices may still report their
        // old state (or none): IsOn then isn't the requested state and callers wait for it.
        var isOn = state?.IsOn;
        var name = device.Label.Trim();
        return new DeviceCommandResult(true, isOn, isOn == turnOn
            ? $"{name} turned {(turnOn ? "on" : "off")}."
            : $"Sent “turn {(turnOn ? "on" : "off")}” to {name}.");
    }

    public async Task<DeviceCommandResult> QueryPowerAsync(DeviceConfig device, CancellationToken cancellationToken)
    {
        var id = RequireId(device);
        var states = await _client.GetStatesAsync([id], cancellationToken).ConfigureAwait(false);
        var state = states.FirstOrDefault(s => s.Id == id);
        if (state is null)
        {
            return new DeviceCommandResult(false, null, "Google Home didn't return this device. Sync devices in Settings.");
        }

        return state.Online == false
            ? new DeviceCommandResult(true, null, $"{device.Label.Trim()} is offline.")
            : new DeviceCommandResult(true, state.IsOn, state.IsOn switch { true => "On", false => "Off", null => "Unknown" });
    }

    public bool CanReadInBatch(DeviceConfig device) => device.IsGoogleHome;

    public async Task<IReadOnlyDictionary<string, DeviceStatus>> ReadStatesAsync(IReadOnlyList<DeviceConfig> devices, CancellationToken cancellationToken)
    {
        var byGoogleId = devices.Where(d => d.IsGoogleHome)
            .GroupBy(d => d.GoogleHomeId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (byGoogleId.Count == 0)
        {
            return new Dictionary<string, DeviceStatus>();
        }

        var states = await _client.GetStatesAsync(byGoogleId.Keys, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, DeviceStatus>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            if (byGoogleId.TryGetValue(state.Id, out var configs))
            {
                foreach (var config in configs)
                {
                    var online = state.Online != false;
                    result[config.Id] = new DeviceStatus(online ? state.IsOn : null, online); // offline: only a stale record
                }
            }
        }

        return result;
    }

    internal static string DescribeError(string error) => error switch
    {
        "deviceOffline" or "offline" => "The device is offline.",
        "deviceTurnedOff" => "The device is turned off at the power source.",
        "deviceNotFound" or "relinkRequired" => "Google Home can't reach this device. Check it in the Google Home app.",
        "pinNeeded" or "challengeNeeded" or "ackNeeded" => "Google Home asks for a confirmation for this device. Use the Google Home app.",
        "transientError" => "Google Home had a temporary problem. Try again.",
        _ => $"Google Home reported \"{error}\".",
    };

    private static string RequireId(DeviceConfig device) =>
        device.IsGoogleHome ? device.GoogleHomeId! : throw new ArgumentException($"{device.Label} is not a Google Home device.", nameof(device));
}
