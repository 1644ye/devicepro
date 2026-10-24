using System.IO;
using System.Windows;
using PeripheralPower.App.Native;
using PeripheralPower.Hardware;

namespace PeripheralPower.App;

public partial class App : Application
{
    SingleInstance? _instance;
    public AppController? Controller { get; private set; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => {
            new Core.RollingLog().Write("unhandled", args.Exception.ToString());
            MessageBox.Show("操作未完成：" + args.Exception.Message, "外设电量", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        try {
            int? probeExit = await ProbeRunner.TryRunAsync(e.Args);
            if (probeExit.HasValue) { Shutdown(probeExit.Value); return; }
            if (!SingleInstance.TryAcquire(out _instance)) { SingleInstance.NotifyExistingInstance(); Shutdown(); return; }
            var dataIndex = Array.IndexOf(e.Args, "--data-dir");
            string? data = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataIndex + 1]) : null;
            Controller = new(data);
            bool silent = e.Args.Contains("--startup") || e.Args.Contains("--autostart") || e.Args.Contains("--tray") || e.Args.Contains("--ui-smoke") || e.Args.Contains("--diagnose");
            Controller.Start(silent);
            var smokeIndex = Array.IndexOf(e.Args, "--ui-smoke");
            if (smokeIndex >= 0 && smokeIndex + 1 < e.Args.Length) {
                await Views.UiSmoke.RunAsync(Controller, Path.GetFullPath(e.Args[smokeIndex + 1]));
                await Controller.ExitAsync();
                return;
            }
            var exportIndex = Array.IndexOf(e.Args, "--diagnose");
            if (exportIndex >= 0 && exportIndex + 1 < e.Args.Length) {
                await Controller.ExportAsync(Path.GetFullPath(e.Args[exportIndex + 1]));
                await Controller.ExitAsync();
            }
        } catch (Exception ex) {
            new Core.RollingLog().Write("startup", ex.ToString());
            MessageBox.Show("启动失败：" + ex.Message, "外设电量", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
