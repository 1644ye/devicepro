using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PeripheralPower.Core;

namespace PeripheralPower.App.Views;

internal static class WindowGeometry
{
    public static void Restore(Window window, WindowPlacement p)
    {
        window.Width = double.IsFinite(p.Width) ? Math.Clamp(p.Width, window.MinWidth, 1800) : 420;
        window.Height = double.IsFinite(p.Height) ? Math.Clamp(p.Height, window.MinHeight, 1400) : 450;
        if (double.IsFinite(p.Left) && double.IsFinite(p.Top)) { window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = p.Left; window.Top = p.Top; }
        window.Dispatcher.BeginInvoke(new Action(() => EnsureVisible(window)));
        HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (m is 0x007E or 0x02E0) window.Dispatcher.BeginInvoke(new Action(() => EnsureVisible(window)));
            return IntPtr.Zero;
        });
    }
    public static WindowPlacement Capture(Window w) => new() { Left = w.Left, Top = w.Top, Width = w.Width, Height = w.Height };
    public static void EnsureVisible(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var r)) return;
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        int width = Math.Min(r.Right - r.Left, info.Work.Right - info.Work.Left);
        int height = Math.Min(r.Bottom - r.Top, info.Work.Bottom - info.Work.Top);
        int x = Math.Clamp(r.Left, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
        int y = Math.Clamp(r.Top, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
        if (x != r.Left || y != r.Top || width != r.Right - r.Left || height != r.Bottom - r.Top)
            SetWindowPos(handle, IntPtr.Zero, x, y, width, height, 0x0014);
    }
    public static void PlaceTooltip(Window window, int x, int y)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(handle, out var r)) return;
        SetWindowPos(handle, IntPtr.Zero, x - (r.Right - r.Left) / 2, y - (r.Bottom - r.Top) - 16, 0, 0, 0x0015);
        EnsureVisible(window);
    }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
