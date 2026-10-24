// Protocol references and upstream licenses are recorded in docs/HARDWARE.md.
// ASUS protocol derived from G-Helper (GPL-3.0); Flydigi framing from OpenFlydigi (MIT).
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PeripheralPower.Core;
using static PeripheralPower.Hardware.NativeMethods;

namespace PeripheralPower.Hardware;

public static class VendorBatteryQueries
{
    internal static async Task<DeviceReadResult?> TryReadAsync(DeviceDescriptor device)
    {
        if(device.HidInterfaces.Any(ProtocolCatalog.IsWobNativeInterface)||device.HidInterfaces.Any(ProtocolCatalog.IsWobNativeWiredInterface))
            return await WobNativeBatteryQuery.ReadAsync(device).ConfigureAwait(false);
        if(device.HidInterfaces.Any(ProtocolCatalog.IsOmniIdentityInterface) && device.HidInterfaces.Any(ProtocolCatalog.IsOmniMouseInterface))
            return await ReadOmniAsync(device).ConfigureAwait(false);
        var iface=device.HidInterfaces.FirstOrDefault(ProtocolCatalog.IsRogMiniInterface)
            ?? device.HidInterfaces.FirstOrDefault(ProtocolCatalog.IsApex5Interface);
        if(iface is null) return null;
        bool rog=ProtocolCatalog.IsRogMiniInterface(iface);
        var now=DateTimeOffset.UtcNow;
        using var handle=CreateFileW(iface.Path,0xC0000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
        if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(),"设备共享读写访问失败；可能被驱动或官方软件占用");
        var attrs=new Attributes{Size=Marshal.SizeOf<Attributes>()};
        if(!HidD_GetAttributes(handle,ref attrs) || attrs.VendorId!=iface.VendorId || attrs.ProductId!=iface.ProductId)
            throw new IOException("设备会话标识已改变，丢弃旧接口");
        if(!HidD_GetPreparsedData(handle,out var pp)) throw new IOException("无法读取 HID 报告能力");
        Caps caps;
        try {if(HidP_GetCaps(pp,out caps)<0) throw new IOException("无效的 HID 能力描述");}
        finally {HidD_FreePreparsedData(pp);}
        if(caps.UsagePage!=iface.UsagePage || caps.Usage!=iface.Usage) throw new IOException("HID Usage 已变化");
        // The exact upstream formats are known. Refuse any changed layout instead of guessing report padding.
        if(caps.InputLength<(rog?11:30) || caps.InputLength>256 || caps.OutputLength!=(rog?64:32))
            return new DeviceReadResult{DeviceId=device.Id,QueryTime=now,Transport=device.Transport,Status=ReadStatus.Unsupported,
                Battery=BatteryReading.Unsupported,Source="厂商状态查询",Error=$"报告长度不符合已知协议（IN={caps.InputLength}, OUT={caps.OutputLength}），未发送命令"};
        var command=new byte[caps.OutputLength];
        if(rog) {command[1]=0x12;command[2]=0x07;}
        else {command[0]=0x03;command[1]=0x5A;command[2]=0xA5;command[3]=0x01;command[4]=0x02;command[5]=0x03;}
        using var stream=new FileStream(handle,FileAccess.ReadWrite,256,true);
        using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(1400));
        // Clear this handle's pending input once, then require the exact status-response header.
        // No repeated drain loop or permanent input listener is created.
        if(!HidD_FlushQueue(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(),"无法清空本次查询的旧 HID 输入队列");
        await stream.WriteAsync(command,deadline.Token).ConfigureAwait(false);
        var watch=Stopwatch.StartNew();
        for(int reads=0;reads<2048 && watch.ElapsedMilliseconds<1400;reads++)
        {
            var reply=new byte[caps.InputLength];
            int count=await stream.ReadAsync(reply,deadline.Token).ConfigureAwait(false);
            if(count==0) throw new IOException("设备在查询期间移除");
            var result=rog?DecodeRogReply(device.Id,reply.AsSpan(0,count),DateTimeOffset.UtcNow)
                :DecodeApex5Reply(device.Id,reply.AsSpan(0,count),DateTimeOffset.UtcNow);
            if(result is not null) return result;
        }
        throw new TimeoutException("状态查询未收到匹配回复；接收器存在不能证明本体在线");
    }

