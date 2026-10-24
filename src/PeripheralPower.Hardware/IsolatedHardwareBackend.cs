using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PeripheralPower.Core;

namespace PeripheralPower.Hardware;

public sealed record HardwareMetrics(double TotalChildCpuSeconds,long CompletedProbeCount,int ActiveProbeCount,long PeakChildWorkingSetBytes,long TimeoutCount);

/// <summary>Disposable process boundary for APIs that cannot reliably cancel a misbehaving driver.</summary>
public sealed class IsolatedHardwareBackend : IHardwareBackend, IDisposable
{
    private readonly SemaphoreSlim _slots=new(3,3);
    private readonly SemaphoreSlim[] _channels=Enumerable.Range(0,64).Select(_=>new SemaphoreSlim(1,1)).ToArray();
    private readonly SemaphoreSlim _discovery=new(1,1);
    private readonly CancellationTokenSource _shutdown=new();
    private readonly string _executable;
    private readonly string? _assembly;
    private readonly ConcurrentDictionary<int,Process> _children=new();
    private string? _fault;
    private long _childCpuTicks,_completedProbes,_peakChildBytes,_timeouts;
    public HardwareMetrics Metrics => new(TimeSpan.FromTicks(Interlocked.Read(ref _childCpuTicks)).TotalSeconds,
        Interlocked.Read(ref _completedProbes),_children.Count,Interlocked.Read(ref _peakChildBytes),Interlocked.Read(ref _timeouts));
    public TimeSpan ReadTimeout {get;init;}=TimeSpan.FromSeconds(5);
    public TimeSpan DiscoveryTimeout {get;init;}=TimeSpan.FromSeconds(12);

    public IsolatedHardwareBackend(string? executablePath=null)
    {
        _executable=executablePath??Environment.ProcessPath??throw new InvalidOperationException("无法确定探测程序路径");
        if(Path.GetFileNameWithoutExtension(_executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
            _assembly=Assembly.GetEntryAssembly()?.Location;
    }
    public async Task<DiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_shutdown.Token);
        await _discovery.WaitAsync(linked.Token).ConfigureAwait(false);
        int locked=0;
        try
        {
            // Discovery includes known receiver identity queries. Exclude device reads while it runs.
            foreach(var channel in _channels) {await channel.WaitAsync(linked.Token).ConfigureAwait(false);locked++;}
            return await RunAsync<DiscoverySnapshot>(null,DiscoveryTimeout,linked.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested){throw;}
        catch(Exception e){return new DiscoverySnapshot{Errors=[$"设备发现失败：{e.Message}"]};}
        finally {for(int i=0;i<locked;i++)_channels[i].Release();_discovery.Release();}
    }
    public async Task<DeviceReadResult> ReadAsync(DeviceDescriptor device,CancellationToken cancellationToken)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_shutdown.Token);
        // Fixed striping avoids retaining one semaphore for every historical device identity.
        var gate=_channels[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(device.ChannelKey.Length>0?device.ChannelKey:device.Id)%64];
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try {return await RunAsync<DeviceReadResult>(device,ReadTimeout,linked.Token).ConfigureAwait(false);}
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested){throw;}
        catch(Exception e){return new DeviceReadResult{DeviceId=device.Id,Status=e is TimeoutException?ReadStatus.Timeout:ReadStatus.Failed,
            Transport=device.Transport,Source="隔离探测进程",Error=e.Message};}
        finally {gate.Release();}
    }

    private async Task<T> RunAsync<T>(DeviceDescriptor? device,TimeSpan timeout,CancellationToken token)
    {
        if(_fault is not null) throw new IOException(_fault);
        await _slots.WaitAsync(token).ConfigureAwait(false);
        string dir=Path.Combine(Path.GetTempPath(),"PeripheralPower","probe-"+Guid.NewGuid().ToString("N"));
        Process? process=null;
        try
        {
            if(_fault is not null) throw new IOException(_fault);
            Directory.CreateDirectory(dir);
            string input=Path.Combine(dir,"input.json"),output=Path.Combine(dir,"output.json");
            var start=new ProcessStartInfo(_executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
            if(_assembly is not null) start.ArgumentList.Add(_assembly);
            start.ArgumentList.Add(device is null?"--probe-discover":"--probe-read");
            if(device is not null){await File.WriteAllTextAsync(input,JsonSerializer.Serialize(device,ProbeRunner.JsonOptions),token);start.ArgumentList.Add(input);}
            start.ArgumentList.Add(output);
            using var limit=CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(timeout);
            process=Process.Start(start)??throw new IOException("无法启动隔离探测进程");
            _children[process.Id]=process;
            try {await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);}
            catch(OperationCanceledException)
            {
                await TerminateAsync(process).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _timeouts);
                throw new TimeoutException($"硬件查询超过 {timeout.TotalSeconds:0.#} 秒；探测进程已终止");
            }
            if(process.ExitCode!=0) throw new IOException($"探测进程退出码 {process.ExitCode}");
            if(!File.Exists(output) || new FileInfo(output).Length>8*1024*1024) throw new IOException("探测输出缺失或过大");
            return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(output,token),ProbeRunner.JsonOptions)
                ?? throw new InvalidDataException("探测结果无效");
        }
        finally
        {
            try
            {
                if(process is not null)
                {
                    try {if(!process.HasExited) await TerminateAsync(process).ConfigureAwait(false);}
                    finally
                    {
                        try
                        {
                            Interlocked.Add(ref _childCpuTicks,process.TotalProcessorTime.Ticks);
                            long peak=process.PeakWorkingSet64,previous=Interlocked.Read(ref _peakChildBytes);
                            while(peak>previous) {long observed=Interlocked.CompareExchange(ref _peakChildBytes,peak,previous);if(observed==previous)break;previous=observed;}
                        }
                        catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
                        Interlocked.Increment(ref _completedProbes);
                        _children.TryRemove(process.Id,out _); process.Dispose();
                    }
                }
            }
            finally
            {
                try{if(Directory.Exists(dir)) Directory.Delete(dir,true);}catch(IOException){}catch(UnauthorizedAccessException){}
                _slots.Release();
            }
        }
    }
    private async Task TerminateAsync(Process process)
    {
        if(process.HasExited) return;
        try {process.Kill(true);}catch(InvalidOperationException){return;}catch(Exception ex){_fault=$"无法终止探测进程，硬件查询停用直到重启：{ex.Message}";throw new IOException(_fault,ex);}
        using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try {await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);}
        catch(OperationCanceledException){_fault="探测进程终止后仍未退出，硬件查询停用直到重启";throw new IOException(_fault);}
    }
    public void Dispose()
    {
        _shutdown.Cancel();
        foreach(var child in _children.Values) try{if(!child.HasExited)child.Kill(true);}catch(Exception ex){_fault=ex.Message;}
        // Do not dispose semaphores while an in-flight cancellation continuation is releasing them.
    }
}
