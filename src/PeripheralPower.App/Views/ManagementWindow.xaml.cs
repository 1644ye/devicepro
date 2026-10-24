using System.Windows;
using System.Windows.Controls;
using PeripheralPower.Core;
using PeripheralPower.App.Native;

namespace PeripheralPower.App.Views;

public partial class ManagementWindow : Window
{
    readonly AppController _controller;
    string? _selectedTarget;
    public string? SelectedTargetId => _selectedTarget;
    public void ShowSettingsPage() => ManagementTabs.SelectedItem = SettingsTab;
    public bool IsSettingsPageSelected => ManagementTabs.SelectedItem == SettingsTab;
    public void RestoreSelection(string? id) { if (id != null) TargetsList.SelectedItem = _controller.AllCards.FirstOrDefault(c => c.Id == id); }
    public ManagementWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        TargetsList.ItemsSource = controller.AllCards;
        DiscoveryList.ItemsSource = controller.Discovered;
        Closing += (_, e) => { if (!controller.IsExiting) { e.Cancel = true; Hide(); } };
        SourceInitialized += (_, _) => WindowGeometry.EnsureVisible(this);
        Activated += (_, _) => UpdateStartupStatus();
        AutoRefreshBox.IsChecked = controller.Config.AutoRefreshEnabled;
        IntervalBox.Text = controller.Config.RefreshIntervalMs.ToString();
        foreach (ComboBoxItem item in IntervalPreset.Items) if (item.Tag?.ToString() == IntervalBox.Text) { IntervalPreset.SelectedItem = item; break; }
        LaunchBox.SelectedIndex = (int)controller.Config.StartupView;
        ThemeBox.SelectedIndex = controller.Config.Theme == AppTheme.Dark ? 1 : 0;
        UpdateStartupStatus();
    }
    void ShowPanel(object sender, RoutedEventArgs e) => _controller.ShowPanel();
    void Refresh(object sender, RoutedEventArgs e) => _controller.Refresh();
    void HideToTray(object sender, RoutedEventArgs e) => Hide();
    async void Discover(object sender, RoutedEventArgs e) { DiscoveryStatus.Text = "正在发现…"; try { await _controller.DiscoverAsync(); DiscoveryStatus.Text = $"发现 {_controller.Discovered.Count} 个设备组"; } catch (Exception ex) { DiscoveryStatus.Text = ex.Message; } }
    void DiscoveredSelected(object sender, SelectionChangedEventArgs e) => DiscoveryDetail.Text = (DiscoveryList.SelectedItem as DiscoveryItem)?.Detail ?? "";
    void AddTarget(object sender, RoutedEventArgs e) { if (DiscoveryList.SelectedItem is DiscoveryItem item) { _controller.AddTarget(item.Device); DiscoveryStatus.Text = "已加入我的设备"; } }
    void LinkTarget(object sender, RoutedEventArgs e) { if (DiscoveryList.SelectedItem is DiscoveryItem item && _selectedTarget != null) { _controller.LinkTarget(_selectedTarget, item.Device.Id); DiscoveryStatus.Text = "已关联到当前目标"; } else DiscoveryStatus.Text = "请先在我的设备中选中一个目标"; }
    void TargetSelected(object sender, SelectionChangedEventArgs e) { if (TargetsList.SelectedItem is CardItem c) { _selectedTarget = c.Id; RenameBox.Text = c.Snapshot.DisplayName; TargetDetail.Text = c.Detail; } }
    void Rename(object sender, RoutedEventArgs e) { if (_selectedTarget != null && !string.IsNullOrWhiteSpace(RenameBox.Text)) _controller.EditTarget(_selectedTarget, t => t with { DisplayName = RenameBox.Text.Trim() }); }
    void ToggleHidden(object sender, RoutedEventArgs e) { if (_selectedTarget != null) _controller.EditTarget(_selectedTarget, t => t with { Hidden = !t.Hidden }); }
    void MoveUp(object sender, RoutedEventArgs e) { if (_selectedTarget != null) _controller.MoveTarget(_selectedTarget, -1); }
    void MoveDown(object sender, RoutedEventArgs e) { if (_selectedTarget != null) _controller.MoveTarget(_selectedTarget, 1); }
    void Remove(object sender, RoutedEventArgs e) { if (_selectedTarget != null) _controller.RemoveTarget(_selectedTarget); }
    void PresetChanged(object sender, SelectionChangedEventArgs e) { if (IntervalBox != null && IntervalPreset.SelectedItem is ComboBoxItem item) IntervalBox.Text = item.Tag?.ToString(); }
    void UpdateStartupStatus()
    {
        var status = StartupService.GetStatus();
        StartupBox.IsChecked = status.Enabled;
        StartupStatus.Text = !string.IsNullOrEmpty(status.Error) ? status.Error : (status.DisabledByWindows ? "已注册，但被 Windows 启动应用设置禁用" : status.Enabled ? "实际状态：已启用，登录后仅显示托盘" : "实际状态：未启用");
    }
    void SaveSettings(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IntervalBox.Text, out int interval) || interval < 30000 || interval > 1800000) { SettingsStatus.Text = "刷新周期必须为 30000～1800000 毫秒的整数。"; return; }
        try {
            _controller.Config.AutoRefreshEnabled = AutoRefreshBox.IsChecked == true;
            _controller.Config.RefreshIntervalMs = interval;
            _controller.Config.StartupView = (StartupView)Math.Max(0, LaunchBox.SelectedIndex);
            _controller.Config.Theme = ThemeBox.SelectedIndex == 1 ? AppTheme.Dark : AppTheme.Light;
            var result = StartupService.SetEnabled(StartupBox.IsChecked == true);
            _controller.ApplySettings();
            UpdateStartupStatus();
            SettingsStatus.Text = !string.IsNullOrEmpty(result.Error) ? "刷新设置已保存；自启：" + result.Error : "设置已保存，刷新周期已立即生效。";
        } catch (Exception ex) { SettingsStatus.Text = "保存失败：" + ex.Message; }
    }
    async void Export(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"外设诊断-{DateTime.Now:yyyyMMdd-HHmmss}.json", Filter = "JSON 诊断|*.json" };
        if (dialog.ShowDialog(this) == true) { try { await _controller.ExportAsync(dialog.FileName); SettingsStatus.Text = "已导出到：" + dialog.FileName; } catch (Exception ex) { SettingsStatus.Text = ex.Message; } }
    }
    void OpenDataFolder(object sender, RoutedEventArgs e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_controller.DataDirectory) { UseShellExecute = true });
    async void ExitApp(object sender, RoutedEventArgs e) => await _controller.ExitAsync();
}
