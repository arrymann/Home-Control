using System.Text.RegularExpressions;
using HomeControl.Core.Settings;
using HomeControl.Helpers;
using HomeControl.Interop;
using HomeControl.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace HomeControl.Views;

/// <summary>How the sync after a sign-in went (reported back to the sign-in window).</summary>
internal enum GoogleSignInOutcome
{
    /// <summary>Google Home accepted the session and the devices were loaded.</summary>
    Synced,

    /// <summary>Google Home rejected the session although the browser has Google cookies.</summary>
    Rejected,

    /// <summary>Something else went wrong (network, Google changed the site…).</summary>
    Failed,
}

/// <summary>
/// Shows home.google.com so the user can sign in to Google. It uses the same private browser
/// profile as the hidden Google Home page, so once home.google.com loads signed in here, the
/// app's background session is signed in too.
/// </summary>
public sealed partial class GoogleSignInWindow : Window
{
    /// <summary>Google's sign-in page, returning to home.google.com afterwards.</summary>
    private const string SignInUrl = "https://accounts.google.com/ServiceLogin?continue=https%3A%2F%2Fhome.google.com%2F";

    private static readonly Regex AuthUserPattern = new(@"(?:/u/|[?&]authuser=)(\d{1,2})(?:[/?&#]|$)", RegexOptions.IgnoreCase);

    private readonly GoogleHomeSession _session;
    private bool _signedIn;
    private bool _sentToSignIn;
    private bool _retriedSignIn;
    private bool _browserStarted;
    private bool _closed;
    private int? _reportedAuthUser;
    private BackdropKind? _backdrop;

