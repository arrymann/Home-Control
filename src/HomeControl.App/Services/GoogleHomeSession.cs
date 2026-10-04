using System.Text.Json;
using HomeControl.Core.GoogleHome;
using HomeControl.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace HomeControl.Services;

public enum GoogleHomeConnection
{
    /// <summary>Not checked yet in this run.</summary>
    Unknown,

    /// <summary>The page loaded signed in and requests work.</summary>
    Connected,

    /// <summary>Google sent the page to the sign-in screen, or rejected the session.</summary>
    SignedOut,

    /// <summary>The page or the API could not be reached.</summary>
    Error,

    /// <summary>The Microsoft Edge WebView2 Runtime is missing.</summary>
    Unavailable,
}

/// <summary>
/// The Google Home web session. A hidden WebView2 keeps https://home.google.com loaded with
/// the user's Google sign-in (stored in a private WebView2 profile under
/// %LOCALAPPDATA%\HomeControl\WebView2) and sends the same API requests the web app sends,
/// from inside that page: the API only accepts the home.google.com origin and its cookies.
/// Every request is authorized like the web app does it, with a SAPISIDHASH computed from
/// the session cookie. All WebView2 calls are marshalled to the UI thread.
/// </summary>
internal sealed class GoogleHomeSession : IFoyerTransport, IDisposable
{
    public const string HomeUrl = "https://home.google.com/";
    private const string ApiBase = "https://googlehomefoyer-pa.clients6.google.com/$rpc/google.internal.home.foyer.v1.";
    private const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PageMaxAge = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan IdleBeforeSuspend = TimeSpan.FromMinutes(2);

    private readonly DispatcherQueue _dispatcher;
    private readonly IntPtr _parentWindow;
    private readonly Func<GoogleHomeSettings> _settings;
    private readonly SemaphoreSlim _pageGate = new(1, 1);
    private readonly DispatcherQueueTimer _idleTimer;

    private Task<CoreWebView2Environment>? _environment;
    private CoreWebView2Controller? _controller;
    private DateTimeOffset _pageLoadedAt = DateTimeOffset.MinValue;
    private bool _pageStale = true;
    private int _activeRequests;
    private GoogleHomeConnection _state;

    /// <param name="parentWindow">A window handle to host the hidden browser in (never shown).</param>
    public GoogleHomeSession(DispatcherQueue dispatcher, IntPtr parentWindow, Func<GoogleHomeSettings> settings)
    {
        _dispatcher = dispatcher;
        _parentWindow = parentWindow;
        _settings = settings;
        _idleTimer = dispatcher.CreateTimer();
        _idleTimer.IsRepeating = false;
        _idleTimer.Interval = IdleBeforeSuspend;
        _idleTimer.Tick += async (_, _) => await SuspendIfIdleAsync();
    }

    public static string UserDataFolder => Path.Combine(AppPaths.DataDirectory, "WebView2");

    /// <summary>Raised on the UI thread when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    public GoogleHomeConnection State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The shared browser environment (one profile for the hidden page and the sign-in window).</summary>
    public Task<CoreWebView2Environment> GetEnvironmentAsync() => RunOnUiThreadAsync(() =>
    {
        if (_environment is { IsFaulted: true } or { IsCanceled: true })
        {
            _environment = null; // retry, e.g. after the WebView2 Runtime was installed
        }

        return _environment ??= CreateEnvironmentAsync();
    });

    /// <summary>Makes the next request reload the page, e.g. right after the user signed in.</summary>
    public void Invalidate() => _pageStale = true;

    public Task<FoyerResponse> SendAsync(string service, string method, string body, CancellationToken cancellationToken) =>
        RunOnUiThreadAsync(() => SendOnUiThreadAsync(service, method, body, cancellationToken));

    /// <summary>Signs out: deletes cookies and all other data of the private browser profile.</summary>
    public Task SignOutAsync() => RunOnUiThreadAsync(async () =>
    {
        await _pageGate.WaitAsync();
        try
        {
            var core = await EnsureControllerAsync();
            await core.Profile.ClearBrowsingDataAsync();
            _pageStale = true;
            State = GoogleHomeConnection.SignedOut;
        }
        finally
        {
            _pageGate.Release();
        }

        return true;
    });

    /// <summary>Runs a script in the hidden page and returns its result as JSON (smoke test only).</summary>
    internal Task<string> EvaluateForTestAsync(string expression) => RunOnUiThreadAsync(async () =>
    {
        var core = await EnsureControllerAsync();
        return await EvaluateAsync(core, expression);
    });

