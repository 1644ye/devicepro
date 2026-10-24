param(
    [string]$Executable = "$PSScriptRoot\..\artifacts\win-x64\PeripheralPower.exe",
    [string]$DiscoveryFile = "$PSScriptRoot\..\artifacts\actual-discovery.json",
    [string]$ProfileDirectory = "$PSScriptRoot\..\artifacts\perf-profile",
    [string]$OutputFile = "$PSScriptRoot\..\artifacts\performance-results.json",
    [ValidateRange(1, 3600)][int]$PhaseSeconds = 600,
    [ValidateRange(1, 60)][int]$SampleSeconds = 5,
    [ValidateRange(1, 600)][int]$NoTargetControlSeconds = 30,
    [ValidateRange(0, 60)][int]$WarmupSeconds = 10,
    [ValidateRange(1, 20)][int]$MaximumTargets = 3
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$ProfileDirectory = [IO.Path]::GetFullPath($ProfileDirectory)
$OutputFile = [IO.Path]::GetFullPath($OutputFile)
$progressFile = [IO.Path]::ChangeExtension($OutputFile, 'progress.json')
$samplesFile = [IO.Path]::ChangeExtension($OutputFile, 'samples.json')
$logicalProcessors = [Environment]::ProcessorCount

if (Test-Path -LiteralPath $ProfileDirectory) { throw "Performance profile already exists: $ProfileDirectory. Choose a fresh directory to preserve previous evidence." }
New-Item -ItemType Directory -Path $ProfileDirectory -Force | Out-Null
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($OutputFile)) -Force | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace PeripheralPowerPerformance {
 public static class Native {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls,string title);
  public static IntPtr FindHost(string title) { return FindWindow(null,title); }
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
  [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp,uint flags,uint timeout,out IntPtr result);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
  [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int max);
  delegate bool EnumWindowCallback(IntPtr hwnd,IntPtr data);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowCallback callback,IntPtr data);
  [StructLayout(LayoutKind.Sequential)] struct IconIdentifier {public uint Size;public IntPtr Window;public uint Id;public Guid Guid;}
  [StructLayout(LayoutKind.Sequential)] struct IconRect {public int Left,Top,Right,Bottom;}
  [DllImport("shell32.dll",EntryPoint="Shell_NotifyIconGetRect")] static extern int IconRectFor(ref IconIdentifier icon,out IconRect rect);
  public static uint FirstTrayIconId(IntPtr hwnd) {
   // Once per phase transition, locate an actual icon. ID 1 is only the no-reading
   // fallback in the multi-icon application; device icons have different stable IDs.
   for(uint id=1;id<=ushort.MaxValue;id++){var icon=new IconIdentifier{Size=(uint)Marshal.SizeOf(typeof(IconIdentifier)),Window=hwnd,Id=id};IconRect rect;if(IconRectFor(ref icon,out rect)==0)return id;}
   throw new InvalidOperationException("No registered application tray icon is available.");
  }
  public static string[] VisibleTitles(int processId) {
   var titles=new List<string>();
   EnumWindows((hwnd,data)=>{uint p;GetWindowThreadProcessId(hwnd,out p);if(p==processId&&IsWindowVisible(hwnd)){var text=new StringBuilder(512);GetWindowText(hwnd,text,text.Capacity);titles.Add(text.ToString());}return true;},IntPtr.Zero);
   return titles.ToArray();
  }
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct ProcessEntry {
   public uint Size,Usage,Id;public UIntPtr Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;
   [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Exe;
  }
  [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint id);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="Process32FirstW")] static extern bool Process32First(IntPtr h,ref ProcessEntry entry);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="Process32NextW")] static extern bool Process32Next(IntPtr h,ref ProcessEntry entry);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  public static int[] ChildIds(int parent) {
   var result=new List<int>();var h=CreateToolhelp32Snapshot(2,0);if(h==new IntPtr(-1))return result.ToArray();
   try{var e=new ProcessEntry{Size=(uint)Marshal.SizeOf(typeof(ProcessEntry))};if(Process32First(h,ref e))do{if(e.Parent==parent)result.Add((int)e.Id);}while(Process32Next(h,ref e));}
   finally{CloseHandle(h);}return result.ToArray();
  }
 }
}
'@

