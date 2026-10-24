using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PeripheralPower.App.Views;

public partial class PanelWindow : Window
{
    private readonly AppController _controller;
    public PanelWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        DataContext = controller.PanelCards;
        controller.PanelCards.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();
        var menu = new ContextMenu();
        Add("管理设备", () => controller.ShowManagement());
        Add("立即刷新", controller.Refresh);
        Add("切换深浅主题", controller.ToggleTheme);
        Add("隐藏到托盘", Hide);
        menu.Items.Add(new Separator());
        Add("退出程序", () => _ = controller.ExitAsync());
        ContextMenu = menu;
        Closing += OnClosing;
        SourceInitialized += (_, _) => WindowGeometry.Restore(this, controller.Config.PanelBounds);
        LocationChanged += (_, _) => SaveBounds();
        SizeChanged += (_, _) => SaveBounds();
        void Add(string name, Action action) { var item = new MenuItem { Header = name }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    }
    void UpdateEmpty() => EmptyButton.Visibility = _controller.PanelCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    void SaveBounds() { if (IsLoaded && WindowState == WindowState.Normal) _controller.SavePanelBounds(WindowGeometry.Capture(this)); }
    void OnClosing(object? sender, CancelEventArgs e) { if (!_controller.IsExiting) { e.Cancel = true; SaveBounds(); Hide(); } }
    void OpenManagement(object sender, RoutedEventArgs e) => _controller.ShowManagement();
    void DragHeader(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 1 && e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}
