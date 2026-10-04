using Google.Assistant.Embedded.V1Alpha2;
using Google.Protobuf;
using Grpc.Core;
using HomeControl.Core.Assistant;
using HomeControl.Core.Devices;
using HomeControl.Core.Models;
using HomeControl.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DeviceConfig = HomeControl.Core.Models.DeviceConfig;

namespace HomeControl.Core.Tests;

public class AssistantDeviceControllerTests
{
    private readonly AssistantSettings _settings = new();

    [Fact]
    public async Task Sends_templated_commands_and_reports_new_state()
    {
        var assistant = new FakeAssistantClient(q => q.StartsWith("turn on", StringComparison.Ordinal) ? "Turning the kitchen light on." : "");
        var controller = new AssistantDeviceController(assistant, () => _settings);
        var device = new DeviceConfig { Name = " Kitchen light " };

        var on = await controller.SetPowerAsync(device, true, default);
        var off = await controller.SetPowerAsync(device, false, default);

        Assert.Equal(["turn on Kitchen light", "turn off Kitchen light"], assistant.Queries);
        Assert.Equal(new DeviceCommandResult(true, true, "Turning the kitchen light on."), on);
        Assert.Equal(new DeviceCommandResult(true, false, "Kitchen light turned off."), off);
    }

    [Fact]
    public async Task Uses_per_device_overrides()
    {
        var assistant = new FakeAssistantClient(_ => "OK");
        var controller = new AssistantDeviceController(assistant, () => _settings);
        var device = new DeviceConfig
        {
            Name = "Movie night",
            OnCommand = "activate {NAME}",
            OffCommand = "  ",
            StateQuery = "is the TV on?",
        };

        await controller.SetPowerAsync(device, true, default);
        await controller.SetPowerAsync(device, false, default);
        await controller.QueryPowerAsync(device, default);

        Assert.Equal(["activate Movie night", "turn off Movie night", "is the TV on?"], assistant.Queries);
    }

    [Theory]
    [InlineData("Sorry, I couldn't reach Kitchen light.")]
    [InlineData("Kitchen light is offline.")]
    [InlineData("Something went wrong. Try again in a few seconds.")]
    [InlineData("Kitchen light isn't responding right now.")]
    public async Task Failure_answers_are_errors(string reply)
    {
        var controller = new AssistantDeviceController(new FakeAssistantClient(_ => reply), () => _settings);

        var result = await controller.SetPowerAsync(new DeviceConfig { Name = "Kitchen light" }, true, default);

        Assert.False(result.Success);
        Assert.Null(result.IsOn);
        Assert.Equal(reply, result.Message);
    }

    [Theory]
    [InlineData("The kitchen light is on.", true)]
    [InlineData("Kitchen light is currently off.", false)]
    [InlineData("It's on at 40% brightness.", true)]
    [InlineData("The 2 lights are turned off.", false)]
    [InlineData("1 light is on and 2 are off.", null)]
    [InlineData("Here's what I found on the web.", null)]
    [InlineData("", null)]
    public void Parses_state_answers(string reply, bool? expected)
    {
        Assert.Equal(expected, AssistantDeviceController.ParseState(reply, _settings));
    }

    [Fact]
    public async Task Query_returns_unknown_state_or_failure()
    {
        var device = new DeviceConfig { Name = "Lamp" };
        var unknown = await new AssistantDeviceController(new FakeAssistantClient(_ => "Hmm."), () => _settings).QueryPowerAsync(device, default);
        var failed = await new AssistantDeviceController(new FakeAssistantClient(_ => "Sorry, Lamp is not available."), () => _settings)
            .QueryPowerAsync(device, default);

        Assert.Equal(new DeviceCommandResult(true, null, "Hmm."), unknown);
        Assert.False(failed.Success);
    }

    [Fact]
    public void Invalid_user_patterns_never_match()
    {
        var settings = new AssistantSettings { StateOnPattern = "(unclosed", StateOffPattern = "(also" };
        Assert.Null(AssistantDeviceController.ParseState("It is on", settings));
    }
}

public class AssistantHtmlTests
{
    [Fact]
    public void Prefers_show_text_content()
    {
        const string html = """
            <html><head><style>.x{}</style><script>var a = "is on";</script></head>
            <body><div class="popout"><div class="show_text_content">Turning on&nbsp;the <b>lamp</b>.</div></div>
            <div class="suggestion">What's the weather?</div></body></html>
            """;

        Assert.Equal("Turning on the lamp .", AssistantHtml.ExtractText(html));
    }

    [Fact]
    public void Falls_back_to_body_text()
    {
        const string html = "<html><head><title>T</title></head><body><p>The lamp</p>\n<p>is &amp; off.</p><script>x()</script></body></html>";
        Assert.Equal("The lamp is & off.", AssistantHtml.ExtractText(html));
    }

    [Fact]
    public void Long_text_is_truncated()
    {
        var text = AssistantHtml.ExtractText("<body>" + new string('a', 1000) + "</body>");
        Assert.Equal(401, text.Length);
        Assert.EndsWith("…", text);
    }
}