function Send-AppMessage([IntPtr]$Handle, [uint32]$Message, [Int64]$WParam = 0, [Int64]$LParam = 0) {
    [IntPtr]$response = [IntPtr]::Zero
    $ok = [PeripheralPowerPerformance.Native]::SendMessageTimeout($Handle, $Message, [IntPtr]::new($WParam), [IntPtr]::new($LParam), 2, 2000, [ref]$response)
    if ($ok -eq [IntPtr]::Zero) { throw "Application did not respond to native message $Message." }
    return $response.ToInt64()
}
function Get-AppStatus([IntPtr]$Handle) {
    $status = Send-AppMessage $Handle ([PeripheralPowerPerformance.Native]::RegisterWindowMessage('PeripheralPower.QueryNativeStatus'))
    if (($status -band 0xFFFF0000L) -ne 0x50500000L) { throw 'Executable does not implement the native diagnostics status message; rebuild the current source.' }
    return [ordered]@{ TimerRunning = (($status -band 1) -ne 0); Suspended = (($status -band 2) -ne 0); RichHover = (($status -band 4) -ne 0) }
}
function New-Profile([string]$Directory, [object[]]$Devices) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $targets = @(); $order = 0
    foreach ($device in $Devices) {
        $targets += [ordered]@{ Id = [Guid]::NewGuid().ToString('N'); DeviceIds = @([string]$device.Id); DisplayName = [string]$device.DisplayName; Hidden = $false; Order = $order }
        $order++
    }
    [ordered]@{ SchemaVersion = 1; Targets = @($targets); AutoRefreshEnabled = $true; RefreshIntervalMs = 60000; StartupView = 'Tray'; Theme = 'Light' } |
        ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $Directory 'config.json') -Encoding utf8
}
function Start-App([string]$Directory) {
    $hostTitle = 'PeripheralPower.MessageHost.' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ([PeripheralPowerPerformance.Native]::FindHost($hostTitle) -ne [IntPtr]::Zero) { throw 'An existing application instance is running; close it before measuring.' }
    $app = Start-Process -FilePath $Executable -ArgumentList @('--tray', '--data-dir', ('"{0}"' -f $Directory)) -WindowStyle Hidden -PassThru
    try {
    $wait = [Diagnostics.Stopwatch]::StartNew(); $handle = [IntPtr]::Zero
    do {
        $app.Refresh()
        if ($app.HasExited) { throw "Application exited before measurement (code $($app.ExitCode)); close any other instance." }
        $handle = [PeripheralPowerPerformance.Native]::FindHost($hostTitle)
        if ($handle -ne [IntPtr]::Zero) {
            [uint32]$ownerId = 0
            [void][PeripheralPowerPerformance.Native]::GetWindowThreadProcessId($handle, [ref]$ownerId)
            if ($ownerId -ne $app.Id) { throw 'A different application instance owns the message host; stop it before measuring.' }
            break
        }
        Start-Sleep -Milliseconds 100
    } while ($wait.Elapsed.TotalSeconds -lt 15)
    if ($handle -eq [IntPtr]::Zero) { throw 'Native message host did not become ready.' }
    return [pscustomobject]@{ Process = $app; Handle = $handle; Directory = $Directory }
    } catch {
        # Only the process started by this measurement is eligible for cleanup.
        $app.Refresh()
        if (!$app.HasExited) { $app.Kill($true); [void]$app.WaitForExit(5000) }
        $app.Dispose()
        throw
    }
}
function Stop-App($Session) {
    if ($null -eq $Session) { return }
    $app = $Session.Process
    try {
        $app.Refresh()
        if (!$app.HasExited) {
            # Deliver WM_ENDSESSION only to our own host; it exercises normal cancellation/exit.
            [void](Send-AppMessage $Session.Handle 0x0016 1)
            if (!$app.WaitForExit(15000)) { $app.Kill($true); $app.WaitForExit(5000) | Out-Null }
        }
    } finally { $app.Dispose() }
}
function Get-Sample($Session, [string]$Phase, [double]$Elapsed) {
    $app = $Session.Process; $app.Refresh()
    if ($app.HasExited) { throw "Application exited during $Phase (code $($app.ExitCode))." }
    $children = @()
    foreach ($childId in [PeripheralPowerPerformance.Native]::ChildIds($app.Id)) {
        try {
            $child = Get-Process -Id $childId -ErrorAction Stop
            $children += [ordered]@{ Id = $childId; CpuSeconds = $child.TotalProcessorTime.TotalSeconds; WorkingSetMB = $child.WorkingSet64 / 1MB; PrivateMB = $child.PrivateMemorySize64 / 1MB }
            $child.Dispose()
        } catch { # Short-lived probes can exit between process snapshot and metric read.
        }
    }
    $state = Get-AppStatus $Session.Handle
    $hardwareMetrics = $null
    $metricsPath = Join-Path $Session.Directory 'hardware-metrics.json'
    if (Test-Path -LiteralPath $metricsPath) {
        try { $hardwareMetrics = Get-Content -LiteralPath $metricsPath -Raw | ConvertFrom-Json } catch { }
    }
    return [pscustomobject][ordered]@{
        Phase = $Phase; Timestamp = [DateTimeOffset]::Now.ToString('o'); ElapsedSeconds = $Elapsed
        ProcessId = $app.Id; MainCpuSeconds = $app.TotalProcessorTime.TotalSeconds
        WorkingSetMB = $app.WorkingSet64 / 1MB; PrivateMB = $app.PrivateMemorySize64 / 1MB
        Threads = $app.Threads.Count; Handles = $app.HandleCount
        TimerRunning = $state.TimerRunning; Suspended = $state.Suspended
        VisibleTitles = @([PeripheralPowerPerformance.Native]::VisibleTitles($app.Id))
        ActiveHelpersAtSample = $children
        CumulativeHardwareMetrics = $hardwareMetrics
    }
}
$allSamples = [Collections.Generic.List[object]]::new()
function Measure-Phase($Session, [string]$Name, [int]$Seconds) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $phaseSamples = [Collections.Generic.List[object]]::new()
    $first = Get-Sample $Session $Name 0; $phaseSamples.Add($first); $allSamples.Add($first)
    do {
        $remaining = $Seconds - $watch.Elapsed.TotalSeconds
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int]([Math]::Min($SampleSeconds, $remaining) * 1000)) }
        $sample = Get-Sample $Session $Name $watch.Elapsed.TotalSeconds
        $phaseSamples.Add($sample); $allSamples.Add($sample)
        [ordered]@{ Phase = $Name; PhaseElapsedSeconds = $watch.Elapsed.TotalSeconds; PhaseDurationSeconds = $Seconds; ProcessId = $Session.Process.Id; LastSample = $sample } |
            ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $progressFile -Encoding utf8
        $allSamples | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $samplesFile -Encoding utf8
    } while ($watch.Elapsed.TotalSeconds -lt $Seconds)
    $last = $phaseSamples[$phaseSamples.Count - 1]
    $elapsed = $last.ElapsedSeconds
    $cpu = $last.MainCpuSeconds - $first.MainCpuSeconds
    $helperCpu = $null; $completedProbes = $null
    if ($null -ne $first.CumulativeHardwareMetrics -and $null -ne $last.CumulativeHardwareMetrics) {
        $helperCpu = $last.CumulativeHardwareMetrics.TotalChildCpuSeconds - $first.CumulativeHardwareMetrics.TotalChildCpuSeconds
        $completedProbes = $last.CumulativeHardwareMetrics.CompletedProbeCount - $first.CumulativeHardwareMetrics.CompletedProbeCount
    }
    $stats = [ordered]@{}
    foreach ($metric in @('WorkingSetMB', 'PrivateMB', 'Threads', 'Handles')) {
        $values = @($phaseSamples | ForEach-Object { $_.$metric })
        $measure = $values | Measure-Object -Minimum -Maximum -Average
        $stats[$metric] = [ordered]@{ First = $values[0]; Last = $values[-1]; Average = $measure.Average; Minimum = $measure.Minimum; Maximum = $measure.Maximum; NetChange = $values[-1] - $values[0] }
    }
    return [ordered]@{
        Name = $Name; Seconds = $elapsed; SampleCount = $phaseSamples.Count; ProcessId = $Session.Process.Id
        MainCpuSeconds = $cpu; AverageMainCpuPercentMachine = 100 * $cpu / $elapsed / $logicalProcessors
        AverageMainCpuPercentSingleCore = 100 * $cpu / $elapsed
        HelperCpuSeconds = $helperCpu; CompletedProbesDuringPhase = $completedProbes
        AverageMainAndHelperCpuPercentMachine = $(if ($null -ne $helperCpu) { 100 * ($cpu + $helperCpu) / $elapsed / $logicalProcessors } else { $null })
        AverageMainAndHelperCpuPercentSingleCore = $(if ($null -ne $helperCpu) { 100 * ($cpu + $helperCpu) / $elapsed } else { $null })
        HardwareMetricsAtStart = $first.CumulativeHardwareMetrics; HardwareMetricsAtEnd = $last.CumulativeHardwareMetrics
        Metrics = $stats; TimerRunningAtStart = $first.TimerRunning; TimerRunningAtEnd = $last.TimerRunning
        VisibleTitlesAtStart = $first.VisibleTitles; VisibleTitlesAtEnd = $last.VisibleTitles
    }
}