    internal GoogleSignInWindow(GoogleHomeSession session)
    {
        _session = session;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(AppPaths.Asset("AppIcon.ico"));
        TitleBarIcon.Source = new BitmapImage(new Uri(AppPaths.Asset("AppIcon.png")));

        var hwnd = WindowHelpers.GetHandle(this);
        var scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = new SizeInt32(Math.Min((int)(560 * scale), work.Width), Math.Min((int)(760 * scale), work.Height));
        AppWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2, size.Width, size.Height));

        Root.Loaded += async (_, _) => await InitializeBrowserAsync();
        Closed += (_, _) => _closed = true;
    }

    /// <summary>
    /// Raised when home.google.com loaded with a Google session, and again when the user
    /// switches to another account. The host then syncs and calls <see cref="ReportSignInResult"/>.
    /// </summary>
    internal event EventHandler? SignedIn;

    /// <summary>
    /// The account index home.google.com used ("/u/1/" when several Google accounts are
    /// signed in), or null when the page didn't say.
    /// </summary>
    internal int? AuthUser { get; private set; }

    internal void ApplyAppearance(ElementTheme theme, BackdropKind backdrop)
    {
        Root.RequestedTheme = theme;
        WindowHelpers.SetDarkFrame(WindowHelpers.GetHandle(this), theme == ElementTheme.Dark);
        WindowHelpers.SetCaptionButtonColors(AppWindow.TitleBar, theme == ElementTheme.Dark);
        if (_backdrop != backdrop)
        {
            _backdrop = backdrop;
            SystemBackdrop = Backdrops.Create(backdrop);
        }

        SolidBackground.Visibility = SystemBackdrop is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows how the sync after the sign-in went; a rejected session goes back to Google's sign-in.</summary>
    internal void ReportSignInResult(GoogleSignInOutcome outcome, string? detail)
    {
        if (_closed)
        {
            return;
        }

        LoadingRing.IsActive = false;
        switch (outcome)
        {
            case GoogleSignInOutcome.Synced:
                ShowStatus(InfoBarSeverity.Success, $"Signed in. Found {detail} in your Google Home – you can close this window.");
                DoneButton.Visibility = Visibility.Visible;
                break;

            case GoogleSignInOutcome.Rejected when !_retriedSignIn && Browser.CoreWebView2 is { } core:
                // Old cookies (e.g. after a password change) look like a session but aren't one.
                _retriedSignIn = true;
                _signedIn = false;
                _sentToSignIn = true;
                ShowStatus(InfoBarSeverity.Warning, "Google Home didn't accept this session. Please sign in again.");
                core.Navigate(SignInUrl);
                break;

            case GoogleSignInOutcome.Rejected:
                ShowStatus(InfoBarSeverity.Error,
                    "Google Home still doesn't accept the sign-in. If several Google accounts are signed in, set the account index under Advanced on the Account page.");
                break;

            default:
                ShowStatus(InfoBarSeverity.Warning, $"Signed in, but your devices couldn't be loaded: {detail}");
                DoneButton.Visibility = Visibility.Visible;
                break;
        }
    }

    private async Task InitializeBrowserAsync()
    {
        if (_browserStarted)
        {
            return;
        }

        _browserStarted = true;
        try
        {
            var environment = await _session.GetEnvironmentAsync();
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.NewWindowRequested += (_, args) =>
            {
                // Keep Google's sign-in steps (e.g. "use another account") in this window; other
                // links (Help, Privacy, Terms…) open in the default browser so the sign-in isn't lost.
                args.Handled = true;
                if (GoogleHomeSession.IsGoogleSignInPage(args.Uri) || GoogleHomeSession.IsHomePage(args.Uri))
                {
                    core.Navigate(args.Uri);
                }
                else if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var link) && link.Scheme == Uri.UriSchemeHttps)
                {
                    _ = Windows.System.Launcher.LaunchUriAsync(link);
                }
            };
            core.NavigationStarting += (_, _) => LoadingRing.IsActive = true;
            core.ProcessFailed += (_, args) => Log.Info($"Sign-in window: browser process failed ({args.ProcessFailedKind}).");
            core.NavigationCompleted += async (_, args) =>
            {
                Log.Info($"Sign-in window: {DescribeUrl(core.Source)} {(args.IsSuccess ? "loaded" : $"failed ({args.WebErrorStatus})")}.");
                try
                {
                    await OnNavigationCompletedAsync(core, args);
                }
                catch (Exception ex)
                {
                    Log.Error("Checking the Google sign-in", ex);
                }
            };
            core.Navigate(GoogleHomeSession.HomeUrl);
        }
        catch (Exception ex)
        {
            Log.Error("Opening the Google sign-in window", ex);
            LoadingRing.IsActive = false;
            ShowStatus(InfoBarSeverity.Error, ex.Message);
        }
    }

    private async Task OnNavigationCompletedAsync(CoreWebView2 core, CoreWebView2NavigationCompletedEventArgs args)
    {
        LoadingRing.IsActive = false;
        if (_closed)
        {
            return;
        }

        if (!args.IsSuccess)
        {
            if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                ShowStatus(InfoBarSeverity.Error, $"The page couldn't be loaded ({args.WebErrorStatus}). Check your internet connection.");
            }

            return;
        }

        if (GoogleHomeSession.IsHomePage(core.Source) && ParseAuthUser(core.Source) is { } authUser)
        {
            AuthUser = authUser;
        }

        if (!GoogleHomeSession.IsHomePage(core.Source))
        {
            if (GoogleHomeSession.IsGoogleSignInPage(core.Source) && !_signedIn)
            {
                ShowStatus(InfoBarSeverity.Informational,
                    "Sign in with the Google account that has your home. If Google says this browser may not be secure, use Google Assistant on the Account page instead.");
            }

            return;
        }

        var cookies = await core.CookieManager.GetCookiesAsync("https://home.google.com");
        if (_closed)
        {
            return;
        }

        if (!cookies.Any(c => c.Name is "SAPISID" or "__Secure-3PAPISID"))
        {
            // home.google.com without a session shows its marketing page (with "Sign in" hidden in a
            // menu at this window size): go to Google's sign-in page instead, once.
            if (!_sentToSignIn)
            {
                _sentToSignIn = true;
                core.Navigate(SignInUrl);
                return;
            }

            ShowStatus(InfoBarSeverity.Informational, "Click “Sign in” on the page (in the ☰ menu) to connect your Google account.");
            return;
        }

        // Signed in to Google. The host checks that Google Home accepts the session (by syncing)
        // and reports back; switching accounts on the page later triggers another check.
        if (_signedIn && AuthUser == _reportedAuthUser)
        {
            return;
        }

        _signedIn = true;
        _reportedAuthUser = AuthUser;
        _session.Invalidate();
        LoadingRing.IsActive = true;
        ShowStatus(InfoBarSeverity.Informational, "Signed in to Google. Loading your devices from Google Home…");
        DoneButton.Visibility = Visibility.Collapsed;
        SignedIn?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Smoke test only: what the window shows right now.</summary>
    internal string DescribeForTest() =>
        $"page {DescribeUrl(Browser.CoreWebView2?.Source)}, browser {Browser.ActualWidth:0}x{Browser.ActualHeight:0}, " +
        $"window content {Root.ActualWidth:0}x{Root.ActualHeight:0}, status “{StatusBar.Message}”";

    /// <summary>Host and path only: sign-in URLs carry long state parameters.</summary>
    private static string DescribeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host + uri.AbsolutePath : "(none)";

    internal static int? ParseAuthUser(string? url) =>
        url is not null && AuthUserPattern.Match(url) is { Success: true } match ? int.Parse(match.Groups[1].Value) : null;

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
    }

    private void OnDoneClick(object sender, RoutedEventArgs e) => Close();
}
