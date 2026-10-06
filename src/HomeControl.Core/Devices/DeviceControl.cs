using HomeControl.Core.Models;

namespace HomeControl.Core.Devices;

/// <summary>Outcome of a device command or state query.</summary>
/// <param name="Success">False when the request failed or the service said it could not do it.</param>
/// <param name="IsOn">The device state after the call, when known.</param>
/// <param name="Message">A reply or an error description.</param>
public sealed record DeviceCommandResult(bool Success, bool? IsOn, string Message)
{
    /// <summary>
    /// The device reported the requested state. A slow device (a TV, say) can accept a command
    /// and still report its old state, or none, for a while.
    /// </summary>
    public bool Confirms(bool turnOn) => Success && IsOn == turnOn;
}

/// <summary>Live state of a device as reported by its service.</summary>
/// <param name="IsOn">On/off, or null when unknown.</param>
/// <param name="Online">False when the service reports the device as unreachable.</param>
public sealed record DeviceStatus(bool? IsOn, bool Online);

/// <summary>Turns devices on and off and reads their state.</summary>
public interface IDeviceController
{
    Task<DeviceCommandResult> SetPowerAsync(DeviceConfig device, bool turnOn, CancellationToken cancellationToken);

    Task<DeviceCommandResult> QueryPowerAsync(DeviceConfig device, CancellationToken cancellationToken);
}

/// <summary>A controller that can read many devices' states in one cheap request.</summary>
public interface IBatchStateReader
{
    /// <summary>True when <paramref name="device"/> can be read by <see cref="ReadStatesAsync"/>.</summary>
    bool CanReadInBatch(DeviceConfig device);

    /// <summary>Reads the states of the given devices, keyed by <see cref="DeviceConfig.Id"/>.</summary>
    Task<IReadOnlyDictionary<string, DeviceStatus>> ReadStatesAsync(IReadOnlyList<DeviceConfig> devices, CancellationToken cancellationToken);
}
