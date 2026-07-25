using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Zhuoying.Capture;

/// <summary>
/// 覆盖整个虚拟屏幕（所有显示器包围盒）的单一截屏窗口：冻结帧背景 + 选区层 + 工具条。
/// 用单窗口而非每屏一个：PMv2 窗口跨屏时像素 1:1 不被系统缩放，而"创建后跨 DPI
/// 移动窗口"会让 Avalonia 渲染缩放与输入命中缩放永久分裂（按钮点不中，
/// 见 docs/TROUBLESHOOTING.md §10），单窗口从创建起 DPI 稳定，天然自洽。
/// </summary>
public sealed class CaptureOverlayWindow : Window
{
    private readonly PixelRect _virtualBounds;
    private readonly Action _requestCopy;
    private readonly Action _requestCancel;
    private readonly Border _toolbar;
    private int _geometryChecks;

    /// <summary>窗口几何已按实际 DPI 重算（会话据此重摆工具条）。</summary>
    public event Action? GeometryChanged;

    public CaptureOverlayWindow(
        WriteableBitmap frame, PixelRect virtualBounds, SelectionController selection,
        Action requestCopy, Action requestCancel)
    {
        _virtualBounds = virtualBounds;
        _requestCopy = requestCopy;
        _requestCancel = requestCancel;

        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = new Cursor(StandardCursorType.Cross);

        Position = virtualBounds.TopLeft;

        var image = new Image { Source = frame, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);

        var selectionLayer = new SelectionLayer(selection, virtualBounds.TopLeft);
        selectionLayer.DoubleTapped += (_, _) => _requestCopy();

        _toolbar = BuildToolbar();
        _toolbar.IsVisible = false;
        var toolbarCanvas = new Canvas();
        toolbarCanvas.Children.Add(_toolbar);

        Content = new Panel { Children = { image, selectionLayer, toolbarCanvas } };

        Opened += (_, _) => { ApplyGeometry(); PostGeometryCheck(); };
        ScalingChanged += (_, _) => PostGeometryCheck();
        KeyDown += OnKeyDownHandler;
        PointerPressed += OnPointerPressedHandler;
    }

    /// <summary>按当前实际 DPI 把窗口对齐到虚拟屏幕包围盒（物理像素 → DIP）。</summary>
    private void ApplyGeometry()
    {
        var s = RenderScaling;
        Width = _virtualBounds.Width / s;
        Height = _virtualBounds.Height / s;
        Position = _virtualBounds.TopLeft;
        GeometryChanged?.Invoke();
    }

    /// <summary>
    /// DPI 调整过程中系统按"建议矩形"缩放窗口，会与我们设置的尺寸互相覆盖
    ///（TROUBLESHOOTING §7），不能同步改尺寸；投递事后校验直至几何收敛。
    /// </summary>
    private void PostGeometryCheck()
    {
        if (_geometryChecks >= 20) // 收敛保险丝，正常一两轮即稳定
            return;
        _geometryChecks++;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var s = RenderScaling;
            var sizeOk = Math.Abs(Width - _virtualBounds.Width / s) < 0.5
                         && Math.Abs(Height - _virtualBounds.Height / s) < 0.5;
            if (sizeOk && Position == _virtualBounds.TopLeft)
                return;
            ApplyGeometry();
            PostGeometryCheck();
        }, Avalonia.Threading.DispatcherPriority.Background);
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
        copyButton.Click += (_, _) => _requestCopy();

        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #33000000"),
            Cursor = new Cursor(StandardCursorType.Arrow), // 覆盖窗口级十字光标
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
    public void ShowToolbarFor(PixelRect physicalSelection)
    {
        var sel = ToLocal(physicalSelection);
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
        y = Math.Clamp(y, 0, Math.Max(0, Bounds.Height - size.Height));

        Canvas.SetLeft(_toolbar, x);
        Canvas.SetTop(_toolbar, y);
    }

    public void HideToolbar() => _toolbar.IsVisible = false;

    /// <summary>虚拟屏幕物理像素矩形 → 窗口 DIP。</summary>
    private Rect ToLocal(PixelRect r)
    {
        var s = RenderScaling;
        return new Rect(
            (r.X - _virtualBounds.X) / s, (r.Y - _virtualBounds.Y) / s,
            r.Width / s, r.Height / s);
    }

    private void OnKeyDownHandler(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _requestCancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter
                 || (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            _requestCopy();
            e.Handled = true;
        }
    }

    private void OnPointerPressedHandler(object? sender, PointerPressedEventArgs e)
    {
        // 右键 = 后退/取消（REQUIREMENTS §4 取消逻辑，当前直接取消本次截屏）
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            _requestCancel();
            e.Handled = true;
        }
    }
}