    public static DeviceReadResult? DecodeRogReply(string id,ReadOnlySpan<byte> bytes,DateTimeOffset observed)
    {
        if(bytes.Length<11 || bytes[0]!=0 || bytes[1]!=0x12 || bytes[2]!=0x07) return null;
        if(bytes[5]>100) throw new InvalidDataException("ROG 电量超出 0～100");
        return new DeviceReadResult {DeviceId=id,QueryTime=observed,Battery=BatteryReading.Percent(bytes[5]),
            Connection=ConnectionState.Connected,Transport=ConnectionTransport.UsbWired,
            Charging=bytes[10]>0?ChargingState.Charging:ChargingState.NotCharging,
            BatterySampledAt=observed,BatteryObservedAt=observed,OnlineEvidenceAt=observed,Status=ReadStatus.Success,
            Source="ROG HID 12/07 状态查询（有线；移植待实机验证）"};
    }

    public static DeviceReadResult? DecodeApex5Reply(string id,ReadOnlySpan<byte> bytes,DateTimeOffset observed)
    {
        if(bytes.Length<30 || bytes[0]!=4 || bytes[1]!=0x5a || bytes[2]!=0xa5 || bytes[3]!=1) return null;
        // Only the single-frame layout and base/EVA Apex 5 SKUs with upstream hardware evidence are accepted.
        if(bytes[4]!=1 || bytes[5]!=0 || bytes[6] is not (128 or 129))
            throw new InvalidDataException("Flydigi 回复的帧格式或型号不在已验证 Apex 5 范围");
        int level=bytes[12]&15; int chargingNibble=bytes[12]>>4;
        if(level>5 || chargingNibble>1) throw new InvalidDataException("Flydigi 电量或充电编码未知");
        // Upstream uses 6 as a UI charging sentinel. It is never interpreted as a charge level here.
        return new DeviceReadResult {DeviceId=id,QueryTime=observed,Battery=BatteryReading.Level(level,5),
            Connection=ConnectionState.Connected,
            Transport=bytes[7]==1?ConnectionTransport.UsbWired:bytes[7]==2?ConnectionTransport.Receiver24Ghz:ConnectionTransport.Unknown,
            Charging=chargingNibble==1?ChargingState.Charging:ChargingState.NotCharging,
            BatterySampledAt=observed,BatteryObservedAt=observed,OnlineEvidenceAt=observed,Status=ReadStatus.Success,
            Source="Flydigi command 1 状态查询（0～5 档；Windows 移植待实机验证）"};
    }

    internal static async Task<DeviceDescriptor> IdentifyOmniAsync(DeviceDescriptor device)
    {
        if(device.AdapterId!=ProtocolCatalog.RogOmni) return device;
        try
        {
            var reply=await ExchangeOmniAsync(device.HidInterfaces.First(ProtocolCatalog.IsOmniIdentityInterface),[1,0xa0,0,0]).ConfigureAwait(false);
            bool mini=ContainsOmniMini(reply);
            var properties=new Dictionary<string,string>(device.Properties){["rog.omni.identity"] = mini?"配对列表含 Harpe Ace Mini PID 1B65（不表示本体在线）":"配对列表未证实 Harpe Ace Mini"};
            return device with {DisplayName=mini?"ROG Harpe Ace Mini（OMNI）":device.DisplayName,Properties=properties,
                MatchReason=mini?"只读 OMNI 01/A0 配对列表确认鼠标 PID 1B65；在线状态仍需电量回复":device.MatchReason};
        }
        catch(Exception ex)
        {
            var properties=new Dictionary<string,string>(device.Properties){["rog.omni.identity.error"]=$"{ex.GetType().Name}: {ex.Message}"};
            return device with {Properties=properties};
        }
    }

    public static bool ContainsOmniMini(ReadOnlySpan<byte> reply)
    {
        if(reply.Length!=64 || reply[0]!=1 || reply[1]!=0xa0) throw new InvalidDataException("OMNI 配对列表回复头无效");
        for(int offset=5;offset+3<reply.Length;offset+=4)
        {
            int pid=reply[offset]|reply[offset+1]<<8;
            if(pid==0) break;
            if(pid==0x1b65) return true;
        }
        return false;
    }

