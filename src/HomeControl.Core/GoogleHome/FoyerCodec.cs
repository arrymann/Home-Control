using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomeControl.Core.GoogleHome;

/// <summary>A home (structure) in Google Home.</summary>
public sealed record GoogleHomeHome(string Id, string Name);

/// <summary>A device as listed by Google Home.</summary>
/// <param name="Id">Google Home device id (a UUID).</param>
/// <param name="AgentId">Partner integration that owns the device (null for some Google devices).</param>
/// <param name="PartnerDeviceId">The partner's own id for the device.</param>
/// <param name="Type">Hardware type, e.g. "action.devices.types.OUTLET".</param>
/// <param name="AssignedType">Type chosen by the user in Google Home, if any.</param>
/// <param name="Traits">Capabilities, e.g. "action.devices.traits.OnOff".</param>
public sealed record GoogleHomeDevice(
    string Id,
    string? AgentId,
    string? PartnerDeviceId,
    string Name,
    string Type,
    string? AssignedType,
    IReadOnlyList<string> Traits,
    string? Room,
    string HomeId,
    string HomeName)
{
    /// <summary>The user's chosen type when set, otherwise the hardware type.</summary>
    public string EffectiveType => string.IsNullOrWhiteSpace(AssignedType) ? Type : AssignedType!;

    public bool SupportsOnOff => Traits.Any(t => t.EndsWith(".OnOff", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Everything <c>GetHomeGraph</c> returned that Home Control uses.</summary>
public sealed record GoogleHomeGraph(IReadOnlyList<GoogleHomeHome> Homes, IReadOnlyList<GoogleHomeDevice> Devices);

/// <summary>Live state from <c>GetTraits</c> (or echoed by <c>UpdateTraits</c>).</summary>
/// <param name="Online">Null when the response did not say.</param>
/// <param name="IsOn">Null when the device has no on/off state in the response.</param>
/// <param name="Error">A device-level error code such as "deviceOffline", if reported.</param>
public sealed record GoogleHomeDeviceState(string Id, bool? Online, bool? IsOn, string? Error);

/// <summary>
/// Builds and parses the wire format of the private Google Home web API ("Foyer", the backend
/// of home.google.com): <c>application/json+protobuf</c>, where every message is a positional
/// JSON array (protobuf field N at index N-1, nulls for absent fields). Scalars inside trait
/// values are wrapped by type: numbers at index 1 (<c>[null,n]</c>), strings at index 2
/// (<c>[null,null,"s"]</c>), booleans at index 3 (<c>[null,null,null,1]</c>).
/// Field positions follow public captures of home.google.com (googlehome-mcp, K2) and the
/// protobuf definitions published by ghome-foyer-api; parsing is tolerant of missing fields.
/// </summary>
public static class FoyerCodec
{
    public const string StructuresService = "StructuresService";
    public const string HomeControlService = "HomeControlService";
    public const string GetHomeGraphMethod = "GetHomeGraph";
    public const string GetTraitsMethod = "GetTraits";
    public const string UpdateTraitsMethod = "UpdateTraits";

    private const string XssiPrefix = ")]}'";

    /// <summary><c>GetHomeGraph</c> takes an empty request.</summary>
    public static string BuildGetHomeGraph() => "[]";

    /// <summary><c>GetTraits</c>: <c>[[["id1"],["id2"],…]]</c>.</summary>
    public static string BuildGetTraits(IEnumerable<string> deviceIds)
    {
        var ids = new JsonArray();
        foreach (var id in deviceIds)
        {
            ids.Add(new JsonArray(JsonValue.Create(id)));
        }

        return new JsonArray(ids).ToJsonString();
    }

    /// <summary>
    /// <c>UpdateTraits</c> switching a device on or off:
    /// <c>[[[[id,[agentId,partnerDeviceId]],[["onOff",[["onOff",[null,null,null,1]]]]]]]]</c>.
    /// </summary>
    public static string BuildSetOnOff(string deviceId, string? agentId, string? partnerDeviceId, bool on)
    {
        var key = new JsonArray(JsonValue.Create(deviceId));
        if (agentId is not null || partnerDeviceId is not null)
        {
            key.Add(new JsonArray(JsonValue.Create(agentId), JsonValue.Create(partnerDeviceId)));
        }

        var field = new JsonArray(JsonValue.Create("onOff"), BoolWrapper(on));
        var trait = new JsonArray(JsonValue.Create("onOff"), new JsonArray(field));
        var command = new JsonArray(key, new JsonArray(trait));
        return new JsonArray(new JsonArray(command)).ToJsonString();
    }

    /// <summary>Removes the <c>)]}'</c> anti-XSSI line some Google endpoints prepend.</summary>
    public static string StripXssiPrefix(string body)
    {
        var trimmed = body.TrimStart();
        return trimmed.StartsWith(XssiPrefix, StringComparison.Ordinal)
            ? trimmed[XssiPrefix.Length..].TrimStart('\r', '\n')
            : body;
    }

    public static GoogleHomeGraph ParseHomeGraph(string json)
    {
        using var document = JsonDocument.Parse(StripXssiPrefix(json));
        var homes = new List<GoogleHomeHome>();
        var devices = new List<GoogleHomeDevice>();

        var homesNode = At(document.RootElement, 1);
        foreach (var home in EnumerateHomes(homesNode))
        {
            var homeId = Str(At(home, 0));
            if (homeId is null)
            {
                continue;
            }

            var homeName = Str(At(home, 1)) ?? string.Empty;
            homes.Add(new GoogleHomeHome(homeId, homeName));

            // Rooms [5]: [roomId, null, roomName, [ROOM_TYPE], [members…]]; a member is
            // [[deviceId,[agentId,partnerId]]] (or just [deviceId,…] in older captures).
            var roomOfDevice = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var room in Items(At(home, 5)))
            {
                var roomName = Str(At(room, 2));
                if (string.IsNullOrWhiteSpace(roomName))
                {
                    continue;
                }

                foreach (var member in Items(At(room, 4)))
                {
                    var key = At(member, 0);
                    var deviceId = key is { ValueKind: JsonValueKind.Array } ? Str(At(key, 0)) : Str(key);
                    if (deviceId is not null)
                    {
                        roomOfDevice[deviceId] = roomName;
                    }
                }
            }

            // Devices [6]: [[deviceId,[agentId,partnerId]], null, null, name, null, type, [traits], …, [20]=[assignedType]].
            foreach (var record in Items(At(home, 6)))
            {
                var key = At(record, 0);
                var deviceId = Str(At(key, 0));
                if (deviceId is null)
                {
                    continue;
                }

                var agentPair = At(key, 1);
                var name = Str(At(record, 3));
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = Str(At(At(record, 17), 1)) ?? string.Empty; // secondary name slot
                }

                var traits = All(At(record, 6))
                    .Where(t => t.ValueKind == JsonValueKind.String)
                    .Select(t => t.GetString()!)
                    .ToList();

                devices.Add(new GoogleHomeDevice(
                    deviceId,
                    Str(At(agentPair, 0)),
                    Str(At(agentPair, 1)),
                    name.Trim(),
                    Str(At(record, 5)) ?? string.Empty,
                    Str(At(At(record, 20), 0)),
                    traits,
                    roomOfDevice.GetValueOrDefault(deviceId),
                    homeId,
                    homeName));
            }
        }

        return new GoogleHomeGraph(homes, devices);
    }

    /// <summary>
    /// Parses a <c>GetTraits</c> or <c>UpdateTraits</c> response:
    /// <c>[[ [[id],[[traitName,[[field,wrapper],…]],…]], … ]]</c>.
    /// </summary>
    public static IReadOnlyList<GoogleHomeDeviceState> ParseTraits(string json)
    {
        using var document = JsonDocument.Parse(StripXssiPrefix(json));
        var states = new List<GoogleHomeDeviceState>();

        foreach (var result in Items(At(document.RootElement, 0)))
        {
            var idNode = At(result, 0);
            var id = idNode is { ValueKind: JsonValueKind.Array } ? Str(At(idNode, 0)) : Str(idNode);
            if (id is null)
            {
                continue;
            }

            bool? online = null, isOn = null;
            string? error = null;
            foreach (var trait in Items(At(result, 1)))
            {
                var fields = Fields(At(trait, 1));
                switch (Str(At(trait, 0)))
                {
                    case "deviceStatus":
                        if (fields.TryGetValue("online", out var onlineWrapper))
                        {
                            online = Bool(onlineWrapper);
                        }

                        if (fields.TryGetValue("error", out var errorWrapper))
                        {
                            error = Str(At(errorWrapper, 2));
                        }

                        break;
                    case "onOff":
                        if (fields.TryGetValue("onOff", out var onOffWrapper))
                        {
                            isOn = Bool(onOffWrapper);
                        }

                        break;
                }
            }

            states.Add(new GoogleHomeDeviceState(id, online, isOn, error));
        }

        return states;
    }

    /// <summary>Reads an RPC error body such as <c>[3,"Invalid argument"]</c>.</summary>
    public static bool TryParseError(string body, out int code, out string message)
    {
        code = 0;
        message = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(StripXssiPrefix(body));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array &&
                At(root, 0) is { ValueKind: JsonValueKind.Number } codeNode &&
                codeNode.TryGetInt32(out code))
            {
                message = Str(At(root, 1)) ?? string.Empty;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        code = 0;
        return false;
    }

    private static IEnumerable<JsonElement> EnumerateHomes(JsonElement? homesNode)
    {
        if (homesNode is not { ValueKind: JsonValueKind.Array } node || node.GetArrayLength() == 0)
        {
            return [];
        }

        // Either a single home ([id, name, …]) or a list of homes ([[id, name, …], …]).
        return node[0].ValueKind == JsonValueKind.String ? [node] : Items(node);
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement? body)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in Items(body))
        {
            if (Str(At(field, 0)) is { } name && At(field, 1) is { ValueKind: JsonValueKind.Array } wrapper)
            {
                fields[name] = wrapper;
            }
        }

        return fields;
    }

    private static JsonArray BoolWrapper(bool value) => new(null, null, null, JsonValue.Create(value ? 1 : 0));

    private static bool? Bool(JsonElement wrapper) => At(wrapper, 3) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var i) => i != 0,
        _ => null,
    };

    private static JsonElement? At(JsonElement? node, int index) =>
        node is { ValueKind: JsonValueKind.Array } array && index >= 0 && index < array.GetArrayLength() &&
        array[index].ValueKind != JsonValueKind.Null
            ? array[index]
            : null;

    private static IEnumerable<JsonElement> Items(JsonElement? node) =>
        node is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Array)
            : [];

    private static IEnumerable<JsonElement> All(JsonElement? node) =>
        node is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    private static string? Str(JsonElement? node) =>
        node is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
}