public sealed class AssistantClientTests : IAsyncLifetime
{
    private readonly FakeEmbeddedAssistant _service = new();
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(_service);
        _app = builder.Build();
        _app.MapGrpcService<FakeEmbeddedAssistant>();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private AssistantClient CreateClient(StaticTokenProvider tokens, AssistantSettings? settings = null)
    {
        settings ??= new AssistantSettings { Endpoint = "http://localhost", LanguageCode = "en-GB", DeviceModelId = "model", DeviceId = "device" };
        return new AssistantClient(tokens, () => settings, _app!.GetTestServer().CreateHandler());
    }

    [Fact]
    public async Task Sends_text_query_with_config_and_bearer_token()
    {
        _service.Respond = _ =>
        [
            new AssistResponse { DialogStateOut = new DialogStateOut { SupplementalDisplayText = "Turning on" } },
            new AssistResponse { AudioOut = new AudioOut { AudioData = ByteString.CopyFrom(1, 2, 3) } },
            new AssistResponse { DialogStateOut = new DialogStateOut { SupplementalDisplayText = " the lamp. " } },
        ];
        using var client = CreateClient(new StaticTokenProvider());

        var reply = await client.SendTextQueryAsync("turn on the lamp", default);

        Assert.Equal("Turning on the lamp.", reply.Text);
        var (config, authorization) = Assert.Single(_service.Requests);
        Assert.Equal("Bearer token-0", authorization);
        Assert.Equal("turn on the lamp", config.TextQuery);
        Assert.Equal("en-GB", config.DialogStateIn.LanguageCode);
        Assert.True(config.DialogStateIn.IsNewConversation);
        Assert.Equal("model", config.DeviceConfig.DeviceModelId);
        Assert.Equal("device", config.DeviceConfig.DeviceId);
        Assert.Equal(AudioOutConfig.Types.Encoding.Linear16, config.AudioOutConfig.Encoding);
        Assert.Equal(ScreenOutConfig.Types.ScreenMode.Playing, config.ScreenOutConfig.ScreenMode);
    }

    [Fact]
    public async Task Uses_html_screen_when_there_is_no_display_text()
    {
        _service.Respond = _ =>
        [
            new AssistResponse { ScreenOut = new ScreenOut { Format = ScreenOut.Types.Format.Html, Data = ByteString.CopyFromUtf8("<body><div class=\"show_text_content\">The lamp ") } },
            new AssistResponse { ScreenOut = new ScreenOut { Format = ScreenOut.Types.Format.Html, Data = ByteString.CopyFromUtf8("is off.</div></body>") } },
        ];
        using var client = CreateClient(new StaticTokenProvider());

        Assert.Equal("The lamp is off.", (await client.SendTextQueryAsync("is the lamp on?", default)).Text);
    }

    [Fact]
    public async Task Retries_once_with_a_fresh_token_when_unauthenticated()
    {
        _service.Respond = auth => auth == "Bearer token-0"
            ? throw new RpcException(new Status(StatusCode.Unauthenticated, "expired"))
            : [new AssistResponse { DialogStateOut = new DialogStateOut { SupplementalDisplayText = "OK" } }];
        var tokens = new StaticTokenProvider();
        using var client = CreateClient(tokens);

        Assert.Equal("OK", (await client.SendTextQueryAsync("hi", default)).Text);
        Assert.Equal(1, tokens.InvalidateCount);
        Assert.Equal(["Bearer token-0", "Bearer token-1"], _service.Requests.Select(r => r.Authorization));
    }

    [Fact]
    public async Task Persistent_unauthenticated_is_an_authentication_error()
    {
        _service.Respond = _ => throw new RpcException(new Status(StatusCode.Unauthenticated, "no"));
        using var client = CreateClient(new StaticTokenProvider());

        var ex = await Assert.ThrowsAsync<AssistantException>(() => client.SendTextQueryAsync("hi", default));
        Assert.True(ex.IsAuthenticationError);
    }

    [Fact]
    public async Task Permission_denied_explains_setup()
    {
        _service.Respond = _ => throw new RpcException(new Status(StatusCode.PermissionDenied, "API not enabled"));
        using var client = CreateClient(new StaticTokenProvider());

        var ex = await Assert.ThrowsAsync<AssistantException>(() => client.SendTextQueryAsync("hi", default));
        Assert.False(ex.IsAuthenticationError);
        Assert.Contains("Google Assistant API is enabled", ex.Message);
    }

    [Fact]
    public async Task Cancellation_throws_operation_cancelled()
    {
        _service.Respond = _ =>
        {
            Thread.Sleep(2000);
            return [];
        };
        using var client = CreateClient(new StaticTokenProvider());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendTextQueryAsync("hi", cts.Token));
    }

    public sealed class FakeEmbeddedAssistant : EmbeddedAssistant.EmbeddedAssistantBase
    {
        public Func<string, IEnumerable<AssistResponse>> Respond { get; set; } = _ => [];

        public List<(AssistConfig Config, string Authorization)> Requests { get; } = [];

        public override async Task Assist(IAsyncStreamReader<AssistRequest> requestStream, IServerStreamWriter<AssistResponse> responseStream, ServerCallContext context)
        {
            var authorization = context.RequestHeaders.GetValue("authorization") ?? string.Empty;
            await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
            {
                lock (Requests)
                {
                    Requests.Add((request.Config, authorization));
                }
            }

            foreach (var response in Respond(authorization))
            {
                await responseStream.WriteAsync(response, context.CancellationToken);
            }
        }
    }
}
