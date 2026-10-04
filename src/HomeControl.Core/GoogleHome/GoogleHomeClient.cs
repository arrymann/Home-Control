namespace HomeControl.Core.GoogleHome;

/// <summary>Raw HTTP result of one Foyer RPC.</summary>
public sealed record FoyerResponse(int StatusCode, string Body);

/// <summary>
/// Sends Foyer RPCs. The Windows app implements this with a signed-in home.google.com page
/// in WebView2, because the API only accepts that origin's session cookies.
/// </summary>
public interface IFoyerTransport
{
    /// <summary>
    /// POSTs <paramref name="body"/> to <c>google.internal.home.foyer.v1.{service}/{method}</c>.
    /// Throws <see cref="GoogleHomeSignInRequiredException"/> when there is no signed-in session.
    /// </summary>
    Task<FoyerResponse> SendAsync(string service, string method, string body, CancellationToken cancellationToken);
}

/// <summary>A Google Home request failed.</summary>
public class GoogleHomeException : Exception
{
    public GoogleHomeException(string message, int? httpStatus = null, int? rpcCode = null, Exception? inner = null)
        : base(message, inner)
    {
        HttpStatus = httpStatus;
        RpcCode = rpcCode;
    }

    public int? HttpStatus { get; }

    /// <summary>google.rpc.Code from the error body, e.g. 3 = INVALID_ARGUMENT.</summary>
    public int? RpcCode { get; }
}

/// <summary>The Google Home session is missing or expired; the user has to sign in again.</summary>
public sealed class GoogleHomeSignInRequiredException : GoogleHomeException
{
    public GoogleHomeSignInRequiredException(string message = "Sign in to Google Home in Settings › Account.", int? httpStatus = null)
        : base(message, httpStatus)
    {
    }
}

/// <summary>Typed calls to the Google Home web API (device list, state, on/off).</summary>
public sealed class GoogleHomeClient
{
    private const int MaxIdsPerRequest = 50;
    private const int RpcUnauthenticated = 16;
    private const int RpcPermissionDenied = 7;

    private readonly IFoyerTransport _transport;

    public GoogleHomeClient(IFoyerTransport transport)
    {
        _transport = transport;
    }

    public async Task<GoogleHomeGraph> GetHomeGraphAsync(CancellationToken cancellationToken)
    {
        var body = await CallAsync(FoyerCodec.StructuresService, FoyerCodec.GetHomeGraphMethod, FoyerCodec.BuildGetHomeGraph(), cancellationToken)
            .ConfigureAwait(false);
        return Parse(() => FoyerCodec.ParseHomeGraph(body));
    }

    public async Task<IReadOnlyList<GoogleHomeDeviceState>> GetStatesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken cancellationToken)
    {
        var states = new List<GoogleHomeDeviceState>();
        foreach (var chunk in deviceIds.Distinct(StringComparer.Ordinal).Chunk(MaxIdsPerRequest))
        {
            var body = await CallAsync(FoyerCodec.HomeControlService, FoyerCodec.GetTraitsMethod, FoyerCodec.BuildGetTraits(chunk), cancellationToken)
                .ConfigureAwait(false);
            states.AddRange(Parse(() => FoyerCodec.ParseTraits(body)));
        }

        return states;
    }

    /// <summary>Switches a device and returns its state as echoed by Google (read back if not echoed).</summary>
    public async Task<GoogleHomeDeviceState?> SetOnOffAsync(
        string deviceId, string? agentId, string? partnerDeviceId, bool on, CancellationToken cancellationToken)
    {
        var request = FoyerCodec.BuildSetOnOff(deviceId, agentId, partnerDeviceId, on);
        var body = await CallAsync(FoyerCodec.HomeControlService, FoyerCodec.UpdateTraitsMethod, request, cancellationToken)
            .ConfigureAwait(false);

        var echoed = Parse(() => FoyerCodec.ParseTraits(body)).FirstOrDefault(s => s.Id == deviceId);
        if (echoed is { IsOn: not null } or { Error: not null })
        {
            return echoed;
        }

        var readBack = await GetStatesAsync([deviceId], cancellationToken).ConfigureAwait(false);
        return readBack.FirstOrDefault(s => s.Id == deviceId) ?? echoed;
    }

    private async Task<string> CallAsync(string service, string method, string request, CancellationToken cancellationToken)
    {
        var response = await _transport.SendAsync(service, method, request, cancellationToken).ConfigureAwait(false);
        var body = FoyerCodec.StripXssiPrefix(response.Body ?? string.Empty);

        if (response.StatusCode is >= 200 and < 300)
        {
            return body;
        }

        FoyerCodec.TryParseError(body, out var rpcCode, out var rpcMessage);
        if (response.StatusCode is 401 or 403 || rpcCode is RpcUnauthenticated or RpcPermissionDenied)
        {
            throw new GoogleHomeSignInRequiredException(
                "Google Home rejected the session. Sign in to Google Home again in Settings › Account.", response.StatusCode);
        }

        var detail = !string.IsNullOrWhiteSpace(rpcMessage) ? rpcMessage
            : response.StatusCode <= 0 ? "Google Home could not be reached."
            : $"HTTP {response.StatusCode}";
        throw new GoogleHomeException($"Google Home: {detail}", response.StatusCode, rpcCode == 0 ? null : rpcCode);
    }

    private static T Parse<T>(Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new GoogleHomeException("Google Home sent a response Home Control doesn't understand.", inner: ex);
        }
    }
}
