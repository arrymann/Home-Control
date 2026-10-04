using HomeControl.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HomeControl;

public static class Program
{
    private const string SingleInstanceMutexName = @"Local\HomeControl.Tray.9E4B2C71";

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Only one tray icon: a second launch asks the running instance to open its popup.
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageWindow.SignalRunningInstance();
            return 0;
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
