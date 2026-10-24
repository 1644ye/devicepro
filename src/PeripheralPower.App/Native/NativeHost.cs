using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PeripheralPower.App.Native;

/// <summary>
/// A hidden, top-level message window. It deliberately is not a message-only
/// window, since Explorer's TaskbarCreated and power broadcasts target top-level windows.
/// Its lifetime is independent of both visible WPF windows.
/// </summary>
public sealed class NativeHost : IDisposable
{
    private const int WmTimer = 0x0113;
    private const int WmDeviceChange = 0x0219;
    private const int WmPowerBroadcast = 0x0218;
    private const int WmEndSession = 0x0016;
    private const int WmQueryEndSession = 0x0011;
    private const int WmCopyData = 0x004A;
    private const int WmTray = 0x8000 + 41;
    private const int WmMouseMove = 0x0200;
    private const int WmContextMenu = 0x007B;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0400 + 1;
    private const int NinPopupOpen = 0x0400 + 6;
    private const int NinPopupClose = 0x0400 + 7;
    private const uint RefreshTimerId = 1;
    private const uint HoverTimerId = 2;
    private const uint EntryIconId = 1;
    private const string EntryKey = "__entry__";
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifShowTip = 0x80;
    private readonly HwndSource _source;
    private readonly int _taskbarCreated;
    private readonly int _queryStatus;
    private readonly Dictionary<string, TrayEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, TrayEntry> _entriesById = [];
    private IntPtr _deviceNotification;
    private bool _disposed, _refreshEnabled, _refreshTimerActive, _suspended, _hovering;
    private uint _hoverId;
    private int _iconSize;
    private int _intervalMs = 60_000;
    private string _summary = "外设电量 · 尚未添加监测设备";
    private int _hoverX, _hoverY;

    public NativeHost()
    {
        var parameters = new HwndSourceParameters(SingleInstance.HostWindowTitle)
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, deliberately no WS_VISIBLE
            ExtendedWindowStyle = 0x00000080 | 0x08000000 // TOOLWINDOW | NOACTIVATE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WindowProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _queryStatus = RegisterWindowMessage(StatusMessageName);
        _iconSize = TrayIconRenderer.GetTrayPixelSize();
        SetDeviceIcons([]);
        var filter = new DeviceBroadcastInterface
        {
            Size = Marshal.SizeOf<DeviceBroadcastInterface>(),
            DeviceType = 5 // DBT_DEVTYP_DEVICEINTERFACE
        };
        // All device interface classes include HID, Bluetooth and receiver interfaces.
        // A notification only queues refresh; enumeration never runs in this callback.
        _deviceNotification = RegisterDeviceNotification(_source.Handle, ref filter, 0x4);
        SingleInstance.SignalHostReady();
    }

    public event EventHandler? RefreshRequested;
    public event EventHandler? PanelRequested;
    public event EventHandler? ManagementRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? TooltipRequested;
    public event EventHandler<TrayHoverEventArgs>? HoverRequested;
    public event EventHandler? HoverEnded;
    public event EventHandler? DevicesChanged;
    public event EventHandler? Suspending;
    public event EventHandler? Resumed;
    public event EventHandler? SessionEnding;

    /// <summary>Optional screen-pixel hit test supplied by the visible rich tooltip.</summary>
    public Func<int, int, bool>? RichTooltipContainsPoint { get; set; }

    /// <summary>Read-only native diagnostics: bit 0 timer active, bit 1 suspended, bit 2 rich hover.</summary>
    public const string StatusMessageName = "PeripheralPower.QueryNativeStatus";

    public IntPtr Handle => _source.Handle;
    public bool DeviceNotificationsAvailable => _deviceNotification != IntPtr.Zero;
    public bool TrayAvailable => _entries.Count > 0 && _entries.Values.All(e => e.Added);
    public int TrayIconCount => _entries.Count;
    public int TrayIconPixelSize => _iconSize;
    public IReadOnlyDictionary<string, uint> TrayDeviceIds => _entries.ToDictionary(e => e.Key, e => e.Value.Id);
    public IReadOnlyList<TrayDeviceIcon> TrayDevices => _entries.Values.Select(e => e.Info).ToArray();
    public bool RefreshTimerRunning => _refreshTimerActive;

