using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PeripheralPower.App.Native;

/// <summary>One instance per interactive Windows user/session. No network listener or service.</summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string UserKey = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
    private static readonly string MutexName = @"Local\PeripheralPower.Instance." + UserKey;
    private static readonly string ReadyEventName = @"Local\PeripheralPower.Ready." + UserKey;
    internal static string HostWindowTitle => "PeripheralPower.MessageHost." + UserKey;
    internal static readonly IntPtr OpenManagementTag = new(0x50505752);
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _ready;
    private bool _disposed;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName);
        _ready.Reset();
    }

    public static bool TryAcquire(out SingleInstance? instance)
    {
        var mutex = new Mutex(false, MutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            mutex.Dispose();
            instance = null;
            return false;
        }
        try { instance = new SingleInstance(mutex); return true; }
        catch { mutex.ReleaseMutex(); mutex.Dispose(); throw; }
    }

    /// <summary>A finite wait lets an instance still starting finish creating its native host.</summary>
    public static bool NotifyExistingInstance()
    {
        var hwnd = FindWindow(null, HostWindowTitle);
        if (hwnd == IntPtr.Zero)
        {
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName);
            ready.WaitOne(TimeSpan.FromSeconds(4));
            hwnd = FindWindow(null, HostWindowTitle);
        }
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var processId);
        AllowSetForegroundWindow(processId);
        var data = new CopyData { Tag = OpenManagementTag };
        var succeeded = SendMessageTimeout(hwnd, 0x004A, IntPtr.Zero, ref data,
            0x0002, 1500, out var response); // SMTO_ABORTIFHUNG
        return succeeded != IntPtr.Zero && response != IntPtr.Zero;
    }

    internal static void SignalHostReady()
    {
        try
        {
            using var ready = EventWaitHandle.OpenExisting(ReadyEventName);
            ready.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { /* Host can also be instantiated in tests. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready.Reset();
        _ready.Dispose();
        // Mutex ownership is thread-affine; dispose from the startup/UI thread.
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CopyData
    {
        public IntPtr Tag;
        public int ByteCount;
        public IntPtr Data;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, ref CopyData data, uint flags, uint timeoutMs, out IntPtr result);
}
