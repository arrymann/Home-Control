using System.Diagnostics;
using System.Runtime.InteropServices;
using HomeControl.Core.Automations;

namespace HomeControl.Services;

/// <summary>Locks the PC, puts it to sleep, turns the display off, or shuts it down with a warning.</summary>
internal static class PcPower
{
    /// <summary>Shut down/restart wait this long, so the user can cancel (shutdown /a).</summary>
    public const int WarningSeconds = 60;

    /// <summary>True between scheduling a shutdown/restart and cancelling it or Windows acting on it.</summary>
    public static bool ShutdownScheduled { get; private set; }

    public static void Run(PcPowerCommand command)
    {
        switch (command)
        {
            case PcPowerCommand.Lock:
                if (!LockWorkStation())
                {
                    throw new InvalidOperationException("Windows didn't lock the PC.");
                }

                break;

            case PcPowerCommand.Sleep:
                // Run after the current message: the app should finish this step before the PC sleeps.
                _ = Task.Run(() => SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false));
                break;

            case PcPowerCommand.DisplayOff:
                PostMessageW(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, MonitorOff);
                break;

            case PcPowerCommand.ShutDown:
            case PcPowerCommand.Restart:
                var restart = command == PcPowerCommand.Restart;
                RunShutdownExe($"/{(restart ? "r" : "s")} /t {WarningSeconds} /d p:0:0 /c \"Home Control automation: the PC will {(restart ? "restart" : "shut down")} in one minute.\"");
                ShutdownScheduled = true;
                break;
        }
    }

    /// <summary>Cancels a shutdown or restart scheduled by an automation.</summary>
    public static void CancelShutdown()
    {
        RunShutdownExe("/a");
        ShutdownScheduled = false;
    }

    private static void RunShutdownExe(string arguments)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
        using var process = Process.Start(new ProcessStartInfo(path, arguments) { CreateNoWindow = true, UseShellExecute = false })
                            ?? throw new InvalidOperationException("shutdown.exe didn't start.");
    }

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint WM_SYSCOMMAND = 0x0112;
    private static readonly IntPtr SC_MONITORPOWER = new(0xF170);
    private static readonly IntPtr MonitorOff = new(2);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
