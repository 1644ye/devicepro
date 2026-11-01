namespace PeripheralPower.Core;

public static class DeviceIdentity
{
    // Store the concrete USB device instance, never an interface, hub or display name.
    public static IEnumerable<string> Keys(DeviceDescriptor device)
    {
        yield return device.Id;
        foreach (var instance in device.InstanceIds)
        {
            var parts = instance.Split('\\');
            if (parts.Length == 3 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase)
                && parts[1].StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                && parts[1].Contains("&PID_", StringComparison.OrdinalIgnoreCase)
                && !parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase)
                && parts[2].Length > 0)
                yield return "usb-instance:" + instance.ToUpperInvariant();
        }
    }

    public static bool Matches(MonitoredTarget target, DeviceDescriptor device, IReadOnlyCollection<DeviceDescriptor> discovered)
    {
        if (target.DeviceIds.Contains(device.Id, StringComparer.OrdinalIgnoreCase)) return true;
        if (Keys(device).Skip(1).Any(key => target.DeviceIds.Contains(key, StringComparer.OrdinalIgnoreCase)
            && discovered.Count(candidate => Keys(candidate).Contains(key, StringComparer.OrdinalIgnoreCase)) == 1)) return true;
        const string omni = @"usb-instance:USB\VID_0B05&PID_1ACE\";
        bool IsOmni(DeviceDescriptor candidate) => Keys(candidate).Any(k => k.StartsWith(omni, StringComparison.OrdinalIgnoreCase));
        return target.DeviceIds.Any(k => k.StartsWith(omni, StringComparison.OrdinalIgnoreCase))
            && IsOmni(device) && discovered.Count(IsOmni) == 1;
    }
}
