using System.Runtime.InteropServices;
using System.Text;

namespace HomeControl.Interop;

/// <summary>Real mouse clicks, the way a person makes them (used by the smoke test).</summary>
internal static class MouseInput
{
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>INPUT with its mouse member, the largest of the union.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Moves the pointer to a screen point and clicks the left button there. Returns what is
    /// under the point, for the report: the window's class, its hit-test answer (1 is client
    /// area, 2 the caption) and whether it belongs to <paramref name="expected"/>.
    /// </summary>
    public static async Task<string> ClickAsync(int x, int y, IntPtr expected)
    {
        var point = new NativeMethods.POINT { X = x, Y = y };
        var under = WindowFromPoint(point);
        var name = new StringBuilder(256);
        GetClassNameW(under, name, name.Capacity);
        var hit = SendMessageW(under, WM_NCHITTEST, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)));
        var report = $"under it: {name}, hit test {hit}, "
                     + (GetAncestor(under, GA_ROOT) == expected ? "in the window" : "another window")
                     + (GetForegroundWindow() == expected ? ", in front" : ", not in front");

        if (!SetCursorPos(x, y))
        {
            return report + $", couldn't move the pointer (error {Marshal.GetLastWin32Error()})";
        }

        await Task.Delay(150); // hover first, as a hand does
        if (!Send(MOUSEEVENTF_LEFTDOWN))
        {
            return report + $", couldn't press (error {Marshal.GetLastWin32Error()})";
        }

        await Task.Delay(60);
        return Send(MOUSEEVENTF_LEFTUP) ? report : report + $", couldn't release (error {Marshal.GetLastWin32Error()})";
    }

    private static bool Send(uint flags)
    {
        var inputs = new[] { new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = flags } } };
        return SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1;
    }
}
