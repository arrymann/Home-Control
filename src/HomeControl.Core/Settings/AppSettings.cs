using HomeControl.Core.Hotkeys;
using HomeControl.Core.Models;

namespace HomeControl.Core.Settings;

public enum ThemePreference
{
    /// <summary>Follow the Windows app theme (Settings › Personalization › Colors).</summary>
    System,
    Light,
    Dark,
}

public enum BackdropKind
{
    Mica,
    MicaAlt,
    Acrylic,
    None,
}

/// <summary>Everything the app persists in settings.json (secrets live in the secret store).</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public BackdropKind Backdrop { get; set; } = BackdropKind.Mica;

    /// <summary>Shortcut that opens the tray popup from anywhere.</summary>
    public Hotkey? OpenPopupHotkey { get; set; }

    /// <summary>Show a notification when a device is toggled with its shortcut.</summary>
    public bool NotifyOnHotkey { get; set; } = true;

    /// <summary>Also ask the Assistant for the state of Assistant devices each time the popup opens.</summary>
    public bool RefreshStatesOnOpen { get; set; }

    public GoogleHomeSettings GoogleHome { get; set; } = new();

    public AssistantSettings Assistant { get; set; } = new();

    /// <summary>Retry through Google Assistant when the Google Home session fails.</summary>
    public bool UseAssistantFallback { get; set; } = true;

    public List<DeviceConfig> Devices { get; set; } = [];

    /// <summary>Home and room groups collapsed on the Devices page (groups start expanded).</summary>
    public List<string> CollapsedDeviceGroups { get; set; } = [];
}

/// <summary>Options for the Google Home web session (the home.google.com backend).</summary>
public sealed class GoogleHomeSettings
{
    /// <summary>Public key the home.google.com web app sends with its API requests.</summary>
    public const string DefaultApiKey = "AIzaSyCMqap8NH88PrhvoBwY1W8ChRUJRjIOJXM";

    /// <summary>The user signed in to Google Home in the app (the session itself lives in WebView2).</summary>
    public bool Enabled { get; set; }

    public string ApiKey { get; set; } = DefaultApiKey;

    /// <summary>Index of the Google account in the browser session (the "u/0" in home.google.com URLs).</summary>
    public int AuthUser { get; set; }

    /// <summary>How often device states are refreshed while the tray popup is open.</summary>
    public int RefreshSeconds { get; set; } = 10;

    public DateTimeOffset? LastSync { get; set; }
}

/// <summary>Options for talking to the Google Assistant SDK.</summary>
public sealed class AssistantSettings
{
    public const string DefaultOnTemplate = "turn on {name}";
    public const string DefaultOffTemplate = "turn off {name}";
    public const string DefaultStateTemplate = "is {name} on?";

    /// <summary>Matches a reply that says the device is on, e.g. "The kitchen light is on."</summary>
    public const string DefaultOnPattern =
        @"\b(?:is|are|it's|its|they're)\s+(?:currently\s+|now\s+|still\s+|already\s+)?(?:turned\s+|switched\s+)?on\b";

    /// <summary>Matches a reply that says the device is off.</summary>
    public const string DefaultOffPattern =
        @"\b(?:is|are|it's|its|they're)\s+(?:currently\s+|now\s+|still\s+|already\s+)?(?:turned\s+|switched\s+)?off\b";

    /// <summary>Matches a reply that means the command did not work.</summary>
    public const string DefaultFailurePattern =
        @"\b(?:sorry|couldn't|could not|can't|cannot|unable|not available|unavailable|offline|isn't responding|not responding|something went wrong|try again|didn't find|don't know|not sure)\b";

    /// <summary>BCP-47 language of the commands, e.g. "en-US".</summary>
    public string LanguageCode { get; set; } = "en-US";

    public string OnCommandTemplate { get; set; } = DefaultOnTemplate;

    public string OffCommandTemplate { get; set; } = DefaultOffTemplate;

    public string StateQueryTemplate { get; set; } = DefaultStateTemplate;

    public string StateOnPattern { get; set; } = DefaultOnPattern;

    public string StateOffPattern { get; set; } = DefaultOffPattern;

    public string FailurePattern { get; set; } = DefaultFailurePattern;

    /// <summary>Registered device model ID. "default" works for text queries.</summary>
    public string DeviceModelId { get; set; } = "default";

    public string DeviceId { get; set; } = "default";

    public string Endpoint { get; set; } = "https://embeddedassistant.googleapis.com";
}
