using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using PeripheralPower.Core;

namespace PeripheralPower.Hardware;

// Read the existing BluetoothDevice/LEDevice.ConnectionStatus through the WinRT ABI.
// No GATT service requests, MaintainConnection, pairing or reconnect commands.
public static class BluetoothConnectionQuery
{
    public static string? Address(DeviceDescriptor device)
    {
        foreach(var id in device.InstanceIds)
        {
            var prefix=id.StartsWith("BTHENUM\\DEV_",StringComparison.OrdinalIgnoreCase)?12:
                id.StartsWith("BTHLE\\DEV_",StringComparison.OrdinalIgnoreCase)?10:0;
            if(prefix==0 || id.Length<prefix+12)continue;
            var hex=id.Substring(prefix,12);
            if(hex.All(Uri.IsHexDigit))return hex.ToUpperInvariant();
        }
        return null;
    }
    // Called on a worker thread inside the bounded probe process.
    public static ConnectionState Read(DeviceDescriptor device)
    {
        var address=Address(device);
        if(device.Transport!=ConnectionTransport.Bluetooth || address is null)return ConnectionState.Unknown;
        int initialized=RoInitialize(1); // MTA
        if(initialized<0)return ConnectionState.Unknown;
        IntPtr name=IntPtr.Zero,factory=IntPtr.Zero,operation=IntPtr.Zero,info=IntPtr.Zero,result=IntPtr.Zero,instance=IntPtr.Zero;
        try
        {
            bool le=device.InstanceIds.Any(id=>id.StartsWith("BTHLE\\DEV_",StringComparison.OrdinalIgnoreCase));
            string runtime=le?"Windows.Devices.Bluetooth.BluetoothLEDevice":"Windows.Devices.Bluetooth.BluetoothDevice";
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtime,runtime.Length,out name));
            var iid=new Guid(le?"c8cf1a19-f0b6-4bf0-8689-41303de2d9f4":"0991df51-57db-4725-bbd7-84f64327ec2c");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name,ref iid,out factory));
            Marshal.ThrowExceptionForHR(Method<FromAddress>(factory,le?7:8)(factory,ulong.Parse(address,NumberStyles.HexNumber),out operation));
            iid=new Guid("00000036-0000-0000-c000-000000000046"); // IAsyncInfo
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(operation,in iid,out info));
            var watch=Stopwatch.StartNew();
            while(true)
            {
                Marshal.ThrowExceptionForHR(Method<GetInt>(info,7)(info,out int status));
                if(status==1)break;
                if(status!=0)return ConnectionState.Unknown;
                if(watch.ElapsedMilliseconds>1000){Method<Invoke>(info,9)(info);return ConnectionState.Unknown;}
                Thread.Sleep(10);
            }
            Marshal.ThrowExceptionForHR(Method<GetPointer>(operation,8)(operation,out result));
            if(result==IntPtr.Zero)return ConnectionState.Unknown;
            iid=new Guid(le?"b5ee2f7b-4ad8-4642-ac48-80a0b500e887":"2335b156-90d2-4a04-aef5-0e20b9e6b707");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(result,in iid,out instance));
            Marshal.ThrowExceptionForHR(Method<GetInt>(instance,le?9:12)(instance,out int connection));
            return connection switch {0=>ConnectionState.Disconnected,1=>ConnectionState.Connected,_=>ConnectionState.Unknown};
        }
        catch(COMException){return ConnectionState.Unknown;}
        finally
        {
            if(result!=IntPtr.Zero)
            {
                var closable=new Guid("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
                if(Marshal.QueryInterface(result,in closable,out var close)>=0)
                {try{Method<Invoke>(close,6)(close);}finally{Marshal.Release(close);}}
            }
            if(instance!=IntPtr.Zero)Marshal.Release(instance);
            if(result!=IntPtr.Zero)Marshal.Release(result);
            if(info!=IntPtr.Zero)Marshal.Release(info);
            if(operation!=IntPtr.Zero)Marshal.Release(operation);
            if(factory!=IntPtr.Zero)Marshal.Release(factory);
            if(name!=IntPtr.Zero)WindowsDeleteString(name);
            RoUninitialize();
        }
    }
    private static T Method<T>(IntPtr obj,int slot) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj),slot*IntPtr.Size));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FromAddress(IntPtr self,ulong address,out IntPtr operation);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInt(IntPtr self,out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPointer(IntPtr self,out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Invoke(IntPtr self);
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll",CharSet=CharSet.Unicode)] private static extern int WindowsCreateString(string value,int length,out IntPtr handle);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr handle);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name,ref Guid iid,out IntPtr factory);
}
