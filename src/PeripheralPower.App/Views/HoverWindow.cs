using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using PeripheralPower.App.Native;

namespace PeripheralPower.App.Views;

public sealed class HoverWindow : Window
{
    readonly TextBlock _text;
    public HoverWindow()
    {
        AllowsTransparency = true; WindowStyle = WindowStyle.None; Background = Brushes.Transparent;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true; SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 600; MaxHeight = Math.Min(900, SystemParameters.WorkArea.Height - 40);
        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 540 };
        _text.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var border = new Border { Padding = new Thickness(14), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = _text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        border.SetResourceReference(Border.BackgroundProperty, "CardBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); Content = border;
    }
    public void ShowSummary(TrayHoverEventArgs args)
    {
        _text.Text = args.Text;
        if (!IsVisible) Show();
        WindowGeometry.PlaceTooltip(this, args.ScreenX, args.ScreenY);
    }
    public bool ContainsScreenPoint(int x, int y)
    {
        if (!IsVisible) return false;
        var topLeft = PointToScreen(new Point(0, 0));
        var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
        return x >= topLeft.X && x <= bottomRight.X && y >= topLeft.Y && y <= bottomRight.Y;
    }
}
