using System.Text.Json;
using PeripheralPower.Core;

namespace PeripheralPower.Hardware;

public static class ProbeRunner
{
    public static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true,PropertyNameCaseInsensitive=true};
    /// <summary>Call before WPF startup and single-instance enforcement. Null means normal GUI mode.</summary>
    public static async Task<int?> TryRunAsync(string[] args)
    {
        if(args.Length==0 || args[0] is not ("--probe-discover" or "--probe-read")) return null;
        bool discovery=args[0]=="--probe-discover";
        if(args.Length!=(discovery?2:3)) return 64;
        string output=args[^1];
        try
        {
            var api=new WindowsDeviceDiscovery();
            if(discovery)
            {
                var snapshot=api.Discover();
                // Known, read-only receiver identity lookup. The process timeout still bounds discovery.
                var devices=new List<DeviceDescriptor>();
                foreach(var descriptor in snapshot.Devices) devices.Add(await VendorBatteryQueries.IdentifyOmniAsync(descriptor).ConfigureAwait(false));
                await File.WriteAllTextAsync(output,JsonSerializer.Serialize(snapshot with {Devices=devices},JsonOptions));
            }
            else
            {
                if(new FileInfo(args[1]).Length>1024*1024) return 65;
                var device=JsonSerializer.Deserialize<DeviceDescriptor>(await File.ReadAllTextAsync(args[1]),JsonOptions)
                    ?? throw new InvalidDataException("设备请求为空");
                DeviceReadResult result;
                try {result=await VendorBatteryQueries.TryReadAsync(device) ?? api.ReadSystemCache(device);}
                catch(Exception ex)
                {
                    result=new DeviceReadResult{DeviceId=device.Id,QueryTime=DateTimeOffset.UtcNow,
                        Status=ex is NotSupportedException?ReadStatus.Unsupported:ex is TimeoutException or OperationCanceledException?ReadStatus.Timeout:ReadStatus.Failed,
                        Battery=ex is NotSupportedException?BatteryReading.Unsupported:BatteryReading.Unknown,
                        Transport=device.Transport,Connection=ConnectionState.Unknown,Source="隔离硬件探测",
                        Error=$"{ex.GetType().Name}: {ex.Message}"+(ex.Data["CleanupError"] is string cleanup?$"；清理失败：{cleanup}":"")};
                }
                await File.WriteAllTextAsync(output,JsonSerializer.Serialize(result,JsonOptions));
            }
            return 0;
        }
        catch(Exception ex)
        {
            if(discovery)
            {
                try {await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new DiscoverySnapshot{Errors=[$"{ex.GetType().Name}: {ex.Message}"]},JsonOptions));return 0;}
                catch {return 74;}
            }
            return 70;
        }
    }
}
