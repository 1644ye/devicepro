using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using PeripheralPower.Core;
using static PeripheralPower.Hardware.NativeMethods;

namespace PeripheralPower.Hardware;

/// <summary>Only call inside the disposable probe process. Never run these synchronous driver calls on UI threads.</summary>
public sealed class WindowsDeviceDiscovery
{
    private sealed record Node(string Id, uint DevInst, string Container, string Name, string Class, string Parent, string Bus, string HardwareIds,string Product,string Manufacturer);

    public DiscoverySnapshot Discover()
    {
        Trace("start");
        var errors = new List<string>();
        var nodes = EnumerateNodes();
        Trace($"nodes={nodes.Count}");
        var interfaces = EnumerateHid(errors);
        Trace($"interfaces={interfaces.Count}");
        var devices = new List<DeviceDescriptor>();
        var candidateKeys=interfaces.Select(i=>nodes.GetValueOrDefault(i.InstanceId)).Where(n=>n is not null)
            .Select(n=>GroupKey(n!,nodes)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in nodes.Values.Where(n => candidateKeys.Contains(GroupKey(n,nodes))
                     || n.Id.StartsWith("BTHENUM\\DEV_",StringComparison.OrdinalIgnoreCase)
                     || n.Id.StartsWith("BTHLE\\DEV_",StringComparison.OrdinalIgnoreCase))
                 .GroupBy(n=>GroupKey(n,nodes),StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToList(); var first = list[0];
            var hid = interfaces.Where(i=>list.Any(n=>n.Id.Equals(i.InstanceId,StringComparison.OrdinalIgnoreCase))).ToList();
            var best = list.OrderBy(n=>n.Id.StartsWith("USB\\VID_",StringComparison.OrdinalIgnoreCase)?0:1).Select(n=>n.Product).FirstOrDefault(s=>!string.IsNullOrWhiteSpace(s))
                ?? hid.Select(h=>h.Product).FirstOrDefault(s=>!string.IsNullOrWhiteSpace(s))
                ?? list.Select(n=>n.Name).FirstOrDefault(s=>!string.IsNullOrWhiteSpace(s)) ?? first.Id;
            var properties = new Dictionary<string,string>();
            foreach(var n in list)
            {
                properties[$"node:{n.Id}"] = $"class={n.Class}; parent={n.Parent}; bus={n.Bus}; product={n.Product}; manufacturer={n.Manufacturer}; hardwareIds={n.HardwareIds}";
                if(CM_Get_DevNode_Status(out var status,out var problem,n.DevInst,0)==0) properties[$"status:{n.Id}"]=$"0x{status:X8}; problem={problem}";
            }
            foreach(var i in hid)
                properties[$"hid:{i.Descriptor.Path}"]=$"input={i.InputLength}; output={i.OutputLength}; feature={i.FeatureLength}; product={i.Product}; serial={i.Serial}; error={i.Error}";
            bool bluetooth = list.Any(n=>n.Id.StartsWith("BTH",StringComparison.OrdinalIgnoreCase) || n.Bus.Equals("{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}",StringComparison.OrdinalIgnoreCase));
            var descriptor = new DeviceDescriptor
            {
                Id=group.Key, ContainerId=group.Key.StartsWith("container:",StringComparison.Ordinal)?group.Key[10..]:"", DisplayName=best, InstanceIds=list.Select(n=>n.Id).ToList(),
                HidInterfaces=hid.Select(h=>h.Descriptor).ToList(), ChannelKey=group.Key,
                Transport=bluetooth?ConnectionTransport.Bluetooth:ConnectionTransport.Unknown,
                Properties=properties
            };
            devices.Add(ProtocolCatalog.Classify(descriptor));
        }
        return new DiscoverySnapshot { Devices=devices.OrderBy(d=>d.DisplayName,StringComparer.CurrentCulture).ToList(), Errors=errors };
    }

    private static string GroupKey(Node node,Dictionary<string,Node> nodes)
    {
        if(node.Container.Length>0) return "container:"+node.Container;
        // A concrete USB device ancestor is a reliable shared identity; never group by a USB hub.
        var ancestor=node;
        for(int depth=0;depth<16;depth++)
        {
            if(ancestor.Id.StartsWith("USB\\VID_",StringComparison.OrdinalIgnoreCase) && !ancestor.Id.Split('\\')[1].Contains("&MI_",StringComparison.OrdinalIgnoreCase))
                return ancestor.Container.Length>0?"container:"+ancestor.Container:"usb:"+ancestor.Id;
            if(!nodes.TryGetValue(ancestor.Parent,out var parent)) break;
            ancestor=parent;
        }
        // Multiple collections of exactly the same HID family and exact parent may be one virtual device.
        // This does not merge unrelated children of the same Bluetooth adapter or USB hub.
        var parts=node.Id.Split('\\');
        if(parts.Length>=3 && parts[0].Equals("HID",StringComparison.OrdinalIgnoreCase))
        {
            int col=parts[1].LastIndexOf("&COL",StringComparison.OrdinalIgnoreCase);
            if(col>=0 && node.Parent.Length>0) return "hid-parent:"+node.Parent+"|"+parts[1][..col];
        }
        return "instance:"+node.Id;
    }

    private static Dictionary<string,Node> EnumerateNodes()
    {
        var result = new Dictionary<string,Node>(StringComparer.OrdinalIgnoreCase);
        var set = SetupDiGetClassDevsW(IntPtr.Zero,null,IntPtr.Zero,AllClasses|Present);
        if(set==InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for(uint index=0;index<16384;index++)
            {
                var info=DevInfo.Create();
                if(!SetupDiEnumDeviceInfo(set,index,ref info)) { if(Marshal.GetLastWin32Error()!=259) throw new Win32Exception(Marshal.GetLastWin32Error()); break; }
                var b=new StringBuilder(1024);
                if(!SetupDiGetDeviceInstanceIdW(set,ref info,b,1024,out _)) continue;
                string id=b.ToString();
                var container=Property(info.DevInst,ContainerId,out var type);
                string cid=type==13 && container?.Length==16 ? new Guid(container).ToString("D") : "";
                if(cid==Guid.Empty.ToString("D") || cid=="00000000-0000-0000-ffff-ffffffffffff") cid="";
                var bus=Property(info.DevInst,BusType,out type);
                string busId=type==13 && bus?.Length==16 ? new Guid(bus).ToString("B") : "";
                string name=RegistryString(set,ref info,12); if(name.Length==0) name=RegistryString(set,ref info,0);
                string parent=CM_Get_Parent(out var p,info.DevInst,0)==0?InstanceId(p):"";
                var product=Property(info.DevInst,BusReportedDescription,out type);
                string productName=type==18 && product is not null?Encoding.Unicode.GetString(product).TrimEnd('\0'):"";
                result[id]=new(id,info.DevInst,cid,name,RegistryString(set,ref info,7),parent,busId,RegistryString(set,ref info,1).Replace('\0',';'),productName,RegistryString(set,ref info,11));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    private sealed record HidNode(string InstanceId,HidInterfaceDescriptor Descriptor,string Product,string Serial,int InputLength,int OutputLength,int FeatureLength,string Error);
    private static List<HidNode> EnumerateHid(List<string> errors)
    {
        var result=new List<HidNode>(); HidD_GetHidGuid(out var guid);
        var set=SetupDiGetClassDevsW(ref guid,null,IntPtr.Zero,Present|DeviceInterface);
        if(set==InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for(uint index=0;index<4096;index++)
            {
                var iface=InterfaceInfo.Create();
                if(!SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,index,ref iface)) { if(Marshal.GetLastWin32Error()!=259) errors.Add($"HID enum: {Marshal.GetLastWin32Error()}"); break; }
                var info=DevInfo.Create(); SetupDiGetDeviceInterfaceDetailW(set,ref iface,IntPtr.Zero,0,out var required,ref info);
                if(required<6 || required>65536) continue;
                var buffer=Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer,IntPtr.Size==8?8:6);
                    if(!SetupDiGetDeviceInterfaceDetailW(set,ref iface,buffer,required,out _,ref info)) continue;
                    string path=Marshal.PtrToStringUni(buffer+4)??"";
                    Trace($"HID {index}: open");
                    var d=new HidInterfaceDescriptor{Path=path}; string product="",serial="",error=""; Caps caps=default;
                    using(var handle=CreateFileW(path,0,3,IntPtr.Zero,3,0,IntPtr.Zero))
                    {
                        if(handle.IsInvalid) error=$"metadata access: Win32 {Marshal.GetLastWin32Error()}";
                        else
                        {
                            var attributes=new Attributes{Size=Marshal.SizeOf<Attributes>()};
                            if(HidD_GetAttributes(handle,ref attributes)) d=d with {VendorId=attributes.VendorId,ProductId=attributes.ProductId};
                            Trace($"HID {index}: attributes");
                            if(HidD_GetPreparsedData(handle,out var pp))
                            {
                                try { if(HidP_GetCaps(pp,out caps)>=0) d=d with {UsagePage=caps.UsagePage,Usage=caps.Usage}; }
                                finally { HidD_FreePreparsedData(pp); }
                            }
                            // Product/serial USB string requests can block for five seconds PER INTERFACE
                            // on sleeping receivers. Discovery uses the OS-cached product description.
                            // Any future live string read must use a separate per-device bounded probe.
                            product=RegistryString(set,ref info,12);
                            if(product.Length==0) product=RegistryString(set,ref info,0);
                            serial="未在发现阶段执行同步 HID 字符串请求";
                            Trace($"HID {index}: cached description");
                        }
                    }
                    result.Add(new(InstanceId(info.DevInst),d,product,serial,caps.InputLength,caps.OutputLength,caps.FeatureLength,error));
                }
                finally {Marshal.FreeHGlobal(buffer);}
            }
        }
        finally {SetupDiDestroyDeviceInfoList(set);}
        return result;
    }

    private static void Trace(string message)
    {
        var path=Environment.GetEnvironmentVariable("PERIPHERALPOWER_PROBE_TRACE");
        if(string.IsNullOrWhiteSpace(path)) return;
        try {File.AppendAllText(path,$"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");}catch(IOException){}catch(UnauthorizedAccessException){}
    }

    public DeviceReadResult ReadSystemCache(DeviceDescriptor device)
    {
        var now=DateTimeOffset.UtcNow; bool present=false;
        foreach(var id in device.InstanceIds)
        {
            if(CM_Locate_DevNodeW(out var node,id,0)!=0) continue;
            present=true;
            foreach(var key in new[]{BatteryLife,BluetoothBatteryCache})
            {
                var raw=Property(node,key,out uint type);
                if(type!=3 || raw?.Length!=1 || raw[0]>100) continue;
                return new DeviceReadResult {DeviceId=device.Id,QueryTime=now,Battery=BatteryReading.Percent(raw[0]),BatteryObservedAt=now,
                    IsSystemCache=true,Status=ReadStatus.Success,Transport=device.Transport,Connection=ConnectionState.Unknown,
                    Source=key.Format==BatteryLife.Format?"Windows System.Devices.BatteryLife 缓存（采样时间未知）":"Windows 蓝牙驱动电量缓存（非公开稳定契约；采样时间未知）"};
            }
        }
        // Devnode presence, especially a paired Bluetooth node or USB receiver, does not prove the peripheral is online.
        return new DeviceReadResult {DeviceId=device.Id,QueryTime=now,Status=ReadStatus.Unsupported,Battery=BatteryReading.Unsupported,
            Connection=present?ConnectionState.Unknown:ConnectionState.Disconnected,Transport=device.Transport,Source="Windows 设备属性",
            Error=present?"系统未公开有效电量；本体连接状态未知。":"设备节点当前不存在。"};
    }
}