    private static async Task<DeviceReadResult> ReadOmniAsync(DeviceDescriptor device)
    {
        var pairing=await ExchangeOmniAsync(device.HidInterfaces.First(ProtocolCatalog.IsOmniIdentityInterface),[1,0xa0,0,0]).ConfigureAwait(false);
        if(!ContainsOmniMini(pairing)) return new DeviceReadResult{DeviceId=device.Id,Status=ReadStatus.Unsupported,Transport=ConnectionTransport.Receiver24Ghz,
            Battery=BatteryReading.Unsupported,Source="OMNI 配对型号查询",Error="接收器配对列表未确认 Harpe Ace Mini (1B65)，未发送鼠标电量查询"};
        var battery=await ExchangeOmniAsync(device.HidInterfaces.First(ProtocolCatalog.IsOmniMouseInterface),[3,0x12,7]).ConfigureAwait(false);
        return DecodeOmniMiniReply(device.Id,battery,DateTimeOffset.UtcNow)
            ?? throw new InvalidDataException("OMNI 电量回复无效");
    }

    public static DeviceReadResult? DecodeOmniMiniReply(string id,ReadOnlySpan<byte> bytes,DateTimeOffset observed)
    {
        if(bytes.Length<11 || bytes[0]!=3 || bytes[1]!=0x12 || bytes[2]!=7) return null;
        if(bytes[5]>100) throw new InvalidDataException("OMNI 电量超出 0～100");
        // G-Helper treats the all-zero, non-charging status as sleeping/offline, not a valid zero percent.
        if(bytes[5]==0 && bytes[10]==0) return new DeviceReadResult{DeviceId=id,QueryTime=observed,Transport=ConnectionTransport.Receiver24Ghz,
            Status=ReadStatus.Failed,Connection=ConnectionState.Unknown,Source="ROG OMNI HID 03/12/07 状态查询",
            Error="接收器未返回有效本体电量，鼠标可能休眠或关机；保留历史值"};
        return new DeviceReadResult{DeviceId=id,QueryTime=observed,Transport=ConnectionTransport.Receiver24Ghz,
            Connection=ConnectionState.Connected,Status=ReadStatus.Success,Battery=BatteryReading.Percent(bytes[5]),
            Charging=bytes[10]>0?ChargingState.Charging:ChargingState.NotCharging,BatteryObservedAt=observed,BatterySampledAt=observed,
            OnlineEvidenceAt=observed,Source="ROG Harpe Ace Mini：OMNI 配对 PID 1B65 + HID 03/12/07 状态查询"};
    }

    private static async Task<byte[]> ExchangeOmniAsync(HidInterfaceDescriptor iface,byte[] prefix)
    {
        if(!ProtocolCatalog.IsOmniIdentityInterface(iface) && !ProtocolCatalog.IsOmniMouseInterface(iface)) throw new InvalidDataException("非 OMNI 协议接口");
        using var handle=CreateFileW(iface.Path,0xc0000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
        if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(),"OMNI 共享访问失败");
        var attributes=new Attributes{Size=Marshal.SizeOf<Attributes>()};
        if(!HidD_GetAttributes(handle,ref attributes) || attributes.VendorId!=0x0b05 || attributes.ProductId!=0x1ace) throw new IOException("OMNI 会话身份已变化");
        if(!HidD_GetPreparsedData(handle,out var pp)) throw new IOException("无法读取 OMNI HID 能力");
        Caps caps;
        try{if(HidP_GetCaps(pp,out caps)<0)throw new IOException("OMNI HID 能力无效");}finally{HidD_FreePreparsedData(pp);}
        if(caps.InputLength!=64 || caps.OutputLength!=64 || caps.UsagePage!=iface.UsagePage || caps.Usage!=1) throw new InvalidDataException("OMNI 报告能力不符，未发送命令");
        using var stream=new FileStream(handle,FileAccess.ReadWrite,128,true);
        using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(1400));
        if(!HidD_FlushQueue(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(),"OMNI 输入队列清理失败");
        var packet=new byte[64];prefix.CopyTo(packet,0);
        await stream.WriteAsync(packet,deadline.Token).ConfigureAwait(false);
        for(int count=0;count<16;count++)
        {
            var reply=new byte[64];int length=await stream.ReadAsync(reply,deadline.Token).ConfigureAwait(false);
            if(length==64 && reply[0]==prefix[0] && reply[1]==prefix[1] && (prefix[1]!=0x12 || reply[2]==prefix[2])) return reply;
        }
        throw new TimeoutException("OMNI 无匹配状态回复");
    }
}
