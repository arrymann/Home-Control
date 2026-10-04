using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeControl.Core.Auth;

/// <summary>An OAuth client registered in Google Cloud Console (type "Desktop app").</summary>
public sealed record OAuthClient(string ClientId, string ClientSecret)
{
    /// <summary>
    /// Reads the <c>client_secret_*.json</c> file that Google Cloud Console offers for download.
    /// Both the "installed" (Desktop app) and "web" layouts are accepted.
    /// </summary>
    public static bool TryParseClientSecretsJson(string json, out OAuthClient? client)
    {
        client = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            JsonElement section = root;
            if (root.TryGetProperty("installed", out var installed))
            {
                section = installed;
            }
            else if (root.TryGetProperty("web", out var web))
            {
                section = web;
            }

            if (section.ValueKind == JsonValueKind.Object &&
                section.TryGetProperty("client_id", out var id) && id.ValueKind == JsonValueKind.String &&
                section.TryGetProperty("client_secret", out var secret) && secret.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(id.GetString()) && !string.IsNullOrWhiteSpace(secret.GetString()))
            {
                client = new OAuthClient(id.GetString()!.Trim(), secret.GetString()!.Trim());
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }
}

/// <summary>Google's OAuth 2.0 endpoints. Overridable for tests.</summary>
public sealed record OAuthEndpoints(Uri AuthorizationEndpoint, Uri TokenEndpoint, Uri RevocationEndpoint)
{
    public static OAuthEndpoints Google { get; } = new(
        new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        new Uri("https://oauth2.googleapis.com/token"),
        new Uri("https://oauth2.googleapis.com/revoke"));
}

/// <summary>Successful response of the token endpoint.</summary>
public sealed class OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresInSeconds { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }
}

internal sealed class OAuthErrorResponse
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

/// <summary>An OAuth request failed.</summary>
public class OAuthException : Exception
{
    public OAuthException(string message, string? errorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The OAuth error code, e.g. "invalid_grant" or "access_denied".</summary>
    public string? ErrorCode { get; }
}

/// <summary>The user has to sign in (again) before the request can be made.</summary>
public sealed class AuthenticationRequiredException : OAuthException
{
    public AuthenticationRequiredException(string message, string? errorCode = null, Exception? inner = null)
        : base(message, errorCode, inner)
    {
    }
}
