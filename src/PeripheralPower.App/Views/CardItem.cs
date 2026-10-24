using PeripheralPower.Core;

namespace PeripheralPower.App.Views;

public sealed class CardItem(DeviceSnapshot snapshot)
{
    public DeviceSnapshot Snapshot { get; } = snapshot;
    public string Id => Snapshot.TargetId;
    public string Name => Snapshot.DisplayName + (Snapshot.Hidden ? "（已隐藏）" : "");
    public DeviceShape Shape => Snapshot.Shape != DeviceShape.Generic ? Snapshot.Shape : DeviceAppearance.FromName(Snapshot.DisplayName);
    public string Icon => Shape switch { DeviceShape.Keyboard => "\uE765", DeviceShape.Mouse => "\uE962", _ => "\uE950" };
    public bool IsEarbud => Shape is DeviceShape.InEar or DeviceShape.EarClip;
    public System.Windows.Visibility EarbudVisibility => IsEarbud ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility GenericVisibility => IsEarbud ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public string EarbudText => Snapshot.Earbuds.Text;
    public string EarbudGeometry => Shape == DeviceShape.EarClip
        ? "M 8,3 C 0,3 0,18 8,18 C 13,18 13,13 9,13 M 16,3 C 24,3 24,18 16,18 C 11,18 11,13 15,13"
        : "M 3,3 C 0,3 0,9 4,9 L 5,19 L 8,19 L 8,6 C 8,2 5,1 3,3 Z M 21,3 C 24,3 24,9 20,9 L 19,19 L 16,19 L 16,6 C 16,2 19,1 21,3 Z";
    public string Battery => Snapshot.Battery.IsValid
        ? (Snapshot.Battery.Kind == BatteryKind.Level ? $"{Snapshot.Battery.Value}/{Snapshot.Battery.Maximum} 档" : $"{(Snapshot.Battery.IsApproximate ? "≈" : "")}{Snapshot.Battery.Value}%")
        : "—";
    public string Connection => $"{Snapshot.TransportText} · {Snapshot.ConnectionText}" + (IsEarbud ? (Shape == DeviceShape.EarClip ? " · 耳夹式" : " · 入耳式") : "");
    public string Updated => Snapshot.TimeText;
    public string Status => IsEarbud && Snapshot.Battery.IsValid && !Snapshot.Earbuds.IsValid
        ? (Snapshot.IsHistorical ? "上次总电量" : Snapshot.IsSystemCache ? "总电量 · 缓存" : "总电量")
        : string.Join(" · ", new[] {
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
