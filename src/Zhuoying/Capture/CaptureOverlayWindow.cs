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
    private readonly Action _requestSave;
    private readonly Action _requestSaveAs;
    private readonly Action _requestPin;
    private readonly Action _requestCancel;
    private readonly EditorState _editorState;
    private readonly EditorLayer _editorLayer;
    private readonly TextEditController _textEdit;
    private readonly EditorToolbar _toolbar;
    private PixelRect _toolbarAnchor; // 最近一次工具条停靠依据的选区
    private int _geometryChecks;

    /// <summary>窗口几何已按实际 DPI 重算（会话据此重摆工具条）。</summary>
    public event Action? GeometryChanged;

    public CaptureOverlayWindow(
        WriteableBitmap frame, PixelRect virtualBounds,
        SelectionController selection, EditorState editorState,
        Action requestCopy, Action requestSave, Action requestSaveAs,
        Action requestPin, Action requestCancel)
    {
        _virtualBounds = virtualBounds;
        // 输出（复制/保存）前先提交进行中的文字编辑，输出才包含最新文本
        _requestCopy = () =>
        {
            _textEdit!.Commit();
            requestCopy();
        };
        _requestSave = () =>
        {
            _textEdit!.Commit();
            requestSave();
        };
        _requestSaveAs = () =>
        {
            _textEdit!.Commit();
            requestSaveAs();
        };
        _requestPin = () =>
        {
            _textEdit!.Commit();
            requestPin();
        };
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

        // 文字就地编辑宿主（位于编辑层之上、工具条之下）
        var textEditHost = new Canvas();
        _textEdit = new TextEditController(
            textEditHost, editorState.Model, editorState, virtualBounds.TopLeft, () => RenderScaling);
        _editorLayer.CommitTextEdit = () => _textEdit.Commit();
        _editorLayer.TextEditRequested += (el, isNew) => _textEdit.Begin(el, isNew);

        // 选中编号徽章时的四角操作按钮（编辑层之上、工具条之下）
        var numberActions = new NumberActionsPanel(editorState, virtualBounds.TopLeft);
        GeometryChanged += numberActions.Refresh;

        _toolbar = new EditorToolbar(
            editorState, _requestCopy, _requestSave, _requestSaveAs, _requestPin, requestCancel);
        _toolbar.Root.IsVisible = false;
        // 行显隐刚改完时中间容器的 measure 尚未失效，立刻 Measure 会拿到旧尺寸
        //（曾导致属性行出现后工具条底边溢出屏幕），投递到布局完成后再重摆
        _toolbar.LayoutChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(
            RepositionToolbar, Avalonia.Threading.DispatcherPriority.Loaded);
        var toolbarCanvas = new Canvas();
        toolbarCanvas.Children.Add(_toolbar.Root);

        Content = new Panel
        {
            Children =
            {
                image, annotationLayer, selectionLayer, _editorLayer,
                textEditHost, numberActions, toolbarCanvas,
            },
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
        // 用户手动拖过工具条后不再自动停靠，但弹层堆叠方向仍按当前位置刷新
        if (_toolbar.ManuallyPositioned)
        {
            PopupPlacement.UpdateDirection(this,
                Canvas.GetLeft(_toolbar.Root), Canvas.GetTop(_toolbar.Root),
                _toolbar.Root.Bounds.Size);
            _toolbar.ApplyStackDirection(PopupPlacement.StackUp);
            return;
        }
        var sel = ToLocal(_toolbarAnchor);
        _toolbar.Root.Measure(Size.Infinity);
        var size = _toolbar.Root.DesiredSize;
        const double gap = 6;

        // 窗口尺寸用虚拟屏幕 / 缩放直接换算——窗口 Bounds 在启动首轮布局前
        // 还是默认值，据其钳位会把工具条摆到半屏高度（issue-default-toolbar-position）
        var s = RenderScaling;
        var winW = _virtualBounds.Width / s;
        var winH = _virtualBounds.Height / s;

        var x = Math.Clamp(sel.Right - size.Width, 0, Math.Max(0, winW - size.Width));
        var y = sel.Bottom + gap;
        if (y + size.Height > winH)
            y = sel.Y - gap - size.Height;
        if (y < 0)
            y = Math.Max(0, sel.Bottom - gap - size.Height);
        y = Math.Clamp(y, 0, Math.Max(0, winH - size.Height));

        Canvas.SetLeft(_toolbar.Root, x);
        Canvas.SetTop(_toolbar.Root, y);
        PopupPlacement.UpdateDirection(this, x, y, size);
        // 向上堆叠时工具条内部行序也翻转（属性行在工具行上方），保证
        // 工具行 → 属性行 → 弹层 的堆叠方向全程一致
        _toolbar.ApplyStackDirection(PopupPlacement.StackUp);
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
        // 文字编辑中：按键属于编辑框（含 Ctrl+Z 等），只拦 Esc 用于结束编辑
        if (_textEdit.IsActive)
        {
            if (e.Key == Key.Escape)
            {
                _textEdit.Commit();
                e.Handled = true;
            }
            return;
        }

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
        else if (e.Key == Key.F3)
        {
            _requestPin();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                _requestSaveAs();
            else
                _requestSave();
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
        else if (e.Key == Key.A)
        {
            _editorState.Tool = EditorTool.Arrow;
            e.Handled = true;
        }
        else if (e.Key == Key.L)
        {
            _editorState.Tool = EditorTool.Polyline;
            e.Handled = true;
        }
        else if (e.Key == Key.T)
        {
            _editorState.Tool = EditorTool.Text;
            e.Handled = true;
        }
        else if (e.Key == Key.N)
        {
            _editorState.Tool = EditorTool.Number;
            e.Handled = true;
        }
        else if (e.Key == Key.M)
        {
            _editorState.Tool = EditorTool.Pixelate;
            e.Handled = true;
        }
        else if (e.Key == Key.B)
        {
            _editorState.Tool = EditorTool.Blur;
            e.Handled = true;
        }
        else if (e.Key == Key.P)
        {
            _editorState.Tool = EditorTool.Pen;
            e.Handled = true;
        }
        else if (e.Key == Key.I)
        {
            _editorState.Tool = EditorTool.Stamp;
            e.Handled = true;
        }
        else if (e.Key == Key.E)
        {
            _editorState.Tool = EditorTool.Eraser;
            e.Handled = true;
        }
        else if (e.Key == Key.C)
        {
            // 无修饰键的 C：放大镜活动时复制中心像素颜色值
            if (_editorLayer.TryCopyMagnifierColor())
                e.Handled = true;
        }
    }

    private void OnPointerPressedHandler(object? sender, PointerPressedEventArgs e)
    {
        // 右键级联：结束文字编辑 → 元素上弹图层菜单 → 取消进行中的绘制 →
        // 重新开始捕捉（整体重置，微信截图式）→ 初始态再右键才取消截屏
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            var p = e.GetPosition(this);
            var s = RenderScaling;
            var phys = new PixelPoint(
                _virtualBounds.X + (int)Math.Round(p.X * s),
                _virtualBounds.Y + (int)Math.Round(p.Y * s));
            if (_textEdit.IsActive)
                _textEdit.Commit();
            else if (_editorLayer.TryShowContextMenu(phys))
            {
                // 菜单已弹出
            }
            else if (!_editorLayer.CancelInProgress()
                     && !_editorLayer.ResetCaptureIfDirty())
            {
                _requestCancel();
            }
            e.Handled = true;
        }
    }
}
