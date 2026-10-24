using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PeripheralPower.App.Native;

public enum TrayDeviceKind { Device, Mouse, Keyboard, Gamepad }

/// <summary>Presentation of an already valid cached battery value; no discovery or I/O lives here.</summary>
public sealed record TrayDeviceIcon(string DeviceId, string Value, double? Fraction,
    bool IsHistorical, TrayDeviceKind Kind, string Tooltip);

/// <summary>Code-drawn circular battery glyph. Every returned private HICON must be destroyed.</summary>
public static class TrayIconRenderer
{
    [ThreadStatic] private static Dictionary<int, RenderTargetBitmap>? _scratchTargets;
    public static int GetTrayPixelSize()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        uint dpi = tray != IntPtr.Zero ? GetDpiForWindow(tray) : 96;
        if (dpi == 0) dpi = 96;
        return Math.Clamp(GetSystemMetricsForDpi(49, dpi), 16, 64); // SM_CXSMICON
    }

    private static DrawingVisual CreateVisual(TrayDeviceIcon data, int pixels)
    {
        if (pixels is < 16 or > 1024) throw new ArgumentOutOfRangeException(nameof(pixels));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(pixels / 100d, pixels / 100d));
            drawing.DrawEllipse(Brush("#16252D"), null, new Point(50, 50), 49, 49);
            var track = data.IsHistorical ? Brush("#39434B") : Brush("#244B40");
            var accent = data.IsHistorical || data.Fraction is null ? Brush("#8C9AA4") : Brush("#2CE5A4");
            DrawArc(drawing, new Point(50, 50), 42, 135, 270, track, 8);
            if (data.Fraction is { } fraction && fraction > 0)
                DrawArc(drawing, new Point(50, 50), 42, 135, 270 * Math.Clamp(fraction, 0, 1), accent, 8);
            var fontSize = data.Value.Length <= 2 ? 38d : data.Value.Length <= 3 ? 31d : 26d;
            var text = new FormattedText(data.Value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                fontSize, data.IsHistorical ? Brush("#B8C1C9") : Brushes.White, 1);
            drawing.DrawText(text, new Point(50 - text.Width / 2, 46 - text.Height / 2));
            DrawDevice(drawing, data.Kind, accent);
            drawing.Pop();
        }
        return visual;
    }

    public static RenderTargetBitmap Render(TrayDeviceIcon data, int pixels)
    {
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(CreateVisual(data, pixels));
        bitmap.Freeze();
        return bitmap;
    }

    public static void SavePng(TrayDeviceIcon data, int pixels, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(data, pixels)));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        encoder.Save(file);
    }

    public static IntPtr CreateIcon(TrayDeviceIcon data, int pixels)
    {
        if (pixels is < 16 or > 64) throw new ArgumentOutOfRangeException(nameof(pixels));
        // Reuse one mutable compositor target per physical tray size. Repeated
        // battery updates must not accumulate finalizable WPF bitmap resources.
        var targets = _scratchTargets ??= [];
        if (!targets.TryGetValue(pixels, out var image))
            targets[pixels] = image = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        image.Clear();
        image.Render(CreateVisual(data, pixels));
        var bytes = new byte[pixels * pixels * 4];
        image.CopyPixels(bytes, pixels * 4, 0);
        image.Clear();
        var header = new BitmapInfo { Size = 40, Width = pixels, Height = -pixels, Planes = 1, BitCount = 32 };
        var color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建托盘位图。");
        IntPtr mask = IntPtr.Zero;
        try
        {
            Marshal.Copy(bytes, 0, bits, bytes.Length);
            mask = CreateBitmap(pixels, pixels, 1, 1, new byte[((pixels + 15) / 16) * 2 * pixels]);
            if (mask == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建托盘图标遮罩。");
            var info = new IconInfo { IsIcon = true, Color = color, Mask = mask };
            var icon = CreateIconIndirect(ref info);
            if (icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建托盘图标。");
            return icon;
        }
        finally
        {
            if (mask != IntPtr.Zero) DeleteObject(mask);
            DeleteObject(color);
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static void DrawArc(DrawingContext drawing, Point center, double radius, double start, double sweep, Brush color, double width)
    {
        var geometry = new StreamGeometry();
        Point At(double angle) => new(center.X + radius * Math.Cos(angle * Math.PI / 180), center.Y + radius * Math.Sin(angle * Math.PI / 180));
        using (var path = geometry.Open())
        {
            path.BeginFigure(At(start), false, false);
            path.ArcTo(At(start + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        var pen = new Pen(color, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        drawing.DrawGeometry(null, pen, geometry);
    }

    private static void DrawDevice(DrawingContext drawing, TrayDeviceKind kind, Brush accent)
    {
        var pen = new Pen(accent, 2.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (kind)
        {
            case TrayDeviceKind.Mouse:
                drawing.DrawRoundedRectangle(null, pen, new Rect(43, 65, 14, 20), 7, 7);
                drawing.DrawLine(pen, new Point(50, 66), new Point(50, 72));
                break;
            case TrayDeviceKind.Keyboard:
                drawing.DrawRoundedRectangle(null, pen, new Rect(36, 68, 28, 15), 3, 3);
                for (int y = 72; y <= 76; y += 4)
                    for (int x = 41; x <= 59; x += 6) drawing.DrawRectangle(accent, null, new Rect(x, y, 2, 2));
                drawing.DrawLine(pen, new Point(44, 80), new Point(56, 80));
                break;
            case TrayDeviceKind.Gamepad:
                drawing.DrawRoundedRectangle(null, pen, new Rect(35, 68, 30, 15), 6, 6);
                drawing.DrawLine(pen, new Point(40, 75), new Point(48, 75));
                drawing.DrawLine(pen, new Point(44, 71), new Point(44, 79));
                drawing.DrawEllipse(accent, null, new Point(56, 73), 1.7, 1.7);
                drawing.DrawEllipse(accent, null, new Point(60, 77), 1.7, 1.7);
                break;
            default:
                drawing.DrawRoundedRectangle(null, pen, new Rect(42, 66, 16, 18), 3, 3);
                drawing.DrawLine(pen, new Point(47, 81), new Point(53, 81));
                break;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPels, YPels; public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint XHotspot, YHotspot; public IntPtr Mask, Color;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? name);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bits, byte[] data);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateIconIndirect(ref IconInfo info);
}
