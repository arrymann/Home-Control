using System.Text.Json;
using HomeControl.Core.Devices;
using HomeControl.Core.GoogleHome;
using HomeControl.Core.Hotkeys;
using HomeControl.Core.Models;

namespace HomeControl.Core.Tests;

/// <summary>Fixtures shaped like the captured home.google.com responses (ids are made up).</summary>
internal static class Fixtures
{
    public const string HomeId = "11111111-1111-4111-8111-111111111111";
    public const string PrinterId = "aaaaaaaa-0000-4000-8000-000000000001";
    public const string BacklightId = "aaaaaaaa-0000-4000-8000-000000000002";
    public const string DeskId = "aaaaaaaa-0000-4000-8000-000000000003";
    public const string PcId = "aaaaaaaa-0000-4000-8000-000000000004";
    public const string TvId = "aaaaaaaa-0000-4000-8000-000000000005";

    private static string Device(string id, string agent, string partner, string name, string type, string traits, string? assignedType = null)
    {
        // [ [id,[agent,partner]], null, null, name, null, type, [traits], null×13, [assignedType] ]
        var slots = string.Join(",", Enumerable.Repeat("null", 13));
        var assigned = assignedType is null ? "null" : $"[\"{assignedType}\"]";
        return $"[[\"{id}\",[\"{agent}\",\"{partner}\"]],null,null,\"{name}\",null,\"{type}\",[{traits}],{slots},{assigned}]";
    }

    private const string OnOff = "\"action.devices.traits.OnOff\"";

    public static string Home(string name = "Pomki") =>
        $$"""
        ["{{HomeId}}","{{name}}",null,[["owner@example.com"]],null,
          [["room-1",null,"Living Room",["LIVING_ROOM"],[
              [["{{PrinterId}}",["tuya-agent","p1"]]],
              [["{{BacklightId}}",["tuya-agent","p2"]]],
              [["{{DeskId}}",["tuya-agent","p3"]]],
              [["{{PcId}}",["tuya-agent","p4"]]],
              [["{{TvId}}",["cast-agent","tv1"]]]]]],
          [{{Device(PrinterId, "tuya-agent", "p1", "3d printer", "action.devices.types.OUTLET", OnOff)}},
           {{Device(BacklightId, "tuya-agent", "p2", "Backlight", "action.devices.types.OUTLET", OnOff, "action.devices.types.LIGHT")}},
           {{Device(DeskId, "tuya-agent", "p3", "Desk", "action.devices.types.OUTLET", OnOff)}},
           {{Device(PcId, "tuya-agent", "p4", "Pc", "action.devices.types.OUTLET", OnOff)}},
           {{Device(TvId, "cast-agent", "tv1", "Living Room TV", "action.devices.types.TV", "\"action.devices.traits.MediaState\"")}}]]
        """;

    /// <summary>Current capture shape: a list of homes at [1].</summary>
    public static string HomeGraphList => $"[\"1730000000\",[{Home()}],null,null,null,null,[[\"LIGHT\",\"Light\"]]]";

    /// <summary>Shape from the published protobuf definitions: a single home at [1].</summary>
    public static string HomeGraphSingle => $"[\"1730000000\",{Home()}]";

    public static string Traits(params (string Id, bool? Online, bool? On)[] devices)
    {
        var results = devices.Select(d =>
        {
            var traits = new List<string>();
            if (d.Online is { } online)
            {
                traits.Add($"[\"deviceStatus\",[[\"online\",[null,null,null,{(online ? 1 : 0)}]]]]");
            }

            if (d.On is { } on)
            {
                traits.Add($"[\"onOff\",[[\"onOff\",[null,null,null,{(on ? 1 : 0)}]]]]");
            }

            return $"[[\"{d.Id}\"],[{string.Join(",", traits)}]]";
        });
        return $"[[{string.Join(",", results)}]]";
    }
}

