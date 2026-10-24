using PeripheralPower.Core;

namespace PeripheralPower.App.Views;

public sealed class CardItem(DeviceSnapshot snapshot)
{
    public DeviceSnapshot Snapshot { get; } = snapshot;
    public string Id => Snapshot.TargetId;
    public string Name => Snapshot.DisplayName + (Snapshot.Hidden ? "（已隐藏）" : "");
    public string Icon => "\uE950";
    public string Battery => Snapshot.Battery.IsValid
        ? (Snapshot.Battery.Kind == BatteryKind.Level ? $"{Snapshot.Battery.Value}/{Snapshot.Battery.Maximum} 档" : $"{(Snapshot.Battery.IsApproximate ? "≈" : "")}{Snapshot.Battery.Value}%")
        : "—";
    public string Connection => $"{Snapshot.TransportText} · {Snapshot.ConnectionText}";
    public string Updated => Snapshot.TimeText;
    public string Status => string.Join(" · ", new[] {
        Snapshot.IsHistorical ? "上次电量" : "",
        Snapshot.IsSystemCache ? "系统缓存" : "",
        Snapshot.Battery.Kind == BatteryKind.Unsupported ? "电量暂不支持" : Snapshot.StatusText,
        Snapshot.Charging == ChargingState.Unknown ? "" : Snapshot.ChargingText
    }.Where(s => !string.IsNullOrEmpty(s)));
    public string Detail => $"来源：{Snapshot.Source}\n读取：{Snapshot.StatusText}；充电：{Snapshot.ChargingText}\n最近查询：{Snapshot.LastQueryAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无"}；在线证据：{Snapshot.OnlineEvidenceAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无"}\n{Snapshot.Error}";
}

public sealed class DiscoveryItem(DeviceDescriptor descriptor)
{
    public DeviceDescriptor Device { get; } = descriptor;
    public string Label => $"{Device.DisplayName}  ·  {Device.AdapterId}  ·  {StateFormatter.Transport(Device.Transport)}";
    public string Detail => $"标识：{Device.Id}\n适配器：{Device.AdapterId}；{Device.MatchReason}\n" +
        string.Join("\n", Device.HidInterfaces.Select(h => $"VID {h.VendorId:X4} / PID {h.ProductId:X4} / Usage {h.UsagePage:X4}:{h.Usage:X4}")) + "\n" +
        string.Join("\n", Device.Properties.Select(p => $"{p.Key}：{p.Value}"));
}
