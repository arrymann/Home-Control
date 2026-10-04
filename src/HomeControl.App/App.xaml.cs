using HomeControl.Services;
using Microsoft.UI.Xaml;

namespace HomeControl;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        // A tray app keeps running with no window open; it exits from the tray menu.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true; // keep the tray icon alive
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>The running app. Pages use it to reach settings and services.</summary>
    internal static AppHost Host { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        var smokeTest = Array.FindIndex(arguments, a => a.Equals(SmokeTest.Argument, StringComparison.OrdinalIgnoreCase));
        var background = smokeTest >= 0 || arguments.Contains(StartupService.BackgroundArgument, StringComparer.OrdinalIgnoreCase);

        Host = new AppHost { IsSmokeTest = smokeTest >= 0 };
        Host.Start(background);

        if (smokeTest >= 0)
        {
            var resultPath = smokeTest + 1 < arguments.Length
                ? arguments[smokeTest + 1]
                : Path.Combine(Path.GetTempPath(), "homecontrol-smoke-test.txt");
            var screenshots = smokeTest + 2 < arguments.Length ? arguments[smokeTest + 2] : null;
            _ = SmokeTest.RunAsync(Host, resultPath, screenshots);
        }
    }
}