public class FoyerCodecTests
{
    [Fact]
    public void Builds_requests_in_the_web_app_format()
    {
        Assert.Equal("[]", FoyerCodec.BuildGetHomeGraph());
        Assert.Equal("""[[["a"],["b"]]]""", FoyerCodec.BuildGetTraits(["a", "b"]));
        Assert.Equal(
            """[[[["dev",["agent","partner"]],[["onOff",[["onOff",[null,null,null,1]]]]]]]]""",
            FoyerCodec.BuildSetOnOff("dev", "agent", "partner", true));
        Assert.Equal(
            """[[[["dev"],[["onOff",[["onOff",[null,null,null,0]]]]]]]]""",
            FoyerCodec.BuildSetOnOff("dev", null, null, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parses_home_graph_in_both_shapes(bool singleHome)
    {
        var graph = FoyerCodec.ParseHomeGraph(singleHome ? Fixtures.HomeGraphSingle : Fixtures.HomeGraphList);

        var home = Assert.Single(graph.Homes);
        Assert.Equal(new GoogleHomeHome(Fixtures.HomeId, "Pomki"), home);
        Assert.Equal(5, graph.Devices.Count);

        var printer = graph.Devices[0];
        Assert.Equal(Fixtures.PrinterId, printer.Id);
        Assert.Equal("3d printer", printer.Name);
        Assert.Equal("tuya-agent", printer.AgentId);
        Assert.Equal("p1", printer.PartnerDeviceId);
        Assert.Equal("Living Room", printer.Room);
        Assert.Equal("Pomki", printer.HomeName);
        Assert.True(printer.SupportsOnOff);

        var backlight = graph.Devices[1];
        Assert.Equal("action.devices.types.LIGHT", backlight.EffectiveType);

        var tv = graph.Devices[4];
        Assert.False(tv.SupportsOnOff);
    }

    [Fact]
    public void Parses_states_with_numeric_and_boolean_flags_and_xssi_prefix()
    {
        var json = ")]}'\n" + """
            [[
              [["d1"],[["deviceStatus",[["online",[null,null,null,1]]]],["onOff",[["onOff",[null,null,null,1]]]]]],
              [["d2"],[["deviceStatus",[["online",[null,null,null,false]],["error",[null,null,"deviceOffline"]]]],["onOff",[["onOff",[null,null,null,0]]]]]],
              [["d3"],[["brightness",[["brightness",[null,40]]]]]],
              [null,[]]
            ]]
            """;

        var states = FoyerCodec.ParseTraits(json);

        Assert.Equal(3, states.Count);
        Assert.Equal(new GoogleHomeDeviceState("d1", true, true, null), states[0]);
        Assert.Equal(new GoogleHomeDeviceState("d2", false, false, "deviceOffline"), states[1]);
        Assert.Equal(new GoogleHomeDeviceState("d3", null, null, null), states[2]);
    }

    [Fact]
    public void Tolerates_unexpected_shapes()
    {
        Assert.Empty(FoyerCodec.ParseHomeGraph("[]").Devices);
        Assert.Empty(FoyerCodec.ParseHomeGraph("""[null,"not a home"]""").Homes);
        Assert.Empty(FoyerCodec.ParseTraits("{}"));
        Assert.Empty(FoyerCodec.ParseTraits("[[1,2,\"x\"]]"));
    }

    [Theory]
    [InlineData("""[3,"Request contains an invalid argument."]""", true, 3, "Request contains an invalid argument.")]
    [InlineData("""[16]""", true, 16, "")]
    [InlineData("""<html>oops</html>""", false, 0, "")]
    [InlineData("""{"error":{}}""", false, 0, "")]
    public void Parses_rpc_errors(string body, bool ok, int code, string message)
    {
        Assert.Equal(ok, FoyerCodec.TryParseError(body, out var parsedCode, out var parsedMessage));
        Assert.Equal(code, parsedCode);
        Assert.Equal(message, parsedMessage);
    }
}

internal sealed class FakeFoyerTransport : IFoyerTransport
{
    private readonly Func<string, string, string, FoyerResponse> _respond;

    public FakeFoyerTransport(Func<string, string, string, FoyerResponse> respond)
    {
        _respond = respond;
    }

    public List<(string Method, string Body)> Calls { get; } = [];

    public Task<FoyerResponse> SendAsync(string service, string method, string body, CancellationToken cancellationToken)
    {
        Calls.Add((method, body));
        return Task.FromResult(_respond(service, method, body));
    }
}

public class GoogleHomeClientTests
{
    [Fact]
    public async Task Set_on_off_uses_the_echoed_state()
    {
        var transport = new FakeFoyerTransport((_, method, _) =>
            new FoyerResponse(200, Fixtures.Traits(("d1", true, true))));
        var client = new GoogleHomeClient(transport);

        var state = await client.SetOnOffAsync("d1", "agent", "p", true, default);

        Assert.Equal(new GoogleHomeDeviceState("d1", true, true, null), state);
        var (method, body) = Assert.Single(transport.Calls);
        Assert.Equal("UpdateTraits", method);
        Assert.Contains("[null,null,null,1]", body);
    }

    [Fact]
    public async Task Set_on_off_reads_back_when_the_response_has_no_state()
    {
        var transport = new FakeFoyerTransport((_, method, _) => method == "UpdateTraits"
            ? new FoyerResponse(200, "[[]]")
            : new FoyerResponse(200, Fixtures.Traits(("d1", true, false))));
        var client = new GoogleHomeClient(transport);

        var state = await client.SetOnOffAsync("d1", "agent", "p", false, default);

        Assert.False(state!.IsOn);
        Assert.Equal(["UpdateTraits", "GetTraits"], transport.Calls.Select(c => c.Method));
    }

    [Theory]
    [InlineData(401, "")]
    [InlineData(403, "")]
    [InlineData(400, """[16,"Request had invalid authentication credentials."]""")]
    [InlineData(400, """[7,"The caller does not have permission"]""")]
    public async Task Auth_failures_require_sign_in(int status, string body)
    {
        var client = new GoogleHomeClient(new FakeFoyerTransport((_, _, _) => new FoyerResponse(status, body)));

        await Assert.ThrowsAsync<GoogleHomeSignInRequiredException>(() => client.GetHomeGraphAsync(default));
    }

    [Fact]
    public async Task Other_failures_carry_the_rpc_message()
    {
        var client = new GoogleHomeClient(new FakeFoyerTransport((_, _, _) => new FoyerResponse(400, """[3,"Bad device"]""")));

        var ex = await Assert.ThrowsAsync<GoogleHomeException>(() => client.GetStatesAsync(["x"], default));

        Assert.IsNotType<GoogleHomeSignInRequiredException>(ex);
        Assert.Equal(3, ex.RpcCode);
        Assert.Equal(400, ex.HttpStatus);
        Assert.Contains("Bad device", ex.Message);
    }

    [Fact]
    public async Task Unreadable_responses_are_google_home_errors()
    {
        var client = new GoogleHomeClient(new FakeFoyerTransport((_, _, _) => new FoyerResponse(200, "<html>")));

        await Assert.ThrowsAsync<GoogleHomeException>(() => client.GetHomeGraphAsync(default));
    }

    [Fact]
    public async Task States_are_requested_in_chunks_without_duplicates()
    {
        var transport = new FakeFoyerTransport((_, _, body) =>
        {
            var ids = JsonDocument.Parse(body).RootElement[0].EnumerateArray().Select(e => e[0].GetString()!).ToArray();
            return new FoyerResponse(200, Fixtures.Traits(ids.Select(id => (id, (bool?)true, (bool?)false)).ToArray()));
        });
        var client = new GoogleHomeClient(transport);
        var ids = Enumerable.Range(0, 120).Select(i => $"d{i}").Append("d0").ToList();

        var states = await client.GetStatesAsync(ids, default);

        Assert.Equal(120, states.Count);
        Assert.Equal(3, transport.Calls.Count);
    }
}

public class GoogleHomeDeviceControllerTests
{
    private static DeviceConfig Desk() => new()
    {
        Source = DeviceSource.GoogleHome,
        Name = "Desk",
        GoogleHomeId = "d1",
        AgentId = "agent",
        PartnerDeviceId = "p",
    };

    [Fact]
    public async Task Reports_new_state_and_device_errors()
    {
        var reply = Fixtures.Traits(("d1", true, true));
        var controller = new GoogleHomeDeviceController(new GoogleHomeClient(new FakeFoyerTransport((_, _, _) => new FoyerResponse(200, reply))));

        var ok = await controller.SetPowerAsync(Desk(), true, default);
        Assert.Equal(new DeviceCommandResult(true, true, "Desk turned on."), ok);

        reply = """[[[["d1"],[["deviceStatus",[["online",[null,null,null,0]],["error",[null,null,"deviceOffline"]]]]]]]]""";
        var offline = await controller.SetPowerAsync(Desk(), true, default);
        Assert.False(offline.Success);
        Assert.Equal("The device is offline.", offline.Message);
    }

    [Theory]
    [InlineData(null)]  // no on/off state in the echo or the read-back
    [InlineData(false)] // a slow device (a TV) still reports its old state
    public async Task A_command_the_device_hasnt_confirmed_yet_succeeds_without_a_state(bool? reported)
    {
        var transport = new FakeFoyerTransport((_, _, _) => new FoyerResponse(200, Fixtures.Traits(("d1", true, reported))));
        var controller = new GoogleHomeDeviceController(new GoogleHomeClient(transport));

        var result = await controller.SetPowerAsync(Desk(), true, default);

        Assert.True(result.Success);
        Assert.Equal(reported, result.IsOn);
        Assert.False(result.Confirms(true));
        Assert.Equal("Sent “turn on” to Desk.", result.Message);
    }

    [Fact]
    public async Task Googles_on_off_value_counts_even_when_it_lists_the_device_offline()
    {
        // Google can list a TV that is on as offline (the Google Home app shows it on).
        var transport = new FakeFoyerTransport((_, _, _) => new FoyerResponse(200, Fixtures.Traits(("d1", false, true))));
        var traced = new List<string>();
        var controller = new GoogleHomeDeviceController(new GoogleHomeClient(transport) { Trace = traced.Add });

        var command = await controller.SetPowerAsync(Desk(), true, default);
        var query = await controller.QueryPowerAsync(Desk(), default);
        var batch = await controller.ReadStatesAsync([Desk()], default);

        Assert.True(command.Success); // no error: the command was accepted
        Assert.True(command.Confirms(true));
        Assert.True(query.IsOn);
        Assert.Equal(new DeviceStatus(true, false), batch.Values.Single());
        var line = Assert.Single(traced); // logged once, not on every read
        Assert.Contains("online=False", line);
        Assert.Contains("\"deviceStatus\"", line);
    }

    [Fact]
    public void Several_records_for_one_device_are_merged_like_the_google_home_app()
    {
        // A TV known through two integrations: the maker's cloud says offline, the Chromecast says on.
        var body = """
            [[
              [["tv",["maker","p1"]],[["deviceStatus",[["online",[null,null,null,0]],["error",[null,null,"deviceOffline"]]]],["onOff",[["onOff",[null,null,null,0]]]]]],
              [["tv",["cast","p2"]],[["deviceStatus",[["online",[null,null,null,1]]]],["onOff",[["onOff",[null,null,null,1]]]]]],
              [["lamp"],[["deviceStatus",[["online",[null,null,null,0]]]],["deviceStatus",[["online",[null,null,null,1]]]],["onOff",[["onOff",[null,null,null,0]]]]]]
            ]]
            """;

        var states = FoyerCodec.ParseTraits(body);

        Assert.Equal(2, states.Count);
        Assert.Equal(new GoogleHomeDeviceState("tv", true, true, null), states[0]);
        Assert.Equal(new GoogleHomeDeviceState("lamp", true, false, null), states[1]); // a trait twice: online wins
        Assert.Contains("\"maker\"", FoyerCodec.RawTraitsById(body)["tv"]);
        Assert.Contains("\"cast\"", FoyerCodec.RawTraitsById(body)["tv"]);
    }

    [Fact]
    public async Task A_command_that_needs_a_confirmation_is_a_failure()
    {
        // The partner gates on/off behind an acknowledgement: nothing was switched.
        var reply = """[[[["d1",["agent","p"]],[["deviceStatus",[["online",[null,null,null,1]],["challenge",[null,null,"ackNeeded"]]]],["onOff",[["onOff",[null,null,null,0]]]]]]]]""";
        var transport = new FakeFoyerTransport((_, _, _) => new FoyerResponse(200, reply));
        var controller = new GoogleHomeDeviceController(new GoogleHomeClient(transport));

        var result = await controller.SetPowerAsync(Desk(), true, default);

        Assert.False(result.Success);
        Assert.False(result.IsOn);
        Assert.Contains("confirmation", result.Message);
        Assert.Single(transport.Calls); // the echo is used, no read-back
    }

    [Fact]
    public async Task Reads_states_in_one_batch_keyed_by_config_id()
    {
        var transport = new FakeFoyerTransport((_, _, _) => new FoyerResponse(200, Fixtures.Traits(("d1", true, true), ("d2", false, false))));
        var controller = new GoogleHomeDeviceController(new GoogleHomeClient(transport));
        var desk = Desk();
        var pc = new DeviceConfig { Source = DeviceSource.GoogleHome, Name = "Pc", GoogleHomeId = "d2" };
        var assistantOnly = new DeviceConfig { Name = "Lamp" };

        var states = await controller.ReadStatesAsync([desk, pc, assistantOnly], default);

        Assert.Single(transport.Calls);
        Assert.Equal(new DeviceStatus(true, true), states[desk.Id]);
        Assert.Equal(new DeviceStatus(false, false), states[pc.Id]);
        Assert.False(states.ContainsKey(assistantOnly.Id));
        Assert.True(controller.CanReadInBatch(desk));
        Assert.False(controller.CanReadInBatch(assistantOnly));
    }
}

public class CompositeDeviceControllerTests
{
    private static DeviceConfig Pc() => new() { Source = DeviceSource.GoogleHome, Name = "Pc", GoogleHomeId = "d4" };

    private static (CompositeDeviceController Controller, FakeAssistantClient Assistant) Create(
        Func<FoyerResponse> foyer, bool fallback, bool googleHomeEnabled = true)
    {
        var assistant = new FakeAssistantClient(_ => "OK, turning on the Pc.");
        var googleHome = new GoogleHomeDeviceController(new GoogleHomeClient(new FakeFoyerTransport((_, _, _) => foyer())));
        var assistantController = new Assistant.AssistantDeviceController(assistant, () => new Settings.AssistantSettings());
        return (new CompositeDeviceController(googleHome, assistantController, () => fallback, () => googleHomeEnabled), assistant);
    }

    [Fact]
    public async Task Signed_out_of_google_home_on_purpose_goes_straight_to_assistant()
    {
        var (controller, assistant) = Create(() => throw new InvalidOperationException("must not be called"), fallback: true, googleHomeEnabled: false);

        var result = await controller.SetPowerAsync(Pc(), true, default);

        Assert.True(result.Success);
        Assert.Equal(["turn on Pc"], assistant.Queries);
        Assert.False(controller.CanReadInBatch(Pc()));
    }

    [Fact]
    public async Task Google_home_devices_go_through_the_web_session()
    {
        var (controller, assistant) = Create(() => new FoyerResponse(200, Fixtures.Traits(("d4", true, true))), fallback: true);

        var result = await controller.SetPowerAsync(Pc(), true, default);

        Assert.True(result.Success);
        Assert.Empty(assistant.Queries);
    }

    [Fact]
    public async Task Falls_back_to_assistant_by_name_when_the_session_fails()
    {
        var (controller, assistant) = Create(() => new FoyerResponse(401, ""), fallback: true);

        var result = await controller.SetPowerAsync(Pc(), true, default);

        Assert.True(result.Success);
        Assert.Equal(["turn on Pc"], assistant.Queries);
        Assert.Contains("via Google Assistant", result.Message);
    }

    [Fact]
    public async Task Without_fallback_session_errors_surface()
    {
        var (controller, assistant) = Create(() => new FoyerResponse(401, ""), fallback: false);

        await Assert.ThrowsAsync<GoogleHomeSignInRequiredException>(() => controller.SetPowerAsync(Pc(), true, default));
        Assert.Empty(assistant.Queries);
    }

    [Fact]
    public async Task Device_reported_failures_do_not_fall_back()
    {
        var (controller, assistant) = Create(
            () => new FoyerResponse(200, """[[[["d4"],[["deviceStatus",[["error",[null,null,"deviceOffline"]]]]]]]]"""),
            fallback: true);

        var result = await controller.SetPowerAsync(Pc(), true, default);

        Assert.False(result.Success);
        Assert.Empty(assistant.Queries);
    }

    [Fact]
    public async Task Assistant_devices_go_to_assistant()
    {
        var (controller, assistant) = Create(() => throw new InvalidOperationException("must not be called"), fallback: false);

        await controller.SetPowerAsync(new DeviceConfig { Name = "Lamp" }, true, default);

        Assert.Equal(["turn on Lamp"], assistant.Queries);
    }
}

public class GoogleHomeSyncTests
{
    [Fact]
    public void Adds_on_off_devices_sorted_by_room_and_name_with_icons_from_type()
    {
        var devices = new List<DeviceConfig>();

        var result = GoogleHomeSync.Merge(devices, FoyerCodec.ParseHomeGraph(Fixtures.HomeGraphList));

        Assert.Equal(new GoogleHomeSyncResult(4, 0, 0, 0), result);
        Assert.Equal(["3d printer", "Backlight", "Desk", "Pc"], devices.Select(d => d.Name));
        Assert.All(devices, d => Assert.Equal(DeviceSource.GoogleHome, d.Source));
        Assert.All(devices, d => Assert.Equal("Living Room", d.Room));
        Assert.All(devices, d => Assert.Equal("Pomki", d.Home));
        Assert.Equal(DeviceKind.Outlet, devices[0].Kind);
        Assert.Equal(DeviceKind.Light, devices[1].Kind); // user assigned "light" in Google Home
        Assert.Equal("p4", devices[3].PartnerDeviceId);
    }

    [Fact]
    public void Keeps_user_choices_links_assistant_devices_and_flags_missing_ones()
    {
        var desk = new DeviceConfig
        {
            Source = DeviceSource.GoogleHome,
            GoogleHomeId = Fixtures.DeskId,
            Name = "Old desk name",
            DisplayName = "My desk",
            Kind = DeviceKind.Light,
            Hotkey = Hotkey.Parse("Ctrl+Alt+D"),
            Hidden = true,
        };
        var manualPc = new DeviceConfig { Name = "pc ", Hotkey = Hotkey.Parse("Ctrl+Alt+P") };
        var gone = new DeviceConfig { Source = DeviceSource.GoogleHome, GoogleHomeId = "gone", Name = "Old lamp" };
        var assistantOnly = new DeviceConfig { Name = "Movie night", Kind = DeviceKind.Scene };
        var devices = new List<DeviceConfig> { desk, manualPc, gone, assistantOnly };

        var result = GoogleHomeSync.Merge(devices, FoyerCodec.ParseHomeGraph(Fixtures.HomeGraphList));

        Assert.Equal(new GoogleHomeSyncResult(Added: 2, Updated: 1, Missing: 1, Linked: 1), result);
        Assert.Equal("Desk", desk.Name);
        Assert.Equal("My desk", desk.DisplayName);
        Assert.Equal(DeviceKind.Light, desk.Kind);
        Assert.True(desk.Hidden);
        Assert.Equal(Hotkey.Parse("Ctrl+Alt+D"), desk.Hotkey);

        Assert.Equal(DeviceSource.GoogleHome, manualPc.Source);
        Assert.Equal(Fixtures.PcId, manualPc.GoogleHomeId);
        Assert.Equal("Pc", manualPc.Name);
        Assert.Equal(Hotkey.Parse("Ctrl+Alt+P"), manualPc.Hotkey);

        Assert.True(gone.Missing);
        Assert.Equal(DeviceSource.Assistant, assistantOnly.Source);
        Assert.Equal(6, devices.Count);

        // A second sync with the same data changes nothing.
        Assert.Equal(new GoogleHomeSyncResult(0, 0, 1, 0), GoogleHomeSync.Merge(devices, FoyerCodec.ParseHomeGraph(Fixtures.HomeGraphList)));
    }

    [Theory]
    [InlineData("action.devices.types.LIGHT", DeviceKind.Light)]
    [InlineData("action.devices.types.OUTLET", DeviceKind.Outlet)]
    [InlineData("action.devices.types.SWITCH", DeviceKind.Switch)]
    [InlineData("action.devices.types.TV", DeviceKind.Tv)]
    [InlineData("action.devices.types.FAN", DeviceKind.Climate)]
    [InlineData("action.devices.types.COFFEE_MAKER", DeviceKind.Coffee)]
    [InlineData("action.devices.types.VACUUM", DeviceKind.Other)]
    [InlineData(null, DeviceKind.Other)]
    public void Maps_types_to_icons(string? type, DeviceKind kind)
    {
        Assert.Equal(kind, GoogleHomeTypes.ToKind(type));
    }
}
