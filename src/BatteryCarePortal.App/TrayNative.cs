using System.Runtime.InteropServices;
using Portal.Core.Tray;

namespace BatteryCarePortal;

/// <summary>Win32 reads the tray pop-up needs: the icon's rectangle and the monitor, work area and DPI it is on. Nothing here writes system state.</summary>
internal static class TrayNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier id, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const uint MonitorDefaultToNull = 0, MonitorDefaultToNearest = 2;

    public readonly record struct Target(PxRect Anchor, PxRect Work, double Scale);

    /// <summary>The icon's rectangle (cursor position when the shell cannot say, e.g. the icon sits in the hidden-icons flyout), plus its monitor's work area and scale.</summary>
    public static Target Locate(Guid iconId, IntPtr iconWindow)
    {
        Rect anchor = default;
        bool found = false;
        foreach (NotifyIconIdentifier candidate in new[]
        {
            new NotifyIconIdentifier { cbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), guidItem = iconId },
            new NotifyIconIdentifier { cbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), hWnd = iconWindow, uID = 1 },
        })
        {
            NotifyIconIdentifier id = candidate;
            if ((id.guidItem == Guid.Empty && id.hWnd == IntPtr.Zero) ||
                Shell_NotifyIconGetRect(ref id, out Rect r) != 0 || r.Right <= r.Left || r.Bottom <= r.Top) continue;
            if (MonitorFromRect(ref r, MonitorDefaultToNull) == IntPtr.Zero) continue;
            anchor = r;
            found = true;
            break;
        }
        if (!found)
        {
            GetCursorPos(out Point cursor);
            anchor = new Rect { Left = cursor.X, Top = cursor.Y, Right = cursor.X + 1, Bottom = cursor.Y + 1 };
        }

        IntPtr monitor = MonitorFromRect(ref anchor, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        PxRect work = monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
            ? new PxRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom)
            : new PxRect(0, 0, 1920, 1040);
        double scale = monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        return new Target(new PxRect(anchor.Left, anchor.Top, anchor.Right, anchor.Bottom), work, scale);
    }

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE: 1 = square, 2 = rounded.</summary>
    public static void SetWindowCorners(IntPtr hwnd, bool rounded)
    {
        int preference = rounded ? 2 : 1;
        try { DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int)); } catch (Exception) { }
    }
}
