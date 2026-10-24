namespace PeripheralPower.Core;

public enum BatteryKind { Unknown, Unsupported, Percentage, Level }
public enum ConnectionState { Unknown, Connected, Disconnected }
public enum ConnectionTransport { Unknown, UsbWired, Receiver24Ghz, Bluetooth }
public enum ChargingState { Unknown, Charging, NotCharging, Full }
public enum ReadStatus { Idle, Reading, Success, Timeout, Failed, Unsupported, Cancelled }
public enum RefreshReason { Startup, Timer, Manual, DeviceChanged, Resume, ConfigurationChanged }
public enum StartupView { Management, Panel, Tray }
public enum AppTheme { System, Light, Dark }
public enum DeviceShape { Generic, Keyboard, Mouse, InEar, EarClip }

public sealed record EarbudBattery
{
    public BatteryReading Left { get; init; } = BatteryReading.Unknown;
    public BatteryReading Right { get; init; } = BatteryReading.Unknown;
    public BatteryReading Case { get; init; } = BatteryReading.Unknown;
    public bool IsValid => Left.IsValid || Right.IsValid || Case.IsValid;
    public string Text => $"左 {Format(Left)} · 右 {Format(Right)} · 盒 {Format(Case)}";
    private static string Format(BatteryReading value) => value.IsValid ? $"{value.Value}%" : "—";
}

public static class DeviceAppearance
{
    public static DeviceShape FromName(string name)
    {
        var compact = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (compact.Contains("MOONDROPPILL")) return DeviceShape.EarClip;
        if (compact.Contains("OPPOENCO")) return DeviceShape.InEar;
        if (compact.Contains("V98PRO")) return DeviceShape.Keyboard;
        if (compact.Contains("ATKA9PRO")) return DeviceShape.Mouse;
        return DeviceShape.Generic;
    }
}

public sealed record BatteryReading
{
    public BatteryKind Kind { get; init; }
    public int? Value { get; init; }
    public int? Maximum { get; init; }
    public bool IsApproximate { get; init; }
    public bool IsValid => Kind switch
    {
        BatteryKind.Percentage => Value is >= 0 and <= 100,
        BatteryKind.Level => Maximum is > 0 && Value is >= 0 && Value <= Maximum,
        _ => false
    };
    public static BatteryReading Unknown { get; } = new();
    public static BatteryReading Unsupported { get; } = new() { Kind = BatteryKind.Unsupported };
    public static BatteryReading Percent(int value, bool approximate = false) =>
        value is >= 0 and <= 100 ? new() { Kind = BatteryKind.Percentage, Value = value, Maximum = 100, IsApproximate = approximate } : throw new ArgumentOutOfRangeException(nameof(value));
    public static BatteryReading Level(int value, int maximum) => maximum > 0 && value >= 0 && value <= maximum
        ? new() { Kind = BatteryKind.Level, Value = value, Maximum = maximum } : throw new ArgumentOutOfRangeException(nameof(value));
}

public sealed record HidInterfaceDescriptor
{
    public string Path { get; init; } = "";
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
}

public sealed record DeviceDescriptor
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ContainerId { get; init; } = "";
    public List<string> InstanceIds { get; init; } = [];
    public List<HidInterfaceDescriptor> HidInterfaces { get; init; } = [];
    public ConnectionTransport Transport { get; init; }
    public string AdapterId { get; init; } = "";
    public string MatchReason { get; init; } = "";
    public string ChannelKey { get; init; } = "";
    public Dictionary<string, string> Properties { get; init; } = [];
}

public sealed record DiscoverySnapshot
{
    public List<DeviceDescriptor> Devices { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<string> Errors { get; init; } = [];
}

/// <summary>Cached values MUST set IsSystemCache and MUST NOT invent BatterySampledAt.</summary>
public sealed record DeviceReadResult
{
    public DeviceShape Shape { get; init; }
    public EarbudBattery Earbuds { get; init; } = new();
    public string DeviceId { get; init; } = "";
    public DateTimeOffset QueryTime { get; init; } = DateTimeOffset.UtcNow;
    public BatteryReading Battery { get; init; } = BatteryReading.Unknown;
    public ConnectionState Connection { get; init; }
    public ConnectionTransport Transport { get; init; }
    public ChargingState Charging { get; init; }
    public ReadStatus Status { get; init; }
    public DateTimeOffset? BatterySampledAt { get; init; }
    public DateTimeOffset? BatteryObservedAt { get; init; }
    public DateTimeOffset? OnlineEvidenceAt { get; init; }
    public bool IsSystemCache { get; init; }
    public string Source { get; init; } = "";
    public string Error { get; init; } = "";
}

public interface IHardwareBackend
{
    Task<DiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken);
    // Cancellation must terminate actual I/O (or its isolated process) before completion.
    Task<DeviceReadResult> ReadAsync(DeviceDescriptor device, CancellationToken cancellationToken);
}

public sealed record MonitoredTarget
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public List<string> DeviceIds { get; init; } = [];
    public string DisplayName { get; init; } = "";
    public bool Hidden { get; init; }
    public int Order { get; init; }
}

public sealed record DeviceSnapshot
{
    public DeviceShape Shape { get; init; }
    public EarbudBattery Earbuds { get; init; } = new();
    public string TargetId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool Hidden { get; init; }
    public int Order { get; init; }
    public BatteryReading Battery { get; init; } = BatteryReading.Unknown;
    public ConnectionState Connection { get; init; }
    public ConnectionTransport Transport { get; init; }
    public ChargingState Charging { get; init; }
    public ReadStatus Status { get; init; }
    public DateTimeOffset? LastQueryAt { get; init; }
    public DateTimeOffset? LastValidBatteryAt { get; init; }
    public DateTimeOffset? BatteryObservedAt { get; init; }
    public DateTimeOffset? OnlineEvidenceAt { get; init; }
    public bool IsSystemCache { get; init; }
    public bool IsHistorical { get; init; }
    public string Source { get; init; } = "";
    public string Error { get; init; } = "";
    public string BatteryText => StateFormatter.Battery(this);
    public string ConnectionText => StateFormatter.Connection(Connection);
    public string TransportText => StateFormatter.Transport(Transport);
    public string ChargingText => StateFormatter.Charging(Charging);
    public string StatusText => StateFormatter.Status(Status);
    public string TimeText => StateFormatter.Time(this, DateTimeOffset.Now);
}
