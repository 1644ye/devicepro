using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace PeripheralPower.App.Native;

/// <summary>Current-user Run registration. The switch reflects registry and Windows approval state.</summary>
public static class StartupService
{
    private const string ValueName = "PeripheralPower";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Approved32Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

    public static StartupStatus GetStatus()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            var command = run?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(command)) return new(false, false, false, null, command);
            var expected = GetStartupCommand();
            if (!string.Equals(command, expected, StringComparison.OrdinalIgnoreCase))
                return new(false, true, false, "启动项指向其他路径或参数，请关闭后重新开启。", command);

            // StartupApproved is an undocumented Explorer implementation detail. Only
            // known enabled/disabled values are interpreted; an unknown value is never
            // optimistically reported as enabled. We do not rewrite Windows approval data.
            foreach (var keyName in new[] { ApprovedKey, Approved32Key })
            {
                using var approved = Registry.CurrentUser.OpenSubKey(keyName);
                if (approved?.GetValue(ValueName) is not byte[] bytes) continue;
                if (bytes.Length < 4) return new(false, true, false, "Windows 启动批准状态无法识别，请检查系统启动应用设置。", command);
                var state = BitConverter.ToUInt32(bytes, 0);
                if (state is 3 or 7)
                    return new(false, true, true, "已注册，但被 Windows 禁用。请在系统“启动应用”中启用。", command);
                if (state is not (2 or 6))
                    return new(false, true, false, "Windows 启动批准状态未知，请检查系统启动应用设置。", command);
            }
            return new(true, true, false, null, command);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            return new(false, false, false, "无法读取启动项：" + ex.Message, null);
        }
    }

    public static StartupStatus SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var command = GetStartupCommand();
                using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                run.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                    run?.DeleteValue(ValueName, throwOnMissingValue: false);
                // Only our own named registration is removed; no other startup item changes.
                foreach (var keyName in new[] { ApprovedKey, Approved32Key })
                {
                    using var approved = Registry.CurrentUser.OpenSubKey(keyName, writable: true);
                    approved?.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            return GetStatus();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            var actual = GetStatus();
            return actual with { Error = "无法更新开机自启：" + ex.Message };
        }
    }

    public static string GetStartupCommand()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assembly)) throw new InvalidOperationException("无法确定应用程序集路径。");
            return Quote(executable) + " " + Quote(assembly) + " --startup";
        }
        return Quote(executable) + " --startup";
    }

    private static string Quote(string value)
    {
        if (value.Contains('"')) throw new InvalidOperationException("启动路径包含无效引号。");
        return "\"" + value + "\"";
    }
}

public sealed record StartupStatus(bool Enabled, bool IsRegistered, bool DisabledByWindows, string? Error, string? Command);