$started = [DateTimeOffset]::Now
$devices = @()
if (Test-Path -LiteralPath $DiscoveryFile) {
    $discovery = Get-Content -LiteralPath $DiscoveryFile -Raw | ConvertFrom-Json
    # This uses only genuinely enumerated stable IDs; no synthetic runtime device is introduced.
    if ($null -ne $discovery.Devices) {
        $validDevices = @($discovery.Devices | Where-Object { ![string]::IsNullOrWhiteSpace($_.Id) })
        # Prefer a positively matched, already enumerated vendor adapter. Generic cache
        # targets remain real identities and are never treated as protocol matches.
        $devices = @($validDevices | Sort-Object @{ Expression = { if ($_.AdapterId -like '*rog*' -or $_.AdapterId -like '*asus*') { 0 } elseif ($_.AdapterId -eq 'windows.device-properties') { 2 } else { 1 } } } | Select-Object -First $MaximumTargets)
    }
} else { throw "Real discovery evidence is missing: $DiscoveryFile" }
$control = $null; $monitored = $null; $results = @(); $failure = $null
try {
    $emptyProfile = Join-Path $ProfileDirectory 'no-target'
    New-Profile $emptyProfile @()
    $control = Start-App $emptyProfile
    Start-Sleep -Seconds $WarmupSeconds
    $results += Measure-Phase $control 'NoTargetControl' $NoTargetControlSeconds
    if ($results[-1].TimerRunningAtStart -or $results[-1].TimerRunningAtEnd) { throw 'No-target control left the periodic timer running.' }
    Stop-App $control; $control = $null
    $monitoredProfile = Join-Path $ProfileDirectory 'monitored'
    New-Profile $monitoredProfile $devices
    $monitored = Start-App $monitoredProfile
    Start-Sleep -Seconds $WarmupSeconds
    $results += Measure-Phase $monitored 'Tray' $PhaseSeconds
    # Version 4 NIN_SELECT is the canonical left-click route to the panel.
    # Right-click WM_CONTEXTMENU now opens global settings and must not be used here.
    $panelIconId = [PeripheralPowerPerformance.Native]::FirstTrayIconId($monitored.Handle)
    [void](Send-AppMessage $monitored.Handle (0x8000 + 41) 0 (($panelIconId -shl 16) -bor 0x0400))
    Start-Sleep -Seconds $WarmupSeconds
    $results += Measure-Phase $monitored 'Panel' $PhaseSeconds
    if (@($results[-1].VisibleTitlesAtStart | Where-Object { $_ -like '我的设备*' }).Count -lt 1) { throw 'Panel phase did not have the visible compact device panel.' }
} catch { $failure = $_.ToString() }
finally {
    Stop-App $control
    Stop-App $monitored
    $helperMetricsPath = Join-Path $ProfileDirectory 'monitored\hardware-metrics.json'
    $helperMetrics = if (Test-Path -LiteralPath $helperMetricsPath) { Get-Content -LiteralPath $helperMetricsPath -Raw | ConvertFrom-Json } else { $null }
    [ordered]@{
        Started = $started.ToString('o'); Finished = [DateTimeOffset]::Now.ToString('o'); Error = $failure
        Executable = $Executable; ExecutableSHA256 = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
        OS = [Environment]::OSVersion.ToString(); LogicalProcessorCount = $logicalProcessors
        Conditions = 'Published Windows x64 application; isolated new profiles; default 60000ms refresh; 5s external sampler by default. Tray and Panel use one unchanged main process.'
        CpuScope = 'Main-process CPU and cumulative helper CPU are separate, with their sum reported per phase when both endpoint metrics exist. Machine percentage = CPU seconds / wall seconds / logical processors * 100; single-core percentage is also provided. Sampler CPU is excluded; handling native status requests is included in main CPU.'
        HelpersScope = 'Active helper memory in samples is an instantaneous observation and misses helpers between samples. Cumulative helper CPU is read from application metrics and differenced at phase endpoints. Exported peak child memory is a lifetime peak, not a simultaneous main+helper average.'
        TargetSource = [IO.Path]::GetFullPath($DiscoveryFile); MonitoredTargets = @($devices); TargetCount = $devices.Count
        HardwareValidation = 'Only devices in the real Windows discovery output are selected. This measures the available environment; it does not verify physical battery accuracy or absent models.'
        Phases = $results; HelperMetrics = $helperMetrics
    } | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $OutputFile -Encoding utf8
    $allSamples | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $samplesFile -Encoding utf8
}
if ($null -ne $failure) { throw $failure }
Write-Output "Performance results: $OutputFile"
