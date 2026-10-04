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

/// <summary>
/// Shows home.google.com so the user can sign in to Google. It uses the same private browser
/// profile as the hidden Google Home page, so once home.google.com loads signed in here, the
/// app's background session is signed in too.
/// </summary>
public sealed partial class GoogleSignInWindow : Window
{
    private static readonly Regex AuthUserPattern = new(@"(?:/u/|[?&]authuser=)(\d{1,2})(?:[/?&#]|$)", RegexOptions.IgnoreCase);

    private readonly GoogleHomeSession _session;
    private bool _signedIn;

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
    }

    /// <summary>Raised once home.google.com has loaded signed in.</summary>
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
        SystemBackdrop = Backdrops.Create(backdrop);
        SolidBackground.Visibility = SystemBackdrop is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var environment = await _session.GetEnvironmentAsync();
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.NewWindowRequested += (_, args) =>
            {
                // Keep Google's sign-in steps (e.g. "use another account") in this window.
                args.Handled = true;
                core.Navigate(args.Uri);
            };
            core.NavigationStarting += (_, _) => LoadingRing.IsActive = true;
            core.NavigationCompleted += async (_, _) =>
            {
                try
                {
                    await OnNavigationCompletedAsync(core);
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

    private async Task OnNavigationCompletedAsync(CoreWebView2 core)
    {
        LoadingRing.IsActive = false;
        if (GoogleHomeSession.IsHomePage(core.Source) && ParseAuthUser(core.Source) is { } authUser)
        {
            AuthUser = authUser;
        }

        if (_signedIn || !GoogleHomeSession.IsHomePage(core.Source))
        {
            if (GoogleHomeSession.IsGoogleSignInPage(core.Source) && !_signedIn)
            {
                ShowStatus(InfoBarSeverity.Informational,
                    "Sign in with the Google account that has your home. If Google says this browser may not be secure, use Google Assistant on the Account page instead.");
            }

            return;
        }

        var cookies = await core.CookieManager.GetCookiesAsync("https://home.google.com");
        if (!cookies.Any(c => c.Name is "SAPISID" or "__Secure-3PAPISID"))
        {
            // home.google.com without a session shows its welcome page; let the user click "Sign in".
            ShowStatus(InfoBarSeverity.Informational, "Click “Sign in” on the page to connect your Google account.");
            return;
        }

        _signedIn = true;
        _session.Invalidate();
        ShowStatus(InfoBarSeverity.Success, "Signed in. Home Control is loading your devices – you can close this window.");
        DoneButton.Visibility = Visibility.Visible;
        SignedIn?.Invoke(this, EventArgs.Empty);
    }

    internal static int? ParseAuthUser(string? url) =>
        url is not null && AuthUserPattern.Match(url) is { Success: true } match ? int.Parse(match.Groups[1].Value) : null;

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
    }

    private void OnDoneClick(object sender, RoutedEventArgs e) => Close();
}
