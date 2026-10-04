using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using HomeControl.Core.Models;
using HomeControl.Core.Settings;

namespace HomeControl.Core.Assistant;

/// <summary>Outcome of a device command or state query.</summary>
/// <param name="Success">False when the request failed or the Assistant said it could not do it.</param>
/// <param name="IsOn">The device state after the call, when known.</param>
/// <param name="Message">The Assistant's answer or an error description.</param>
public sealed record DeviceCommandResult(bool Success, bool? IsOn, string Message);

/// <summary>Turns devices on and off and reads their state.</summary>
public interface IDeviceController
{
    Task<DeviceCommandResult> SetPowerAsync(DeviceConfig device, bool turnOn, CancellationToken cancellationToken);

    Task<DeviceCommandResult> QueryPowerAsync(DeviceConfig device, CancellationToken cancellationToken);
}

/// <summary>
/// Controls devices through Google Assistant text commands ("turn on Kitchen light") and
/// interprets the answers with configurable regular expressions.
/// </summary>
public sealed class AssistantDeviceController : IDeviceController
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new();

    private readonly IAssistantClient _client;
    private readonly Func<AssistantSettings> _settings;

    public AssistantDeviceController(IAssistantClient client, Func<AssistantSettings> settings)
    {
        _client = client;
        _settings = settings;
    }

    public async Task<DeviceCommandResult> SetPowerAsync(DeviceConfig device, bool turnOn, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var command = FormatCommand(device, turnOn, settings);
        var reply = await _client.SendTextQueryAsync(command, cancellationToken).ConfigureAwait(false);

        if (IsMatch(settings.FailurePattern, reply.Text))
        {
            return new DeviceCommandResult(false, null, reply.Text);
        }

        var message = string.IsNullOrWhiteSpace(reply.Text)
            ? $"{device.Label.Trim()} turned {(turnOn ? "on" : "off")}."
            : reply.Text;
        return new DeviceCommandResult(true, turnOn, message);
    }

    public async Task<DeviceCommandResult> QueryPowerAsync(DeviceConfig device, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var reply = await _client.SendTextQueryAsync(FormatStateQuery(device, settings), cancellationToken).ConfigureAwait(false);

        var state = ParseState(reply.Text, settings);
        if (state is null && IsMatch(settings.FailurePattern, reply.Text))
        {
            return new DeviceCommandResult(false, null, reply.Text);
        }

        return new DeviceCommandResult(true, state, reply.Text);
    }

    public static string FormatCommand(DeviceConfig device, bool turnOn, AssistantSettings settings)
    {
        var custom = turnOn ? device.OnCommand : device.OffCommand;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return ApplyTemplate(custom, device);
        }

        return ApplyTemplate(turnOn ? settings.OnCommandTemplate : settings.OffCommandTemplate, device);
    }

    public static string FormatStateQuery(DeviceConfig device, AssistantSettings settings) =>
        ApplyTemplate(string.IsNullOrWhiteSpace(device.StateQuery) ? settings.StateQueryTemplate : device.StateQuery, device);

    /// <summary>
    /// Reads "on"/"off" from an answer such as "The kitchen light is on." Returns null when
    /// the answer is ambiguous ("1 light is on and 2 are off") or does not mention a state.
    /// </summary>
    public static bool? ParseState(string reply, AssistantSettings settings)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return null;
        }

        var on = IsMatch(settings.StateOnPattern, reply);
        var off = IsMatch(settings.StateOffPattern, reply);
        return on == off ? null : on;
    }

    private static string ApplyTemplate(string template, DeviceConfig device) =>
        template.Replace("{name}", device.Name.Trim(), StringComparison.OrdinalIgnoreCase).Trim();

    private static bool IsMatch(string? pattern, string text)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var regex = RegexCache.GetOrAdd(pattern, static p =>
        {
            try
            {
                return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException)
            {
                return null; // invalid user pattern: never matches
            }
        });

        try
        {
            return regex?.IsMatch(text) ?? false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