    /// <summary>Call on the UI thread. Unchanged settings do not reset an existing timer.</summary>
    public void SetRefreshTimer(bool enabled, int intervalMs = 60_000)
    {
        VerifyAccess();
        if (intervalMs is < 30_000 or > 1_800_000)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "刷新周期必须为 30000～1800000 毫秒。");
        if (_refreshEnabled == enabled && _intervalMs == intervalMs &&
            _refreshTimerActive == (enabled && !_suspended)) return;
        _refreshEnabled = enabled;
        _intervalMs = intervalMs;
        StopRefreshTimer();
        if (enabled && !_suspended) StartRefreshTimer();
    }

    /// <summary>Only cached text is accepted. No hardware operation occurs on hover.</summary>
    public void SetTooltip(string summary)
    {
        VerifyAccess();
        var normalized = string.IsNullOrWhiteSpace(summary) ? "外设电量 · 尚未添加监测设备" : summary.Replace('\0', ' ');
        if (normalized == _summary) return;
        _summary = normalized;
        if (_entries.TryGetValue(EntryKey, out var entry))
        {
            UpdateEntry(entry, entry.Info with { Tooltip = normalized });
        }
    }

    /// <summary>One icon per valid, visible cached device. An empty set keeps one entry icon.</summary>
    public void SetDeviceIcons(IEnumerable<TrayDeviceIcon> devices)
    {
        VerifyAccess();
        var incoming = devices.Where(d => !string.IsNullOrWhiteSpace(d.DeviceId))
            .GroupBy(d => d.DeviceId, StringComparer.Ordinal).Select(g => g.First()).ToList();
        foreach (var item in incoming)
        {
            if (item.Fraction is null || !double.IsFinite(item.Fraction.Value) || item.Fraction < 0 || item.Fraction > 1)
                throw new ArgumentException("设备托盘图标必须来自有效电量读数。", nameof(devices));
        }
        if (incoming.Count == 0) incoming.Add(new(EntryKey, "—", null, true, TrayDeviceKind.Device, _summary));
        var keys = incoming.Select(d => d.DeviceId).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _entries.Values.Where(e => !keys.Contains(e.Info.DeviceId)).ToArray()) RemoveEntry(removed);
        foreach (var item in incoming)
        {
            if (_entries.TryGetValue(item.DeviceId, out var existing)) { UpdateEntry(existing, item); continue; }
            uint id = item.DeviceId == EntryKey ? EntryIconId : NextIconId();
            var entry = new TrayEntry(id, item, TrayIconRenderer.CreateIcon(item, _iconSize));
            _entries[item.DeviceId] = entry;
            _entriesById[id] = entry;
            AddTrayIcon(entry);
        }
    }

    private uint NextIconId()
    {
        for (uint id = 2; id <= ushort.MaxValue; id++) if (!_entriesById.ContainsKey(id)) return id;
        throw new InvalidOperationException("托盘图标数超过 Windows 允许的范围。");
    }

    private void UpdateEntry(TrayEntry entry, TrayDeviceIcon info, bool forceRender = false)
    {
        if (entry.Info == info && !forceRender) return;
        bool render = forceRender || entry.Info.Value != info.Value || entry.Info.Fraction != info.Fraction ||
            entry.Info.IsHistorical != info.IsHistorical || entry.Info.Kind != info.Kind;
        var oldIcon = entry.Icon;
        if (render) entry.Icon = TrayIconRenderer.CreateIcon(info, _iconSize);
        entry.Info = info;
        if (entry.Added)
        {
            var data = CreateNotifyData(entry);
            data.Flags = TooltipFlags(entry) | (render ? NifIcon : 0);
            entry.Added = ShellNotifyIcon(1, ref data);
        }
        if (!entry.Added) AddTrayIcon(entry);
        if (render) DestroyIcon(oldIcon);
        if (_hovering && _hoverId == entry.Id)
        {
            if (UseRichTooltip(entry)) HoverRequested?.Invoke(this, new(info.Tooltip, _hoverX, _hoverY));
            else EndHover();
        }
    }

    private void RemoveEntry(TrayEntry entry)
    {
        if (_hovering && _hoverId == entry.Id) EndHover();
        if (entry.Added) { var data = CreateNotifyData(entry); ShellNotifyIcon(2, ref data); }
        DestroyIcon(entry.Icon);
        _entries.Remove(entry.Info.DeviceId);
        _entriesById.Remove(entry.Id);
    }

    private static bool UseRichTooltip(TrayEntry entry) => entry.Info.Tooltip.Length > 127;
    private static uint TooltipFlags(TrayEntry entry) => NifTip | (UseRichTooltip(entry) ? 0 : NifShowTip);

    private NotifyIconData CreateNotifyData(TrayEntry entry) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _source.Handle,
        Id = entry.Id,
        Flags = NifMessage | NifIcon | TooltipFlags(entry),
        CallbackMessage = WmTray,
        Icon = entry.Icon,
        Tip = UseRichTooltip(entry) ? "" : entry.Info.Tooltip,
        Info = "",
        InfoTitle = ""
    };

    private void AddTrayIcon(TrayEntry entry)
    {
        if (_disposed) return;
        var data = CreateNotifyData(entry);
        entry.Added = ShellNotifyIcon(0, ref data);
        // A repeated TaskbarCreated broadcast is harmless even if the old icon
        // still exists (also makes recovery robust to overlapping shell restarts).
        if (!entry.Added) entry.Added = ShellNotifyIcon(1, ref data);
        if (entry.Added)
        {
            data.TimeoutOrVersion = 4; // NOTIFYICON_VERSION_4
            if (!ShellNotifyIcon(4, ref data))
            {
                ShellNotifyIcon(2, ref data);
                entry.Added = false;
            }
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (_queryStatus != 0 && message == _queryStatus)
        {
            handled = true;
            return new IntPtr(0x50500000 | (_refreshTimerActive ? 1 : 0) | (_suspended ? 2 : 0) | (_hovering ? 4 : 0));
        }
        if (_taskbarCreated != 0 && message == _taskbarCreated)
        {
            EndHover();
            foreach (var entry in _entries.Values) { entry.Added = false; AddTrayIcon(entry); }
            handled = true;
            return IntPtr.Zero;
        }
        switch (message)
        {
            case 0x02E0: // WM_DPICHANGED
            case 0x007E: // WM_DISPLAYCHANGE
            case 0x001A: // WM_SETTINGCHANGE
                var size = TrayIconRenderer.GetTrayPixelSize();
                if (size != _iconSize)
                {
                    _iconSize = size;
                    foreach (var entry in _entries.Values) UpdateEntry(entry, entry.Info, forceRender: true);
                }
                break;
            case WmTimer:
                if ((ulong)wParam.ToInt64() == RefreshTimerId)
                {
                    if (_refreshTimerActive && !_suspended) RefreshRequested?.Invoke(this, EventArgs.Empty);
                    handled = true;
                }
                else if ((ulong)wParam.ToInt64() == HoverTimerId)
                {
                    CheckHover();
                    handled = true;
                }
                break;
            case WmTray:
                var notification = unchecked((ushort)lParam.ToInt64());
                var iconId = unchecked((ushort)(lParam.ToInt64() >> 16));
                if (!_entriesById.TryGetValue(iconId, out var selectedEntry)) break;
                handled = true;
                switch (notification)
                {
                    // Version 4's semantic selection message is the sole left-click
                    // route. Raw DOWN/UP/DBLCLK messages are deliberately ignored,
                    // so a physical click cannot be counted twice. This follows the
                    // Windows classic NotificationIcon sample's NIN_SELECT guidance.
                    case NinSelect:
                    case NinKeySelect:
                        EndHover();
                        PanelRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    // Version 4 sends WM_CONTEXTMENU for right click. Do not also
                    // act on WM_RBUTTONUP, and do not create any tray context menu.
                    case WmContextMenu:
                        EndHover();
                        SettingsRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    case WmMouseMove:
                    case NinPopupOpen:
                        // Reformat cached dates on hover, so midnight requires no
                        // continuously running clock/display timer.
                        TooltipRequested?.Invoke(this, EventArgs.Empty);
                        if (_entriesById.TryGetValue(iconId, out var hoveredEntry) && UseRichTooltip(hoveredEntry)) BeginHover(wParam, hoveredEntry);
                        break;
                    case NinPopupClose:
                        // Moving from the icon into its interactive rich tooltip must
                        // leave the tooltip open. The existing hover-only timer allows
                        // crossing the small gap, then checks cached UI geometry only.
                        break;
                }
                break;
            case WmDeviceChange:
                var change = wParam.ToInt64();
                if (change is 0x8000 or 0x8004 or 0x0007) // arrival, removal, topology change
                    if (!_suspended) DevicesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case WmPowerBroadcast:
                handled = true;
                switch (wParam.ToInt64())
                {
                    case 4: // PBT_APMSUSPEND
                        if (!_suspended)
                        {
                            _suspended = true;
                            StopRefreshTimer();
                            EndHover();
                            Suspending?.Invoke(this, EventArgs.Empty);
                        }
                        break;
                    case 6: // PBT_APMRESUMECRITICAL
                    case 7: // PBT_APMRESUMESUSPEND
                    case 0x12: // PBT_APMRESUMEAUTOMATIC (may precede RESUMESUSPEND)
                        if (_suspended)
                        {
                            _suspended = false;
                            if (_refreshEnabled) StartRefreshTimer();
                            Resumed?.Invoke(this, EventArgs.Empty);
                        }
                        break;
                }
                return new IntPtr(1);
            case WmQueryEndSession:
                handled = true;
                return new IntPtr(1);
            case WmEndSession:
                if (wParam != IntPtr.Zero) SessionEnding?.Invoke(this, EventArgs.Empty);
                break;
            case WmCopyData:
                if (lParam != IntPtr.Zero)
                {
                    var data = Marshal.PtrToStructure<SingleInstance.CopyData>(lParam);
                    if (data.Tag == SingleInstance.OpenManagementTag && data.ByteCount == 0)
                    {
                        // Dispatch after SendMessageTimeout returns; a modal window or
                        // other lengthy UI operation cannot stall the launching process.
                        _source.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!_disposed) ManagementRequested?.Invoke(this, EventArgs.Empty);
                        }));
                        handled = true;
                        return new IntPtr(1);
                    }
                }
                break;
        }
        return IntPtr.Zero;
    }

    private void StartRefreshTimer()
    {
        if (SetTimer(_source.Handle, (UIntPtr)RefreshTimerId, (uint)_intervalMs, IntPtr.Zero) == UIntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 Win32 刷新定时器。");
        _refreshTimerActive = true;
    }

    private void StopRefreshTimer()
    {
        KillTimer(_source.Handle, (UIntPtr)RefreshTimerId);
        _refreshTimerActive = false;
    }

    private void BeginHover(IntPtr coordinates, TrayEntry entry)
    {
        _hoverX = unchecked((short)coordinates.ToInt64());
        _hoverY = unchecked((short)(coordinates.ToInt64() >> 16));
        if (_hovering && _hoverId == entry.Id) return;
        EndHover();
        _hovering = true;
        _hoverId = entry.Id;
        // This timer exists only while showing an oversized tooltip. Normal background
        // residency has just the configured 30s–30min timer, or no timer at all.
        if (SetTimer(_source.Handle, (UIntPtr)HoverTimerId, 500, IntPtr.Zero) == UIntPtr.Zero)
        {
            _hovering = false;
            return;
        }
        HoverRequested?.Invoke(this, new(entry.Info.Tooltip, _hoverX, _hoverY));
    }

    private void CheckHover()
    {
        if (!_hovering || !GetCursorPos(out var cursor)) { EndHover(); return; }
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = _source.Handle, Id = _hoverId
        };
        var result = ShellNotifyIconGetRect(ref identifier, out var rect);
        if (result != 0)
        {
            // Safe fallback when Explorer cannot provide a rectangle (e.g. during restart).
            rect = new Rect { Left = _hoverX - 20, Right = _hoverX + 20, Top = _hoverY - 20, Bottom = _hoverY + 20 };
        }
        if (cursor.X < rect.Left || cursor.X >= rect.Right || cursor.Y < rect.Top || cursor.Y >= rect.Bottom)
        {
            if (RichTooltipContainsPoint?.Invoke(cursor.X, cursor.Y) != true) EndHover();
        }
    }

    private void EndHover()
    {
        if (!_hovering) return;
        _hovering = false;
        KillTimer(_source.Handle, (UIntPtr)HoverTimerId);
        HoverEnded?.Invoke(this, EventArgs.Empty);
    }

    private void VerifyAccess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _source.Dispatcher.VerifyAccess();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _source.Dispatcher.VerifyAccess();
        StopRefreshTimer();
        EndHover();
        _disposed = true;
        if (_deviceNotification != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_deviceNotification);
            _deviceNotification = IntPtr.Zero;
        }
        foreach (var entry in _entries.Values.ToArray()) RemoveEntry(entry);
        _source.RemoveHook(WindowProc);
        _source.Dispose();
    }

    private sealed class TrayEntry(uint id, TrayDeviceIcon info, IntPtr icon)
    {
        public uint Id { get; } = id;
        public TrayDeviceIcon Info { get; set; } = info;
        public IntPtr Icon { get; set; } = icon;
        public bool Added { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceBroadcastInterface
    {
        public int Size, DeviceType, Reserved;
        public Guid ClassGuid;
        public short Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint interval, IntPtr timerProc);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")] private static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", EntryPoint = "RegisterDeviceNotificationW", SetLastError = true)] private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DeviceBroadcastInterface filter, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterDeviceNotification(IntPtr handle);
}

public sealed class TrayHoverEventArgs(string text, int screenX, int screenY) : EventArgs
{
    public string Text { get; } = text;
    public int ScreenX { get; } = screenX;
    public int ScreenY { get; } = screenY;
}
