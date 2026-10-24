using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PeripheralPower.Hardware;

internal static class NativeMethods
{
    internal const uint AllClasses = 4, Present = 2, DeviceInterface = 16;
    internal static readonly IntPtr InvalidHandle = new(-1);
    [StructLayout(LayoutKind.Sequential)] internal struct DevInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; public static DevInfo Create() => new() { Size = (uint)Marshal.SizeOf<DevInfo>() }; }
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceInfo { public uint Size; public Guid ClassGuid; public uint Flags; public UIntPtr Reserved; public static InterfaceInfo Create() => new() { Size = (uint)Marshal.SizeOf<InterfaceInfo>() }; }
    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [StructLayout(LayoutKind.Sequential)] internal struct Attributes { public int Size; public ushort VendorId, ProductId, Version; }
    [StructLayout(LayoutKind.Sequential)] internal struct Caps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
        public ushort LinkCollectionNodes, InputButtonCaps, InputValueCaps, InputDataIndices, OutputButtonCaps, OutputValueCaps, OutputDataIndices, FeatureButtonCaps, FeatureValueCaps, FeatureDataIndices;
    }
    internal static readonly PropertyKey ContainerId = new(new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    internal static readonly PropertyKey BusType = new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 21);
    internal static readonly PropertyKey BatteryLife = new(new("49CD1F76-5626-4B17-A4E8-18B4AA1A2213"), 10);
    internal static readonly PropertyKey BusReportedDescription = new(new("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), 4);
    // Windows' Bluetooth provider cache: not a guaranteed public SDK contract; strict BYTE validation.
    internal static readonly PropertyKey BluetoothBatteryCache = new(new("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DevInfo info);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DevInfo info, StringBuilder id, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DevInfo info, uint property, out uint type, byte[] data, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceInfo iface);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceInfo iface, IntPtr detail, uint size, out uint required, ref DevInfo info);
    [DllImport("setupapi.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] internal static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref PropertyKey key, out uint type, byte[] data, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] internal static extern uint CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_Parent(out uint parent, uint child, uint flags);
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] internal static extern uint CM_Get_Device_IDW(uint devInst, StringBuilder buffer, uint size, uint flags);
    [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")] internal static extern int HidP_GetValueCaps(int type, [Out] byte[] caps, ref ushort count, IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetButtonCaps(int type, [Out] byte[] caps, ref ushort count, IntPtr data);
    [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, uint size);
    [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] buffer, uint size);
    [DllImport("hid.dll")] [return:MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FlushQueue(SafeFileHandle handle);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    internal static byte[]? Property(uint devInst, PropertyKey key, out uint type)
    {
        var buffer = new byte[4096]; uint size = (uint)buffer.Length;
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out type, buffer, ref size, 0) != 0 || size > buffer.Length) return null;
        return buffer.AsSpan(0, (int)size).ToArray();
    }
    internal static string InstanceId(uint devInst) { var b = new StringBuilder(1024); return CM_Get_Device_IDW(devInst, b, 1024, 0) == 0 ? b.ToString() : ""; }
    internal static string RegistryString(IntPtr set, ref DevInfo info, uint property)
    {
        var buffer = new byte[8192];
        return SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, buffer, (uint)buffer.Length, out uint size)
            ? Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, buffer.Length)).TrimEnd('\0') : "";
    }
}
