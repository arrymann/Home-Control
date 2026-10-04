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
    private bool _loading;

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
        Load();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Account.StateChanged -= OnAccountStateChanged;
        _signInCts?.Cancel();
    }

    private void OnAccountStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateStatus);

    private void Load()
    {
        _loading = true;
        var client = Account.Client;
        ClientIdBox.Text = client?.ClientId ?? string.Empty;
        ClientSecretBox.Password = client?.ClientSecret ?? string.Empty;
        ClientExpander.IsExpanded = client is null;

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
            SignInError.Message = ex.Message;
            SignInError.IsOpen = true;
        }
        finally
        {
            _signInCts?.Dispose();
            _signInCts = null;
            UpdateStatus();
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
