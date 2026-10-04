using HomeControl.Core.GoogleHome;
using HomeControl.Core.Models;

namespace HomeControl.Core.Devices;

/// <summary>
/// Routes each device to the service that switches it: Google Home devices through the
/// Google Home web session, everything else through Google Assistant. When the Google Home
/// session fails (signed out, Google changed something, no connection), the command is
/// retried through Assistant by device name if Assistant is set up and the fallback is on.
/// While the user is signed out of Google Home on purpose, Google Home devices go straight
/// to Assistant.
/// </summary>
public sealed class CompositeDeviceController : IDeviceController, IBatchStateReader
{
    private readonly GoogleHomeDeviceController _googleHome;
    private readonly IDeviceController _assistant;
    private readonly Func<bool> _assistantFallbackAvailable;
    private readonly Func<bool> _googleHomeEnabled;

    /// <param name="assistantFallbackAvailable">True when Assistant is signed in and the fallback is enabled.</param>
    /// <param name="googleHomeEnabled">False while the user is signed out of Google Home (default: always true).</param>
    public CompositeDeviceController(
        GoogleHomeDeviceController googleHome,
        IDeviceController assistant,
        Func<bool> assistantFallbackAvailable,
        Func<bool>? googleHomeEnabled = null)
    {
        _googleHome = googleHome;
        _assistant = assistant;
        _assistantFallbackAvailable = assistantFallbackAvailable;
        _googleHomeEnabled = googleHomeEnabled ?? (() => true);
    }

    public async Task<DeviceCommandResult> SetPowerAsync(DeviceConfig device, bool turnOn, CancellationToken cancellationToken)
    {
        if (!UsesGoogleHome(device))
        {
            return await _assistant.SetPowerAsync(device, turnOn, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await _googleHome.SetPowerAsync(device, turnOn, cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleHomeException ex) when (CanFallBack(device))
        {
            var result = await _assistant.SetPowerAsync(device, turnOn, cancellationToken).ConfigureAwait(false);
            return result with { Message = $"{result.Message} (via Google Assistant: {ex.Message})" };
        }
    }

    public async Task<DeviceCommandResult> QueryPowerAsync(DeviceConfig device, CancellationToken cancellationToken)
    {
        if (!UsesGoogleHome(device))
        {
            return await _assistant.QueryPowerAsync(device, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await _googleHome.QueryPowerAsync(device, cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleHomeException) when (CanFallBack(device))
        {
            return await _assistant.QueryPowerAsync(device, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool CanReadInBatch(DeviceConfig device) => UsesGoogleHome(device) && _googleHome.CanReadInBatch(device);

    public Task<IReadOnlyDictionary<string, DeviceStatus>> ReadStatesAsync(IReadOnlyList<DeviceConfig> devices, CancellationToken cancellationToken) =>
        _googleHome.ReadStatesAsync(devices, cancellationToken);

    private bool UsesGoogleHome(DeviceConfig device) => device.IsGoogleHome && _googleHomeEnabled();

    private bool CanFallBack(DeviceConfig device) =>
        !string.IsNullOrWhiteSpace(device.Name) && _assistantFallbackAvailable();
}
