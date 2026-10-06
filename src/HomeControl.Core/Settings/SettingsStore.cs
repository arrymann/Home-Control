using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeControl.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // The file is meant to be readable by humans; it is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();

    public SettingsStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>
    /// Set when the existing file could not be read. The broken file is kept next to the
    /// original (".corrupt") so nothing is lost when defaults are saved over it.
    /// </summary>
    public string? LoadError { get; private set; }

    public AppSettings Load()
    {
        lock (_gate)
        {
            LoadError = null;
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                Normalize(settings);
                return settings;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LoadError = ex.Message;
                TryBackupCorruptFile();
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write to a temporary file first so a crash never leaves a half-written file.
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tempPath, FilePath, overwrite: true);
        }
    }

    private static void Normalize(AppSettings settings)
    {
        settings.Assistant ??= new AssistantSettings();
        settings.GoogleHome ??= new GoogleHomeSettings();
        if (string.IsNullOrWhiteSpace(settings.GoogleHome.ApiKey)) settings.GoogleHome.ApiKey = GoogleHomeSettings.DefaultApiKey;
        if (settings.GoogleHome.AuthUser < 0) settings.GoogleHome.AuthUser = 0;
        settings.GoogleHome.RefreshSeconds = Math.Clamp(settings.GoogleHome.RefreshSeconds, 3, 300);
        settings.Devices ??= [];
        settings.Devices.RemoveAll(d => d is null);
        settings.CollapsedDeviceGroups = (settings.CollapsedDeviceGroups ?? []).Where(g => !string.IsNullOrEmpty(g)).Distinct().ToList();
        settings.Scenes ??= [];
        settings.Scenes.RemoveAll(s => s is null);

        var seen = new HashSet<string>();
        foreach (var device in settings.Devices)
        {
            device.Name ??= string.Empty;
            if (string.IsNullOrWhiteSpace(device.Id) || !seen.Add(device.Id))
            {
                device.Id = Guid.NewGuid().ToString("N");
                seen.Add(device.Id);
            }

            if (device.Hotkey is { IsValid: false })
            {
                device.Hotkey = null;
            }
        }

        var sceneIds = new HashSet<string>(seen);
        foreach (var scene in settings.Scenes)
        {
            scene.Name ??= string.Empty;
            scene.DeviceIds = (scene.DeviceIds ?? []).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
            if (string.IsNullOrWhiteSpace(scene.Id) || !sceneIds.Add(scene.Id))
            {
                scene.Id = Guid.NewGuid().ToString("N");
                sceneIds.Add(scene.Id);
            }

            if (scene.Hotkey is { IsValid: false })
            {
                scene.Hotkey = null;
            }
        }

        if (settings.OpenPopupHotkey is { IsValid: false })
        {
            settings.OpenPopupHotkey = null;
        }

        var assistant = settings.Assistant;
        if (string.IsNullOrWhiteSpace(assistant.LanguageCode)) assistant.LanguageCode = "en-US";
        if (string.IsNullOrWhiteSpace(assistant.OnCommandTemplate)) assistant.OnCommandTemplate = AssistantSettings.DefaultOnTemplate;
        if (string.IsNullOrWhiteSpace(assistant.OffCommandTemplate)) assistant.OffCommandTemplate = AssistantSettings.DefaultOffTemplate;
        if (string.IsNullOrWhiteSpace(assistant.StateQueryTemplate)) assistant.StateQueryTemplate = AssistantSettings.DefaultStateTemplate;
        if (string.IsNullOrWhiteSpace(assistant.StateOnPattern)) assistant.StateOnPattern = AssistantSettings.DefaultOnPattern;
        if (string.IsNullOrWhiteSpace(assistant.StateOffPattern)) assistant.StateOffPattern = AssistantSettings.DefaultOffPattern;
        assistant.FailurePattern ??= AssistantSettings.DefaultFailurePattern;
        if (string.IsNullOrWhiteSpace(assistant.DeviceModelId)) assistant.DeviceModelId = "default";
        if (string.IsNullOrWhiteSpace(assistant.DeviceId)) assistant.DeviceId = "default";
        if (string.IsNullOrWhiteSpace(assistant.Endpoint)) assistant.Endpoint = "https://embeddedassistant.googleapis.com";
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }
}
