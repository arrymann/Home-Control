using System.Net;
using System.Net.Sockets;
using HomeControl.Core.Auth;
using HomeControl.Core.Security;

namespace HomeControl.Core.Tests;

public class OAuthClientTests
{
    [Fact]
    public void Parses_desktop_client_secrets_file()
    {
        const string json = """
            {"installed":{"client_id":"123.apps.googleusercontent.com","project_id":"home","auth_uri":"https://accounts.google.com/o/oauth2/auth",
             "token_uri":"https://oauth2.googleapis.com/token","client_secret":"GOCSPX-abc","redirect_uris":["http://localhost"]}}
            """;

        Assert.True(OAuthClient.TryParseClientSecretsJson(json, out var client));
        Assert.Equal(new OAuthClient("123.apps.googleusercontent.com", "GOCSPX-abc"), client);
    }

    [Fact]
    public void Parses_web_client_secrets_file()
    {
        Assert.True(OAuthClient.TryParseClientSecretsJson("""{"web":{"client_id":"id","client_secret":"secret"}}""", out var client));
        Assert.Equal("id", client!.ClientId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"installed":{"client_id":"id"}}""")]
    [InlineData("""{"installed":{"client_id":"","client_secret":"x"}}""")]
    [InlineData("not json")]
    public void Rejects_incomplete_files(string json)
    {
        Assert.False(OAuthClient.TryParseClientSecretsJson(json, out _));
    }
}

public class LoopbackAuthorizationFlowTests
{
    private static readonly OAuthEndpoints Endpoints = new(
        new Uri("https://auth.test/authorize"), new Uri("https://auth.test/token"), new Uri("https://auth.test/revoke"));

    private static readonly OAuthClient Client = new("client-id", "client-secret");

    [Fact]
    public async Task Completes_flow_with_pkce_and_ignores_stray_connections()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"access_token":"at","expires_in":3599,"refresh_token":"rt","token_type":"Bearer"}"""));
        var flow = new LoopbackAuthorizationFlow(new HttpClient(handler), Endpoints);

        Uri? authorizationUrl = null;
        using var browser = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var tokens = await flow.AuthorizeAsync(Client, ["scope-a", "scope-b"], url =>
        {
            authorizationUrl = url;
            var query = ParseQuery(url);
            var redirect = new Uri(query["redirect_uri"]);

            _ = Task.Run(async () =>
            {
                // A speculative connection that never sends anything must not block the flow.
                using var idle = new TcpClient();
                await idle.ConnectAsync(redirect.Host, redirect.Port);

                Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(new Uri(redirect, "/favicon.ico"))).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync($"{redirect}?code=evil&state=wrong")).StatusCode);

                var page = await browser.GetStringAsync($"{redirect}?state={Uri.EscapeDataString(query["state"])}&code=4%2Fauth-code&scope=x");
                Assert.Contains("signed in", page);
                await Task.Delay(500);
            });
        }, cts.Token);

        Assert.Equal("at", tokens.AccessToken);
        Assert.Equal("rt", tokens.RefreshToken);

        var authQuery = ParseQuery(authorizationUrl!);
        Assert.StartsWith("https://auth.test/authorize?", authorizationUrl!.ToString());
        Assert.Equal("client-id", authQuery["client_id"]);
        Assert.Equal("code", authQuery["response_type"]);
        Assert.Equal("scope-a scope-b", authQuery["scope"]);
        Assert.Equal("S256", authQuery["code_challenge_method"]);
        Assert.Equal("offline", authQuery["access_type"]);
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/$", authQuery["redirect_uri"]);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://auth.test/token", request.RequestUri!.ToString());
        var form = ParseForm(body);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("4/auth-code", form["code"]);
        Assert.Equal("client-secret", form["client_secret"]);
        Assert.Equal(authQuery["redirect_uri"], form["redirect_uri"]);
        Assert.Equal(authQuery["code_challenge"], LoopbackAuthorizationFlow.CreateCodeChallenge(form["code_verifier"]));
    }

    [Fact]
    public async Task Access_denied_is_reported()
    {
        var flow = new LoopbackAuthorizationFlow(new HttpClient(new FakeHttpHandler((_, _) => throw new InvalidOperationException())), Endpoints);
        using var browser = new HttpClient();

        var ex = await Assert.ThrowsAsync<OAuthException>(() => flow.AuthorizeAsync(Client, ["s"], url =>
        {
            var query = ParseQuery(url);
            _ = browser.GetAsync($"{query["redirect_uri"]}?state={Uri.EscapeDataString(query["state"])}&error=access_denied");
        }, CancellationToken.None));

        Assert.Equal("access_denied", ex.ErrorCode);
    }

    [Fact]
    public async Task Can_be_cancelled_while_waiting_for_the_browser()
    {
        var flow = new LoopbackAuthorizationFlow(new HttpClient(new FakeHttpHandler((_, _) => throw new InvalidOperationException())), Endpoints);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.AuthorizeAsync(Client, ["s"], _ => { }, cts.Token));
    }

    [Fact]
    public async Task Missing_refresh_token_is_an_error()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"at","expires_in":3599}"""));
        var flow = new LoopbackAuthorizationFlow(new HttpClient(handler), Endpoints);
        using var browser = new HttpClient();

        var ex = await Assert.ThrowsAsync<OAuthException>(() => flow.AuthorizeAsync(Client, ["s"], url =>
        {
            var query = ParseQuery(url);
            _ = browser.GetAsync($"{query["redirect_uri"]}?state={Uri.EscapeDataString(query["state"])}&code=c");
        }, CancellationToken.None));

        Assert.Equal("missing_refresh_token", ex.ErrorCode);
    }

    internal static Dictionary<string, string> ParseQuery(Uri uri) => ParseForm(uri.Query.TrimStart('?'));

    internal static Dictionary<string, string> ParseForm(string form) =>
        form.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
}

public class GoogleAccountTests
{
    private static readonly OAuthEndpoints Endpoints = new(
        new Uri("https://auth.test/authorize"), new Uri("https://auth.test/token"), new Uri("https://auth.test/revoke"));

    [Fact]
    public async Task Refreshes_and_caches_access_token_until_close_to_expiry()
    {
        using var dir = new TempDirectory();
        var secrets = SignedInSecrets(dir);
        var calls = 0;
        var handler = new FakeHttpHandler((_, _) =>
        {
            calls++;
            return FakeHttpHandler.Json(HttpStatusCode.OK, $$"""{"access_token":"at-{{calls}}","expires_in":3600}""");
        });
        var time = new ManualTimeProvider();
        var account = new GoogleAccount(new HttpClient(handler), secrets, Endpoints, time);

        Assert.True(account.IsSignedIn);
        Assert.Equal("at-1", await account.GetAccessTokenAsync(default));
        Assert.Equal("at-1", await account.GetAccessTokenAsync(default));

        time.Now += TimeSpan.FromMinutes(59);
        Assert.Equal("at-2", await account.GetAccessTokenAsync(default));

        account.InvalidateAccessToken();
        Assert.Equal("at-3", await account.GetAccessTokenAsync(default));

        var form = LoopbackAuthorizationFlowTests.ParseForm(handler.Requests[0].Body);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("rt", form["refresh_token"]);
        Assert.Equal("id", form["client_id"]);
    }

    [Fact]
    public async Task Concurrent_requests_refresh_once()
    {
        using var dir = new TempDirectory();
        var calls = 0;
        var handler = new FakeHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(50);
            return FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"at","expires_in":3600}""");
        });
        var account = new GoogleAccount(new HttpClient(handler), SignedInSecrets(dir), Endpoints);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => account.GetAccessTokenAsync(default).AsTask()));

        Assert.All(tokens, t => Assert.Equal("at", t));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Revoked_refresh_token_signs_out()
    {
        using var dir = new TempDirectory();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}"""));
        var account = new GoogleAccount(new HttpClient(handler), SignedInSecrets(dir), Endpoints);
        var changed = 0;
        account.StateChanged += (_, _) => changed++;

        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() => account.GetAccessTokenAsync(default).AsTask());

        Assert.Equal("invalid_grant", ex.ErrorCode);
        Assert.False(account.IsSignedIn);
        Assert.True(account.HasClient);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Server_errors_do_not_sign_out()
    {
        using var dir = new TempDirectory();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.InternalServerError, "oops"));
        var account = new GoogleAccount(new HttpClient(handler), SignedInSecrets(dir), Endpoints);

        var ex = await Assert.ThrowsAsync<OAuthException>(() => account.GetAccessTokenAsync(default).AsTask());

        Assert.IsNotType<AuthenticationRequiredException>(ex);
        Assert.True(account.IsSignedIn);
    }

    [Fact]
    public async Task Without_sign_in_asks_for_authentication()
    {
        using var dir = new TempDirectory();
        var secrets = new SecretStore(dir.File("s.dat"), new XorProtector());
        var account = new GoogleAccount(new HttpClient(new FakeHttpHandler((_, _) => throw new InvalidOperationException())), secrets, Endpoints);

        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => account.GetAccessTokenAsync(default).AsTask());

        account.SetClient(new OAuthClient("id", "secret"));
        Assert.True(account.HasClient);
        Assert.False(account.IsSignedIn);
        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => account.GetAccessTokenAsync(default).AsTask());
    }

    [Fact]
    public void Changing_client_id_signs_out_but_updating_secret_does_not()
    {
        using var dir = new TempDirectory();
        var secrets = SignedInSecrets(dir);
        var account = new GoogleAccount(new HttpClient(new FakeHttpHandler((_, _) => throw new InvalidOperationException())), secrets, Endpoints);

        account.SetClient(new OAuthClient("id", "new-secret"));
        Assert.True(account.IsSignedIn);

        account.SetClient(new OAuthClient("other-id", "new-secret"));
        Assert.False(account.IsSignedIn);
        Assert.Equal("other-id", account.Client!.ClientId);
    }

    [Fact]
    public async Task Sign_out_revokes_and_forgets_token()
    {
        using var dir = new TempDirectory();
        var handler = new FakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var account = new GoogleAccount(new HttpClient(handler), SignedInSecrets(dir), Endpoints);

        await account.SignOutAsync(default);

        Assert.False(account.IsSignedIn);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://auth.test/revoke", request.RequestUri!.ToString());
        Assert.Equal("token=rt", body);
    }

    private static SecretStore SignedInSecrets(TempDirectory dir)
    {
        var secrets = new SecretStore(dir.File("secrets.dat"), new XorProtector());
        secrets.Set(SecretStore.ClientIdKey, "id");
        secrets.Set(SecretStore.ClientSecretKey, "secret");
        secrets.Set(SecretStore.RefreshTokenKey, "rt");
        return secrets;
    }
}
