using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeControl.Core.Auth;

/// <summary>
/// OAuth 2.0 authorization code flow for installed apps: opens the browser, receives the
/// redirect on a loopback port and exchanges the code (with PKCE) for tokens.
/// A raw <see cref="TcpListener"/> is used instead of HttpListener so no URL ACL or admin
/// rights are needed.
/// </summary>
public sealed class LoopbackAuthorizationFlow
{
    private const int MaxRequestLines = 100;

    private readonly HttpClient _http;
    private readonly OAuthEndpoints _endpoints;

    public LoopbackAuthorizationFlow(HttpClient http, OAuthEndpoints? endpoints = null)
    {
        _http = http;
        _endpoints = endpoints ?? OAuthEndpoints.Google;
    }

    /// <summary>
    /// Runs the whole flow. <paramref name="openBrowser"/> receives the URL the user has to visit.
    /// Cancel <paramref name="cancellationToken"/> to give up waiting for the browser.
    /// </summary>
    public async Task<OAuthTokenResponse> AuthorizeAsync(
        OAuthClient client,
        IEnumerable<string> scopes,
        Action<Uri> openBrowser,
        CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{port}/";
            var state = CreateRandomToken(32);
            var codeVerifier = CreateRandomToken(64);

            var authorizationUrl = BuildAuthorizationUrl(client, scopes, redirectUri, state, CreateCodeChallenge(codeVerifier));
            openBrowser(authorizationUrl);

            var code = await ReceiveCodeAsync(listener, state, cancellationToken).ConfigureAwait(false);
            return await ExchangeCodeAsync(client, code, codeVerifier, redirectUri, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    internal Uri BuildAuthorizationUrl(OAuthClient client, IEnumerable<string> scopes, string redirectUri, string state, string codeChallenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            // Offline access + consent prompt guarantee a refresh token is returned.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
        };

        return new Uri(_endpoints.AuthorizationEndpoint + "?" + EncodeQuery(query));
    }

    internal static string CreateCodeChallenge(string codeVerifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    /// <summary>
    /// Waits for the browser to hit the redirect URI. Connections are handled concurrently
    /// because browsers open speculative connections that never send a request.
    /// </summary>
    private static async Task<string> ReceiveCodeAsync(TcpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = cancellationToken.Register(() => result.TrySetCanceled(cancellationToken));
        var stopToken = stop.Token;

        _ = Task.Run(async () =>
        {
            while (!stopToken.IsCancellationRequested)
            {
                TcpClient connection;
                try
                {
                    connection = await listener.AcceptTcpClientAsync(stopToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = HandleConnectionAsync(connection, expectedState, result, stopToken);
            }
        }, CancellationToken.None);

        try
        {
            return await result.Task.ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
        }
    }

    private static async Task HandleConnectionAsync(
        TcpClient connection, string expectedState, TaskCompletionSource<string> result, CancellationToken cancellationToken)
    {
        using var client = connection;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var token = timeout.Token;

            await using var stream = client.GetStream();
            var target = await ReadRequestTargetAsync(stream, token).ConfigureAwait(false);
            if (target is null)
            {
                return;
            }

            var queryStart = target.IndexOf('?', StringComparison.Ordinal);
            var path = queryStart >= 0 ? target[..queryStart] : target;
            if (path != "/")
            {
                // favicon.ico and similar
                await WriteResponseAsync(stream, HttpStatusCode.NotFound, string.Empty, token).ConfigureAwait(false);
                return;
            }

            var query = ParseQuery(queryStart >= 0 ? target[(queryStart + 1)..] : string.Empty);
            query.TryGetValue("state", out var state);
            query.TryGetValue("code", out var code);
            query.TryGetValue("error", out var error);

            if (!string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                // Not our request (or a forged one). Ignore it and keep waiting.
                await WriteResponseAsync(stream, HttpStatusCode.BadRequest,
                    ResultPage(false, "The sign-in response did not match the request. Please start again from Home Control."),
                    token).ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrEmpty(error))
            {
                var denied = error == "access_denied";
                var message = denied ? "Access was not granted." : $"Google returned an error: {error}";
                await WriteResponseAsync(stream, HttpStatusCode.OK, ResultPage(false, message + " You can close this tab."), token)
                    .ConfigureAwait(false);
                result.TrySetException(new OAuthException(message, error));
                return;
            }

            if (string.IsNullOrEmpty(code))
            {
                await WriteResponseAsync(stream, HttpStatusCode.BadRequest, ResultPage(false, "No authorization code was received."), token)
                    .ConfigureAwait(false);
                result.TrySetException(new OAuthException("No authorization code was received.", "missing_code"));
                return;
            }

            await WriteResponseAsync(stream, HttpStatusCode.OK,
                ResultPage(true, "You're signed in. You can close this tab and return to Home Control."),
                token).ConfigureAwait(false);
            result.TrySetResult(code);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Speculative or aborted connection; keep waiting for the real one.
        }
    }

    private async Task<OAuthTokenResponse> ExchangeCodeAsync(
        OAuthClient client, string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        };

        var tokens = await PostTokenRequestAsync(_http, _endpoints.TokenEndpoint, form, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            throw new OAuthException("Google did not return a refresh token. Remove Home Control's access at myaccount.google.com/permissions and sign in again.",
                "missing_refresh_token");
        }

        return tokens;
    }

    /// <summary>POSTs a form to the token endpoint and maps OAuth errors to exceptions.</summary>
    internal static async Task<OAuthTokenResponse> PostTokenRequestAsync(
        HttpClient http, Uri tokenEndpoint, IDictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(tokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            OAuthErrorResponse? error = null;
            try
            {
                error = JsonSerializer.Deserialize<OAuthErrorResponse>(body);
            }
            catch (JsonException)
            {
            }

            var errorCode = error?.Error;
            var description = error?.ErrorDescription ?? errorCode ?? $"HTTP {(int)response.StatusCode}";
            if (errorCode is "invalid_grant" or "unauthorized_client" or "invalid_client")
            {
                throw new AuthenticationRequiredException($"Google rejected the sign-in ({description}). Please sign in again.", errorCode);
            }

            throw new OAuthException($"Token request failed: {description}", errorCode);
        }

        var tokens = JsonSerializer.Deserialize<OAuthTokenResponse>(body);
        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
        {
            throw new OAuthException("The token response did not contain an access token.");
        }

        return tokens;
    }

    private static async Task<string?> ReadRequestTargetAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(requestLine))
        {
            return null;
        }

        // Drain the headers; the request has no body we care about.
        for (var i = 0; i < MaxRequestLines; i++)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line))
            {
                break;
            }
        }

        var parts = requestLine.Split(' ');
        return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
    }

    private static async Task WriteResponseAsync(Stream stream, HttpStatusCode status, string html, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var header =
            $"HTTP/1.1 {(int)status} {status}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ResultPage(bool success, string message)
    {
        var title = success ? "Signed in" : "Sign-in failed";
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Home Control – {{title}}</title>
            <style>
              :root { color-scheme: light dark; --bg: #f3f3f3; --card: #ffffff; --text: #1b1b1b; --accent: {{(success ? "#0f7b0f" : "#c42b1c")}}; }
              @media (prefers-color-scheme: dark) { :root { --bg: #202020; --card: #2b2b2b; --text: #ffffff; --accent: {{(success ? "#6ccb5f" : "#ff99a4")}}; } }
              body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: var(--bg); color: var(--text);
                     font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; }
              main { background: var(--card); border-radius: 8px; padding: 32px 40px; max-width: 420px; box-shadow: 0 8px 16px rgba(0,0,0,.14); }
              h1 { font-size: 20px; font-weight: 600; margin: 0 0 8px; color: var(--accent); }
              p { margin: 0; font-size: 14px; line-height: 20px; }
            </style>
            </head>
            <body><main><h1>{{title}}</h1><p>{{WebUtility.HtmlEncode(message)}}</p></main></body>
            </html>
            """;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separator >= 0 ? pair[..separator] : pair;
            var value = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            values[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return values;
    }

    private static string EncodeQuery(IDictionary<string, string> values) =>
        string.Join('&', values.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

    private static string CreateRandomToken(int byteCount) => Base64Url(RandomNumberGenerator.GetBytes(byteCount));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
