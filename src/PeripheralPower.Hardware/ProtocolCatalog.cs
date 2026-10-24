using PeripheralPower.Core;

namespace PeripheralPower.Hardware;

public static class ProtocolCatalog
{
    public const string RogWired="rog.harpe-ace-mini.wired";
    public const string RogOmni="rog.omni.query-identity";
    public const string Apex5="flydigi.apex5.query";
    public const string WobNative="wob.zen65.native-24g";
    public static DeviceDescriptor Classify(DeviceDescriptor device)
    {
        if(device.HidInterfaces.Any(AtkBatteryQuery.IsInterface)) return device with {AdapterId=AtkBatteryQuery.Adapter,
            DisplayName=device.HidInterfaces.Any(i=>AtkBatteryQuery.IsInterface(i)&&i.ProductId==0x1178)
                ? "ATK 鼠标（2.4G · 8K NANO 接收器）" : "ATK A9 Pro（USB）",
            Transport=device.HidInterfaces.Any(i=>AtkBatteryQuery.IsInterface(i)&&i.ProductId==0x1178)?ConnectionTransport.Receiver24Ghz:ConnectionTransport.UsbWired,
            MatchReason="ATK HUB HOLTEK：373B:1178 / A9 Pro 112D，FF10:0001；只读 82 电量/链路状态"};
        if(VgnBatteryQuery.Matches(device)) return device with {AdapterId=VgnBatteryQuery.Adapter,Transport=ConnectionTransport.Receiver24Ghz,
            MatchReason="ATK HUB 3.2.28：V98pro / 320F:5088 / FF1C:0092；EVISION 04/1A 只读电量"};
        if(OppoBatteryQuery.Matches(device))
            return device with {AdapterId="oppo.melody.battery",MatchReason="OPPO 名称及 Melody 服务 UUID 匹配；只读 0106 左右耳及盒电量，失败回退系统缓存"};
        // 1ACE is shared by OMNI mice. A separate pairing-list query must prove the Mini PID.
        if(device.HidInterfaces.Any(IsRogMiniInterface))
            return device with {DisplayName="ROG Harpe Ace Mini",AdapterId=RogWired,Transport=ConnectionTransport.UsbWired,
                MatchReason="G-Helper: ASUS 0B05:1B63 / MI_00；有线型号；仅电量查询，Windows 待读取验证"};
        if(device.HidInterfaces.Any(IsOmniIdentityInterface) && device.HidInterfaces.Any(IsOmniMouseInterface))
            return device with {AdapterId=RogOmni,Transport=ConnectionTransport.Receiver24Ghz,
                MatchReason="ASUS 0B05:1ACE / MI_02 COL01 FF02 + COL03 FF01；须先查询配对型号，接收器存在不证明鼠标在线"};
        if(device.HidInterfaces.Any(IsApex5Interface))
            return device with {AdapterId=Apex5,MatchReason="OpenFlydigi: 37D7:2501 / UsagePage FFA0 / MI_02；须验证设备回复型号后采用电量；Windows 待实机验证"};
        if(device.HidInterfaces.Any(IsWobNativeInterface))
            return device with {AdapterId=WobNative,DisplayName="WOB ZEN65（原生驱动 2.4G）",Transport=ConnectionTransport.Receiver24Ghz,
                MatchReason="普通 ZEN65 官方原生配置：36B0:0001 / MI_01 COL02 / FF87:0020；仅65字节单一报告格式启用，待对应实机验证"};
        if(device.HidInterfaces.Any(i=>IsWobNativeWiredInterface(i)&&device.Properties.TryGetValue("hid:"+i.Path,out var caps)
                &&caps.StartsWith("input=65; output=65;",StringComparison.Ordinal)))
            return device with {AdapterId=WobNative,DisplayName="WOB ZEN65（USB 原生接口）",Transport=ConnectionTransport.UsbWired,
                MatchReason="普通 ZEN65 官方配置 36B0:3025 / FF60:0061 + 实际 IN/OUT 65 字节；拒绝 VIA 33 字节格式"};
        if(device.HidInterfaces.Any(i=>i.VendorId==0x320f && i.ProductId==0x5088)
            && device.DisplayName.Contains("V98pro",StringComparison.OrdinalIgnoreCase))
            return device with {AdapterId="windows.device-properties",Transport=ConnectionTransport.Receiver24Ghz,
                MatchReason="V98pro 2.4G 接收器已识别；尚无经验证的电量协议，仅尝试系统缓存"};
        return device with {AdapterId="windows.device-properties",MatchReason="未匹配专用协议；仅查询 Windows 已有电量缓存，不发送厂商命令"};
    }
    public static bool IsRogMiniInterface(HidInterfaceDescriptor i) => i.VendorId==0x0B05 && i.ProductId==0x1B63
        && i.Path.Contains("mi_00",StringComparison.OrdinalIgnoreCase) && i.UsagePage>=0xff00;
    public static bool IsApex5Interface(HidInterfaceDescriptor i) => i.VendorId==0x37D7 && i.ProductId==0x2501
        && i.UsagePage==0xffa0 && i.Path.Contains("mi_02",StringComparison.OrdinalIgnoreCase);
    public static bool IsOmniIdentityInterface(HidInterfaceDescriptor i) => i.VendorId==0x0B05 && i.ProductId==0x1ACE
        && i.UsagePage==0xff02 && i.Usage==1 && i.Path.Contains("mi_02&col01",StringComparison.OrdinalIgnoreCase);
    public static bool IsOmniMouseInterface(HidInterfaceDescriptor i) => i.VendorId==0x0B05 && i.ProductId==0x1ACE
        && i.UsagePage==0xff01 && i.Usage==1 && i.Path.Contains("mi_02&col03",StringComparison.OrdinalIgnoreCase);
    public static bool IsWobNativeInterface(HidInterfaceDescriptor i) => i.VendorId==0x36b0 && i.ProductId==0x0001
        && i.UsagePage==0xff87 && i.Usage==0x20 && i.Path.Contains("mi_01&col02",StringComparison.OrdinalIgnoreCase);
    public static bool IsWobNativeWiredInterface(HidInterfaceDescriptor i) => i.VendorId==0x36b0 && i.ProductId==0x3025
        && i.UsagePage==0xff60 && i.Usage==0x61 && i.Path.Contains("mi_01",StringComparison.OrdinalIgnoreCase);
}
