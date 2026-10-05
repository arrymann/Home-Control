using HomeControl.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HomeControl;

public static class Program
{
    private const string SingleInstanceMutexName = @"Local\HomeControl.Tray.9E4B2C71";

    /// <summary>Closes the running instance instead of starting (used before replacing the app's files).</summary>
    private const string ExitArgument = "--exit";

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Only one tray icon: a second launch asks the running instance to open its popup.
        var exit = Environment.GetCommandLineArgs().Skip(1).Contains(ExitArgument, StringComparer.OrdinalIgnoreCase);
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            if (exit)
            {
                MessageWindow.SignalRunningInstanceToExit();
            }
            else
            {
                MessageWindow.SignalRunningInstance();
            }

            return 0;
        }

        if (exit)
        {
            return 0; // nothing running
        }

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }
}
