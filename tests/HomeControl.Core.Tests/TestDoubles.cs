using System.Net;
using HomeControl.Core.Assistant;
using HomeControl.Core.Auth;
using HomeControl.Core.Security;

namespace HomeControl.Core.Tests;

/// <summary>HTTP handler that answers with a delegate and records requests.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;

    public FakeHttpHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request, body));
        }

        return _respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}

/// <summary>Reversible "encryption" so tests can see that data is transformed.</summary>
internal sealed class XorProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();

    public byte[] Unprotect(byte[] data) => Protect(data);
}

internal sealed class FakeAssistantClient : IAssistantClient
{
    private readonly Func<string, string> _reply;

    public FakeAssistantClient(Func<string, string> reply)
    {
        _reply = reply;
    }

    public List<string> Queries { get; } = [];

    public Task<AssistantReply> SendTextQueryAsync(string query, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        return Task.FromResult(new AssistantReply(_reply(query)));
    }
}

internal sealed class StaticTokenProvider : IAccessTokenProvider
{
    private int _version;

    public int InvalidateCount { get; private set; }

    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult($"token-{_version}");

    public void InvalidateAccessToken()
    {
        InvalidateCount++;
        _version++;
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Creates a unique temporary directory and deletes it on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "homecontrol-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
