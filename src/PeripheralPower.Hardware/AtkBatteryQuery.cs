using System.ComponentModel;
using System.Runtime.InteropServices;
using PeripheralPower.Core;
using static PeripheralPower.Hardware.NativeMethods;

namespace PeripheralPower.Hardware;

// ATK HUB 3.2.28 HOLTEK: feature report 0, GetConfigData 0x82.
// Do not reuse Nordic 08/04: this receiver has a different chipset and report layout.
public static class AtkBatteryQuery
{
    public const string Adapter="atk.holtek.battery";
    public static bool IsInterface(HidInterfaceDescriptor i) => i.VendorId==0x373b
        && i.ProductId is 0x1178 or 0x112d && i.UsagePage==0xff10 && i.Usage==1;
    public static byte[] CreateQuery() => [0,0x82,0,0,0,0,0,0,0];
    internal static async Task<DeviceReadResult> ReadAsync(DeviceDescriptor device)
    {
        var iface=device.HidInterfaces.First(IsInterface);
        using var handle=CreateFileW(iface.Path,0xc0000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
        if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var attr=new Attributes{Size=Marshal.SizeOf<Attributes>()};
        if(!HidD_GetAttributes(handle,ref attr)||attr.VendorId!=iface.VendorId||attr.ProductId!=iface.ProductId)
            throw new IOException("ATK 设备身份已改变");
        if(!HidD_GetPreparsedData(handle,out var pp))throw new IOException("无法读取 ATK 能力");
        Caps caps;
        try{if(HidP_GetCaps(pp,out caps)<0)throw new IOException("无效 ATK HID 能力");}finally{HidD_FreePreparsedData(pp);}
        if(caps.UsagePage!=0xff10||caps.Usage!=1||caps.InputLength!=65||caps.FeatureLength!=9)
            throw new NotSupportedException("ATK 报告布局未验证，未发送命令");
        using var stream=new FileStream(handle,FileAccess.ReadWrite,130,true);
        using var limit=new CancellationTokenSource(TimeSpan.FromMilliseconds(1800));
        if(!HidD_FlushQueue(handle))throw new Win32Exception(Marshal.GetLastWin32Error());
        var query=CreateQuery();
        if(!HidD_SetFeature(handle,query,query.Length))throw new Win32Exception(Marshal.GetLastWin32Error(),"ATK 状态查询失败");
        for(int i=0;i<32;i++)
        {
            var packet=new byte[65];int length=await stream.ReadAsync(packet,limit.Token).ConfigureAwait(false);
            if(length==0)throw new IOException("ATK 接口已断开");
            var result=Decode(device.Id,packet.AsSpan(0,length),iface.ProductId==0x1178,DateTimeOffset.UtcNow);
            if(result is not null)return result;
        }
        throw new TimeoutException("未收到 ATK 配置状态回复");
    }
    public static DeviceReadResult? Decode(string id,ReadOnlySpan<byte> packet,bool receiver,DateTimeOffset now)
    {
        if(packet.Length!=65||packet[0]!=0||packet[1]!=0x82)return null;
        var result=new DeviceReadResult{DeviceId=id,Shape=DeviceShape.Mouse,QueryTime=now,
            Transport=receiver?ConnectionTransport.Receiver24Ghz:ConnectionTransport.UsbWired,
            Source="ATK HUB HOLTEK 00/82 实时电量与链路状态"};
        if(receiver && packet[10]==0)return result with {Connection=ConnectionState.Disconnected,Status=ReadStatus.Success,
            Error="接收器仍连接，鼠标无线链路已断开。"};
        int battery=packet[9]&0x7f;
        if(battery>100)throw new InvalidDataException("ATK 电量超出 0～100");
        return result with {Battery=BatteryReading.Percent(battery),Charging=(packet[9]&0x80)!=0?ChargingState.Charging:ChargingState.NotCharging,
            Connection=ConnectionState.Connected,Status=ReadStatus.Success,BatterySampledAt=now,BatteryObservedAt=now,OnlineEvidenceAt=now};
    }
}
