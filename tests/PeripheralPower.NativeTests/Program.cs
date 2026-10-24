using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PeripheralPower.Core;
using PeripheralPower.App.Native;

internal static class Program
{
    private static readonly List<TestResult> Results = [];
    private const uint WmTray = 0x8000 + 41;
    private static int _refreshes, _panels, _settings, _management, _suspends, _resumes, _devices, _hovers, _hoverEnds;

    [STAThread]
    private static int Main(string[] args)
    {
        var preview = GetArgument(args, "--render-preview");
        if (preview is not null) { RenderPreview(Path.GetFullPath(preview)); return 0; }
        if (args.Contains("--ipc-client"))
        {
            if (SingleInstance.TryAcquire(out var other)) { other!.Dispose(); return 92; }
            return SingleInstance.NotifyExistingInstance() ? 0 : 93;
        }
        var output = GetArgument(args, "--output") ?? Path.Combine(AppContext.BaseDirectory, "native-test-results.json");
        var started = DateTimeOffset.Now;
        if (!SingleInstance.TryAcquire(out var instance))
        {
            Results.Add(new("exclusive test host", "skip", "An application/test instance is running. Close it before running native tests."));
            WriteResults(output, started);
            return 2;
        }
        using (instance)
        using (var host = new NativeHost())
        {
            host.RefreshRequested += (_, _) => _refreshes++;
            host.PanelRequested += (_, _) => _panels++;
            host.ManagementRequested += (_, _) => _management++;
            host.SettingsRequested += (_, _) => _settings++;
            host.Suspending += (_, _) => _suspends++;
            host.Resumed += (_, _) => _resumes++;
            host.DevicesChanged += (_, _) => _devices++;
            host.HoverRequested += (_, _) => _hovers++;
            host.HoverEnded += (_, _) => _hoverEnds++;
            Check("hidden host receives native messages with no visible WPF window", () =>
            {
                Assert(host.Handle != IntPtr.Zero, "HwndSource was not created.");
                Assert(!IsWindowVisible(host.Handle), "Message host must not be visible.");
                Assert(host.DeviceNotificationsAvailable, "RegisterDeviceNotification failed.");
            });
            Check("timer default 60000ms and 30000/1800000ms boundaries", () =>
            {
                foreach (var interval in new[] { 60_000, 30_000, 1_800_000 })
                {
                    var before = _refreshes;
                    host.SetRefreshTimer(true, interval);
                    Assert(host.RefreshTimerRunning, "SetTimer did not start.");
                    Send(host, 0x0113, 1);
                    Assert(_refreshes == before + 1, "WM_TIMER did not submit exactly once.");
                }
                AssertThrows<ArgumentOutOfRangeException>(() => host.SetRefreshTimer(true, 29_999));
                AssertThrows<ArgumentOutOfRangeException>(() => host.SetRefreshTimer(true, 1_800_001));
            });
            Check("stopping timer rejects stale WM_TIMER; left click opens panel without refresh", () =>
            {
                host.SetRefreshTimer(false);
                var before = _refreshes;
                Assert(!host.RefreshTimerRunning, "Timer remains active.");
                Send(host, 0x0113, 1);
                Assert(_refreshes == before, "Stopped timer accepted a stale event.");
                Tray(host, 0x0400);
                Assert(_refreshes == before, "Left click must not request a refresh.");
            });
            Check("read-only native status exposes timer enable/disable truth", () =>
            {
                var message = RegisterWindowMessage(NativeHost.StatusMessageName);
                host.SetRefreshTimer(false);
                var stopped = SendMessage(host.Handle, message, IntPtr.Zero, IntPtr.Zero).ToInt64();
                Assert((stopped & 0xFFFF0000L) == 0x50500000L && (stopped & 1) == 0, "Stopped status is invalid.");
                host.SetRefreshTimer(true);
                var running = SendMessage(host.Handle, message, IntPtr.Zero, IntPtr.Zero).ToInt64();
                Assert((running & 1) == 1, "Running status is invalid.");
                host.SetRefreshTimer(false);
            });
            Check("left down/up/select opens one panel and never refreshes", () =>
            {
                var before = _refreshes; var panels = _panels;
                Tray(host, 0x0201); // WM_LBUTTONDOWN
                Tray(host, 0x0202); // raw WM_LBUTTONUP is deliberately ignored
                Tray(host, 0x0400); // version 4 NIN_SELECT is the sole left-click route
                Assert(_refreshes == before, "Left click submitted a hardware refresh.");
                Assert(_panels == panels + 1, "Left click did not request exactly one panel presentation.");
                Tray(host, 0x0203); // double-click message alone must not add another request
                Assert(_panels == panels + 1, "WM_LBUTTONDBLCLK opened a second panel.");
            });
            Check("right down/up/context requests global settings once without panel or refresh", () =>
            {
                var refreshes = _refreshes; var panels = _panels; var before = _settings;
                for (var i = 0; i < 10; i++)
                {
                    Tray(host, 0x0204); Tray(host, 0x0205); Tray(host, 0x007B);
                }
                Assert(_settings == before + 10, "Right-click was duplicated or lost.");
                Assert(_panels == panels, "Right click opened the panel instead of global settings.");
                Assert(_refreshes == refreshes, "Right-click submitted a hardware refresh.");
            });
            Check("tooltip hover only reads cached text and rich overlay closes", () =>
            {
                var before = _refreshes; var hover = _hovers; var ended = _hoverEnds;
                host.SetTooltip("设备：电量未知（尚无有效读数）");
                Tray(host, 0x0200);
                Assert(_hovers == hover, "Short text should use the native tooltip.");
                host.SetTooltip(string.Join('\n', Enumerable.Repeat("设备：历史值 45%（2026-01-01 12:30:00 获取；系统缓存）", 8)));
                Tray(host, 0x0200); Tray(host, 0x0200); Tray(host, 0x0406);
                Assert(_hovers == hover + 1, "Repeated mouse messages opened repeated overlays.");
                Assert(_refreshes == before, "Hover triggered hardware refresh.");
                host.RichTooltipContainsPoint = (_, _) => true;
                Tray(host, 0x0407);
                Send(host, 0x0113, 2);
                Assert(_hoverEnds == ended, "Moving into the rich tooltip closed it.");
                host.RichTooltipContainsPoint = null;
                host.SetTooltip("短提示");
                Assert(_hoverEnds == ended + 1, "Rich overlay did not close.");
            });
            Check("device arrival/removal only enqueue device-change events", () =>
            {
                var before = _devices;
                DeviceNotification(host, 0x8000); DeviceNotification(host, 0x8004); Send(host, 0x0219, 7);
                Assert(_devices == before + 3, $"Device notification was not routed (before={before}, after={_devices}).");
            });
            Check("suspend stops timer and suppresses queries; resume notifications merge once", () =>
            {
                host.SetRefreshTimer(true);
                var before = _refreshes; var devices = _devices;
                Send(host, 0x0218, 4);
                Assert(!host.RefreshTimerRunning && _suspends == 1, "Suspend did not stop the timer.");
                Send(host, 0x0113, 1); Tray(host, 0x0400); DeviceNotification(host, 0x8000);
                Assert(_refreshes == before && _devices == devices, "A query was submitted during suspend.");
                Send(host, 0x0218, 0x12); Send(host, 0x0218, 7);
                Assert(_resumes == 1 && host.RefreshTimerRunning, "Resume was duplicated or timer did not resume.");
                host.SetRefreshTimer(false);
            });
            Check("TaskbarCreated recreates tray registration, including repeated broadcast", () =>
            {
                if (!host.TrayAvailable) throw new SkipException("Explorer notification area is unavailable in this execution desktop.");
                var message = RegisterWindowMessage("TaskbarCreated");
                Send(host, message, 0); Send(host, message, 0);
                Assert(host.TrayAvailable, "Tray icon was not restored.");
            });
            Check("second process cannot own mutex and opens management via bounded IPC", () =>
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
                info.ArgumentList.Add("--ipc-client");
                using var child = Process.Start(info) ?? throw new InvalidOperationException("IPC child did not start.");
                PumpUntil(() => child.HasExited, TimeSpan.FromSeconds(8));
                Assert(child.HasExited && child.ExitCode == 0, "Duplicate-process notification failed.");
                PumpUntil(() => _management == 1, TimeSpan.FromSeconds(2));
                Assert(_management == 1, "ManagementRequested was not delivered once.");
            });
            Check("valid device icons replace fallback; stable IDs, removal and multi-icon routes", () =>
            {
                Assert(host.TrayIconCount == 1 && host.TrayDeviceIds.ContainsKey("__entry__"), "No-reading fallback is missing.");
                var first = new TrayDeviceIcon("test-mouse", "82", .82, false, TrayDeviceKind.Mouse, "开发测试鼠标：82%");
                var second = new TrayDeviceIcon("test-keyboard", "3/5", .6, true, TrayDeviceKind.Keyboard, "开发测试键盘：历史 3/5 档");
                host.SetDeviceIcons([first, second]);
                Assert(host.TrayIconCount == 2 && !host.TrayDeviceIds.ContainsKey("__entry__"), "Device icons did not replace fallback.");
                var ids = host.TrayDeviceIds;
                host.SetDeviceIcons([second, first with { Value = "81", Fraction = .81 }]);
                Assert(host.TrayDeviceIds["test-mouse"] == ids["test-mouse"] && host.TrayDeviceIds["test-keyboard"] == ids["test-keyboard"], "Updating/reordering changed stable icon IDs.");
                int panels = _panels, settings = _settings, refreshes = _refreshes;
                foreach (var id in ids.Values) { Tray(host, 0x0400, id); Tray(host, 0x007B, id); }
                Assert(_panels == panels + 2 && _settings == settings + 2 && _refreshes == refreshes, "Per-device click routing is incorrect.");
                host.SetDeviceIcons([second]);
                Tray(host, 0x0400, ids["test-mouse"]);
                Assert(_panels == panels + 2, "A deleted icon callback was accepted.");
                Send(host, RegisterWindowMessage("TaskbarCreated"), 0);
                Assert(host.TrayIconCount == 1 && host.TrayDeviceIds.ContainsKey("test-keyboard"), "Explorer recovery changed the device set.");
                host.SetDeviceIcons([]);
                Assert(host.TrayIconCount == 1 && host.TrayDeviceIds.ContainsKey("__entry__"), "Removing all readings did not restore fallback.");
            });
            Check("dynamic icon updates release USER/GDI resources", () =>
            {
                var process = Process.GetCurrentProcess();
                // Warm WPF/font caches before measuring native handles.
                host.SetDeviceIcons([new("stress", "50", .5, false, TrayDeviceKind.Mouse, "开发测试")]);
                for (int i = 0; i < 120; i++)
                    host.SetDeviceIcons([new("stress", (i % 101).ToString(), i % 101 / 100d, i % 2 == 0, TrayDeviceKind.Mouse, "开发测试")]);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                int userBefore = GetGuiResources(process.Handle, 1), gdiBefore = GetGuiResources(process.Handle, 0);
                for (int i = 0; i < 120; i++)
                    host.SetDeviceIcons([new("stress", (i % 101).ToString(), i % 101 / 100d, i % 2 == 0, TrayDeviceKind.Mouse, "开发测试")]);
                // WPF RenderTargetBitmap owns finalizable compositor resources.
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                int userAfter = GetGuiResources(process.Handle, 1), gdiAfter = GetGuiResources(process.Handle, 0);
                Assert(userAfter - userBefore <= 3 && gdiAfter - gdiBefore <= 3, $"Icon updates leaked resources: USER {userBefore}->{userAfter}, GDI {gdiBefore}->{gdiAfter}.");
                host.SetDeviceIcons([]);
            });
            Check("icon renderer preserves fraction, history and alpha at supported tray sizes", () =>
            {
                var valid = new TrayDeviceIcon("render", "82", .82, false, TrayDeviceKind.Mouse, "开发测试");
                foreach (int pixels in new[] { 16, 20, 24, 32, 48 })
                {
                    var bitmap = TrayIconRenderer.Render(valid, pixels);
                    Assert(bitmap.PixelWidth == pixels && bitmap.PixelHeight == pixels, "Renderer ignored requested DPI pixel size.");
                    var data = new byte[pixels * pixels * 4]; bitmap.CopyPixels(data, pixels * 4, 0);
                    Assert(data[3] == 0, "Icon corners must be transparent.");
                    Assert(data.Where((_, i) => i % 4 == 3).Any(alpha => alpha > 0), "Icon is empty.");
                }
                int GreenPixels(TrayDeviceIcon value)
                {
                    var bitmap = TrayIconRenderer.Render(value, 128); var data = new byte[128 * 128 * 4]; bitmap.CopyPixels(data, 128 * 4, 0);
                    int count = 0; for (int i = 0; i < data.Length; i += 4) if (data[i + 1] > data[i + 2] + 40 && data[i + 1] > data[i] + 20) count++;
                    return count;
                }
                Assert(GreenPixels(valid) > GreenPixels(valid with { Fraction = .2 }), "Battery ring does not reflect the cached fraction.");
                Assert(GreenPixels(valid with { IsHistorical = true }) == 0, "Historical icon was presented with a live green ring.");
            });
        }
        Check("disposing host destroys native window", () =>
        {
            var disposable = new NativeHost();
            var handle = disposable.Handle;
            disposable.Dispose(); disposable.Dispose();
            Assert(!IsWindow(handle), "Native window survived Dispose.");
        });
        Check("current-user startup registration and rollback", TestStartup);
        Check("cached valid devices alone get icons; UI routes reuse windows without hardware", TestUiRoutes);
        WriteResults(output, started);
        return Results.Any(r => r.Outcome == "fail") ? 1 : 0;
    }

    private static void TestUiRoutes()
    {
        var app = new PeripheralPower.App.App();
        app.InitializeComponent();
        var directory = Path.Combine(Path.GetTempPath(), "PeripheralPower.NativeUiTest-" + Guid.NewGuid().ToString("N"));
        // Explicit development fixtures verify cache projection, never enter the user's profile,
        // and never start the hardware coordinator. Unknown/hidden targets get no icon.
        var targets = new List<MonitoredTarget>
        {
            new() { Id = "fixture-mouse", DisplayName = "开发测试鼠标", DeviceIds = ["fixture-mouse-device"] },
            new() { Id = "fixture-keyboard", DisplayName = "开发测试键盘", DeviceIds = ["fixture-keyboard-device"] },
            new() { Id = "fixture-unknown", DisplayName = "开发测试未知设备", DeviceIds = ["fixture-unknown-device"] },
            new() { Id = "fixture-hidden", DisplayName = "开发测试隐藏设备", DeviceIds = ["fixture-hidden-device"], Hidden = true }
        };
        var store = new ConfigStore(directory);
        Assert(store.Save(new AppConfig { Targets = targets, AutoRefreshEnabled = false }), "Could not create isolated fixture configuration.");
        var cache = new DeviceStateCache(); cache.SetTargets(targets);
        var time = DateTimeOffset.Now.AddMinutes(-2);
        foreach (var target in targets.Where(t => t.Id != "fixture-unknown"))
            cache.Apply(target.Id, new DeviceReadResult { DeviceId = target.DeviceIds[0], Battery = target.Id == "fixture-keyboard" ? BatteryReading.Level(3, 5) : BatteryReading.Percent(82),
                Status = ReadStatus.Success, QueryTime = time, BatteryObservedAt = time, BatterySampledAt = time, Connection = ConnectionState.Connected, Source = "Explicit development fixture; no hardware" });
        Assert(cache.Save(store.CachePath), "Could not save the isolated fixture cache.");
        var controller = new PeripheralPower.App.AppController(directory);
        try
        {
            Assert(controller.Native.TrayIconCount == 2 && controller.Native.TrayDeviceIds.ContainsKey("fixture-mouse") && controller.Native.TrayDeviceIds.ContainsKey("fixture-keyboard"), "Unknown/hidden devices got icons or valid cache was omitted.");
            Assert(controller.Native.TrayDevices.All(d => d.IsHistorical), "Loaded cache was presented as a live reading.");
            uint iconId = controller.Native.TrayDeviceIds["fixture-mouse"];
            for (int i = 0; i < 10; i++) Tray(controller.Native, 0x0400, iconId);
            Assert(app.Windows.OfType<PeripheralPower.App.Views.PanelWindow>().Count() == 1, "Left click created duplicate panels.");
            Assert(!app.Windows.OfType<PeripheralPower.App.Views.ManagementWindow>().Any(), "Left click unexpectedly opened management.");
            for (int i = 0; i < 10; i++) Tray(controller.Native, 0x007B, iconId);
            var settings = app.Windows.OfType<PeripheralPower.App.Views.ManagementWindow>().Single();
            Assert(settings.IsVisible && settings.IsSettingsPageSelected, "Right click did not open the global settings page.");
            Assert(controller.Discovered.Count == 0 && !controller.Coordinator.IsRefreshing, "Clicking tray requested hardware work.");
            Assert(!File.Exists(Path.Combine(directory, "hardware-metrics.json")), "The no-target route test ran a hardware probe.");
        }
        finally { controller.ExitAsync().GetAwaiter().GetResult(); }
    }

    private static void RenderPreview(string path)
    {
        var examples = new[]
        {
            new TrayDeviceIcon("sample-mouse", "82", .82, false, TrayDeviceKind.Mouse, "开发示例"),
            new TrayDeviceIcon("sample-keyboard", "46", .46, false, TrayDeviceKind.Keyboard, "开发示例"),
            new TrayDeviceIcon("sample-gamepad", "3/5", .6, false, TrayDeviceKind.Gamepad, "开发示例"),
            new TrayDeviceIcon("sample-history", "46", .46, true, TrayDeviceKind.Mouse, "历史开发示例"),
            new TrayDeviceIcon("sample-entry", "—", null, true, TrayDeviceKind.Device, "无有效电量入口")
        };
        var labels = new[] { "鼠标 · 82%", "键盘 · 46%", "手柄 · 3/5 档", "历史值 · 灰色", "暂无有效值 · 入口" };
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(239, 243, 247)), null, new Rect(0, 0, 840, 350));
            void Label(string text, double x, double y, double size = 14) => drawing.DrawText(new FormattedText(text,
                System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, new SolidColorBrush(Color.FromRgb(37, 52, 65)), 1), new Point(x, y));
            Label("托盘图标设计预览 · 开发示例（非设备实时读数）", 26, 17, 19);
            for (int i = 0; i < examples.Length; i++)
            {
                drawing.DrawImage(TrayIconRenderer.Render(examples[i], 128), new Rect(36 + i * 160, 65, 128, 128));
                Label(labels[i], 42 + i * 160, 204);
            }
            Label("Windows DPI 原始像素大小：", 28, 252);
            int x = 255;
            foreach (int size in new[] { 16, 20, 24, 32, 48 })
            {
                drawing.DrawImage(TrayIconRenderer.Render(examples[0], size), new Rect(x, 244, size, size));
                Label(size + " px", x - 2, 302, 12);
                x += 94;
            }
        }
        var image = new RenderTargetBitmap(840, 350, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path); encoder.Save(file);
        Console.WriteLine("Preview (development examples only): " + path);
    }

    private static void TestStartup()
    {
        const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string approvedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string approved32Path = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
        foreach (var path in new[] { runPath, approvedPath, approved32Path })
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            if (key?.GetValue("PeripheralPower") is not null)
                throw new SkipException("Existing application startup settings were preserved. Test requires no preexisting registration/approval value.");
        }
        try
        {
            var enabled = StartupService.SetEnabled(true);
            Assert(enabled.IsRegistered && enabled.Enabled && enabled.Error is null, "Registration failed: " + enabled.Error);
            using var run = Registry.CurrentUser.OpenSubKey(runPath);
            Assert(run?.GetValue("PeripheralPower") as string == StartupService.GetStartupCommand(), "Actual Run command differs.");
            Assert(enabled.Command!.EndsWith(" --startup", StringComparison.Ordinal), "Silent-start parameter is missing.");
            using (var approved = Registry.CurrentUser.CreateSubKey(approvedPath, writable: true))
            {
                var disabledFlag = new byte[12]; disabledFlag[0] = 3;
                approved.SetValue("PeripheralPower", disabledFlag, RegistryValueKind.Binary);
            }
            var windowsDisabled = StartupService.GetStatus();
            Assert(windowsDisabled.IsRegistered && !windowsDisabled.Enabled && windowsDisabled.DisabledByWindows, "Windows disabled state was falsely shown as enabled.");
            var attemptedEnable = StartupService.SetEnabled(true);
            Assert(!attemptedEnable.Enabled && attemptedEnable.DisabledByWindows, "Registering a Run item silently bypassed Windows approval state.");
            using (var approved = Registry.CurrentUser.OpenSubKey(approvedPath, writable: true))
            {
                var unknownFlag = new byte[12]; unknownFlag[0] = 99;
                approved!.SetValue("PeripheralPower", unknownFlag, RegistryValueKind.Binary);
            }
            var unknownApproval = StartupService.GetStatus();
            Assert(!unknownApproval.Enabled && !string.IsNullOrWhiteSpace(unknownApproval.Error), "Unknown Windows approval state was falsely shown as enabled.");
            var disabled = StartupService.SetEnabled(false);
            Assert(!disabled.Enabled && !disabled.IsRegistered && disabled.Error is null, "Unregistration failed: " + disabled.Error);
        }
        finally
        {
            StartupService.SetEnabled(false);
            foreach (var path in new[] { runPath, approvedPath, approved32Path })
            {
                using var key = Registry.CurrentUser.OpenSubKey(path);
                Assert(key?.GetValue("PeripheralPower") is null, "Test left a startup value behind.");
            }
        }
    }

    private static void PumpUntil(Func<bool> complete, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) => { if (complete() || clock.Elapsed >= limit) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void Check(string name, Action test)
    {
        try { test(); Results.Add(new(name, "pass", "Actual Win32 messages/registry APIs; hardware and manual shell interactions are separate checks.")); }
        catch (SkipException ex) { Results.Add(new(name, "skip", ex.Message)); }
        catch (Exception ex) { Results.Add(new(name, "fail", ex.ToString())); }
        Console.WriteLine($"{Results[^1].Outcome.ToUpperInvariant()}: {name} — {Results[^1].Detail}");
    }

    private static void WriteResults(string path, DateTimeOffset started)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Started = started, Finished = DateTimeOffset.Now,
            Category = "Windows native message integration; programmatic messages are not physical shell clicks/sleep",
            OS = Environment.OSVersion.ToString(), Framework = RuntimeInformation.FrameworkDescription,
            UserInteractive = Environment.UserInteractive, Tests = Results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Results: " + Path.GetFullPath(path));
    }

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Send(NativeHost host, uint message, long wParam) => SendMessage(host.Handle, message, new IntPtr(wParam), IntPtr.Zero);
    private static void Tray(NativeHost host, uint notification, uint iconId = 1) => SendMessage(host.Handle, WmTray, IntPtr.Zero, new IntPtr((iconId << 16) | notification));
    private static void DeviceNotification(NativeHost host, uint change)
    {
        // Arrival/removal messages carry a DEV_BROADCAST_HDR-derived payload.
        // User32 rejects a null payload for these events before invoking our hook.
        var payload = new DeviceInterfaceNotification
        {
            Size = Marshal.SizeOf<DeviceInterfaceNotification>(), DeviceType = 5,
            ClassGuid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030"), Name = "TEST"
        };
        var pointer = Marshal.AllocHGlobal(payload.Size);
        try
        {
            Marshal.StructureToPtr(payload, pointer, false);
            SendMessage(host.Handle, 0x0219, new IntPtr(change), pointer);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    private static string? GetArgument(string[] args, string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    private sealed record TestResult(string Name, string Outcome, string Detail);
    private sealed class SkipException(string message) : Exception(message);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInterfaceNotification
    {
        public int Size, DeviceType, Reserved;
        public Guid ClassGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string Name;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, uint flags);
}
