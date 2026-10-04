using System.Text;
using Google.Assistant.Embedded.V1Alpha2;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using HomeControl.Core.Auth;
using HomeControl.Core.Settings;

namespace HomeControl.Core.Assistant;

/// <summary>What the Assistant answered to a text query.</summary>
/// <param name="Text">The spoken/displayed answer, if any.</param>
public sealed record AssistantReply(string Text);

/// <summary>Sends text queries to Google Assistant.</summary>
public interface IAssistantClient
{
    Task<AssistantReply> SendTextQueryAsync(string query, CancellationToken cancellationToken);
}

/// <summary>An Assistant request failed. <see cref="IsAuthenticationError"/> means the user must sign in again.</summary>
public sealed class AssistantException : Exception
{
    public AssistantException(string message, bool isAuthenticationError = false, Exception? inner = null)
        : base(message, inner)
    {
        IsAuthenticationError = isAuthenticationError;
    }

    public bool IsAuthenticationError { get; }
}

/// <summary>
/// Client for the Google Assistant SDK gRPC API (<c>google.assistant.embedded.v1alpha2</c>).
/// Text queries such as "turn on the kitchen light" control any device linked to the
/// user's Google Home, exactly as if spoken to a speaker.
/// </summary>
public sealed class AssistantClient : IAssistantClient, IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly IAccessTokenProvider _tokens;
    private readonly Func<AssistantSettings> _settings;
    private readonly HttpMessageHandler? _handler;
    private readonly object _channelGate = new();
    private GrpcChannel? _channel;
    private string? _channelEndpoint;

    /// <param name="tokens">OAuth access tokens with the Assistant scope.</param>
    /// <param name="settings">Returns the current settings (read on every request).</param>
    /// <param name="handler">Optional HTTP handler (tests).</param>
    public AssistantClient(IAccessTokenProvider tokens, Func<AssistantSettings> settings, HttpMessageHandler? handler = null)
    {
        _tokens = tokens;
        _settings = settings;
        _handler = handler;
    }

    public async Task<AssistantReply> SendTextQueryAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        try
        {
            return await SendOnceAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            // The cached access token may have been revoked or expired early; retry once with a fresh one.
            _tokens.InvalidateAccessToken();
            try
            {
                return await SendOnceAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException retryEx)
            {
                throw Translate(retryEx);
            }
        }
        catch (RpcException ex)
        {
            throw Translate(ex);
        }
    }

    private async Task<AssistantReply> SendOnceAsync(string query, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var accessToken = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var client = new EmbeddedAssistant.EmbeddedAssistantClient(GetChannel(settings.Endpoint));

        var headers = new Metadata { { "authorization", "Bearer " + accessToken } };
        using var call = client.Assist(headers, DateTime.UtcNow + Deadline, cancellationToken);

        await call.RequestStream.WriteAsync(CreateRequest(query, settings), cancellationToken).ConfigureAwait(false);
        await call.RequestStream.CompleteAsync().ConfigureAwait(false);

        var displayText = new StringBuilder();
        var html = new StringBuilder();
        await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var text = response.DialogStateOut?.SupplementalDisplayText;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (displayText.Length > 0)
                {
                    displayText.Append(' ');
                }

                displayText.Append(text.Trim());
            }

            if (response.ScreenOut is { Data.IsEmpty: false } screen)
            {
                html.Append(screen.Data.ToStringUtf8());
            }
        }

        var reply = displayText.Length > 0 ? displayText.ToString() : AssistantHtml.ExtractText(html.ToString());
        return new AssistantReply(reply);
    }

    internal static AssistRequest CreateRequest(string query, AssistantSettings settings) => new()
    {
        Config = new AssistConfig
        {
            TextQuery = query,
            // Audio output is mandatory in the API even for text queries; keep it silent.
            AudioOutConfig = new AudioOutConfig
            {
                Encoding = AudioOutConfig.Types.Encoding.Linear16,
                SampleRateHertz = 16000,
                VolumePercentage = 0,
            },
            // The HTML "screen" is the only place some answers show up as text.
            ScreenOutConfig = new ScreenOutConfig { ScreenMode = ScreenOutConfig.Types.ScreenMode.Playing },
            DialogStateIn = new DialogStateIn
            {
                LanguageCode = settings.LanguageCode,
                IsNewConversation = true,
            },
            DeviceConfig = new DeviceConfig
            {
                DeviceId = settings.DeviceId,
                DeviceModelId = settings.DeviceModelId,
            },
        },
    };

    private GrpcChannel GetChannel(string endpoint)
    {
        lock (_channelGate)
        {
            if (_channel is not null && _channelEndpoint == endpoint)
            {
                return _channel;
            }

            _channel?.Dispose();
            _channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
            {
                HttpHandler = _handler ?? new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
                },
                DisposeHttpClient = _handler is null,
                ThrowOperationCanceledOnCancellation = true,
                MaxReceiveMessageSize = 16 * 1024 * 1024,
            });
            _channelEndpoint = endpoint;
            return _channel;
        }
    }

    private static AssistantException Translate(RpcException ex) => ex.StatusCode switch
    {
        StatusCode.Unauthenticated => new AssistantException("Google rejected the sign-in. Please sign in again.", true, ex),
        StatusCode.PermissionDenied => new AssistantException(
            "Permission denied. Make sure the Google Assistant API is enabled in your Google Cloud project and your account is a test user of the OAuth consent screen.",
            false, ex),
        StatusCode.ResourceExhausted => new AssistantException("Google Assistant quota exceeded. Try again later.", false, ex),
        StatusCode.DeadlineExceeded => new AssistantException("Google Assistant did not answer in time.", false, ex),
        StatusCode.Unavailable => new AssistantException("Google Assistant is unreachable. Check your internet connection.", false, ex),
        StatusCode.Cancelled => new AssistantException("The request was cancelled.", false, ex),
        _ => new AssistantException($"Google Assistant error: {ex.Status.Detail} ({ex.StatusCode}).", false, ex),
    };

    public void Dispose()
    {
        lock (_channelGate)
        {
            _channel?.Dispose();
            _channel = null;
        }
    }
}
