namespace HomeControl.Services;

/// <summary>Minimal file log in %LOCALAPPDATA%\HomeControl (a tray app has no console).</summary>
internal static class Log
{
    private const long MaxSize = 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "home-control.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string context, Exception exception) => Write("ERROR", $"{context}: {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxSize)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down.
        }
    }
}

internal static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeControl");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string SecretsFile => Path.Combine(DataDirectory, "secrets.dat");

    public static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);
}
