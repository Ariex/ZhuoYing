using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Zhuoying.Capture;

/// <summary>
/// 自定义取色按钮：彩虹格图标，点击弹出**可拖拽的置顶取色浮窗**（PixPin 式；
/// 真窗口而非 Popup——Popup 承载 ColorView 曾出现左列被裁掉的渲染残缺）。
/// 选色实时回调 onPick（画布实时预览）；点击底部颜色预览条 = 确认并关闭；
/// 顶栏可拖动移动；点击窗口外（失焦）或 Esc 关闭。
/// 禁用 Alpha——透明度在样式体系里是独立属性。
/// 嵌套在悬浮弹层内使用时，外层看门狗应检查 <see cref="AnyOpen"/> 防止误关。
/// </summary>
public sealed class ColorPickButton : Panel
{
    private static int s_openCount;

    /// <summary>当前是否有取色浮窗打开（外层弹层看门狗用）。</summary>
    public static bool AnyOpen => s_openCount > 0;

    private readonly Func<Color> _get;
    private readonly Action<Color> _onPick;
    private readonly Action? _onOpened;
    private readonly Action? _onClosed;
    private readonly Button _button;
    private Window? _window;
    private bool _syncing;

    public ColorPickButton(
        Func<Color> get, Action<Color> onPick,
        Action? onOpened = null, Action? onClosed = null, double buttonSize = 26)
    {
        _get = get;
        _onPick = onPick;
        _onOpened = onOpened;
        _onClosed = onClosed;

        _button = new Button
        {
            Width = buttonSize,
            Height = buttonSize,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            Content = RainbowIcon(buttonSize - 10),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_button, "自定义颜色");
        _button.Click += (_, _) =>
        {
            if (_window != null)
                _window.Close();
            else
                OpenWindow();
        };
        Children.Add(_button);
    }

    private void OpenWindow()
    {
        var colorView = new ColorView
        {
            IsAlphaEnabled = false,
            IsAlphaVisible = false,
            Width = 250,
        };
        _syncing = true;
        colorView.Color = _get();
        _syncing = false;
        colorView.ColorChanged += (_, e) =>
        {
            if (!_syncing)
                _onPick(e.NewColor); // 实时预览
        };

        var closeButton = new Button
        {
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = "✕",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
            },
        };
        var titleBar = new DockPanel
        {
            Background = Brushes.Transparent, // 命中需要非 null 背景（拖动区）
            Margin = new Thickness(2, 0, 0, 4),
            Children =
            {
                new TextBlock
                {
                    Text = "颜色",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        DockPanel.SetDock(closeButton, Dock.Right);
        titleBar.Children.Insert(0, closeButton);
        titleBar.Cursor = new Cursor(StandardCursorType.SizeAll);

        var root = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
            Padding = new Thickness(8, 6, 8, 8),
            Margin = new Thickness(10), // 给阴影留位
            BoxShadow = BoxShadows.Parse("0 2 10 0 #40000000"),
            Cursor = new Cursor(StandardCursorType.Arrow),
            Child = new StackPanel { Children = { titleBar, colorView } },
        };

        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            Topmost = true,
            CanResize = false,
            ShowActivated = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Background = Brushes.Transparent,
            Content = root,
        };

        titleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed)
            {
                window.BeginMoveDrag(e);
                e.Handled = true;
            }
        };
        closeButton.Click += (_, _) => window.Close();
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                window.Close();
                e.Handled = true;
            }
        };
        // 点击浮窗外 = 关闭（等价此前的 light-dismiss；拖动/窗内操作不受影响）。
        // 置顶 owner 会在 Show 后短暂抢回激活，只在浮窗真正激活过之后才响应失活
        var everActivated = false;
        window.Activated += (_, _) => everActivated = true;
        window.Deactivated += (_, _) =>
        {
            if (everActivated)
                window.Close();
        };

        // 初始位置：按钮下缘附近，打开后按实际尺寸钳入所在屏幕
        var anchor = TryAnchor();
        window.Position = anchor ?? new PixelPoint(200, 200);
        var previewHooked = false;
        window.Opened += (_, _) =>
        {
            s_openCount++;
            _onOpened?.Invoke();
            ClampToScreen(window, anchor);
            window.Activate();
            // 底部颜色预览条 = "确认并关闭"：模板应用后挂一次点击处理
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (previewHooked)
                    return;
                var previewer = colorView.GetVisualDescendants()
                    .OfType<ColorPreviewer>().FirstOrDefault();
                if (previewer == null)
                    return;
                previewHooked = true;
                previewer.Cursor = new Cursor(StandardCursorType.Hand);
                ToolTip.SetTip(previewer, "确认并关闭");
                previewer.PointerReleased += (_, _) => window.Close();
            }, Avalonia.Threading.DispatcherPriority.Background);
        };
        window.Closed += (_, _) =>
        {
            s_openCount = Math.Max(0, s_openCount - 1);
            _window = null;
            _onClosed?.Invoke();
        };

        _window = window;
        // 找真正的宿主窗口做 owner（按钮可能嵌在弹层里）：会话关闭时浮窗随之关闭
        TopLevel? host = TopLevel.GetTopLevel(this);
        while (host is PopupRoot popupRoot)
            host = popupRoot.ParentTopLevel;
        if (host is Window owner)
            window.Show(owner);
        else
            window.Show();
    }

    private PixelPoint? TryAnchor()
    {
        try
        {
            return _button.PointToScreen(new Point(0, _button.Bounds.Height + 4));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>按实际窗口尺寸把浮窗钳入锚点所在屏幕（含阴影边距）。</summary>
    private static void ClampToScreen(Window window, PixelPoint? anchor)
    {
        if (anchor is not { } a)
            return;
        var screen = window.Screens.ScreenFromPoint(a);
        if (screen == null)
            return;
        var scaling = window.DesktopScaling;
        var w = (int)(window.ClientSize.Width * scaling);
        var h = (int)(window.ClientSize.Height * scaling);
        var x = Math.Clamp(a.X, screen.Bounds.X, Math.Max(screen.Bounds.X, screen.Bounds.Right - w));
        var y = a.Y;
        if (y + h > screen.Bounds.Bottom)
            y = a.Y - h - (int)(34 * scaling); // 下方放不下：翻到按钮上方
        y = Math.Clamp(y, screen.Bounds.Y, Math.Max(screen.Bounds.Y, screen.Bounds.Bottom - h));
        window.Position = new PixelPoint(x, y);
    }

    /// <summary>彩虹格图标：锥形渐变小方块 + 白点。</summary>
    private static Control RainbowIcon(double size) => new Border
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(3),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
        Background = new ConicGradientBrush
        {
            Center = RelativePoint.Center,
            GradientStops =
            {
                new GradientStop(Colors.Red, 0),
                new GradientStop(Colors.Yellow, 0.17),
                new GradientStop(Colors.Lime, 0.33),
                new GradientStop(Colors.Cyan, 0.5),
                new GradientStop(Colors.Blue, 0.67),
                new GradientStop(Colors.Magenta, 0.83),
                new GradientStop(Colors.Red, 1),
            },
        },
    };
}
