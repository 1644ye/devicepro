using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PeripheralPower.App.Native;
using PeripheralPower.App.Views;
using PeripheralPower.Core;
using PeripheralPower.Hardware;

namespace PeripheralPower.App;

public sealed class AppController
{
    readonly ConfigStore _store;
    readonly DeviceStateCache _cache = new();
    readonly RollingLog _log;
    readonly IsolatedHardwareBackend _hardware = new();
    readonly RefreshCoordinator _coordinator;
    readonly NativeHost _native;
    readonly CancellationTokenSource _lifetime = new();
    Task? _discoveryTask;
    CancellationTokenSource? _discoveryCancellation;
    PanelWindow? _panel;
    ManagementWindow? _management;
    HoverWindow? _hover;
    bool _suspended;
    public AppConfig Config { get; }
    public string DataDirectory => _store.DirectoryPath;
    public bool IsExiting { get; private set; }
    public ObservableCollection<CardItem> PanelCards { get; } = [];
    public ObservableCollection<CardItem> AllCards { get; } = [];
    public ObservableCollection<DiscoveryItem> Discovered { get; } = [];
    public NativeHost Native => _native;
    public RefreshCoordinator Coordinator => _coordinator;

    public AppController(string? dataDirectory = null)
    {
        _store = new(dataDirectory); Config = _store.Load();
        _log = new(_store.LogDirectory);
        if (_store.LastError != null) _log.Write("configuration", _store.LastError);
        _cache.Load(_store.CachePath);
        _coordinator = new(_hardware, _cache, new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        _coordinator.SetTargets(Config.Targets);
        _coordinator.SnapshotChanged += snapshot => {
            UpdateCards();
            if (snapshot.Status != ReadStatus.Reading) _cache.Save(_store.CachePath);
            SaveHardwareMetrics();
            if (!string.IsNullOrEmpty(snapshot.Error)) _log.Write("device", snapshot.DisplayName + ": " + snapshot.Error);
        };
        _coordinator.DiscoveryChanged += UpdateDiscovery;
        _coordinator.BackgroundError += error => _log.Write("refresh", error);
        _native = new();
        _native.RefreshRequested += (_, _) => Refresh();
        _native.PanelRequested += (_, _) => ShowPanel();
        _native.ManagementRequested += (_, _) => ShowManagement();
        _native.SettingsRequested += (_, _) => ShowSettings();
        _native.DevicesChanged += (_, _) => {
            if (!_suspended) _coordinator.RequestRefresh(RefreshReason.DeviceChanged);
        };
        _native.Suspending += (_, _) => { _suspended = true; _discoveryCancellation?.Cancel(); _coordinator.InvalidateSession(); _log.Write("power", "系统休眠；已取消读取。"); };
        _native.Resumed += (_, _) => { _suspended = false; UpdateTimer(); _coordinator.RequestRefresh(RefreshReason.Resume); _log.Write("power", "系统恢复；提交合并刷新。"); };
        _native.SessionEnding += (_, _) => _ = ExitAsync();
        _native.HoverRequested += (_, args) => { _hover ??= new(); _hover.ShowSummary(args); };
        _native.HoverEnded += (_, _) => _hover?.Hide();
        _native.RichTooltipContainsPoint = (x, y) => _hover?.ContainsScreenPoint(x, y) == true;
        _native.TooltipRequested += (_, _) => UpdateTooltip();
        UpdateCards(); UpdateTimer(); ApplyTheme();
        _log.Write("lifecycle", "应用启动 " + Environment.Version);
    }
    public void Start(bool silent)
    {
        if (!silent) switch (Config.StartupView) { case StartupView.Management: ShowManagement(); break; case StartupView.Panel: ShowPanel(); break; }
        _coordinator.RequestRefresh(RefreshReason.Startup);
    }
    public void ShowPanel()
    {
        if (IsExiting) return;
        _panel ??= new(this);
        Present(_panel);
    }
    public void ShowManagement()
    {
        if (IsExiting) return;
        _management ??= new(this);
        Present(_management);
        if (Discovered.Count == 0) _ = DiscoverAndLogAsync();
    }
    public void ShowSettings()
    {
        if (IsExiting) return;
        _management ??= new(this);
        _management.ShowSettingsPage();
        Present(_management);
    }
    static void Present(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        WindowGeometry.EnsureVisible(window);
        window.Activate();
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        SetForegroundWindow(handle);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr handle);
    public void Refresh() { if (!_suspended && !IsExiting) _coordinator.RequestRefresh(RefreshReason.Manual); }
    async Task DiscoverAndLogAsync() { try { await DiscoverAsync(); } catch (OperationCanceledException) when (_suspended || IsExiting) { } catch (Exception ex) { _log.Write("discovery", ex.Message); } }
    public Task DiscoverAsync()
    {
        if (_suspended || IsExiting) return Task.CompletedTask;
        if (_discoveryTask is { IsCompleted: false }) return _discoveryTask;
        return _discoveryTask = DiscoverCoreAsync();
    }
    async Task DiscoverCoreAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _discoveryCancellation = timeout;
        try {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var found = await _hardware.DiscoverAsync(timeout.Token);
            if (!_suspended && !IsExiting) UpdateDiscovery(found);
            SaveHardwareMetrics();
        } finally { _discoveryCancellation = null; }
    }
    DiscoverySnapshot _lastDiscovery = new();
    void UpdateDiscovery(DiscoverySnapshot discovery)
    {
        _lastDiscovery = discovery;
        Discovered.Clear(); foreach (var d in discovery.Devices.OrderBy(d => d.DisplayName)) Discovered.Add(new(d));
        foreach (var error in discovery.Errors) _log.Write("discovery", error);
    }
    void UpdateCards()
    {
        var selected = _management?.SelectedTargetId;
        AllCards.Clear(); PanelCards.Clear();
        foreach (var state in _cache.GetSnapshots()) { var card = new CardItem(state); AllCards.Add(card); if (!state.Hidden) PanelCards.Add(card); }
        _management?.RestoreSelection(selected);
        UpdateTooltip();
    }
    void UpdateTooltip()
    {
        if (_native is null) return;
        var now = DateTimeOffset.Now;
        var states = _cache.GetSnapshots().Where(s => !s.Hidden).OrderBy(s => s.Order).ToArray();
        _native.SetTooltip(StateFormatter.TraySummary(states, now));
        _native.SetDeviceIcons(states.Where(StateFormatter.ShowDeviceTrayIcon).Select(s =>
        {
            var battery = s.Battery;
            bool historical = s.IsHistorical || s.IsSystemCache || s.Connection == ConnectionState.Disconnected ||
                s.Status is ReadStatus.Failed or ReadStatus.Timeout or ReadStatus.Cancelled;
            string value = battery.Kind == BatteryKind.Level ? $"{battery.Value}/{battery.Maximum}" : battery.Value!.Value.ToString();
            double fraction = battery.Kind == BatteryKind.Level
                ? battery.Value!.Value / (double)battery.Maximum!.Value : battery.Value!.Value / 100d;
            string tooltip = StateFormatter.TrayLine(s, now);
            if (historical && !s.IsHistorical) tooltip += s.IsSystemCache ? "\n系统缓存，设备采样时间未知" : "\n历史电量，当前状态尚未确认";
            return new TrayDeviceIcon(s.TargetId, value, fraction, historical, GetTrayDeviceKind(s), tooltip);
        }));
    }
    TrayDeviceKind GetTrayDeviceKind(DeviceSnapshot snapshot)
    {
        if(snapshot.Shape==DeviceShape.Mouse)return TrayDeviceKind.Mouse;
        if(snapshot.Shape==DeviceShape.Keyboard)return TrayDeviceKind.Keyboard;
        var descriptor = _lastDiscovery.Devices.FirstOrDefault(d => d.Id == snapshot.DeviceId);
        if (descriptor is not null)
        {
            // Composite keyboards also expose a mouse collection for macros.
            // Use the verified adapter family before generic HID usages.
            if (descriptor.AdapterId.StartsWith("wob.", StringComparison.Ordinal)) return TrayDeviceKind.Keyboard;
            if (descriptor.AdapterId.StartsWith("rog.", StringComparison.Ordinal)) return TrayDeviceKind.Mouse;
            if (descriptor.AdapterId.StartsWith("flydigi.", StringComparison.Ordinal)) return TrayDeviceKind.Gamepad;
            if (descriptor.HidInterfaces.Any(h => h.UsagePage == 1 && h.Usage is 4 or 5)) return TrayDeviceKind.Gamepad;
            if (descriptor.HidInterfaces.Any(h => h.UsagePage == 1 && h.Usage == 6)) return TrayDeviceKind.Keyboard;
            if (descriptor.HidInterfaces.Any(h => h.UsagePage == 1 && h.Usage == 2)) return TrayDeviceKind.Mouse;
        }
        return TrayDeviceKind.Device;
    }
    void SaveHardwareMetrics() { try { JsonStorage.WriteAtomic(System.IO.Path.Combine(DataDirectory, "hardware-metrics.json"), _hardware.Metrics); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Write("metrics", ex.Message); } }
    void UpdateTimer() => _native.SetRefreshTimer(Config.AutoRefreshEnabled && Config.Targets.Count > 0 && !_suspended, Config.RefreshIntervalMs);
    public void AddTarget(DeviceDescriptor descriptor)
    {
        if (Config.Targets.Any(t => t.DeviceIds.Contains(descriptor.Id, StringComparer.OrdinalIgnoreCase))) return;
        Config.Targets.Add(new() { DeviceIds = [descriptor.Id], DisplayName = descriptor.DisplayName, Order = Config.Targets.Count });
        TargetsChanged();
    }
    public void LinkTarget(string id, string deviceId)
    {
        // An identity belongs to one target only; this also prevents duplicate channel access.
        if (Config.Targets.Any(t => t.DeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase))) return;
        EditTarget(id, t => t with { DeviceIds = [..t.DeviceIds, deviceId] });
    }
    public void EditTarget(string id, Func<MonitoredTarget, MonitoredTarget> edit)
    {
        int index = Config.Targets.FindIndex(t => t.Id == id);
        if (index < 0) return; Config.Targets[index] = edit(Config.Targets[index]); TargetsChanged();
    }
    public void RemoveTarget(string id) { Config.Targets.RemoveAll(t => t.Id == id); TargetsChanged(); }
    public void MoveTarget(string id, int direction)
    {
        var sorted = Config.Targets.OrderBy(t => t.Order).ToList();
        int index = sorted.FindIndex(t => t.Id == id), other = index + direction;
        if (index < 0 || other < 0 || other >= sorted.Count) return;
        (sorted[index], sorted[other]) = (sorted[other], sorted[index]);
        Config.Targets = sorted.Select((t, i) => t with { Order = i }).ToList(); TargetsChanged();
    }
    void TargetsChanged()
    {
        _coordinator.SetTargets(Config.Targets); SaveConfig(); UpdateCards(); UpdateTimer();
        if (!_suspended) _coordinator.RequestRefresh(RefreshReason.ConfigurationChanged);
    }
    void SaveConfig() { if (!_store.Save(Config)) throw new IOException(_store.LastError); }
    public void SavePanelBounds(WindowPlacement placement) { Config.PanelBounds = placement; /* Persist on hide/exit, avoiding disk writes while dragging. */ }
    public void ApplySettings() { SaveConfig(); UpdateTimer(); ApplyTheme(); }
    public void ToggleTheme() { Config.Theme = Config.Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark; ApplySettings(); }
    void ApplyTheme()
    {
        bool dark = Config.Theme == AppTheme.Dark;
        var values = dark
            ? new[] { "#141D29", "#1E2A39", "#EEF3F8", "#A4B3C5", "#344458", "#54D9BF" }
            : new[] { "#F3F5F8", "#FFFFFF", "#182433", "#64748B", "#DDE3EB", "#167D70" };
        var keys = new[] { "CanvasBrush", "CardBrush", "InkBrush", "MutedBrush", "LineBrush", "AccentBrush" };
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i]));
    }
    public async Task ExportAsync(string path)
    {
        if (_lastDiscovery.Devices.Count == 0) await DiscoverAsync();
        var snapshots = _cache.GetSnapshots();
        await Task.Run(() => DiagnosticExporter.Export(path, Config, _lastDiscovery, snapshots));
    }
    public async Task ExitAsync()
    {
        if (IsExiting) return; IsExiting = true;
        _native.SetRefreshTimer(false, Config.RefreshIntervalMs); _native.Dispose();
        _lifetime.Cancel();
        await _coordinator.DisposeAsync();
        if (_discoveryTask is { } discovering) { try { await discovering; } catch (OperationCanceledException) { } catch (Exception ex) { _log.Write("shutdown", ex.Message); } }
        _hardware.Dispose();
        _cache.Save(_store.CachePath); _store.Save(Config); _log.Write("lifecycle", "正常退出；定时器、读取进程及托盘已释放。");
        _hover?.Close(); _panel?.Close(); _management?.Close();
        _lifetime.Dispose();
        Application.Current.Shutdown();
    }
}