    private async Task<FoyerResponse> SendOnUiThreadAsync(string service, string method, string body, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeRequests);
        _idleTimer.Stop();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var core = await EnsurePageAsync(cancellationToken);
                var settings = _settings();
                var script = BuildRequestScript(ApiBase + service + "/" + method, body, settings.ApiKey, settings.AuthUser);

                string json;
                try
                {
                    json = await EvaluateAsync(core, script).WaitAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The page navigated or its renderer was replaced mid-request: reload once and retry.
                    Log.Error($"Google Home request {method} failed in the page (attempt {attempt + 1})", ex);
                    _pageStale = true;
                    if (attempt == 0)
                    {
                        continue;
                    }

                    throw ex as GoogleHomeException ?? new GoogleHomeException("The Google Home page stopped responding.", inner: ex);
                }

                var response = ParseRequestResult(json);
                State = response.StatusCode switch
                {
                    401 or 403 => GoogleHomeConnection.SignedOut,
                    >= 200 and < 300 => GoogleHomeConnection.Connected,
                    _ => State,
                };
                return response;
            }
        }
        catch (GoogleHomeSignInRequiredException)
        {
            State = GoogleHomeConnection.SignedOut;
            throw;
        }
        catch (GoogleHomeException)
        {
            if (State != GoogleHomeConnection.Unavailable)
            {
                State = GoogleHomeConnection.Error;
            }

            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // WebView2 failures (e.g. the browser process could not start) surface as COM errors.
            Log.Error($"Google Home request {method} failed", ex);
            _pageStale = true;
            State = GoogleHomeConnection.Error;
            throw new GoogleHomeException("The Google Home page could not be opened: " + ex.Message, inner: ex);
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeRequests) == 0)
            {
                _idleTimer.Start();
            }
        }
    }

    /// <summary>Returns the hidden page, (re)loading home.google.com when needed.</summary>
    private async Task<CoreWebView2> EnsurePageAsync(CancellationToken cancellationToken)
    {
        await _pageGate.WaitAsync(cancellationToken);
        try
        {
            var core = await EnsureControllerAsync();
            if (core.IsSuspended)
            {
                core.Resume();
            }

            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;

            if (_pageStale || DateTimeOffset.UtcNow - _pageLoadedAt > PageMaxAge || !IsHomePage(core.Source))
            {
                await NavigateAsync(core, HomeUrl, cancellationToken);
                _pageLoadedAt = DateTimeOffset.UtcNow;
                _pageStale = false;
            }

            if (!IsHomePage(core.Source))
            {
                // Google redirected to its sign-in page: there is no session.
                _pageStale = true;
                throw new GoogleHomeSignInRequiredException();
            }

            return core;
        }
        finally
        {
            _pageGate.Release();
        }
    }

    private async Task<CoreWebView2> EnsureControllerAsync()
    {
        if (_controller is { } existing)
        {
            return existing.CoreWebView2;
        }

        var environment = await GetEnvironmentAsync();
        var controller = await environment.CreateCoreWebView2ControllerAsync(
            CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_parentWindow));

        // Never shown, but sized like a desktop window so the web app lays out normally.
        controller.IsVisible = false;
        controller.Bounds = new Rect(0, 0, 1280, 900);

        var core = controller.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.NewWindowRequested += (_, args) => args.Handled = true; // never open popups
        core.ProcessFailed += (_, args) =>
        {
            Log.Info($"Google Home browser process failed ({args.ProcessFailedKind}); it will be recreated.");
            if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                _environment = null;
            }

            // Recreated on the next request, after this event handler has returned.
            _dispatcher.TryEnqueue(() =>
            {
                if (_controller == controller)
                {
                    ResetController();
                }
            });
        };

        _controller = controller;
        _pageStale = true;
        return core;
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        try
        {
            Directory.CreateDirectory(UserDataFolder);
            return await CoreWebView2Environment.CreateWithOptionsAsync(
                string.Empty, UserDataFolder, new CoreWebView2EnvironmentOptions());
        }
        catch (Exception ex)
        {
            State = GoogleHomeConnection.Unavailable;
            Log.Error("Creating the WebView2 environment", ex);
            throw new GoogleHomeException(
                $"Google Home needs the Microsoft Edge WebView2 Runtime. Install it from {WebView2DownloadUrl} and try again.", inner: ex);
        }
    }

    private static async Task NavigateAsync(CoreWebView2 core, string url, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            // A navigation that was still running is cancelled by ours (or ours by a redirect):
            // wait for the one that actually finishes.
            if (!args.IsSuccess && args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            {
                return;
            }

            completed.TrySetResult(args);
        }

        core.NavigationCompleted += OnCompleted;
        try
        {
            core.Navigate(url);
            var finished = await Task.WhenAny(completed.Task, Task.Delay(NavigationTimeout, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (finished != completed.Task)
            {
                throw new GoogleHomeException("home.google.com did not load in time. Check your internet connection.");
            }

            var result = await completed.Task;
            if (!result.IsSuccess && !IsGoogleSignInPage(core.Source))
            {
                throw new GoogleHomeException($"home.google.com could not be loaded ({result.WebErrorStatus}).");
            }
        }
        finally
        {
            core.NavigationCompleted -= OnCompleted;
        }
    }

    /// <summary>Evaluates a script that returns a promise and gives back the awaited value.</summary>
    private static async Task<string> EvaluateAsync(CoreWebView2 core, string expression)
    {
        var parameters = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true });
        var raw = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.TryGetProperty("exceptionDetails", out var exception))
        {
            throw new GoogleHomeException("A script in the Google Home page failed: " + exception.GetRawText());
        }

        if (root.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        }

        return "null";
    }

    /// <summary>
    /// A fetch from the home.google.com page with the web app's headers. The authorization
    /// value is computed per request: "SAPISIDHASH ts_sha1(ts + ' ' + SAPISID + ' ' + origin)"
    /// (plus the 1P/3P variants), exactly like Google's own web clients.
    /// </summary>
    internal static string BuildRequestScript(string url, string body, string apiKey, int authUser) =>
        $$"""
        (async () => {
          const jar = {};
          for (const part of document.cookie.split(';')) {
            const i = part.indexOf('=');
            if (i > 0) jar[part.slice(0, i).trim()] = part.slice(i + 1).trim();
          }
          const base = jar['SAPISID'] || jar['__Secure-3PAPISID'] || jar['__Secure-1PAPISID'];
          if (!base) return JSON.stringify({ status: 401, body: '' });
          const ts = Math.floor(Date.now() / 1000);
          const hash = async (value) => {
            const data = new TextEncoder().encode(ts + ' ' + value + ' ' + location.origin);
            const digest = await crypto.subtle.digest('SHA-1', data);
            return ts + '_' + Array.from(new Uint8Array(digest), b => b.toString(16).padStart(2, '0')).join('');
          };
          const authorization = 'SAPISIDHASH ' + await hash(jar['SAPISID'] || base)
            + ' SAPISID1PHASH ' + await hash(jar['__Secure-1PAPISID'] || base)
            + ' SAPISID3PHASH ' + await hash(jar['__Secure-3PAPISID'] || base);
          try {
            const response = await fetch({{JsonSerializer.Serialize(url)}}, {
              method: 'POST',
              mode: 'cors',
              credentials: 'include',
              cache: 'no-store',
              headers: {
                'authorization': authorization,
                'content-type': 'application/json+protobuf',
                'x-goog-api-key': {{JsonSerializer.Serialize(apiKey)}},
                'x-goog-authuser': {{JsonSerializer.Serialize(authUser.ToString(System.Globalization.CultureInfo.InvariantCulture))}},
                'x-user-agent': 'grpc-web-javascript/0.1'
              },
              body: {{JsonSerializer.Serialize(body)}}
            });
            return JSON.stringify({ status: response.status, body: await response.text() });
          } catch (error) {
            return JSON.stringify({ status: 0, body: '', error: String((error && error.message) || error) });
          }
        })()
        """;

    internal static FoyerResponse ParseRequestResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var s) && s.TryGetInt32(out var code) ? code : 0;
            var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString()! : string.Empty;
            if (status == 0 && root.TryGetProperty("error", out var error))
            {
                throw new GoogleHomeException($"Google Home could not be reached ({error.GetString()}).");
            }

            return new FoyerResponse(status, body);
        }
        catch (JsonException ex)
        {
            throw new GoogleHomeException("The Google Home page returned something unexpected.", inner: ex);
        }
    }

    internal static bool IsHomePage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("home.google.com", StringComparison.OrdinalIgnoreCase);

    internal static bool IsGoogleSignInPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase);

    private async Task SuspendIfIdleAsync()
    {
        if (_activeRequests > 0 || _controller is not { } controller)
        {
            return;
        }

        try
        {
            var core = controller.CoreWebView2;
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            if (!core.IsSuspended)
            {
                await core.TrySuspendAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Suspending the Google Home page", ex);
        }
    }

    private void ResetController()
    {
        try
        {
            _controller?.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Closing the Google Home page", ex);
        }

        _controller = null;
        _pageStale = true;
    }

    private Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> work)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return work();
        }

        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                result.TrySetResult(await work());
            }
            catch (OperationCanceledException ex)
            {
                result.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        });

        if (!queued)
        {
            result.TrySetException(new GoogleHomeException("The app is shutting down."));
        }

        return result.Task;
    }

    public void Dispose()
    {
        _idleTimer.Stop();
        ResetController();
    }
}
