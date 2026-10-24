namespace PeripheralPower.Core;

/// <summary>
/// A bounded mailbox (one active cycle plus one pending request), shared by all views and triggers.
/// The hardware backend must honor cancellation by terminating its isolated worker and closing handles.
/// No periodic timer or retry loop lives here: Windows messages submit finite work through RequestRefresh.
/// </summary>
public sealed class RefreshCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IHardwareBackend _backend;
    private readonly DeviceStateCache _cache;
    private readonly SynchronizationContext? _uiContext;
    private readonly TimeSpan _readTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, SemaphoreSlim> _channels = new(StringComparer.OrdinalIgnoreCase);
    private MonitoredTarget[] _targets = [];
    private CancellationTokenSource? _activeCycle;
    private long _generation;
    private bool _pending;
    private bool _running;
    private bool _disposed;
    private TaskCompletionSource _idle = CompletedIdle();
    private DiscoverySnapshot _lastDiscovery = new();

    public event Action<DeviceSnapshot>? SnapshotChanged;
    public event Action<DiscoverySnapshot>? DiscoveryChanged;
    public event Action<string>? BackgroundError;

    public RefreshCoordinator(IHardwareBackend backend, DeviceStateCache cache,
        SynchronizationContext? uiContext = null, TimeSpan? readTimeout = null)
    {
        _backend = backend;
        _cache = cache;
        _uiContext = uiContext;
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(8);
        if (_readTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(readTimeout));
    }

    public DiscoverySnapshot LastDiscovery { get { lock (_gate) return _lastDiscovery; } }
    public bool IsRefreshing { get { lock (_gate) return _running; } }
    public int PendingRequestCount { get { lock (_gate) return _pending ? 1 : 0; } }

    public void SetTargets(IEnumerable<MonitoredTarget> targets)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Clone identities so callers changing a configuration cannot mutate a live cycle.
            _targets = targets.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First() with { DeviceIds = x.First().DeviceIds.ToList() }).ToArray();
            _generation++;
            _pending = false;
            _activeCycle?.Cancel();
            _cache.SetTargets(_targets);
            _cache.InvalidateSession();
        }
    }

    public void InvalidateSession()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            _pending = false;
            _activeCycle?.Cancel();
            _cache.InvalidateSession();
        }
    }

    public bool RequestRefresh(RefreshReason reason = RefreshReason.Manual)
    {
        lock (_gate)
        {
            if (_disposed || _targets.Length == 0) return false;
            if (reason is RefreshReason.DeviceChanged or RefreshReason.Resume)
            {
                _generation++;
                _activeCycle?.Cancel();
                _cache.InvalidateSession();
            }
            _pending = true;
            if (_running) return true;
            _running = true;
            _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(WorkerAsync);
            return true;
        }
    }

    public Task WhenIdleAsync() { lock (_gate) return _idle.Task; }

    private async Task WorkerAsync()
    {
        // This loop drains at most one pending slot. It never schedules periodic work or sleeps.
        while (true)
        {
            MonitoredTarget[] targets;
            long generation;
            CancellationTokenSource cycle;
            lock (_gate)
            {
                if (_disposed || !_pending || _targets.Length == 0)
                {
                    _running = false;
                    _idle.TrySetResult();
                    return;
                }
                _pending = false;
                targets = _targets;
                generation = _generation;
                cycle = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _activeCycle = cycle;
            }
            try { await RefreshAsync(targets, generation, cycle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cycle.IsCancellationRequested) { }
            catch (Exception ex) { Publish(generation, () => BackgroundError?.Invoke(ex.Message)); }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeCycle, cycle)) _activeCycle = null;
                    foreach (var channel in _channels.Values) channel.Dispose();
                    _channels.Clear();
                }
                cycle.Dispose();
            }
        }
    }

    private async Task RefreshAsync(MonitoredTarget[] targets, long generation, CancellationToken cancellationToken)
    {
        DiscoverySnapshot discovery;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_readTimeout);
            discovery = await _backend.DiscoverAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var status = ex is OperationCanceledException ? ReadStatus.Timeout : ReadStatus.Failed;
            foreach (var target in targets)
                Apply(target.Id, new() { Status = status, Error = "设备发现失败：" + ex.Message }, generation);
            return;
        }
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            _lastDiscovery = discovery;
        }
        Publish(generation, () => DiscoveryChanged?.Invoke(discovery));

        // Query all manually linked mode identities once per cycle. A cached Bluetooth entry
        // must not hide a valid current wired result simply because it enumerated first.
        var selected = targets.SelectMany(target => discovery.Devices
            .Where(device => target.DeviceIds.Contains(device.Id, StringComparer.OrdinalIgnoreCase))
            .Select(device => (Target: target, Device: device))).ToArray();
        var foundTargets = selected.Select(x => x.Target.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets.Where(x => !foundTargets.Contains(x.Id)))
            Apply(target.Id, new() { Status = ReadStatus.Failed, Error = "当前系统枚举中未发现目标；连接状态无法确认。" }, generation);

        foreach (var targetId in foundTargets)
        {
            DeviceSnapshot? reading;
            lock (_gate)
            {
                if (_disposed || generation != _generation) return;
                reading = _cache.MarkReading(targetId);
            }
            if (reading is not null) Publish(generation, () => SnapshotChanged?.Invoke(reading));
        }

        var bestResults = new Dictionary<string, DeviceReadResult>(StringComparer.OrdinalIgnoreCase);
        var aggregationGate = new object();
        void ApplyBest(string targetId, DeviceReadResult result)
        {
            lock (aggregationGate)
            {
                if (bestResults.TryGetValue(targetId, out var prior))
                {
                    var strength = EvidenceStrength(result).CompareTo(EvidenceStrength(prior));
                    if (strength < 0 || (strength == 0 && result.QueryTime < prior.QueryTime)) return;
                }
                bestResults[targetId] = result;
                Apply(targetId, result, generation);
            }
        }
        var jobs = selected.GroupBy(x => x.Device.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => ReadDeviceAsync(group.First().Device, group.Select(x => x.Target.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), generation, ApplyBest, cancellationToken));
        await Task.WhenAll(jobs).ConfigureAwait(false);
    }

    private static int EvidenceStrength(DeviceReadResult result)
    {
        var strength = result.Battery.IsValid && result.Status == ReadStatus.Success
            ? result.IsSystemCache ? 20 : 200 : 0;
        strength += result.Connection switch { ConnectionState.Connected => 50, ConnectionState.Disconnected => -30, _ => 0 };
        strength += result.Status == ReadStatus.Success ? 10 : 0;
        return strength;
    }

    private async Task ReadDeviceAsync(DeviceDescriptor device, string[] targetIds, long generation,
        Action<string, DeviceReadResult> applyResult, CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(device.ChannelKey) ? device.Id : device.ChannelKey;
        SemaphoreSlim channel;
        lock (_gate)
        {
            if (!_channels.TryGetValue(key, out channel!)) _channels[key] = channel = new(1, 1);
        }
        await channel.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeviceReadResult result;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_readTimeout);
            try
            {
                result = await _backend.ReadAsync(device, timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (OperationCanceledException)
            {
                result = new() { DeviceId = device.Id, Status = ReadStatus.Timeout, Error = "读取超过时限，隔离读取进程已终止。" };
            }
            catch (Exception ex)
            {
                result = new() { DeviceId = device.Id, Status = ReadStatus.Failed, Error = ex.Message };
            }
            foreach (var target in targetIds) applyResult(target, result);
        }
        finally { channel.Release(); }
    }

    private void Apply(string targetId, DeviceReadResult result, long generation)
    {
        DeviceSnapshot? state;
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            state = _cache.Apply(targetId, result);
        }
        if (state is not null) Publish(generation, () => SnapshotChanged?.Invoke(state));
    }

    private void Publish(long generation, Action callback)
    {
        void Deliver(object? _)
        {
            lock (_gate) { if (_disposed || generation != _generation) return; }
            try { callback(); } catch { /* A closed UI view cannot fault the hardware worker. */ }
        }
        if (_uiContext is not null) _uiContext.Post(Deliver, null);
        else Deliver(null);
    }

    public async ValueTask DisposeAsync()
    {
        Task idle;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _pending = false;
            _shutdown.Cancel();
            _activeCycle?.Cancel();
            idle = _idle.Task;
        }
        await idle.ConfigureAwait(false);
        foreach (var channel in _channels.Values) channel.Dispose();
        _shutdown.Dispose();
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
