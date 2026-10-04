using HomeControl.Core.Security;

namespace HomeControl.Core.Auth;

/// <summary>Supplies OAuth access tokens for API calls.</summary>
public interface IAccessTokenProvider
{
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>Forget the cached access token (e.g. after the API rejected it).</summary>
    void InvalidateAccessToken();
}

/// <summary>
/// The user's Google sign-in: the OAuth client, the refresh token (both kept in the
/// encrypted <see cref="SecretStore"/>) and a cached access token.
/// </summary>
public sealed class GoogleAccount : IAccessTokenProvider
{
    public const string AssistantScope = "https://www.googleapis.com/auth/assistant-sdk-prototype";

    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly SecretStore _secrets;
    private readonly OAuthEndpoints _endpoints;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiry;

    public GoogleAccount(HttpClient http, SecretStore secrets, OAuthEndpoints? endpoints = null, TimeProvider? timeProvider = null)
    {
        _http = http;
        _secrets = secrets;
        _endpoints = endpoints ?? OAuthEndpoints.Google;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised (on any thread) when the client or sign-in state changes.</summary>
    public event EventHandler? StateChanged;

    public OAuthClient? Client
    {
        get
        {
            var id = _secrets.Get(SecretStore.ClientIdKey);
            var secret = _secrets.Get(SecretStore.ClientSecretKey);
            return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) ? null : new OAuthClient(id, secret);
        }
    }

    public bool HasClient => Client is not null;

    public bool IsSignedIn => HasClient && !string.IsNullOrEmpty(_secrets.Get(SecretStore.RefreshTokenKey));

    /// <summary>
    /// Stores a new OAuth client. Tokens belong to a client, so switching to a different
    /// client signs the user out.
    /// </summary>
    public void SetClient(OAuthClient? client)
    {
        var current = Client;
        if (current == client)
        {
            return;
        }

        if (current?.ClientId != client?.ClientId)
        {
            ClearTokens();
        }

        _secrets.Set(SecretStore.ClientIdKey, client?.ClientId);
        _secrets.Set(SecretStore.ClientSecretKey, client?.ClientSecret);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs the browser sign-in and stores the refresh token.</summary>
    public async Task SignInAsync(Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        var client = Client ?? throw new InvalidOperationException("Set up the OAuth client before signing in.");
        var flow = new LoopbackAuthorizationFlow(_http, _endpoints);
        var tokens = await flow.AuthorizeAsync(client, [AssistantScope], openBrowser, cancellationToken).ConfigureAwait(false);

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _secrets.Set(SecretStore.RefreshTokenKey, tokens.RefreshToken);
            CacheAccessToken(tokens);
        }
        finally
        {
            _refreshLock.Release();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Revokes the refresh token at Google (best effort) and forgets it locally.</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        var refreshToken = _secrets.Get(SecretStore.RefreshTokenKey);
        ClearTokens();
        StateChanged?.Invoke(this, EventArgs.Empty);

        if (string.IsNullOrEmpty(refreshToken))
        {
            return;
        }

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken });
            using var response = await _http.PostAsync(_endpoints.RevocationEndpoint, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The local token is gone either way.
        }
    }

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetCachedToken(out var cached))
        {
            return cached;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedToken(out cached))
            {
                return cached;
            }

            var client = Client ?? throw new AuthenticationRequiredException("Set up your Google OAuth client in Settings › Account.");
            var refreshToken = _secrets.Get(SecretStore.RefreshTokenKey);
            if (string.IsNullOrEmpty(refreshToken))
            {
                throw new AuthenticationRequiredException("Sign in with Google in Settings › Account.");
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = client.ClientId,
                ["client_secret"] = client.ClientSecret,
            };

            OAuthTokenResponse tokens;
            try
            {
                tokens = await LoopbackAuthorizationFlow.PostTokenRequestAsync(_http, _endpoints.TokenEndpoint, form, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AuthenticationRequiredException)
            {
                // The refresh token was revoked or expired (7 days for apps in "Testing").
                _secrets.Set(SecretStore.RefreshTokenKey, null);
                StateChanged?.Invoke(this, EventArgs.Empty);
                throw;
            }

            if (!string.IsNullOrEmpty(tokens.RefreshToken) && tokens.RefreshToken != refreshToken)
            {
                _secrets.Set(SecretStore.RefreshTokenKey, tokens.RefreshToken);
            }

            CacheAccessToken(tokens);
            return tokens.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void InvalidateAccessToken()
    {
        _accessToken = null;
        _accessTokenExpiry = DateTimeOffset.MinValue;
    }

    private bool TryGetCachedToken(out string token)
    {
        token = _accessToken ?? string.Empty;
        return _accessToken is not null && _time.GetUtcNow() < _accessTokenExpiry - ExpiryMargin;
    }

    private void CacheAccessToken(OAuthTokenResponse tokens)
    {
        var lifetime = tokens.ExpiresInSeconds > 0 ? TimeSpan.FromSeconds(tokens.ExpiresInSeconds) : TimeSpan.FromMinutes(30);
        _accessTokenExpiry = _time.GetUtcNow() + lifetime;
        _accessToken = tokens.AccessToken;
    }

    private void ClearTokens()
    {
        InvalidateAccessToken();
        _secrets.Set(SecretStore.RefreshTokenKey, null);
    }
}
