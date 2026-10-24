using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using PeripheralPower.Core;
using static PeripheralPower.Hardware.NativeMethods;

namespace PeripheralPower.Hardware;

/// <summary>Ordinary ZEN65 native-driver branch only. Sources: docs/ZEN65-LOCAL.md. No VIA/RT fallback.</summary>
public static class WobNativeBatteryQuery
{
    public static byte[] BuildRequest(byte command)
    {
        if(command is not (0x10 or 0x11 or 0x30 or 0x55)) throw new ArgumentOutOfRangeException(nameof(command));
        var frame=new byte[64];frame[0]=0xaa;frame[1]=command;frame[4]=command==0x30?(byte)10:(byte)0;
        int checksum=frame[1]+frame[4];frame[5]=(byte)checksum;frame[6]=(byte)(checksum>>8);
        return frame;
    }

    public static bool IsSuccessfulReply(ReadOnlySpan<byte> frame,byte command) =>
        frame.Length==64 && frame[0]==0xaa && frame[1]==command && frame[7]==0x55;

    public static DeviceReadResult DecodePower(string id,ReadOnlySpan<byte> frame,bool linked,DateTimeOffset observed)
    {
        if(!IsSuccessfulReply(frame,0x30)) throw new InvalidDataException("ZEN65 原生电量回复头、长度或状态无效");
        if(!linked) return new DeviceReadResult{DeviceId=id,QueryTime=observed,Status=ReadStatus.Success,
            Connection=ConnectionState.Disconnected,Transport=ConnectionTransport.Receiver24Ghz,Source="ZEN65 原生 AA55 链路标志",
            Error="接收器报告本体链路未建立，未采用电量字段"};
        if(frame[8]>100) throw new InvalidDataException("ZEN65 原生电量超出 0～100");
        return new DeviceReadResult{DeviceId=id,QueryTime=observed,Status=ReadStatus.Success,Connection=ConnectionState.Connected,
            Transport=ConnectionTransport.Receiver24Ghz,Battery=BatteryReading.Percent(frame[8]),
            // The official UI maps this byte to charging, but the firmware stores a low-battery flag
            // at the copied RAM address. Do not promote a conflicting field meaning to product truth.
            Charging=ChargingState.Unknown,
            BatterySampledAt=observed,BatteryObservedAt=observed,OnlineEvidenceAt=observed,
            Source=$"普通 ZEN65 原生 AA30 电量查询；状态字节 0x{frame[9]:X2}，充电语义未确认",
            Error=""};
    }

