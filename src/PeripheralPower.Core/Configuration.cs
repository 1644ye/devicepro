using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeripheralPower.Core;

public sealed record WindowPlacement
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 380;
    public double Height { get; set; } = 500;
}

public sealed record AppConfig
{
    public const int MinimumIntervalMs = 30_000;
    public const int MaximumIntervalMs = 1_800_000;
    public const int DefaultIntervalMs = 60_000;
    public int SchemaVersion { get; set; } = 1;
    public List<MonitoredTarget> Targets { get; set; } = [];
    public bool AutoRefreshEnabled { get; set; } = true;
    public int RefreshIntervalMs { get; set; } = DefaultIntervalMs;
    public StartupView StartupView { get; set; } = StartupView.Management;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public WindowPlacement PanelBounds { get; set; } = new();
    public WindowPlacement ManagementBounds { get; set; } = new() { Width = 1000, Height = 700 };

    public static bool IsValidInterval(int milliseconds) => milliseconds is >= MinimumIntervalMs and <= MaximumIntervalMs;
}

public static class JsonStorage
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    // Replace atomically in the same directory. Never truncate the existing settings on a failed write.
    public static void WriteAtomic<T>(string path, T value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public sealed class ConfigStore
{
    private readonly object _gate = new();
    public string DirectoryPath { get; }
    public string ConfigPath => Path.Combine(DirectoryPath, "config.json");
    public string CachePath => Path.Combine(DirectoryPath, "state-cache.json");
    public string LogDirectory => Path.Combine(DirectoryPath, "logs");
    public string? LastError { get; private set; }

    public ConfigStore(string? directory = null) => DirectoryPath = directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeripheralPower");

    public AppConfig Load()
    {
        lock (_gate)
        {
            LastError = null;
            try
            {
                if (!File.Exists(ConfigPath)) return new();
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonStorage.Options) ?? throw new InvalidDataException("配置为空。");
                if (!AppConfig.IsValidInterval(config.RefreshIntervalMs))
                {
                    LastError = "配置中的刷新周期超出范围，已恢复为 60 秒。";
                    config.RefreshIntervalMs = AppConfig.DefaultIntervalMs;
                }
                config.Targets = (config.Targets ?? []).Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Id))
                    .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.First())
                    .Select(x => x with
                    {
                        DeviceIds = (x.DeviceIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        DisplayName = string.IsNullOrWhiteSpace(x.DisplayName) ? "未命名设备" : x.DisplayName
                    }).ToList();
                config.PanelBounds ??= new();
                config.ManagementBounds ??= new() { Width = 1000, Height = 700 };
                return config;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                LastError = $"无法读取配置：{ex.Message}";
                return new();
            }
        }
    }

    public bool Save(AppConfig config)
    {
        lock (_gate)
        {
            LastError = null;
            if (!AppConfig.IsValidInterval(config.RefreshIntervalMs))
            {
                LastError = "刷新周期必须为 30000～1800000 毫秒。";
                return false;
            }
            try { JsonStorage.WriteAtomic(ConfigPath, config); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                LastError = $"无法保存配置：{ex.Message}";
                return false;
            }
        }
    }
}
