using System.Runtime.InteropServices;
using HomeControl.Core.Automations;
using HomeControl.Services;

namespace HomeControl.Interop;

/// <summary>
/// Watches what happens to the PC through the tray's hidden window: lock/unlock (WTS session
/// notifications), sleep/wake and plugged-in/battery (WM_POWERBROADCAST), display on/off
/// (power setting notifications), idle time (GetLastInputInfo), and Windows shutting down or
/// signing out (WM_QUERYENDSESSION/WM_ENDSESSION).
/// </summary>
internal sealed class PcMonitor : IDisposable
{
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const uint WM_POWERBROADCAST = 0x0218;
    private const uint WM_WTSSESSION_CHANGE = 0x02B1;

    private const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;

    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_UNLOCK = 0x8;

    private const long ENDSESSION_LOGOFF = 0x80000000;

    /// <summary>GUID_CONSOLE_DISPLAY_STATE: 0 off, 1 on, 2 dimmed.</summary>
    private static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    private readonly MessageWindow _window;
    private IntPtr _displayNotification;
    private bool _sessionNotifications;
    private bool _suspended;
    private bool _displayStateKnown;

    public PcMonitor(MessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;

        _sessionNotifications = WTSRegisterSessionNotification(window.Handle, NOTIFY_FOR_THIS_SESSION);
        var guid = ConsoleDisplayState;
        _displayNotification = RegisterPowerSettingNotification(window.Handle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
        OnBattery = ReadOnBattery();

        // Ask to be told about shutdown early, while the network is certainly still up.
        SetProcessShutdownParameters(0x3FF, 0);
    }

    /// <summary>Raised on the UI thread when something happens to the PC.</summary>
    public event EventHandler<PcEvent>? PcEventOccurred;

    /// <summary>
    /// Raised on the UI thread when Windows is about to end the session (true: signing out).
    /// The handler returns a task; Windows is held (briefly, with a reason shown) until it ends.
    /// </summary>
    public Func<bool, Task>? SessionEnding { get; set; }

    /// <summary>Whether anything wants to run at shutdown (so shutdown isn't delayed for nothing).</summary>
    public Func<bool, bool>? HasSessionEndingWork { get; set; }

    public bool Locked { get; private set; }

    public bool? DisplayOn { get; private set; }

    /// <summary>Null on PCs without a battery reading.</summary>
    public bool? OnBattery { get; private set; }

    /// <summary>Time since the last keyboard or mouse input.</summary>
    public TimeSpan Idle
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info))
            {
                return TimeSpan.Zero;
            }

            var elapsed = unchecked((uint)Environment.TickCount - info.dwTime); // wraps like GetTickCount
            return TimeSpan.FromMilliseconds(elapsed);
        }
    }

    public PcStatus Status => new(Locked, Idle, OnBattery, DisplayOn);

    private void OnMessage(uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WM_WTSSESSION_CHANGE:
                switch ((int)wParam)
                {
                    case WTS_SESSION_LOCK:
                        Locked = true;
                        Raise(PcEvent.Locked);
                        break;
                    case WTS_SESSION_UNLOCK:
                        Locked = false;
                        Raise(PcEvent.Unlocked);
                        break;
                }

                break;

            case WM_POWERBROADCAST:
                OnPowerBroadcast((int)wParam, lParam);
                break;

            case WM_QUERYENDSESSION:
                if (HasSessionEndingWork?.Invoke(IsSignOut(lParam)) == true)
                {
                    // Shown by Windows if we take more than a few seconds.
                    ShutdownBlockReasonCreate(_window.Handle, "Home Control is switching devices before Windows shuts down…");
                }

                break; // DefWindowProc answers TRUE: shutting down is fine

            case WM_ENDSESSION:
                if (wParam != IntPtr.Zero)
                {
                    RunBeforeSessionEnds(IsSignOut(lParam));
                }
                else
                {
                    ShutdownBlockReasonDestroy(_window.Handle); // shutdown was cancelled
                }

                break;
        }
    }

    private void OnPowerBroadcast(int type, IntPtr lParam)
    {
        switch (type)
        {
            case PBT_APMSUSPEND:
                _suspended = true;
                Raise(PcEvent.Sleeping);
                break;

            case PBT_APMRESUMEAUTOMATIC:
            case PBT_APMRESUMESUSPEND:
                // Both arrive after a normal wake; report it once.
                if (_suspended)
                {
                    _suspended = false;
                    Raise(PcEvent.Resumed);
                }

                break;

            case PBT_APMPOWERSTATUSCHANGE:
                var onBattery = ReadOnBattery();
                if (onBattery is { } now && OnBattery is { } before && now != before)
                {
                    OnBattery = now;
                    Raise(now ? PcEvent.OnBattery : PcEvent.PluggedIn);
                }
                else
                {
                    OnBattery = onBattery;
                }

                break;

            case PBT_POWERSETTINGCHANGE when lParam != IntPtr.Zero:
                var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                if (setting.PowerSetting == ConsoleDisplayState && setting.DataLength >= 4)
                {
                    var on = setting.Data != 0; // dimmed counts as on
                    var changed = DisplayOn != on;
                    DisplayOn = on;

                    // The first notification only reports the current state.
                    if (_displayStateKnown && changed)
                    {
                        Raise(on ? PcEvent.DisplayOn : PcEvent.DisplayOff);
                    }

                    _displayStateKnown = true;
                }

                break;
        }
    }

    /// <summary>
    /// Runs the shutdown work while Windows waits for this window's WM_ENDSESSION to return.
    /// The work needs the UI thread (it is this thread), so messages are pumped here until it
    /// finishes or the time is up.
    /// </summary>
    private void RunBeforeSessionEnds(bool signingOut)
    {
        try
        {
            if (SessionEnding is not { } handler || HasSessionEndingWork?.Invoke(signingOut) != true)
            {
                return;
            }

            Log.Info($"Windows is {(signingOut ? "signing out" : "shutting down")}: running automations.");
            var work = handler(signingOut);
            var deadline = Environment.TickCount64 + 10_000;
            while (!work.IsCompleted && Environment.TickCount64 < deadline)
            {
                if (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
                else
                {
                    MsgWaitForMultipleObjectsEx(0, null, 50, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                }
            }

            Log.Info(work.IsCompleted ? "Shutdown automations finished." : "Shutdown automations took too long; Windows continues.");
        }
        catch (Exception ex)
        {
            Log.Error("Running shutdown automations", ex);
        }
        finally
        {
            ShutdownBlockReasonDestroy(_window.Handle);
        }
    }

    private void Raise(PcEvent pcEvent)
    {
        Log.Info($"PC event: {pcEvent}");
        PcEventOccurred?.Invoke(this, pcEvent);
    }

    private static bool IsSignOut(IntPtr lParam) => ((long)lParam & ENDSESSION_LOGOFF) != 0;

    private static bool? ReadOnBattery() =>
        GetSystemPowerStatus(out var status) ? status.ACLineStatus switch { 0 => true, 1 => false, _ => null } : null;

    public void Dispose()
    {
        _window.MessageReceived -= OnMessage;
        if (_sessionNotifications)
        {
            WTSUnRegisterSessionNotification(_window.Handle);
            _sessionNotifications = false;
        }

        if (_displayNotification != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_displayNotification);
            _displayNotification = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------ interop

    private const int NOTIFY_FOR_THIS_SESSION = 0;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    private const uint PM_REMOVE = 0x0001;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data; // the settings used here are DWORDs
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public NativeMethods.POINT pt;
        public uint lPrivate;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[]? pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);
}
