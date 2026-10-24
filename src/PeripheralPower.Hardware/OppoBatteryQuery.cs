// Melody framing and battery layout documented by OppoPodsManager (GPL-3.0-or-later).
// Only the read-only 0x0106 battery query is sent. No pairing, settings or channel scanning.
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using PeripheralPower.Core;

namespace PeripheralPower.Hardware;

public static class OppoBatteryQuery
{
    public const string Service = "0000079a-d102-11e1-9b23-00025b00a5a5";
    public static bool Matches(DeviceDescriptor device) => device.Transport == ConnectionTransport.Bluetooth
        && device.DisplayName.Contains("OPPO Enco", StringComparison.OrdinalIgnoreCase)
        && device.InstanceIds.Any(id => id.Contains(Service, StringComparison.OrdinalIgnoreCase));

    internal static DeviceReadResult Read(DeviceDescriptor device)
    {
        var root = device.InstanceIds.FirstOrDefault(id => id.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase));
        if (root is null || root.Length < 24 || !ulong.TryParse(root.AsSpan(12,12), NumberStyles.HexNumber, null, out var address))
            throw new InvalidDataException("缺少可验证的经典蓝牙地址");
        var startup = new byte[512];
        int error = WSAStartup(0x0202, startup);
        if (error != 0) throw new Win32Exception(error);
        IntPtr handle = new(-1);
        try
        {
            handle = socket(32, 1, 3);
            if (handle == new IntPtr(-1)) throw new Win32Exception(WSAGetLastError());
            // SOCKADDR_BTH is packed to one byte by the Windows Bluetooth SDK.
            var endpoint = new Address { Family = 32, Device = address, ServiceId = new Guid(Service) };
            if (connect(handle, ref endpoint, Marshal.SizeOf<Address>()) != 0) throw new Win32Exception(WSAGetLastError(), "OPPO 电量服务连接失败");
            int timeout = 1200;
            if (setsockopt(handle, 0xffff, 0x1006, ref timeout, 4) != 0) throw new Win32Exception(WSAGetLastError());
            if (setsockopt(handle, 0xffff, 0x1005, ref timeout, 4) != 0) throw new Win32Exception(WSAGetLastError());
            byte[] query = [0xaa,7,0,0,6,1,0xf0,0,0];
            for (int offset=0; offset<query.Length;)
            {
                var part=query[offset..]; int sent=send(handle,part,part.Length,0);
                if(sent<=0) throw new Win32Exception(WSAGetLastError());
                offset+=sent;
            }
            var pending = new List<byte>();
            for (int reads=0; reads<16; reads++)
            {
                var buffer=new byte[512]; int count=recv(handle,buffer,buffer.Length,0);
                if(count<=0) throw new IOException($"OPPO 未返回电量（WSA {WSAGetLastError()}）");
                pending.AddRange(buffer.AsSpan(0,count).ToArray());
                while(pending.Count>=2)
                {
                    if(pending[0]!=0xaa || pending[1]<7) {pending.RemoveAt(0);continue;}
                    int length=pending[1]+2;
                    if(pending.Count<length) break;
                    var frame=pending.GetRange(0,length).ToArray();pending.RemoveRange(0,length);
                    var result=Decode(device.Id,frame,DateTimeOffset.UtcNow);
                    if(result is not null) return result;
                }
            }
            throw new TimeoutException("未收到 OPPO 0106 电量查询响应");
        }
        finally {if(handle!=new IntPtr(-1)) closesocket(handle); WSACleanup();}
    }

    public static DeviceReadResult? Decode(string id, ReadOnlySpan<byte> frame, DateTimeOffset now)
    {
        if(frame.Length<10 || frame[0]!=0xaa || frame[1]+2!=frame.Length
            || frame[4]!=6 || frame[5]!=0x81 || (frame[7]|frame[8]<<8)!=frame.Length-9 || frame[9]!=0) return null;
        var data=frame[10..];
        if(data.Length==0) return null;
        BatteryReading left=BatteryReading.Unknown,right=BatteryReading.Unknown,box=BatteryReading.Unknown;
        static BatteryReading Value(int value) => value is >0 and <=100 ? BatteryReading.Percent(value) : BatteryReading.Unknown;
        if(data[0]==4 && data.Length==7)
        {
            if(data[2]>1 || data[4]>1 || data[6]>1) return null;
            left=Value(data[1]);right=Value(data[3]);box=Value(data[5]);
        }
        else
        {
            if(data[0] is <1 or >3 || data.Length!=1+data[0]*2) return null;
            var seen=new HashSet<byte>();
            for(int i=1;i<data.Length;i+=2)
            {
                if(!seen.Add(data[i]) || data[i] is <1 or >3) return null;
                var value=Value(data[i+1]&0x7f);
                switch(data[i]) {case 1:left=value;break;case 2:right=value;break;case 3:box=value;break;}
            }
        }
        var buds=new EarbudBattery{Left=left,Right=right,Case=box};
        if(!buds.IsValid) return null;
        // Main figure is the lowest available ear, never the case or a fabricated second ear.
        var ears=new[]{left,right}.Where(b=>b.IsValid).ToArray();
        return new DeviceReadResult {DeviceId=id,QueryTime=now,Shape=DeviceShape.InEar,Earbuds=buds,
            Battery=ears.Length>0?BatteryReading.Percent(ears.Min(b=>b.Value!.Value)):BatteryReading.Unknown,
            Status=ReadStatus.Success,Transport=ConnectionTransport.Bluetooth,Connection=ConnectionState.Connected,
            BatteryObservedAt=now,BatterySampledAt=now,OnlineEvidenceAt=now,Source="OPPO Melody 0106：左右耳/盒独立读数；主电量为较低耳电量"};
    }
    [StructLayout(LayoutKind.Sequential,Pack=1)] private struct Address {public ushort Family; public ulong Device;public Guid ServiceId;public uint Port;}
    [DllImport("ws2_32.dll")] private static extern int WSAStartup(ushort version,byte[] data);
    [DllImport("ws2_32.dll")] private static extern int WSACleanup();
    [DllImport("ws2_32.dll")] private static extern int WSAGetLastError();
    [DllImport("ws2_32.dll")] private static extern IntPtr socket(int family,int type,int protocol);
    [DllImport("ws2_32.dll")] private static extern int connect(IntPtr socket,ref Address address,int size);
    [DllImport("ws2_32.dll")] private static extern int setsockopt(IntPtr socket,int level,int option,ref int value,int size);
    [DllImport("ws2_32.dll")] private static extern int send(IntPtr socket,byte[] buffer,int length,int flags);
    [DllImport("ws2_32.dll")] private static extern int recv(IntPtr socket,byte[] buffer,int length,int flags);
    [DllImport("ws2_32.dll")] private static extern int closesocket(IntPtr socket);
}
