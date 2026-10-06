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
    private readonly Dictionary<string, string> _traced = new(StringComparer.Ordinal);
    private HashSet<string> _mediaIds = new(StringComparer.Ordinal);

    public GoogleHomeClient(IFoyerTransport transport)
    {
        _transport = transport;
    }

    /// <summary>
    /// Gets raw readings, once per change, so a wrong one can be looked into (the app writes them
    /// to its log): what the device list says about TVs, and the state records of TVs and of
    /// devices Google reports as offline, with an error, or as not reporting on/off.
    /// </summary>
    public Action<string>? Trace { get; set; }

    public async Task<GoogleHomeGraph> GetHomeGraphAsync(CancellationToken cancellationToken)
    {
        var body = await CallAsync(FoyerCodec.StructuresService, FoyerCodec.GetHomeGraphMethod, FoyerCodec.BuildGetHomeGraph(), cancellationToken)
            .ConfigureAwait(false);
        var graph = Parse(() => FoyerCodec.ParseHomeGraph(body));
        TraceMediaDevices(body);
        return graph;
    }

    public async Task<IReadOnlyList<GoogleHomeDeviceState>> GetStatesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken cancellationToken)
    {
        var states = new List<GoogleHomeDeviceState>();
        foreach (var chunk in deviceIds.Distinct(StringComparer.Ordinal).Chunk(MaxIdsPerRequest))
        {
            var body = await CallAsync(FoyerCodec.HomeControlService, FoyerCodec.GetTraitsMethod, FoyerCodec.BuildGetTraits(chunk), cancellationToken)
                .ConfigureAwait(false);
            var parsed = Parse(() => FoyerCodec.ParseTraits(body));
            TraceStates(body, parsed);
            states.AddRange(parsed);
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

        var echoes = Parse(() => FoyerCodec.ParseTraits(body));
        TraceStates(body, echoes);
        var echoed = echoes.FirstOrDefault(s => s.Id == deviceId);
        if (echoed is { IsOn: not null } or { Error: not null })
        {
            return echoed;
        }

        var readBack = await GetStatesAsync([deviceId], cancellationToken).ConfigureAwait(false);
        return readBack.FirstOrDefault(s => s.Id == deviceId) ?? echoed;
    }

    private void TraceMediaDevices(string body)
    {
        if (Trace is not { } trace)
        {
            return;
        }

        try
        {
            var media = FoyerCodec.DescribeMediaDevices(body);
            _mediaIds = new HashSet<string>(media.Select(m => m.Id), StringComparer.Ordinal);
            foreach (var (id, text) in media)
            {
                if (IsNew("graph " + id, text))
                {
                    trace($"Google Home lists TV-like device {id}: {text}");
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Diagnostics only.
        }
    }

    private void TraceStates(string body, IReadOnlyList<GoogleHomeDeviceState> states)
    {
        if (Trace is not { } trace)
        {
            return;
        }

        var mediaIds = _mediaIds;
        var traced = states.Where(s => s.Online == false || s.Error is not null || s.CommandOnly || mediaIds.Contains(s.Id)).ToList();
        if (traced.Count == 0)
        {
            return;
        }

        try
        {
            var raw = FoyerCodec.RawTraitsById(body);
            foreach (var state in traced)
            {
                var record = raw.GetValueOrDefault(state.Id, "?");
                record = record.Length <= 4000 ? record : record[..4000] + "…";
                if (IsNew(state.Id, record))
                {
                    trace($"Google Home device {state.Id}: online={state.Online?.ToString() ?? "?"}, on={state.IsOn?.ToString() ?? "?"}" +
                          $"{(state.CommandOnly ? " (command-only)" : "")}, error={state.Error ?? "none"}. Raw: {record}");
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Diagnostics only.
        }
    }

    /// <summary>True the first time <paramref name="text"/> is seen for <paramref name="key"/> (and whenever it changes).</summary>
    private bool IsNew(string key, string text)
    {
        lock (_traced)
        {
            if (_traced.TryGetValue(key, out var last) && last == text)
            {
                return false;
            }

            _traced[key] = text;
            return true;
        }
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
