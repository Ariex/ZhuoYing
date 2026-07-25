using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Zhuoying.Capture;

/// <summary>
/// 覆盖整个虚拟屏幕（所有显示器包围盒）的单一截屏窗口：
/// 冻结帧 → 标注元素层 → 选区遮罩层 → 编辑器输入层 → 工具条。
/// 用单窗口而非每屏一个：PMv2 窗口跨屏时像素 1:1 不被系统缩放，而"创建后跨 DPI
/// 移动窗口"会让 Avalonia 渲染缩放与输入命中缩放永久分裂（按钮点不中，
/// 见 docs/TROUBLESHOOTING.md §10），单窗口从创建起 DPI 稳定，天然自洽。
/// </summary>
public sealed class CaptureOverlayWindow : Window
{
    private readonly PixelRect _virtualBounds;
    private readonly Action _requestCopy;
    private readonly Action _requestCancel;
    private readonly EditorState _editorState;
    private readonly EditorLayer _editorLayer;
    private readonly EditorToolbar _toolbar;
    private PixelRect _toolbarAnchor; // 最近一次工具条停靠依据的选区
    private int _geometryChecks;

    /// <summary>窗口几何已按实际 DPI 重算（会话据此重摆工具条）。</summary>
    public event Action? GeometryChanged;

    public CaptureOverlayWindow(
        WriteableBitmap frame, PixelRect virtualBounds,
        SelectionController selection, EditorState editorState,
        Action requestCopy, Action requestCancel)
    {
        _virtualBounds = virtualBounds;
        _requestCopy = requestCopy;
        _requestCancel = requestCancel;
        _editorState = editorState;

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

        var annotationLayer = new AnnotationLayer(editorState.Model, virtualBounds.TopLeft);
        var selectionLayer = new SelectionLayer(selection, virtualBounds.TopLeft);
        _editorLayer = new EditorLayer(editorState, selection, virtualBounds.TopLeft);
        _editorLayer.DoubleTapped += (_, e) =>
        {
            // 双击空白处 = 复制（双击元素等编辑操作不触发）
            var p = e.GetPosition(_editorLayer);
            var s = RenderScaling;
            var phys = new PixelPoint(
                virtualBounds.X + (int)Math.Round(p.X * s),
                virtualBounds.Y + (int)Math.Round(p.Y * s));
            if (_editorLayer.IsBlankAt(phys))
                _requestCopy();
        };

        _toolbar = new EditorToolbar(editorState, requestCopy, requestCancel);
        _toolbar.Root.IsVisible = false;
        _toolbar.LayoutChanged += RepositionToolbar;
        var toolbarCanvas = new Canvas();
        toolbarCanvas.Children.Add(_toolbar.Root);

        Content = new Panel
        {
            Children = { image, annotationLayer, selectionLayer, _editorLayer, toolbarCanvas },
        };

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

    /// <summary>工具条停靠在选区右下：优先选区下方，放不下换上方，再不行放选区内部。</summary>
    public void ShowToolbarFor(PixelRect physicalSelection)
    {
        _toolbarAnchor = physicalSelection;
        _toolbar.Root.IsVisible = true;
        RepositionToolbar();
    }

    public void HideToolbar() => _toolbar.Root.IsVisible = false;

    private void RepositionToolbar()
    {
        if (!_toolbar.Root.IsVisible || _toolbarAnchor.Width <= 0)
            return;
        var sel = ToLocal(_toolbarAnchor);
        _toolbar.Root.Measure(Size.Infinity);
        var size = _toolbar.Root.DesiredSize;
        const double gap = 6;

        var x = Math.Clamp(sel.Right - size.Width, 0, Math.Max(0, Bounds.Width - size.Width));
        var y = sel.Bottom + gap;
        if (y + size.Height > Bounds.Height)
            y = sel.Y - gap - size.Height;
        if (y < 0)
            y = Math.Max(0, sel.Bottom - gap - size.Height);
        y = Math.Clamp(y, 0, Math.Max(0, Bounds.Height - size.Height));

        Canvas.SetLeft(_toolbar.Root, x);
        Canvas.SetTop(_toolbar.Root, y);
    }

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
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.Escape)
        {
            // Esc 级联：取消创建中元素 → 退出形状工具/取消选中 → 取消截屏
            if (!_editorLayer.HandleEscape())
                _requestCancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter || (ctrl && e.Key == Key.C))
        {
            _requestCopy();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z)
        {
            _editorState.Model.Undo();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Y)
        {
            _editorState.Model.Redo();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            _editorLayer.DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.V)
        {
            _editorState.Tool = EditorTool.Select;
            e.Handled = true;
        }
        else if (e.Key == Key.S)
        {
            _editorState.Tool = EditorTool.Shape;
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
