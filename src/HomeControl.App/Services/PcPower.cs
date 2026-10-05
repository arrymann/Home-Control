using System.Diagnostics;
using System.Runtime.InteropServices;
using HomeControl.Core.Automations;
using Microsoft.UI.Dispatching;

namespace HomeControl.Services;

/// <summary>Locks the PC, puts it to sleep, turns the display off, or shuts it down after a warning.</summary>
internal static class PcPower
{
    /// <summary>Shut down/restart wait this long, so the user can cancel from the tray menu.</summary>
    public const int WarningSeconds = 60;

    private static DispatcherQueueTimer? _countdown;

    /// <summary>True while a shutdown/restart is counting down (it can still be cancelled).</summary>
    public static bool ShutdownScheduled => _countdown is not null;

    /// <summary>Called on the UI thread when Windows refused to shut down or restart.</summary>
    public static event EventHandler<string>? ShutdownFailed;

    /// <summary>Runs a command. Call on the UI thread.</summary>
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
                if (!IsPwrSuspendAllowed())
                {
                    // Modern Standby PCs have no classic sleep; they go to standby when the screen goes off.
                    TurnDisplayOff();
                    break;
                }

                // On another thread, so this step finishes (and is logged) before the PC sleeps.
                _ = Task.Run(() =>
                {
                    if (!SetSuspendState(false, false, false))
                    {
                        Log.Info($"The PC didn't go to sleep (error {Marshal.GetLastWin32Error()}).");
                    }
                });
                break;

            case PcPowerCommand.DisplayOff:
                TurnDisplayOff();
                break;

            case PcPowerCommand.ShutDown:
            case PcPowerCommand.Restart:
                StartCountdown(command == PcPowerCommand.Restart);
                break;
        }
    }

    /// <summary>Cancels a shutdown or restart that is still counting down.</summary>
    public static void CancelShutdown()
    {
        _countdown?.Stop();
        _countdown = null;
    }

    /// <summary>
    /// The countdown runs in the app rather than with "shutdown /t 60", which would force apps
    /// with unsaved work to close. "/t 0" lets them ask the user, as a normal shutdown does.
    /// </summary>
    private static void StartCountdown(bool restart)
    {
        CancelShutdown();
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(WarningSeconds);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) =>
        {
            if (_countdown != timer)
            {
                return; // cancelled
            }

            _countdown = null;
            var verb = restart ? "restart" : "shut down";
            try
            {
                var exitCode = await Task.Run(() => RunShutdownExe($"/{(restart ? "r" : "s")} /t 0 /d p:0:0"));
                if (exitCode != 0)
                {
                    Log.Info($"shutdown.exe exited with {exitCode}.");
                    ShutdownFailed?.Invoke(null, $"Windows didn't {verb} (error {exitCode}).");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Trying to {verb}", ex);
                ShutdownFailed?.Invoke(null, $"Windows didn't {verb}: {ex.Message}");
            }
        };
        _countdown = timer;
        timer.Start();
    }

    private static int RunShutdownExe(string arguments)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
        using var process = Process.Start(new ProcessStartInfo(path, arguments) { CreateNoWindow = true, UseShellExecute = false })
                            ?? throw new InvalidOperationException("shutdown.exe didn't start.");
        return process.WaitForExit(10_000) ? process.ExitCode : throw new TimeoutException("shutdown.exe didn't finish.");
    }

    private static void TurnDisplayOff() => PostMessageW(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, MonitorOff);

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint WM_SYSCOMMAND = 0x0112;
    private static readonly IntPtr SC_MONITORPOWER = new(0xF170);
    private static readonly IntPtr MonitorOff = new(2);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    // BOOLEAN (one byte) parameters and result, not BOOL.
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool IsPwrSuspendAllowed();

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
