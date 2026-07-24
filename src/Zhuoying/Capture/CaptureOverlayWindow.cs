using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Zhuoying.Platform;

namespace Zhuoying.Capture;

/// <summary>
/// 覆盖单个显示器的截屏窗口：冻结帧背景 + 选区层 + 工具条。
/// </summary>
public sealed class CaptureOverlayWindow : Window
{
    private readonly MonitorInfo _monitor;
    private readonly Action<PixelRect> _onCopy;
    private readonly SelectionLayer _selectionLayer;
    private readonly Border _toolbar;
    private readonly Canvas _toolbarCanvas;

    public CaptureOverlayWindow(WriteableBitmap frame, MonitorInfo monitor, Action<PixelRect> onCopy)
    {
        _monitor = monitor;
        _onCopy = onCopy;

        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = new Cursor(StandardCursorType.Cross);

        Position = monitor.Bounds.TopLeft;
        Width = monitor.Bounds.Width / monitor.Scaling;
        Height = monitor.Bounds.Height / monitor.Scaling;

        var image = new Image { Source = frame, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);

        _selectionLayer = new SelectionLayer();
        _selectionLayer.DragStarted += () => _toolbar!.IsVisible = false;
        _selectionLayer.DragCompleted += _ => PositionToolbar();
        _selectionLayer.DoubleTapped += (_, _) => CopySelection();

        _toolbar = BuildToolbar();
        _toolbarCanvas = new Canvas();
        _toolbarCanvas.Children.Add(_toolbar);

        Content = new Panel { Children = { image, _selectionLayer, _toolbarCanvas } };

        Opened += OnOpened;
        KeyDown += OnKeyDownHandler;
        PointerPressed += OnPointerPressedHandler;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // 打开后按窗口实际 DPI 纠正尺寸（构造时用的是查询到的显示器缩放）
        var s = RenderScaling;
        Width = _monitor.Bounds.Width / s;
        Height = _monitor.Bounds.Height / s;
        Position = _monitor.Bounds.TopLeft;

        // 默认选区 = 当前屏幕全屏（REQUIREMENTS §4.3）
        _selectionLayer.SetSelection(new Rect(0, 0, Width, Height));
        PositionToolbar();
        Activate();
        Focus();
    }

    private Border BuildToolbar()
    {
        var copyButton = new Button
        {
            Width = 35,
            Height = 35,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = BuildCopyIcon(),
        };
        ToolTip.SetTip(copyButton, "复制 (Enter)");
        // Fluent 主题悬停/按下会换成主题色背景并改前景色，在白色工具条上会与图标混色，
        // 显式覆写为灰阶状态色
        AddStateBackground(copyButton, ":pointerover", Color.FromRgb(0xEA, 0xEA, 0xEA));
        AddStateBackground(copyButton, ":pressed", Color.FromRgb(0xD4, 0xD4, 0xD4));
        copyButton.Click += (_, _) => CopySelection();

        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #33000000"),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { copyButton },
            },
        };
    }

    /// <summary>22×22 复制图标：后层纸片 + 前层纸片（描边矢量，不随主题前景色变化）。</summary>
    private static Control BuildCopyIcon()
    {
        var stroke = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        var back = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 12, Height = 12, RadiusX = 2, RadiusY = 2,
            Stroke = stroke, StrokeThickness = 2,
        };
        Canvas.SetLeft(back, 3);
        Canvas.SetTop(back, 3);
        var front = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 12, Height = 12, RadiusX = 2, RadiusY = 2,
            Stroke = stroke, StrokeThickness = 2,
            Fill = Brushes.White,
        };
        Canvas.SetLeft(front, 7);
        Canvas.SetTop(front, 7);
        return new Canvas { Width = 22, Height = 22, Children = { back, front } };
    }

    private static void AddStateBackground(Button button, string pseudoClass, Color color)
    {
        var style = new Style(x => x.OfType<Button>().Class(pseudoClass)
            .Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
            .Name("PART_ContentPresenter"));
        style.Setters.Add(new Setter(
            Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty,
            new SolidColorBrush(color)));
        button.Styles.Add(style);
    }

    /// <summary>工具条停靠在选区右下：优先选区下方，放不下换上方，再不行放选区内部。</summary>
    private void PositionToolbar()
    {
        var sel = _selectionLayer.Selection;
        _toolbar.IsVisible = true;
        _toolbar.Measure(Size.Infinity);
        var size = _toolbar.DesiredSize;
        const double gap = 6;

        var x = Math.Clamp(sel.Right - size.Width, 0, Math.Max(0, Bounds.Width - size.Width));
        var y = sel.Bottom + gap;
        if (y + size.Height > Bounds.Height)
            y = sel.Y - gap - size.Height;
        if (y < 0)
            y = Math.Max(0, sel.Bottom - gap - size.Height);

        Canvas.SetLeft(_toolbar, x);
        Canvas.SetTop(_toolbar, y);
    }

    private void CopySelection()
    {
        var sel = _selectionLayer.Selection;
        if (sel.Width <= 0 || sel.Height <= 0)
            return;
        var s = RenderScaling;
        var x1 = (int)Math.Round(sel.X * s);
        var y1 = (int)Math.Round(sel.Y * s);
        var x2 = (int)Math.Round(sel.Right * s);
        var y2 = (int)Math.Round(sel.Bottom * s);
        var physical = new PixelRect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
        _onCopy(physical);
    }

    private void OnKeyDownHandler(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter
                 || (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            CopySelection();
            e.Handled = true;
        }
    }

    private void OnPointerPressedHandler(object? sender, PointerPressedEventArgs e)
    {
        // 右键 = 后退/取消（REQUIREMENTS §4 取消逻辑，一期直接取消本次截屏）
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            Close();
            e.Handled = true;
        }
    }
}
