using System.Diagnostics;
using HomeControl.Core.Auth;
using HomeControl.Core.Settings;
using HomeControl.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace HomeControl.Views;

public sealed partial class AccountPage : Page
{
    private static readonly string[] Languages =
    [
        "en-US", "en-GB", "en-AU", "en-CA", "en-IN", "de-DE", "fr-FR", "fr-CA", "it-IT", "es-ES", "es-MX",
        "pt-BR", "nl-NL", "da-DK", "sv-SE", "nb-NO", "ja-JP", "ko-KR", "hi-IN",
    ];

    private CancellationTokenSource? _signInCts;
    private string? _syncSummary;
    private bool _loading;
    private bool _onScreen;

    public AccountPage()
    {
        InitializeComponent();
    }

    private static GoogleAccount Account => App.Host.Account;

    private static AssistantSettings Assistant => App.Host.Settings.Assistant;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Account.StateChanged += OnAccountStateChanged;
        App.Host.GoogleHomeChanged += OnGoogleHomeChanged;
        _onScreen = true;
        Load();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Account.StateChanged -= OnAccountStateChanged;
        App.Host.GoogleHomeChanged -= OnGoogleHomeChanged;
        _onScreen = false;

        // Switching pages cancels a sign-in that is waiting for the browser; closing the settings
        // window (which navigates to a blank Page) lets it finish in the background.
        if (e.SourcePageType != typeof(Page))
        {
            _signInCts?.Cancel();
        }
    }

    private void OnAccountStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateStatus);

    private void OnGoogleHomeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateGoogleHomeStatus);

    private void Load()
    {
        _loading = true;
        var client = Account.Client;
        ClientIdBox.Text = client?.ClientId ?? string.Empty;
        ClientSecretBox.Password = client?.ClientSecret ?? string.Empty;

        var googleHome = App.Host.Settings.GoogleHome;
        AuthUserBox.Value = googleHome.AuthUser;
        RefreshSecondsBox.Value = googleHome.RefreshSeconds;
        FallbackSwitch.IsOn = App.Host.Settings.UseAssistantFallback;
        UpdateGoogleHomeStatus();

        var languages = Languages.ToList();
        if (!languages.Contains(Assistant.LanguageCode, StringComparer.OrdinalIgnoreCase))
        {
            languages.Insert(0, Assistant.LanguageCode);
        }

        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedItem = languages.First(l => l.Equals(Assistant.LanguageCode, StringComparison.OrdinalIgnoreCase));
        LoadCommandBoxes();
        UpdateStatus();
        _loading = false;
    }

    private void LoadCommandBoxes()
    {
        OnTemplateBox.Text = Assistant.OnCommandTemplate;
        OffTemplateBox.Text = Assistant.OffCommandTemplate;
        StateTemplateBox.Text = Assistant.StateQueryTemplate;
        OnPatternBox.Text = Assistant.StateOnPattern;
        OffPatternBox.Text = Assistant.StateOffPattern;
        FailurePatternBox.Text = Assistant.FailurePattern;
    }

    private void UpdateStatus()
    {
        var signingIn = _signInCts is not null;
        ClientStatus.Text = Account.HasClient ? "Ready" : "Not set up";

        SignInCard.Description = Account.IsSignedIn
            ? "Signed in. Home Control can switch your devices."
            : signingIn
                ? "Finish signing in in your browser…"
                : Account.HasClient ? "Not signed in." : "Set up the OAuth client first.";

        SignInButton.Visibility = Account.IsSignedIn || signingIn ? Visibility.Collapsed : Visibility.Visible;
        SignInButton.IsEnabled = Account.HasClient;
        SignOutButton.Visibility = Account.IsSignedIn && !signingIn ? Visibility.Visible : Visibility.Collapsed;
        CancelSignInButton.Visibility = signingIn ? Visibility.Visible : Visibility.Collapsed;
        SignInProgress.IsActive = signingIn;
        TestButton.IsEnabled = Account.IsSignedIn;
    }

    private void UpdateGoogleHomeStatus()
    {
        var host = App.Host;
        var settings = host.Settings.GoogleHome;
        var state = host.GoogleHomeState;
        var syncing = host.IsSyncingGoogleHome;
        var signedOut = !settings.Enabled || state == GoogleHomeConnection.SignedOut;

        var count = host.Settings.Devices.Count(d => d.IsGoogleHome && !d.Missing);
        var devices = count == 1 ? "1 device" : $"{count} devices";
        var synced = settings.LastSync is { } lastSync ? $"  ·  synced {lastSync.ToLocalTime():g}" : string.Empty;

        GoogleHomeCard.Description = state switch
        {
            _ when syncing => "Loading your devices from Google Home…",
            GoogleHomeConnection.Unavailable => "Google Home needs the Microsoft Edge WebView2 Runtime.",
            _ when !settings.Enabled => "Not signed in. Sign in with the Google account that has your home.",
            GoogleHomeConnection.SignedOut => "Google ended the session. Sign in again.",
            GoogleHomeConnection.Error => $"Signed in, but the last request failed  ·  {devices}{synced}",
            _ => $"Signed in  ·  {devices}{synced}",
        };

        GoogleHomeSignInButton.Visibility = signedOut ? Visibility.Visible : Visibility.Collapsed;
        GoogleHomeSyncButton.Visibility = signedOut ? Visibility.Collapsed : Visibility.Visible;
        GoogleHomeSyncButton.IsEnabled = !syncing;
        GoogleHomeSignOutButton.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        GoogleHomeProgress.IsActive = syncing;

        if (host.GoogleHomeMessage is { } problem)
        {
            GoogleHomeError.Severity = InfoBarSeverity.Warning;
            GoogleHomeError.Title = "Google Home";
            GoogleHomeError.Message = problem;
            GoogleHomeError.IsOpen = true;
        }
        else if (_syncSummary is { } summary)
        {
            GoogleHomeError.Severity = InfoBarSeverity.Success;
            GoogleHomeError.Title = "Synced";
            GoogleHomeError.Message = summary;
            GoogleHomeError.IsOpen = true;
        }
        else
        {
            GoogleHomeError.IsOpen = false;
        }
    }

    private void OnGoogleHomeSignInClick(object sender, RoutedEventArgs e) => App.Host.OpenGoogleSignIn();

    private async void OnGoogleHomeSyncClick(object sender, RoutedEventArgs e)
    {
        _syncSummary = null;
        try
        {
            var result = await App.Host.SyncGoogleHomeAsync();
            _syncSummary = result.ToString();
        }
        catch (Exception ex)
        {
            Log.Error("Syncing Google Home", ex);
        }

        UpdateGoogleHomeStatus();
    }

    private async void OnGoogleHomeSignOutClick(object sender, RoutedEventArgs e)
    {
        _syncSummary = null;
        GoogleHomeSignOutButton.IsEnabled = false;
        try
        {
            await App.Host.SignOutGoogleHomeAsync();
        }
        finally
        {
            GoogleHomeSignOutButton.IsEnabled = true;
            UpdateGoogleHomeStatus();
        }
    }

    private void OnGoogleHomeOptionChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(sender.Value))
        {
            return;
        }

        var settings = App.Host.Settings.GoogleHome;
        if (!double.IsNaN(AuthUserBox.Value))
        {
            settings.AuthUser = (int)Math.Clamp(AuthUserBox.Value, 0, 9);
        }

        if (!double.IsNaN(RefreshSecondsBox.Value))
        {
            settings.RefreshSeconds = (int)Math.Clamp(RefreshSecondsBox.Value, 3, 300);
        }

        App.Host.SaveSettings();
    }

    private void OnFallbackToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || FallbackSwitch.IsOn == App.Host.Settings.UseAssistantFallback)
        {
            return;
        }

        App.Host.Settings.UseAssistantFallback = FallbackSwitch.IsOn;
        App.Host.SaveSettings();
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Host.SettingsWindowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var json = await File.ReadAllTextAsync(file.Path);
            if (!OAuthClient.TryParseClientSecretsJson(json, out var client) || client is null)
            {
                ClientMessage.Text = "That file doesn't contain an OAuth client ID and secret.";
                return;
            }

            ClientIdBox.Text = client.ClientId;
            ClientSecretBox.Password = client.ClientSecret;
            SaveClient();
        }
        catch (Exception ex)
        {
            Log.Error("Importing client secrets", ex);
            ClientMessage.Text = "Could not read the file: " + ex.Message;
        }
    }

    private void OnSaveClientClick(object sender, RoutedEventArgs e) => SaveClient();

    private bool SaveClient()
    {
        var id = ClientIdBox.Text.Trim();
        var secret = ClientSecretBox.Password.Trim();
        if (id.Length == 0 || secret.Length == 0)
        {
            ClientMessage.Text = "Enter both the client ID and the client secret.";
            return false;
        }

        if (!id.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            ClientMessage.Text = "That doesn't look like a Google OAuth client ID.";
            return false;
        }

        Account.SetClient(new OAuthClient(id, secret));
        ClientMessage.Text = "Saved.";
        UpdateStatus();
        return true;
    }

    private async void OnSignInClick(object sender, RoutedEventArgs e)
    {
        var typed = new OAuthClient(ClientIdBox.Text.Trim(), ClientSecretBox.Password.Trim());
        if (Account.Client != typed && !SaveClient())
        {
            ClientExpander.IsExpanded = true;
            return;
        }

        SignInError.IsOpen = false;
        _signInCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        UpdateStatus();
        try
        {
            await Account.SignInAsync(OpenBrowser, _signInCts.Token);
            App.Host.Home.UpdateAccountState();
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user or timed out.
        }
        catch (Exception ex) when (ex is OAuthException or HttpRequestException)
        {
            if (_onScreen)
            {
                SignInError.Message = ex.Message;
                SignInError.IsOpen = true;
            }
            else
            {
                Log.Error("Signing in to Google Assistant", ex);
            }
        }
        finally
        {
            _signInCts?.Dispose();
            _signInCts = null;
            if (_onScreen)
            {
                UpdateStatus();
            }
        }
    }

    private void OnCancelSignInClick(object sender, RoutedEventArgs e) => _signInCts?.Cancel();

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        await Account.SignOutAsync(CancellationToken.None);
        App.Host.Home.UpdateAccountState();
        UpdateStatus();
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        var query = TestQueryBox.Text.Trim();
        if (query.Length == 0)
        {
            return;
        }

        TestButton.IsEnabled = false;
        TestProgress.IsActive = true;
        TestResult.Text = string.Empty;
        try
        {
            var reply = await App.Host.Assistant.SendTextQueryAsync(query, CancellationToken.None);
            TestResult.Text = string.IsNullOrWhiteSpace(reply.Text)
                ? "Google Assistant answered without text (that's normal for some commands)."
                : $"Google Assistant: “{reply.Text}”";
        }
        catch (Exception ex)
        {
            TestResult.Text = ex.Message;
        }
        finally
        {
            TestProgress.IsActive = false;
            UpdateStatus();
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedItem is not string language || language == Assistant.LanguageCode)
        {
            return;
        }

        Assistant.LanguageCode = language;
        App.Host.SaveSettings();
    }

    private void OnTemplateLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        Assistant.OnCommandTemplate = Or(OnTemplateBox.Text, AssistantSettings.DefaultOnTemplate);
        Assistant.OffCommandTemplate = Or(OffTemplateBox.Text, AssistantSettings.DefaultOffTemplate);
        Assistant.StateQueryTemplate = Or(StateTemplateBox.Text, AssistantSettings.DefaultStateTemplate);
        Assistant.StateOnPattern = Or(OnPatternBox.Text, AssistantSettings.DefaultOnPattern);
        Assistant.StateOffPattern = Or(OffPatternBox.Text, AssistantSettings.DefaultOffPattern);
        Assistant.FailurePattern = FailurePatternBox.Text.Trim();
        App.Host.SaveSettings();
    }

    private void OnResetCommandsClick(object sender, RoutedEventArgs e)
    {
        var defaults = new AssistantSettings();
        Assistant.OnCommandTemplate = defaults.OnCommandTemplate;
        Assistant.OffCommandTemplate = defaults.OffCommandTemplate;
        Assistant.StateQueryTemplate = defaults.StateQueryTemplate;
        Assistant.StateOnPattern = defaults.StateOnPattern;
        Assistant.StateOffPattern = defaults.StateOffPattern;
        Assistant.FailurePattern = defaults.FailurePattern;
        App.Host.SaveSettings();

        _loading = true;
        LoadCommandBoxes();
        _loading = false;
    }

    private static void OpenBrowser(Uri uri) =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
}
