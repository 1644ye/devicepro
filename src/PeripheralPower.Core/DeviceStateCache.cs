using System.Text.Json;

namespace PeripheralPower.Core;

/// <summary>Thread-safe immutable snapshots; reads never query hardware.</summary>
public sealed class DeviceStateCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceSnapshot> _states = new(StringComparer.OrdinalIgnoreCase);
    public string? LastError { get; private set; }

    public bool Load(string path)
    {
        lock (_gate)
        {
            LastError = null;
            try
            {
                if (!File.Exists(path)) return true;
                var states = JsonSerializer.Deserialize<List<DeviceSnapshot>>(File.ReadAllText(path), JsonStorage.Options) ?? [];
                foreach (var state in states.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.TargetId)))
                    _states[state.TargetId] = state with
                    {
                        Battery = state.Battery ?? BatteryReading.Unknown,
                        IsHistorical = state.Battery?.IsValid == true,
                        Connection = ConnectionState.Unknown,
                        Charging = ChargingState.Unknown,
                        Status = ReadStatus.Idle,
                        Error = "历史缓存；等待本次会话的设备证据。"
                    };
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LastError = $"无法读取历史状态：{ex.Message}";
                return false;
            }
        }
    }

    public bool Save(string path)
    {
        lock (_gate)
        {
            LastError = null;
            try { JsonStorage.WriteAtomic(path, _states.Values.ToArray()); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                LastError = $"无法保存历史状态：{ex.Message}";
                return false;
            }
        }
    }

    public IReadOnlyList<DeviceSnapshot> GetSnapshots()
    {
        lock (_gate) return _states.Values.OrderBy(x => x.Order).ThenBy(x => x.DisplayName).ToArray();
    }

    public DeviceSnapshot? GetSnapshot(string targetId)
    {
        lock (_gate) return _states.GetValueOrDefault(targetId);
    }

    public void InvalidateSession()
    {
        lock (_gate)
            foreach (var (id, state) in _states.ToArray())
                _states[id] = state with
                {
                    Connection = ConnectionState.Unknown,
                    Charging = ChargingState.Unknown,
                    Status = ReadStatus.Cancelled,
                    IsHistorical = state.Battery.IsValid
                };
    }

    public void SetTargets(IEnumerable<MonitoredTarget> targets)
    {
        lock (_gate)
        {
            var current = targets.ToArray();
            var keys = current.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _states.Keys.Where(x => !keys.Contains(x)).ToArray()) _states.Remove(key);
            foreach (var target in current)
            {
                var prior = _states.GetValueOrDefault(target.Id) ?? new() { TargetId = target.Id };
                _states[target.Id] = prior with { DisplayName = target.DisplayName, Hidden = target.Hidden, Order = target.Order };
            }
        }
    }

    public DeviceSnapshot? MarkReading(string targetId)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(targetId, out var prior)) return null;
            return _states[targetId] = prior with { Status = ReadStatus.Reading };
        }
    }

    public DeviceSnapshot? Apply(string targetId, DeviceReadResult result)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(targetId, out var prior)) return null;
            var valid = result.Battery.IsValid && result.Status == ReadStatus.Success;
            var battery = valid ? result.Battery : prior.Battery.IsValid ? prior.Battery : result.Battery;
            var keepPrior = !valid && prior.Battery.IsValid;
            // The old successful timestamp survives errors and cache observations.
            // For a cache value, the formatter explicitly refuses to label this timestamp as its sample time.
            return _states[targetId] = prior with
            {
                DeviceId = result.DeviceId,
                Battery = battery,
                Connection = result.Connection,
                Transport = result.Transport,
                Charging = result.Charging,
                Status = result.Status,
                LastQueryAt = result.QueryTime,
                LastValidBatteryAt = valid && !result.IsSystemCache ? result.BatterySampledAt : prior.LastValidBatteryAt,
                BatteryObservedAt = valid ? result.BatteryObservedAt : prior.BatteryObservedAt,
                OnlineEvidenceAt = result.OnlineEvidenceAt ?? prior.OnlineEvidenceAt,
                IsSystemCache = keepPrior ? prior.IsSystemCache : result.IsSystemCache,
                IsHistorical = battery.IsValid && (keepPrior || result.Connection == ConnectionState.Disconnected),
                Source = keepPrior ? prior.Source : result.Source,
                Error = result.Error
            };
        }
    }
}
