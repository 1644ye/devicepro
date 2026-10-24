using System.Text.Json;
using PeripheralPower.Core;
using PeripheralPower.Hardware;

// Explicitly synthetic test backends. These are never referenced by the desktop application.
// This entry point exists only in the tests executable to verify real process termination.
if (args.Length > 0 && args[0] == "--probe-read")
{
    var device = JsonSerializer.Deserialize<DeviceDescriptor>(await File.ReadAllTextAsync(args[1]))!;
    if (device.Properties.TryGetValue("TestProcessMarker", out var marker))
        await File.WriteAllTextAsync(marker, Environment.ProcessId.ToString());
    if (device.Id == "test-process-hang") await Task.Delay(Timeout.Infinite);
    await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(Success(device.Id, 42)));
    return 0;
}
var tests = new (string Name, Func<Task> Run)[]
{
    ("VGN EVISION：事务匹配、边界与接口限制", VgnFixtures),
    ("ATK HOLTEK：电量、充电、接收器离线", AtkFixtures),
    ("断开隐藏、重连恢复及不完整发现保留未知", DisconnectTrayFixtures),
    ("OPPO 独立左右耳、缺席编码、帧边界与缓存回退", OppoBatteryFixtures),
    ("电量范围和档位保持原始精度", BatteryValidation),
    ("失败保留有效电量和时间，不推断断开", FailurePreservesEvidence),
    ("缓存观察时间不能成为采样时间", CacheTimeSemantics),
    ("托盘日期、历史、长度与纯缓存格式化", TooltipSemantics),
    ("默认周期和两个边界可保存，越界不覆盖", ConfigBoundaries),
    ("损坏配置与不可写路径容错", ConfigFailures),
    ("历史状态重启后不冒充当前在线", PersistedHistory),
    ("无目标不发现或读取", NoTargetNoWork),
    ("100次并发刷新最多合并1次后续任务", CoalescedRequests),
    ("共享通道串行，独立设备及时发布", IndependentUpdatesAndChannelLock),
    ("手动关联到同一身份不会重复读取", DuplicateIdentity),
    ("关联多模式时实时证据优于旧缓存和迟到失败", LinkedModeEvidence),
    ("旧会话迟到结果和排队UI消息均丢弃", StaleSessionResults),
    ("单设备超时不阻塞其他结果且可恢复", TimeoutAndRecovery),
    ("移除最后目标取消读取及后续请求", RemovalCancels),
    ("退出等待底层取消和清理", DisposeWaitsForCleanup),
    ("日志大小轮转有界，诊断可导出", DiagnosticsBounded),
    ("500轮读取异常的队列、并发、资源压力", BoundedFailureStress),
    ("协议fixture：ROG精确百分比与无关回复", RogProtocolFixtures),
    ("协议fixture：Apex档位和充电字段分离", ApexProtocolFixtures),
    ("协议fixture：精确身份匹配拒绝名称和近似型号", ProtocolIdentityFixtures),
    ("协议fixture：OMNI配对身份、有效电量和休眠空回复", OmniProtocolFixtures),
    ("协议fixture：普通ZEN65原生分支精确身份和只读命令", WobNativeProtocolFixtures),
    ("真实子进程隔离fixture：超时kill后重试", IsolatedProcessTimeout),
    ("真实子进程隔离fixture：取消完成前已退出", IsolatedProcessCancellation)
};
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run().WaitAsync(TimeSpan.FromSeconds(10)); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + "\n" + ex); }
}
Console.WriteLine($"RESULT {tests.Length - failed}/{tests.Length} passed (synthetic automated tests; no hardware claims)");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message = "断言失败") { if (!condition) throw new InvalidOperationException(message); }
static Task VgnFixtures()
{
    var now=DateTimeOffset.UtcNow;
    var packet=VgnBatteryQuery.CreateQuery(0x1234);
    Check(packet.Length==64 && packet[0]==4 && packet[1]==0x34 && packet[2]==0x12 && packet[3]==0x1a);
    packet[8]=55;packet[9]=0;
    var result=VgnBatteryQuery.Decode("vgn",packet,0x1234,now)!;
    Check(result.Battery.Value==55 && result.Connection==ConnectionState.Connected && !result.IsSystemCache);
    Check(VgnBatteryQuery.Decode("vgn",packet,1,now)==null);
    Check(VgnBatteryQuery.Decode("vgn",packet.AsSpan(0,10),0x1234,now)==null);
    packet[9]=2;Check(VgnBatteryQuery.Decode("vgn",packet,0x1234,now)!.Charging==ChargingState.Charging);
    packet[8]=0;Check(VgnBatteryQuery.Decode("vgn",packet,0x1234,now)!.Battery.Value==0);
    packet[8]=101;
    try{VgnBatteryQuery.Decode("vgn",packet,0x1234,now);throw new Exception("越界值未拒绝");}catch(InvalidDataException){}
    var iface=new HidInterfaceDescriptor{VendorId=0x320f,ProductId=0x5088,UsagePage=0xff1c,Usage=0x92};
    Check(VgnBatteryQuery.Matches(new DeviceDescriptor{DisplayName="VGN V98pro V2 2.4G Dongle",HidInterfaces=[iface]}));
    Check(!VgnBatteryQuery.Matches(new DeviceDescriptor{DisplayName="VGN V98pro",HidInterfaces=[iface with {Usage=1}]}));
    return Task.CompletedTask;
}
static Task AtkFixtures()
{
    var now=DateTimeOffset.UtcNow;var packet=new byte[65];packet[1]=0x82;packet[9]=0x80|45;packet[10]=1;
    var result=AtkBatteryQuery.Decode("atk",packet,true,now)!;
    Check(result.Battery.Value==45 && result.Charging==ChargingState.Charging && result.Connection==ConnectionState.Connected);
    packet[10]=0;result=AtkBatteryQuery.Decode("atk",packet,true,now)!;
    Check(!result.Battery.IsValid && result.Connection==ConnectionState.Disconnected && result.BatterySampledAt is null);
    Check(AtkBatteryQuery.Decode("atk",packet,false,now)!.Battery.Value==45);
    packet[10]=1;packet[9]=127;
    try{AtkBatteryQuery.Decode("atk",packet,true,now);throw new Exception("越界值未拒绝");}catch(InvalidDataException){}
    packet[1]=0x81;Check(AtkBatteryQuery.Decode("atk",packet,true,now)==null);
    Check(AtkBatteryQuery.Decode("atk",packet.AsSpan(0,10),true,now)==null);
    Check(AtkBatteryQuery.CreateQuery().SequenceEqual(new byte[]{0,0x82,0,0,0,0,0,0,0}));
    var receiver=ProtocolCatalog.Classify(new DeviceDescriptor{DisplayName="Wireless mouse dongle-8k NANO",
        HidInterfaces=[new HidInterfaceDescriptor{VendorId=0x373b,ProductId=0x1178,UsagePage=0xff10,Usage=1}]});
    Check(receiver.AdapterId==AtkBatteryQuery.Adapter && receiver.Transport==ConnectionTransport.Receiver24Ghz
        && receiver.DisplayName.Contains("ATK") && receiver.DisplayName.Contains("2.4G"));
    Check(!AtkBatteryQuery.IsInterface(new HidInterfaceDescriptor{VendorId=0x373b,ProductId=0x1178,UsagePage=0xff02,Usage=2}));
    Check(BluetoothConnectionQuery.Address(new DeviceDescriptor{InstanceIds=["BTHLE\\DEV_F71E262ED88C\\test"]})=="F71E262ED88C");
    Check(BluetoothConnectionQuery.Address(new DeviceDescriptor{InstanceIds=["BTHENUM\\DEV_invalid-data"]})==null);
    return Task.CompletedTask;
}
static async Task DisconnectTrayFixtures()
{
    var cache=new DeviceStateCache();var backend=new FakeBackend{Read=(d,_,_)=>Task.FromResult(Success(d.Id))};
    await using var coordinator=new RefreshCoordinator(backend,cache);
    coordinator.SetTargets([Target()]);coordinator.RequestRefresh();await coordinator.WhenIdleAsync();
    Check(StateFormatter.ShowDeviceTrayIcon(cache.GetSnapshot("a")!));
    backend.Devices.Clear();coordinator.RequestRefresh(RefreshReason.DeviceChanged);await coordinator.WhenIdleAsync();
    var state=cache.GetSnapshot("a")!;
    Check(state.Connection==ConnectionState.Disconnected && state.IsHistorical && state.Battery.IsValid);
    Check(!StateFormatter.ShowDeviceTrayIcon(state));
    backend.Errors.Add("模拟部分枚举失败");coordinator.RequestRefresh();await coordinator.WhenIdleAsync();
    Check(cache.GetSnapshot("a")!.Connection==ConnectionState.Unknown);
    backend.Errors.Clear();backend.Devices.Add(Device());coordinator.RequestRefresh();await coordinator.WhenIdleAsync();
    Check(StateFormatter.ShowDeviceTrayIcon(cache.GetSnapshot("a")!));
    cache.Apply("a",new DeviceReadResult{DeviceId="a",Status=ReadStatus.Success,Connection=ConnectionState.Disconnected});
    Check(!StateFormatter.ShowDeviceTrayIcon(cache.GetSnapshot("a")!) && cache.GetSnapshot("a")!.Battery.IsValid);
    Check(!StateFormatter.ShowDeviceTrayIcon(cache.GetSnapshot("a")! with {Connection=ConnectionState.Connected,Hidden=true}));
}
static Task OppoBatteryFixtures()
{
    var now=DateTimeOffset.UtcNow;
    static byte[] Frame(params byte[] payload) => new byte[]{0xaa,(byte)(7+payload.Length),0,0,6,0x81,0xf0,(byte)payload.Length,0}.Concat(payload).ToArray();
    var result=OppoBatteryQuery.Decode("oppo",Frame(0,3,1,65,2,90,3,0),now)!;
    Check(result.Earbuds.Left.Value==65 && result.Earbuds.Right.Value==90 && !result.Earbuds.Case.IsValid);
    Check(result.Battery.Value==65 && !result.IsSystemCache);
    var one=OppoBatteryQuery.Decode("oppo",Frame(0,3,1,0,2,100,3,0),now)!;
    Check(!one.Earbuds.Left.IsValid && one.Earbuds.Right.Value==100);
    Check(OppoBatteryQuery.Decode("oppo",Frame(0,3,1,255,2,127,3,0),now)==null);
    Check(OppoBatteryQuery.Decode("oppo",Frame(1,1,1,80),now)==null);
    Check(OppoBatteryQuery.Decode("oppo",Frame(0,2,1,80),now)==null);
    Check(OppoBatteryQuery.Decode("oppo",Frame(0,2,1,80,1,90),now)==null);
    var truncated=Frame(0,1,2,80)[..^1];
    Check(OppoBatteryQuery.Decode("oppo",truncated,now)==null);
    var separate=OppoBatteryQuery.Decode("oppo",Frame(0,4,70,1,80,0,50,0),now)!;
    Check(separate.Earbuds.Left.Value==70 && separate.Earbuds.Case.Value==50);
    var cache=new DeviceStateCache();cache.SetTargets([Target("a","oppo")]);cache.Apply("a",result);
    cache.Apply("a",new DeviceReadResult{DeviceId="oppo",Status=ReadStatus.Timeout});
    Check(cache.GetSnapshot("a")!.IsHistorical && cache.GetSnapshot("a")!.Earbuds.Right.Value==90);
    cache.Apply("a",new DeviceReadResult{DeviceId="oppo",Status=ReadStatus.Success,Battery=BatteryReading.Percent(80),IsSystemCache=true});
    Check(!cache.GetSnapshot("a")!.Earbuds.IsValid, "总电量回退不能把旧左右耳冒充当前读数");
    var boxOnly=OppoBatteryQuery.Decode("oppo",Frame(0,1,3,50),now)!;
    cache.Apply("a",boxOnly);
    Check(!cache.GetSnapshot("a")!.Battery.IsValid && cache.GetSnapshot("a")!.Earbuds.Case.Value==50);
    cache.InvalidateSession();Check(cache.GetSnapshot("a")!.IsHistorical);
    Check(DeviceAppearance.FromName("MOONDROP PILL")==DeviceShape.EarClip);
    Check(DeviceAppearance.FromName("OPPO Enco Free4（丹拿版）")==DeviceShape.InEar);
    Check(DeviceAppearance.FromName("ATK A9 PRO BLE")==DeviceShape.Mouse);
    Check(DeviceAppearance.FromName("VGN V98pro V2 2.4G Dongle")==DeviceShape.Keyboard);
    Check(!OppoBatteryQuery.Matches(new DeviceDescriptor{DisplayName="OPPO Enco Free4",Transport=ConnectionTransport.Bluetooth}));
    return Task.CompletedTask;
}
static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static MonitoredTarget Target(string id = "a", params string[] deviceIds) => new()
{
    Id = id, DisplayName = "测试外设 " + id, DeviceIds = deviceIds.Length > 0 ? deviceIds.ToList() : [id]
};
static DeviceDescriptor Device(string id = "a", string channel = "") => new() { Id = id, DisplayName = id, ChannelKey = channel };
static DeviceReadResult Success(string id, int value = 76) => new()
{
    DeviceId = id, Status = ReadStatus.Success, Battery = BatteryReading.Percent(value),
    BatterySampledAt = DateTimeOffset.UtcNow, Connection = ConnectionState.Connected,
    Transport = ConnectionTransport.Bluetooth, Source = "测试后端"
};
static DeviceStateCache NewCache()
{
    var cache = new DeviceStateCache();
    cache.SetTargets([Target()]);
    return cache;
}
static string TempDirectory()
{
    var directory = Path.Combine(Path.GetTempPath(), "PeripheralPower.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    return directory;
}

static Task BatteryValidation()
{
    Check(!new BatteryReading { Kind = BatteryKind.Percentage, Value = 101 }.IsValid);
    Check(!new BatteryReading { Kind = BatteryKind.Level, Value = 6, Maximum = 5 }.IsValid);
    Check(!new BatteryReading { Kind = BatteryKind.Level, Value = 0, Maximum = 0 }.IsValid);
    Check(BatteryReading.Percent(0).IsValid && BatteryReading.Level(0, 5).IsValid);
    Check(StateFormatter.Battery(new() { Battery = BatteryReading.Level(3, 5) }) == "3/5 档");
    Check(StateFormatter.Battery(new() { Battery = BatteryReading.Percent(60, true) }) == "约 60%");
    return Task.CompletedTask;
}

static Task FailurePreservesEvidence()
{
    var cache = NewCache();
    var reading = Success("a");
    cache.Apply("a", reading);
    cache.Apply("a", new() { Status = ReadStatus.Failed, Error = "被官方驱动占用" });
    var state = cache.GetSnapshot("a")!;
    Check(state.Battery.Value == 76 && state.LastValidBatteryAt == reading.BatterySampledAt);
    Check(state.Connection == ConnectionState.Unknown && state.Status == ReadStatus.Failed);
    Check(state.IsHistorical && state.BatteryText.Contains("上次电量") && state.Error.Contains("占用"));
    cache.Apply("a", new() { Status = ReadStatus.Timeout });
    Check(cache.GetSnapshot("a")!.LastValidBatteryAt == reading.BatterySampledAt);
    return Task.CompletedTask;
}

static Task CacheTimeSemantics()
{
    var cache = NewCache();
    var live = Success("a");
    cache.Apply("a", live);
    var cached = new DeviceReadResult
    {
        Status = ReadStatus.Success, Battery = BatteryReading.Percent(33), IsSystemCache = true,
        Source = "Windows 系统缓存", BatteryObservedAt = DateTimeOffset.UtcNow
    };
    cache.Apply("a", cached);
    var state = cache.GetSnapshot("a")!;
    Check(state.LastValidBatteryAt == live.BatterySampledAt);
    Check(state.TimeText.Contains("采样时间未知") && !state.TimeText.Contains("更新"));
    var onlyCache = NewCache();
    onlyCache.Apply("a", cached);
    Check(onlyCache.GetSnapshot("a")!.LastValidBatteryAt is null);
    return Task.CompletedTask;
}

static Task TooltipSemantics()
{
    var now = DateTimeOffset.Now;
    var state = new DeviceSnapshot
    {
        DisplayName = "设备", Battery = BatteryReading.Level(3, 5), LastValidBatteryAt = now.AddDays(-1), IsHistorical = true
    };
    var text = StateFormatter.TrayLine(state, now);
    Check(text.Contains("上次电量 3/5 档") && text.Contains(now.AddDays(-1).ToString("yyyy-MM-dd")));
    Check(!StateFormatter.TrayLine(state with { LastValidBatteryAt = now }, now).Contains(now.ToString("yyyy-MM-dd")));
    var longText = new string('中', 125) + "😀结束";
    var truncated = StateFormatter.NativeTrayText(longText);
    Check(truncated.Length <= 127 && !char.IsHighSurrogate(truncated[^2]));
    Check(StateFormatter.TraySummary([state with { Hidden = true }], now).Contains("尚未添加"));
    Check(StateFormatter.TrayLine(new() { DisplayName = "未知" }, now).Contains("尚无有效读数"));
    return Task.CompletedTask;
}

static Task ConfigBoundaries()
{
    var store = new ConfigStore(TempDirectory());
    var config = store.Load();
    Check(config.RefreshIntervalMs == 60000 && config.AutoRefreshEnabled && config.Targets.Count == 0);
    foreach (var value in new[] { 30000, 1800000 })
    {
        config.RefreshIntervalMs = value;
        Check(store.Save(config), store.LastError ?? "保存失败");
        Check(store.Load().RefreshIntervalMs == value);
    }
    foreach (var value in new[] { 29999, 1800001, -1, int.MaxValue })
    {
        config.RefreshIntervalMs = value;
        Check(!store.Save(config) && !string.IsNullOrWhiteSpace(store.LastError));
        Check(store.Load().RefreshIntervalMs == 1800000, "越界写入覆盖了有效配置");
    }
    return Task.CompletedTask;
}

static Task ConfigFailures()
{
    var directory = TempDirectory();
    var store = new ConfigStore(directory);
    File.WriteAllText(store.ConfigPath, "invalid json");
    Check(store.Load().RefreshIntervalMs == 60000 && store.LastError is not null);
    var blockingFile = Path.Combine(directory, "existing-file");
    File.WriteAllText(blockingFile, "not a directory");
    var unwritable = new ConfigStore(Path.Combine(blockingFile, "settings"));
    Check(!unwritable.Save(new()) && unwritable.LastError is not null);
    File.WriteAllText(store.ConfigPath, "{\"RefreshIntervalMs\":3}");
    Check(store.Load().RefreshIntervalMs == 60000 && store.LastError is not null);
    File.WriteAllText(store.ConfigPath, "{\"Targets\":[null,{\"Id\":\"target\",\"DeviceIds\":null,\"DisplayName\":null}]}");
    var normalized = store.Load();
    Check(normalized.Targets.Count == 1 && normalized.Targets[0].DeviceIds.Count == 0 && normalized.Targets[0].DisplayName == "未命名设备");
    return Task.CompletedTask;
}

static Task PersistedHistory()
{
    var path = Path.Combine(TempDirectory(), "state.json");
    var first = NewCache();
    var value = Success("a");
    first.Apply("a", value);
    Check(first.Save(path));
    var second = new DeviceStateCache();
    Check(second.Load(path));
    second.SetTargets([Target()]);
    var state = second.GetSnapshot("a")!;
    Check(state.IsHistorical && state.Connection == ConnectionState.Unknown && state.Status == ReadStatus.Idle);
    Check(state.Battery.Value == 76 && state.LastValidBatteryAt == value.BatterySampledAt);
    File.WriteAllText(path, "[null,{\"TargetId\":\"broken\",\"Battery\":null}]");
    Check(second.Load(path) && !second.GetSnapshot("broken")!.Battery.IsValid);
    return Task.CompletedTask;
}

static async Task NoTargetNoWork()
{
    var backend = new FakeBackend();
    await using var coordinator = new RefreshCoordinator(backend, new());
    Check(!coordinator.RequestRefresh() && !coordinator.RequestRefresh(RefreshReason.Timer));
    await coordinator.WhenIdleAsync();
    Check(backend.Discoveries == 0 && backend.Reads == 0);
}

static async Task CoalescedRequests()
{
    var entered = Signal();
    var release = Signal();
    var backend = new FakeBackend { Read = async (device, call, ct) =>
    {
        if (call == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
        return Success(device.Id);
    }};
    await using var coordinator = new RefreshCoordinator(backend, NewCache());
    coordinator.SetTargets([Target()]);
    coordinator.RequestRefresh();
    await entered.Task;
    Parallel.For(0, 100, _ => coordinator.RequestRefresh(RefreshReason.Manual));
    Check(coordinator.PendingRequestCount == 1);
    release.SetResult();
    await coordinator.WhenIdleAsync();
    Check(backend.Reads == 2 && backend.MaximumActive == 1 && coordinator.PendingRequestCount == 0);
}

static async Task IndependentUpdatesAndChannelLock()
{
    var firstEntered = Signal();
    var release = Signal();
    var independentUpdated = Signal();
    var secondStarted = false;
    var backend = new FakeBackend { Devices = [Device("a", "shared"), Device("b", "shared"), Device("c", "other")], Read = async (device, call, ct) =>
    {
        if (device.Id == "a") { firstEntered.TrySetResult(); await release.Task.WaitAsync(ct); }
        if (device.Id == "b") secondStarted = true;
        return Success(device.Id);
    }};
    var cache = new DeviceStateCache();
    await using var coordinator = new RefreshCoordinator(backend, cache);
    coordinator.SetTargets([Target("a"), Target("b"), Target("c")]);
    coordinator.SnapshotChanged += state => { if (state.TargetId == "c" && state.Status == ReadStatus.Success) independentUpdated.TrySetResult(); };
    coordinator.RequestRefresh();
    await firstEntered.Task;
    await independentUpdated.Task;
    Check(!secondStarted && cache.GetSnapshot("a")!.Status == ReadStatus.Reading);
    release.SetResult();
    await coordinator.WhenIdleAsync();
    Check(secondStarted && backend.Reads == 3);
}

static async Task DuplicateIdentity()
{
    var backend = new FakeBackend();
    await using var coordinator = new RefreshCoordinator(backend, new());
    coordinator.SetTargets([Target("one", "a"), Target("two", "a")]);
    coordinator.RequestRefresh();
    await coordinator.WhenIdleAsync();
    Check(backend.Reads == 1);
}

static async Task LinkedModeEvidence()
{
    foreach (var liveFirst in new[] { false, true })
    {
        var backend = new FakeBackend
        {
            Devices = liveFirst ? [Device("live", "shared"), Device("cache", "shared"), Device("failed", "shared")]
                : [Device("cache", "shared"), Device("live", "shared"), Device("failed", "shared")],
            Read = (device, call, ct) => Task.FromResult(device.Id switch
            {
                "live" => Success(device.Id, 83),
                "cache" => new() { DeviceId = device.Id, Status = ReadStatus.Success, Battery = BatteryReading.Percent(19), IsSystemCache = true, Source = "旧蓝牙缓存" },
                _ => new() { DeviceId = device.Id, Status = ReadStatus.Failed, Error = "旧模式不可访问" }
            })
        };
        var cache = new DeviceStateCache();
        await using var coordinator = new RefreshCoordinator(backend, cache);
        coordinator.SetTargets([Target("linked", "live", "cache", "failed")]);
        coordinator.RequestRefresh();
        await coordinator.WhenIdleAsync();
        var state = cache.GetSnapshot("linked")!;
        Check(backend.Reads == 3 && state.Status == ReadStatus.Success && state.Battery.Value == 83 && !state.IsSystemCache);
    }
}

static async Task StaleSessionResults()
{
    var entered = Signal();
    var release = Signal();
    var observed = new List<int>();
    var context = new QueuedContext();
    var backend = new FakeBackend { Read = async (device, call, ct) =>
    {
        if (call == 1) { entered.TrySetResult(); await release.Task; return Success(device.Id, 90); }
        return Success(device.Id, 20);
    }};
    var cache = new DeviceStateCache();
    await using var coordinator = new RefreshCoordinator(backend, cache, context);
    coordinator.SetTargets([Target()]);
    coordinator.SnapshotChanged += state => { if (state.Status == ReadStatus.Success) observed.Add(state.Battery.Value!.Value); };
    coordinator.RequestRefresh();
    await entered.Task;
    coordinator.RequestRefresh(RefreshReason.DeviceChanged);
    release.SetResult();
    await coordinator.WhenIdleAsync();
    context.Drain();
    Check(cache.GetSnapshot("a")!.Battery.Value == 20 && observed.SequenceEqual([20]));
}

static async Task TimeoutAndRecovery()
{
    var fastPublished = Signal();
    var slowCalls = 0;
    var backend = new FakeBackend { Devices = [Device("slow"), Device("fast")], Read = async (device, call, ct) =>
    {
        if (device.Id == "slow" && Interlocked.Increment(ref slowCalls) == 1) await Task.Delay(Timeout.Infinite, ct);
        return Success(device.Id);
    }};
    var cache = new DeviceStateCache();
    await using var coordinator = new RefreshCoordinator(backend, cache, readTimeout: TimeSpan.FromMilliseconds(60));
    coordinator.SetTargets([Target("slow"), Target("fast")]);
    coordinator.SnapshotChanged += state => { if (state.TargetId == "fast" && state.Status == ReadStatus.Success) fastPublished.TrySetResult(); };
    coordinator.RequestRefresh();
    await fastPublished.Task;
    Check(cache.GetSnapshot("fast")!.Battery.Value == 76);
    await coordinator.WhenIdleAsync();
    Check(cache.GetSnapshot("slow")!.Status == ReadStatus.Timeout && backend.Active == 0);
    coordinator.RequestRefresh();
    await coordinator.WhenIdleAsync();
    Check(cache.GetSnapshot("slow")!.Status == ReadStatus.Success && backend.Active == 0);
}

static async Task RemovalCancels()
{
    var entered = Signal();
    var cancelled = Signal();
    var backend = new FakeBackend { Read = async (device, call, ct) =>
    {
        entered.TrySetResult();
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        return Success(device.Id);
    }};
    var cache = new DeviceStateCache();
    await using var coordinator = new RefreshCoordinator(backend, cache);
    coordinator.SetTargets([Target()]);
    coordinator.RequestRefresh();
    await entered.Task;
    coordinator.RequestRefresh();
    coordinator.SetTargets([]);
    await cancelled.Task;
    await coordinator.WhenIdleAsync();
    Check(!coordinator.RequestRefresh() && backend.Reads == 1 && cache.GetSnapshots().Count == 0);
}

static async Task DisposeWaitsForCleanup()
{
    var entered = Signal();
    var cancelled = Signal();
    var closeHandles = Signal();
    var backend = new FakeBackend { Read = async (device, call, ct) =>
    {
        entered.TrySetResult();
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { cancelled.TrySetResult(); await closeHandles.Task; throw; }
        return Success(device.Id);
    }};
    var coordinator = new RefreshCoordinator(backend, new());
    coordinator.SetTargets([Target()]);
    coordinator.RequestRefresh();
    await entered.Task;
    var dispose = coordinator.DisposeAsync().AsTask();
    await cancelled.Task;
    Check(!dispose.IsCompleted && backend.Active == 1);
    closeHandles.SetResult();
    await dispose;
    Check(backend.Active == 0 && !coordinator.RequestRefresh());
}

static Task DiagnosticsBounded()
{
    var directory = TempDirectory();
    var log = new RollingLog(Path.Combine(directory, "logs"), 4096, 2);
    for (var i = 0; i < 100; i++) log.Write("test", i + new string('x', 200));
    var files = Directory.GetFiles(log.DirectoryPath);
    Check(files.Length <= 3 && files.All(x => new FileInfo(x).Length < 8192));
    var path = Path.Combine(directory, "diagnostic.json");
    DiagnosticExporter.Export(path, new(), new() { Devices = [Device()] }, NewCache().GetSnapshots());
    using var json = JsonDocument.Parse(File.ReadAllText(path));
    Check(json.RootElement.GetProperty("Discovery").GetProperty("Devices").GetArrayLength() == 1);
    return Task.CompletedTask;
}

static async Task BoundedFailureStress()
{
    var backend = new FakeBackend { Read = (device, call, ct) => throw new IOException("预期的设备占用错误") };
    var cache = new DeviceStateCache();
    await using var coordinator = new RefreshCoordinator(backend, cache);
    coordinator.SetTargets([Target()]);
    for (var warmup = 0; warmup < 20; warmup++)
    {
        coordinator.RequestRefresh();
        await coordinator.WhenIdleAsync();
    }
    using var process = System.Diagnostics.Process.GetCurrentProcess();
    process.Refresh();
    var initialThreads = process.Threads.Count;
    var initialHandles = process.HandleCount;
    var initialMemory = process.PrivateMemorySize64;
    for (var i = 0; i < 500; i++)
    {
        coordinator.RequestRefresh();
        await coordinator.WhenIdleAsync();
        Check(coordinator.PendingRequestCount == 0 && backend.Active == 0 && !coordinator.IsRefreshing);
    }
    process.Refresh();
    Console.WriteLine($"  pressure sample (synthetic, 500 cycles): threads {initialThreads}->{process.Threads.Count}; handles {initialHandles}->{process.HandleCount}; private bytes {initialMemory}->{process.PrivateMemorySize64}");
    Check(backend.Reads == 520 && backend.MaximumActive == 1 && cache.GetSnapshot("a")!.Status == ReadStatus.Failed);
    Check(process.Threads.Count <= initialThreads + 16 && process.HandleCount <= initialHandles + 32
        && process.PrivateMemorySize64 <= initialMemory + 64 * 1024 * 1024, "短时异常压力出现异常资源增长");
}

static Task RogProtocolFixtures()
{
    var time = DateTimeOffset.UtcNow;
    var frame = new byte[64]; frame[1] = 0x12; frame[2] = 0x07; frame[5] = 76; frame[10] = 1;
    var result = VendorBatteryQueries.DecodeRogReply("fixture", frame, time)!;
    Check(result.Battery.Value == 76 && result.Battery.Kind == BatteryKind.Percentage);
    Check(result.Charging == ChargingState.Charging && result.BatterySampledAt == time && !result.IsSystemCache);
    Check(VendorBatteryQueries.DecodeRogReply("fixture", frame.AsSpan(0, 10), time) is null);
    frame[5] = 101;
    try { VendorBatteryQueries.DecodeRogReply("fixture", frame, time); throw new Exception("无效电量未拒绝"); }
    catch (InvalidDataException) { }
    frame[1] = 0x13;
    Check(VendorBatteryQueries.DecodeRogReply("fixture", frame, time) is null);
    return Task.CompletedTask;
}

static Task ApexProtocolFixtures()
{
    var time = DateTimeOffset.UtcNow;
    byte[] frame = new byte[30];
    frame[0] = 4; frame[1] = 0x5a; frame[2] = 0xa5; frame[3] = 1; frame[4] = 1; frame[6] = 128; frame[7] = 2;
    for (var charging = 0; charging <= 1; charging++)
    for (var level = 0; level <= 5; level++)
    {
        frame[12] = (byte)(charging * 16 + level);
        var result = VendorBatteryQueries.DecodeApex5Reply("fixture", frame, time)!;
        Check(result.Battery.Kind == BatteryKind.Level && result.Battery.Value == level && result.Battery.Maximum == 5);
        Check(result.Charging == (charging == 0 ? ChargingState.NotCharging : ChargingState.Charging));
        Check(result.Transport == ConnectionTransport.Receiver24Ghz);
    }
    foreach (var invalid in new byte[] { 6, 15, 0x26, 0x20 })
    {
        frame[12] = invalid;
        try { VendorBatteryQueries.DecodeApex5Reply("fixture", frame, time); throw new Exception("未知编码未拒绝"); }
        catch (InvalidDataException) { }
    }
    frame[12] = 3; frame[6] = 127;
    try { VendorBatteryQueries.DecodeApex5Reply("fixture", frame, time); throw new Exception("其他型号未拒绝"); }
    catch (InvalidDataException) { }
    frame[0] = 2;
    Check(VendorBatteryQueries.DecodeApex5Reply("fixture", frame, time) is null);
    return Task.CompletedTask;
}

static Task ProtocolIdentityFixtures()
{
    var named = Device() with { DisplayName = "ROG WOB ZEN65 Flydigi Apex 5" };
    Check(ProtocolCatalog.Classify(named).AdapterId == "windows.device-properties");
    var rog = named with { HidInterfaces = [new() { VendorId = 0x0b05, ProductId = 0x1b63, Path = "test#mi_00", UsagePage = 0xff00 }] };
    Check(ProtocolCatalog.Classify(rog).AdapterId == ProtocolCatalog.RogWired);
    Check(ProtocolCatalog.Classify(rog with { HidInterfaces = [rog.HidInterfaces[0] with { ProductId = 0x1ace }] }).AdapterId != ProtocolCatalog.RogWired);
    Check(ProtocolCatalog.Classify(rog with { HidInterfaces = [rog.HidInterfaces[0] with { Path = "test#mi_01" }] }).AdapterId != ProtocolCatalog.RogWired);
    var apex = named with { HidInterfaces = [new() { VendorId = 0x37d7, ProductId = 0x2501, Path = "test#mi_02", UsagePage = 0xffa0 }] };
    Check(ProtocolCatalog.Classify(apex).AdapterId == ProtocolCatalog.Apex5);
    Check(ProtocolCatalog.Classify(apex with { HidInterfaces = [apex.HidInterfaces[0] with { UsagePage = 1 }] }).AdapterId != ProtocolCatalog.Apex5);
    return Task.CompletedTask;
}

static async Task IsolatedProcessTimeout()
{
    // Tests must run via `dotnet PeripheralPower.Tests.dll`; the backend then invokes this DLL.
    using var backend = new IsolatedHardwareBackend { ReadTimeout = TimeSpan.FromSeconds(1) };
    var marker = Path.Combine(TempDirectory(), "child.pid");
    var device = Device("test-process-hang") with { Properties = new() { ["TestProcessMarker"] = marker } };
    var result = await backend.ReadAsync(device, CancellationToken.None);
    Check(result.Status == ReadStatus.Timeout && File.Exists(marker));
    Check(ProcessExited(int.Parse(File.ReadAllText(marker))), "超时返回时实际helper仍存活");
    var recovered = await backend.ReadAsync(Device("test-process-fast"), CancellationToken.None);
    Check(recovered.Status == ReadStatus.Success && recovered.Battery.Value == 42, "超时后无法恢复有限读取");
}

static Task WobNativeProtocolFixtures()
{
    var allowed = new byte[] { 0x10, 0x11, 0x30, 0x55 };
    for (var command = 0; command <= 255; command++)
    {
        if (!allowed.Contains((byte)command))
        {
            try { WobNativeBatteryQuery.BuildRequest((byte)command); throw new Exception("未知原生命令未拒绝"); }
            catch (ArgumentOutOfRangeException) { }
            continue;
        }
        var frame = WobNativeBatteryQuery.BuildRequest((byte)command);
        Check(frame.Length == 64 && frame[0] == 0xaa && frame[1] == command);
        Check(frame[4] == (command == 0x30 ? 10 : 0));
        var checksum = frame[5] | frame[6] << 8;
        Check(checksum == frame.Where((_, index) => index is >= 1 and < 64 && index is not (5 or 6)).Sum(x => x));
        Check(frame.Where((_, index) => index is not (0 or 1 or 4 or 5 or 6)).All(x => x == 0));
    }
    var reply = new byte[64]; reply[0] = 0xaa; reply[1] = 0x30; reply[7] = 0x55; reply[8] = 67;
    var now = DateTimeOffset.UtcNow;
    // Driver UI and this firmware disagree about the meaning of byte 9. No encoding proves charging.
    for (var status = 0; status <= 255; status++)
    {
        reply[9] = (byte)status;
        var result = WobNativeBatteryQuery.DecodePower("native-fixture", reply, true, now);
        Check(result.Battery.Value == 67 && result.Charging == ChargingState.Unknown && result.BatterySampledAt == now && !result.IsSystemCache);
        Check(result.Source.Contains($"0x{status:X2}") && result.Error.Length == 0);
        Check(result.Connection == ConnectionState.Connected && result.Transport == ConnectionTransport.Receiver24Ghz);
    }
    var offline = WobNativeBatteryQuery.DecodePower("native-fixture", reply, false, now);
    Check(offline.Connection == ConnectionState.Disconnected && !offline.Battery.IsValid && offline.BatterySampledAt is null);
    reply[8] = 101;
    try { WobNativeBatteryQuery.DecodePower("native-fixture", reply, true, now); throw new Exception("电量超出100未拒绝"); }
    catch (InvalidDataException) { }
    reply[7] = 0xfe;
    Check(!WobNativeBatteryQuery.IsSuccessfulReply(reply, 0x30));
    reply[7] = 0x55;
    Check(!WobNativeBatteryQuery.IsSuccessfulReply(reply.AsSpan(0, 63), 0x30));
    Check(!WobNativeBatteryQuery.IsSuccessfulReply(reply, 0x55));
    reply[0] = 0;
    Check(!WobNativeBatteryQuery.IsSuccessfulReply(reply, 0x30));
    var native = new HidInterfaceDescriptor { VendorId = 0x36b0, ProductId = 1, Path = "fixture#mi_01&col02", UsagePage = 0xff87, Usage = 0x20 };
    Check(ProtocolCatalog.IsWobNativeInterface(native));
    foreach (var wrong in new[]
    {
        native with { ProductId = 0x3002 }, native with { VendorId = 0x3554, ProductId = 0xfa09 },
        native with { UsagePage = 0xff60, Usage = 0x61 }, native with { Usage = 0x21 }, native with { Path = "fixture#mi_02&col05" }
    }) Check(!ProtocolCatalog.IsWobNativeInterface(wrong), "跨型号或接口套用原生协议");
    var wired = native with { ProductId = 0x3025, UsagePage = 0xff60, Usage = 0x61 };
    Check(ProtocolCatalog.IsWobNativeWiredInterface(wired));
    foreach (var wrong in new[]
    {
        wired with { ProductId = 0x3002 }, wired with { VendorId = 0x3554, ProductId = 0xfa09 },
        wired with { UsagePage = 0xff87, Usage = 0x20 }, wired with { Usage = 0x62 }, wired with { Path = "fixture#mi_02&col05" }
    }) Check(!ProtocolCatalog.IsWobNativeWiredInterface(wrong), "跨型号或接口套用有线原生协议");
    var device = Device() with { HidInterfaces = [wired], Properties = new() { ["hid:" + wired.Path] = "input=65; output=65; feature=0;" } };
    var classified = ProtocolCatalog.Classify(device);
    Check(classified.AdapterId == ProtocolCatalog.WobNative && classified.Transport == ConnectionTransport.UsbWired);
    foreach (var caps in new[] { "input=33; output=33;", "input=65; output=33;", "input=33; output=65;", "" })
        Check(ProtocolCatalog.Classify(device with { Properties = new() { ["hid:" + wired.Path] = caps } }).AdapterId != ProtocolCatalog.WobNative,
            "未确认双向65字节的接口错误启用原生查询");
    Check(ProtocolCatalog.Classify(device with { Properties = [] }).AdapterId != ProtocolCatalog.WobNative);
    return Task.CompletedTask;
}

static Task OmniProtocolFixtures()
{
    var time = DateTimeOffset.UtcNow;
    var pairing = new byte[64]; pairing[0] = 1; pairing[1] = 0xa0;
    pairing[5] = 0x34; pairing[6] = 0x12; // An unrelated paired entry is insufficient.
    Check(!VendorBatteryQueries.ContainsOmniMini(pairing));
    pairing[9] = 0x65; pairing[10] = 0x1b;
    Check(VendorBatteryQueries.ContainsOmniMini(pairing));
    pairing[5] = pairing[6] = 0;
    Check(!VendorBatteryQueries.ContainsOmniMini(pairing), "0终止后的残留字节不可识别为配对设备");
    try { VendorBatteryQueries.ContainsOmniMini(pairing.AsSpan(0, 63)); throw new Exception("短帧未拒绝"); }
    catch (InvalidDataException) { }
    pairing[1] = 0x12;
    try { VendorBatteryQueries.ContainsOmniMini(pairing); throw new Exception("坏回复头未拒绝"); }
    catch (InvalidDataException) { }

    var frame = new byte[64]; frame[0] = 3; frame[1] = 0x12; frame[2] = 7; frame[5] = 46;
    var result = VendorBatteryQueries.DecodeOmniMiniReply("fixture", frame, time)!;
    Check(result.Status == ReadStatus.Success && result.Battery.Value == 46 && result.Connection == ConnectionState.Connected);
    Check(result.Transport == ConnectionTransport.Receiver24Ghz && result.BatterySampledAt == time && result.Charging == ChargingState.NotCharging);
    frame[5] = 0;
    var sleeping = VendorBatteryQueries.DecodeOmniMiniReply("fixture", frame, time)!;
    Check(!sleeping.Battery.IsValid && sleeping.Connection == ConnectionState.Unknown && sleeping.BatterySampledAt is null);
    Check(sleeping.Status == ReadStatus.Failed);
    frame[5] = 101;
    try { VendorBatteryQueries.DecodeOmniMiniReply("fixture", frame, time); throw new Exception("无效电量未拒绝"); }
    catch (InvalidDataException) { }
    frame[0] = 0;
    Check(VendorBatteryQueries.DecodeOmniMiniReply("fixture", frame, time) is null);
    return Task.CompletedTask;
}

static async Task IsolatedProcessCancellation()
{
    using var backend = new IsolatedHardwareBackend { ReadTimeout = TimeSpan.FromSeconds(5) };
    var marker = Path.Combine(TempDirectory(), "child.pid");
    var device = Device("test-process-hang") with { Properties = new() { ["TestProcessMarker"] = marker } };
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    try { await backend.ReadAsync(device, cancellation.Token); throw new Exception("取消未传播"); }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    Check(File.Exists(marker) && ProcessExited(int.Parse(File.ReadAllText(marker))), "取消返回时实际helper仍存活");
}

static bool ProcessExited(int id)
{
    try { using var process = System.Diagnostics.Process.GetProcessById(id); return process.HasExited; }
    catch (ArgumentException) { return true; }
}

sealed class FakeBackend : IHardwareBackend
{
    public List<string> Errors { get; } = [];
    public List<DeviceDescriptor> Devices { get; init; } = [new() { Id = "a", DisplayName = "a" }];
    public Func<DeviceDescriptor, int, CancellationToken, Task<DeviceReadResult>>? Read { get; init; }
    public int Discoveries;
    public int Reads;
    public int Active;
    public int MaximumActive;
    public Task<DiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Discoveries);
        return Task.FromResult(new DiscoverySnapshot { Devices = Devices, Errors = Errors });
    }
    public async Task<DeviceReadResult> ReadAsync(DeviceDescriptor device, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref Reads);
        var current = Interlocked.Increment(ref Active);
        int prior;
        do { prior = MaximumActive; } while (current > prior && Interlocked.CompareExchange(ref MaximumActive, current, prior) != prior);
        try
        {
            return Read is not null ? await Read(device, call, cancellationToken) : new()
            {
                DeviceId = device.Id, Status = ReadStatus.Success, Battery = BatteryReading.Percent(70), BatterySampledAt = DateTimeOffset.UtcNow
            };
        }
        finally { Interlocked.Decrement(ref Active); }
    }
}

sealed class QueuedContext : SynchronizationContext
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
    public void Drain() { while (_queue.TryDequeue(out var item)) item.Callback(item.State); }
}