    internal static async Task<DeviceReadResult> ReadAsync(DeviceDescriptor device)
    {
        var iface=device.HidInterfaces.FirstOrDefault(ProtocolCatalog.IsWobNativeInterface)
            ?? device.HidInterfaces.First(ProtocolCatalog.IsWobNativeWiredInterface);
        bool wired=ProtocolCatalog.IsWobNativeWiredInterface(iface);
        using var handle=CreateFileW(iface.Path,0xc0000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
        if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(),"ZEN65 原生接口共享访问失败");
        var attributes=new Attributes{Size=Marshal.SizeOf<Attributes>()};
        if(!HidD_GetAttributes(handle,ref attributes)||attributes.VendorId!=0x36b0||attributes.ProductId!=(wired?0x3025:1))
            throw new IOException("ZEN65 原生设备身份已变化，未发送查询");
        if(!HidD_GetPreparsedData(handle,out var pp)) throw new IOException("无法取得 HID 报告能力，未发送查询");
        byte reportId;
        try
        {
            if(HidP_GetCaps(pp,out var caps)<0||caps.UsagePage!=iface.UsagePage||caps.Usage!=iface.Usage||caps.InputLength!=65||caps.OutputLength!=65)
                throw new NotSupportedException("仅支持已确认 ZEN65 原生接口（无线 FF87:0020 / 有线 FF60:0061）和 IN/OUT 65 字节；未向其他格式发送查询");
            var inputIds=ReportIds(pp,0,caps.InputValueCaps,caps.InputButtonCaps);
            var outputIds=ReportIds(pp,1,caps.OutputValueCaps,caps.OutputButtonCaps);
            if(inputIds.Count!=1||outputIds.Count!=1||inputIds.Single()!=outputIds.Single())
                throw new NotSupportedException("HID 报告 ID 未唯一匹配，未猜测报告前缀");
            reportId=inputIds.Single();
        }
        finally{HidD_FreePreparsedData(pp);}
        using var stream=new FileStream(handle,FileAccess.ReadWrite,128,true);
        async Task<byte[]> Exchange(byte command)
        {
            using var deadline=new CancellationTokenSource(750);
            if(!HidD_FlushQueue(handle)) throw new IOException("ZEN65 查询前旧输入队列清理失败");
            var packet=new byte[65];packet[0]=reportId;BuildRequest(command).CopyTo(packet,1);
            await stream.WriteAsync(packet,deadline.Token).ConfigureAwait(false);
            for(int reads=0;reads<8;reads++)
            {
                var response=new byte[65];int count=await stream.ReadAsync(response,deadline.Token).ConfigureAwait(false);
                if(count!=65||response[0]!=reportId||response[1]!=0xaa||response[2]!=command)continue;
                var frame=response.AsSpan(1).ToArray();
                if(!IsSuccessfulReply(frame,command)) throw new IOException($"ZEN65 原生命令 {command:X2} 被拒绝，状态 {frame[7]:X2}");
                return frame;
            }
            throw new TimeoutException("ZEN65 无匹配状态回复");
        }
        // AA55 uses the receiver channel directly, outside the native data transaction.
        var link=wired?null:await Exchange(0x55).ConfigureAwait(false);
        if(link is not null&&link[8]==0) return new DeviceReadResult{DeviceId=device.Id,Status=ReadStatus.Success,Connection=ConnectionState.Disconnected,
            Transport=ConnectionTransport.Receiver24Ghz,Source="ZEN65 原生 AA55 链路标志",Error="接收器报告本体链路未建立"};
        byte[] power=[];
        Exception? originalError=null,cleanupError=null;
        bool startAttempted=false;
        try
        {
            // A lost start ACK does not prove the device rejected AA10. Always attempt bounded AA11.
            startAttempted=true;
            await Exchange(0x10).ConfigureAwait(false);
            power=await Exchange(0x30).ConfigureAwait(false);
        }
        catch(Exception ex){originalError=ex;}
        finally
        {
            if(startAttempted)
                try{await Exchange(0x11).ConfigureAwait(false);}catch(Exception ex){cleanupError=ex;}
        }
        if(originalError is not null)
        {
            if(cleanupError is not null)originalError.Data["CleanupError"]=$"AA11: {cleanupError.GetType().Name}: {cleanupError.Message}";
            ExceptionDispatchInfo.Capture(originalError).Throw();
        }
        if(cleanupError is not null)throw new IOException($"ZEN65 AA11 会话清理失败：{cleanupError.Message}",cleanupError);
        var result=DecodePower(device.Id,power,true,DateTimeOffset.UtcNow);
        return wired?result with {Transport=ConnectionTransport.UsbWired,Source=$"普通 ZEN65 USB 原生 AA10/AA30/AA11 只读状态查询；状态字节 0x{power[9]:X2}，充电未知"}:result;
    }

    private static HashSet<byte> ReportIds(IntPtr pp,int type,ushort valueCount,ushort buttonCount)
    {
        var ids=new HashSet<byte>();
        if(valueCount>0&&valueCount<=256)
        {
            var data=new byte[valueCount*72];var count=valueCount;
            if(HidP_GetValueCaps(type,data,ref count,pp)>=0)for(int i=0;i<count;i++)ids.Add(data[i*72+2]);
        }
        if(buttonCount>0&&buttonCount<=256)
        {
            var data=new byte[buttonCount*72];var count=buttonCount;
            if(HidP_GetButtonCaps(type,data,ref count,pp)>=0)for(int i=0;i<count;i++)ids.Add(data[i*72+2]);
        }
        return ids;
    }
}
