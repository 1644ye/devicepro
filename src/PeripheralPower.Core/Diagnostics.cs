using System.Text;

namespace PeripheralPower.Core;

/// <summary>Best effort, bounded local logs. No input reports or keyboard data are accepted here.</summary>
public sealed class RollingLog
{
    private readonly object _gate = new();
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;
    private readonly Dictionary<string, (DateTimeOffset At, int Suppressed)> _recent = [];
    public string DirectoryPath { get; }
    public string? LastError { get; private set; }

    public RollingLog(string? directory = null, long maximumBytes = 512 * 1024, int retainedFiles = 3)
    {
        DirectoryPath = directory ?? new ConfigStore().LogDirectory;
        _maximumBytes = Math.Max(4096, maximumBytes);
        _retainedFiles = Math.Clamp(retainedFiles, 1, 10);
    }

    public void Write(string category, string message)
    {
        lock (_gate)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var safeCategory = category.Replace('\r', ' ').Replace('\n', ' ');
                if (safeCategory.Length > 128) safeCategory = safeCategory[..128];
                var safeMessage = message.Replace('\r', ' ').Replace('\n', ' ');
                if (safeMessage.Length > 4096) safeMessage = safeMessage[..4096];
                var key = safeCategory + ":" + safeMessage;
                if (_recent.TryGetValue(key, out var previous) && now - previous.At < TimeSpan.FromMinutes(1))
                {
                    _recent[key] = (previous.At, previous.Suppressed + 1);
                    return;
                }
                // Bound both retained disk data and the in-memory deduplication index.
                if (_recent.Count >= 128) _recent.Clear();
                _recent[key] = (now, 0);
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "application.log");
                if (File.Exists(path) && new FileInfo(path).Length >= _maximumBytes)
                {
                    for (var i = _retainedFiles; i >= 1; i--)
                    {
                        var source = i == 1 ? path : path + "." + (i - 1);
                        var destination = path + "." + i;
                        if (File.Exists(source)) File.Move(source, destination, true);
                    }
                }
                File.AppendAllText(path, $"{now:O}\t{safeCategory}\t{safeMessage}{Environment.NewLine}", Encoding.UTF8);
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                LastError = ex.Message;
            }
        }
    }
}

public static class DiagnosticExporter
{
    public static void Export(string path, AppConfig config, DiscoverySnapshot discovery, IEnumerable<DeviceSnapshot> snapshots)
    {
        JsonStorage.WriteAtomic(path, new
        {
            FormatVersion = 1,
            ExportedAt = DateTimeOffset.UtcNow,
            OperatingSystem = Environment.OSVersion.ToString(),
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Note = "仅包含设备身份、接口、能力及状态；不读取键盘内容或高频输入报告。系统缓存观察时间不代表设备采样时间。",
            Configuration = config,
            Discovery = discovery,
            States = snapshots.ToArray()
        });
    }
}
