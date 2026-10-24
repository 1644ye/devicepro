using System.ComponentModel;
using System.Runtime.InteropServices;
using PeripheralPower.Core;
using static PeripheralPower.Hardware.NativeMethods;

namespace PeripheralPower.Hardware;

// Protocol facts verified against the locally installed ATK HUB 3.2.28 EVISION controller.
// Independent implementation; only the battery command is sent, never configuration writes.
public static class VgnBatteryQuery
{
    public const string Adapter = "vgn.v98pro.evision";
    public static bool IsInterface(HidInterfaceDescriptor i) => i.VendorId==0x320f && i.ProductId==0x5088
        && i.UsagePage==0xff1c && i.Usage==0x92;
    public static bool Matches(DeviceDescriptor d) => d.DisplayName.Contains("V98pro",StringComparison.OrdinalIgnoreCase)
        && d.HidInterfaces.Any(IsInterface);
    public static byte[] CreateQuery(ushort sequence)
    {
        if(sequence==0) throw new ArgumentOutOfRangeException(nameof(sequence));
        var packet=new byte[64];
        packet[0]=4;packet[1]=(byte)sequence;packet[2]=(byte)(sequence>>8);packet[3]=0x1a;
        return packet;
    }
    internal static async Task<DeviceReadResult> ReadAsync(DeviceDescriptor device)
    {
        if(!Matches(device)) throw new NotSupportedException("不是已验证的 V98pro 接口");
        var iface=device.HidInterfaces.First(IsInterface);
        using var handle=CreateFileW(iface.Path,0xc0000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
        if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var attributes=new Attributes{Size=Marshal.SizeOf<Attributes>()};
        if(!HidD_GetAttributes(handle,ref attributes)||attributes.VendorId!=iface.VendorId||attributes.ProductId!=iface.ProductId)
            throw new IOException("VGN 会话设备标识已变化");
        if(!HidD_GetPreparsedData(handle,out var pp)) throw new IOException("无法读取 VGN HID 能力");
        Caps caps;
        try {if(HidP_GetCaps(pp,out caps)<0) throw new IOException("无效 HID 能力");}finally{HidD_FreePreparsedData(pp);}
        if(caps.UsagePage!=0xff1c || caps.Usage!=0x92 || caps.InputLength!=64 || caps.OutputLength!=64)
            throw new NotSupportedException("VGN 报告布局不符，未发送命令");
        using var stream=new FileStream(handle,FileAccess.ReadWrite,128,true);
        using var limit=new CancellationTokenSource(TimeSpan.FromMilliseconds(1800));
        if(!HidD_FlushQueue(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        ushort sequence=(ushort)Random.Shared.Next(1,65536);
        await stream.WriteAsync(CreateQuery(sequence),limit.Token).ConfigureAwait(false);
        for(int i=0;i<32;i++)
        {
            var buffer=new byte[64];int count=await stream.ReadAsync(buffer,limit.Token).ConfigureAwait(false);
            if(count==0) throw new IOException("VGN 接口已关闭");
            var result=Decode(device.Id,buffer.AsSpan(0,count),sequence,DateTimeOffset.UtcNow);
            if(result is not null) return result;
        }
        throw new TimeoutException("VGN 电量查询没有匹配响应");
    }
    public static DeviceReadResult? Decode(string id,ReadOnlySpan<byte> packet,ushort sequence,DateTimeOffset now)
    {
        if(packet.Length!=64 || packet[0]!=4 || (packet[1]|packet[2]<<8)!=sequence) return null;
        if(packet[3]==0xff) throw new IOException("VGN 接收器返回查询失败；未确认键盘在线");
        if(packet[3]!=0x1a) return null;
        if(packet[8]>100 || packet[9]>2) throw new InvalidDataException("VGN 电量/充电编码超出已知范围");
        return new DeviceReadResult{DeviceId=id,Shape=DeviceShape.Keyboard,QueryTime=now,
            Battery=BatteryReading.Percent(packet[8]),Charging=packet[9]==2?ChargingState.Charging:ChargingState.NotCharging,
            Connection=ConnectionState.Connected,Transport=ConnectionTransport.Receiver24Ghz,Status=ReadStatus.Success,
            BatterySampledAt=now,BatteryObservedAt=now,OnlineEvidenceAt=now,Source="VGN V98pro：ATK HUB EVISION 04/1A 实时电量查询"};
    }
}
