using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PeripheralPower.Core;

namespace PeripheralPower.App.Views;

// Explicit developer-only integration mode; it uses real discovery and never injects sample devices.
internal static class UiSmoke
{
    public static async Task RunAsync(AppController controller, string directory)
    {
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        for (int i = 0; i < 20; i++) controller.ShowPanel();
        results.Add(new { Check = "Repeated panel presentation has one window", Passed = Application.Current.Windows.OfType<PanelWindow>().Count() == 1 });
        var panel = Application.Current.Windows.OfType<PanelWindow>().Single();
        panel.Close();
        results.Add(new { Check = "Panel close hides and keeps native host alive", Passed = !panel.IsVisible && controller.Native.Handle != IntPtr.Zero });
        controller.ShowPanel();
        for (int i = 0; i < 20; i++) controller.ShowManagement();
        results.Add(new { Check = "Repeated management presentation has one window", Passed = Application.Current.Windows.OfType<ManagementWindow>().Count() == 1 });
        await controller.DiscoverAsync();
        await controller.Coordinator.WhenIdleAsync();
        var management = Application.Current.Windows.OfType<ManagementWindow>().Single();
        foreach (var d in controller.Discovered.OrderBy(d => d.Device.AdapterId is "atk.holtek.battery" or "vgn.v98pro.evision" ? -1 : DeviceAppearance.FromName(d.Device.DisplayName) switch
            { DeviceShape.EarClip => 0, DeviceShape.InEar => 1, DeviceShape.Mouse => 2, DeviceShape.Keyboard => 3, _ => 4 }).Take(6).ToArray()) controller.AddTarget(d.Device);
        await controller.Coordinator.WhenIdleAsync();
        controller.Config.Theme = AppTheme.Light; controller.ApplySettings();
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(management, Path.Combine(directory, "management-light.png"));
        Capture(panel, Path.Combine(directory, "panel-light.png"));
        controller.ToggleTheme();
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(management, Path.Combine(directory, "management-dark.png"));
        Capture(panel, Path.Combine(directory, "panel-dark.png"));
        var tabs = (System.Windows.Controls.TabControl)management.FindName("ManagementTabs");
        tabs.SelectedIndex = 2;
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(management, Path.Combine(directory, "settings-dark.png"));
        tabs.SelectedIndex = 1;
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(management, Path.Combine(directory, "discovery-dark.png"));
        var initial = controller.Config.RefreshIntervalMs;
        foreach (int interval in new[] { 30000, 60000, 1800000 }) {
            controller.Native.SetRefreshTimer(true, interval);
            results.Add(new { Check = $"Native timer interval {interval}", Passed = controller.Native.RefreshTimerRunning });
        }
        controller.Native.SetRefreshTimer(false, initial);
        results.Add(new { Check = "Automatic timer stops", Passed = !controller.Native.RefreshTimerRunning });
        await controller.ExportAsync(Path.Combine(directory, "diagnostic.json"));
        management.Close();
        results.Add(new { Check = "Management close hides", Passed = !management.IsVisible });
        JsonStorage.WriteAtomic(Path.Combine(directory, "results.json"), results);
    }
    static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) {
            var rect = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(window.Background, null, rect);
            context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, rect);
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
