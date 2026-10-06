using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace HomeControl.Interop;

/// <summary>The screen edge the taskbar is on.</summary>
internal enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>The taskbar's edge and the part of the display a popup can use beside it.</summary>
internal readonly record struct TaskbarPlacement(TaskbarEdge Edge, RectInt32 Area);

/// <summary>
/// Finds the taskbar. The work area alone isn't enough: it doesn't change for a taskbar that
/// hides itself, nor for one moved to the top by tools that Windows 11 needs for that.
/// </summary>
internal static class Taskbar
{
    private const uint ABM_GETTASKBARPOS = 0x5;

    /// <summary>Where the taskbar is on the display that holds <paramref name="anchor"/> (a point in or by the tray).</summary>
    public static TaskbarPlacement GetPlacement(PointInt32 anchor)
    {
        var display = DisplayArea.GetFromPoint(anchor, DisplayAreaFallback.Nearest);
        var outer = display.OuterBounds;
        var work = display.WorkArea;

        if (GetMainTaskbar() is { } bar && Intersects(bar, outer))
        {
            var edge = EdgeOf(bar, outer);
            return new TaskbarPlacement(edge, Beside(work, bar, edge));
        }

        // Another display's taskbar: the side the work area gives up, else the edge nearest the anchor.
        return new TaskbarPlacement(EdgeFromWorkArea(work, outer) ?? NearestEdge(outer, anchor), work);
    }

    /// <summary>A point at the tray end of the main taskbar, for when the icon's position is unknown.</summary>
    public static PointInt32? GetTrayCorner()
    {
        if (GetMainTaskbar() is not { } bar)
        {
            return null;
        }

        return bar.Width >= bar.Height
            ? new PointInt32(bar.X + bar.Width - 1, bar.Y + bar.Height / 2)
            : new PointInt32(bar.X + bar.Width / 2, bar.Y + bar.Height - 1);
    }

    private static RectInt32? GetMainTaskbar()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == UIntPtr.Zero)
        {
            return null;
        }

        var r = data.rc;
        return r.Right > r.Left && r.Bottom > r.Top ? new RectInt32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;
    }

    /// <summary>A wide bar is at the top or bottom, whichever it's nearer; a tall one at the left or right.</summary>
    private static TaskbarEdge EdgeOf(RectInt32 bar, RectInt32 outer)
    {
        if (bar.Width >= bar.Height)
        {
            var toTop = Math.Abs(bar.Y - outer.Y);
            var toBottom = Math.Abs(outer.Y + outer.Height - (bar.Y + bar.Height));
            return toTop < toBottom ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        var toLeft = Math.Abs(bar.X - outer.X);
        var toRight = Math.Abs(outer.X + outer.Width - (bar.X + bar.Width));
        return toLeft < toRight ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    /// <summary>The work area without the taskbar (when the work area still includes it).</summary>
    private static RectInt32 Beside(RectInt32 work, RectInt32 bar, TaskbarEdge edge)
    {
        int left = work.X, top = work.Y, right = work.X + work.Width, bottom = work.Y + work.Height;
        switch (edge)
        {
            case TaskbarEdge.Top:
                top = Math.Max(top, bar.Y + bar.Height);
                break;
            case TaskbarEdge.Bottom:
                bottom = Math.Min(bottom, bar.Y);
                break;
            case TaskbarEdge.Left:
                left = Math.Max(left, bar.X + bar.Width);
                break;
            case TaskbarEdge.Right:
                right = Math.Min(right, bar.X);
                break;
        }

        // An odd report (a bar covering most of the screen): keep the work area.
        return right - left >= 200 && bottom - top >= 200 ? new RectInt32(left, top, right - left, bottom - top) : work;
    }

    private static TaskbarEdge? EdgeFromWorkArea(RectInt32 work, RectInt32 outer)
    {
        if (work.Y + work.Height < outer.Y + outer.Height) return TaskbarEdge.Bottom;
        if (work.Y > outer.Y) return TaskbarEdge.Top;
        if (work.X > outer.X) return TaskbarEdge.Left;
        if (work.X + work.Width < outer.X + outer.Width) return TaskbarEdge.Right;
        return null;
    }

    private static TaskbarEdge NearestEdge(RectInt32 outer, PointInt32 point)
    {
        var distances = new[]
        {
            (TaskbarEdge.Bottom, outer.Y + outer.Height - point.Y),
            (TaskbarEdge.Top, point.Y - outer.Y),
            (TaskbarEdge.Left, point.X - outer.X),
            (TaskbarEdge.Right, outer.X + outer.Width - point.X),
        };
        return distances.MinBy(d => d.Item2).Item1;
    }

    private static bool Intersects(RectInt32 a, RectInt32 b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public NativeMethods.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
}
