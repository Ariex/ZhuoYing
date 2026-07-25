using System;
using Avalonia;

namespace Zhuoying.Capture;

/// <summary>
/// 跨屏共享的选区状态与拖拽交互逻辑。坐标一律为虚拟屏幕物理像素。
/// 各显示器的 SelectionLayer 把指针事件换算成物理像素后委托到本类，
/// 渲染时再把选区换算回各自窗口的 DIP，因此混合 DPI 下各屏看到的是同一份选区。
/// 命中半径、拖拽阈值等 DIP 尺寸由事件来源窗口按其缩放换算成物理像素传入。
/// </summary>
public sealed class SelectionController
{
    private enum DragMode
    {
        None,
        Create,
        Move,
        Resize,
        /// <summary>按在选区外：已预览"包围盒扩展到按下点"，拖拽则转为 Create，直接松开则保留扩展结果。</summary>
        Expand,
    }

    /// <summary>8 个手柄的锚点系数（x, y ∈ {0, 0.5, 1}），顺序：四角 + 四边中点。</summary>
    public static readonly (double X, double Y)[] HandleAnchors =
    [
        (0, 0), (0.5, 0), (1, 0),
        (1, 0.5), (1, 1), (0.5, 1),
        (0, 1), (0, 0.5),
    ];

    private readonly PixelRect _virtualBounds;
    private PixelRect _selection;
    private PixelRect _dragStartRect;
    private PixelPoint _dragStart;
    private DragMode _mode;
    private int _handleIndex;
    private double _dragThreshold; // 物理像素，按下时按来源窗口缩放确定
    private bool _isDefault = true;

    public SelectionController(PixelRect virtualBounds, PixelRect initialSelection)
    {
        _virtualBounds = virtualBounds;
        _selection = initialSelection;
    }

    public PixelRect Selection => _selection;
    public bool IsDragging => _mode != DragMode.None;

    /// <summary>选区仍是会话开始时的默认全屏选区（此时内部按下画新矩形而非移动）。</summary>
    public bool IsDefaultSelection => _isDefault;

    /// <summary>正在新建拖拽（此时不画手柄）。</summary>
    public bool IsCreating => _mode == DragMode.Create;

    /// <summary>选区变化，所有窗口的选区层重绘。</summary>
    public event Action? Changed;

    /// <summary>拖拽开始（用于隐藏工具条）。</summary>
    public event Action? DragStarted;

    /// <summary>拖拽结束，参数为最终选区。</summary>
    public event Action<PixelRect>? DragCompleted;

    /// <param name="pos">虚拟屏幕物理像素。</param>
    /// <param name="scaling">事件来源窗口的缩放，用于把 DIP 尺寸换算为物理像素。</param>
    public void PointerPressed(PixelPoint pos, double scaling)
    {
        pos = Clamp(pos);
        _dragStart = pos;
        _dragStartRect = _selection;
        _dragThreshold = SelectionMetrics.DragThreshold * scaling;

        var handle = HitHandle(pos, SelectionMetrics.HandleHitRadius * scaling);
        if (handle >= 0)
        {
            _mode = DragMode.Resize;
            _handleIndex = handle;
        }
        else if (_isDefault && _selection.Contains(pos))
        {
            // 默认全屏选区：内部按下即开始画新矩形（否则无法重新框选）
            _mode = DragMode.Create;
            _selection = new PixelRect(pos, new PixelSize(0, 0));
        }
        else if (_selection.Contains(pos))
        {
            _mode = DragMode.Move;
        }
        else
        {
            // 选区外按下（可能在另一块屏幕上）：先预览把选区包围盒扩展到按下点；
            // 后续拖拽超过阈值则转为画新矩形，直接松开则保留扩展结果
            _mode = DragMode.Expand;
            _selection = BoundingBox(_dragStartRect, pos);
        }

        DragStarted?.Invoke();
        Changed?.Invoke();
    }

    public void PointerMoved(PixelPoint pos)
    {
        pos = Clamp(pos);
        switch (_mode)
        {
            case DragMode.None:
                return;
            case DragMode.Create:
                _selection = FromCorners(_dragStart, pos);
                break;
            case DragMode.Move:
                var dx = Math.Clamp(pos.X - _dragStart.X,
                    _virtualBounds.X - _dragStartRect.X, _virtualBounds.Right - _dragStartRect.Right);
                var dy = Math.Clamp(pos.Y - _dragStart.Y,
                    _virtualBounds.Y - _dragStartRect.Y, _virtualBounds.Bottom - _dragStartRect.Bottom);
                _selection = new PixelRect(
                    new PixelPoint(_dragStartRect.X + dx, _dragStartRect.Y + dy), _dragStartRect.Size);
                break;
            case DragMode.Resize:
                _selection = ResizeByHandle(pos);
                break;
            case DragMode.Expand:
                if (Math.Abs(pos.X - _dragStart.X) >= _dragThreshold
                    || Math.Abs(pos.Y - _dragStart.Y) >= _dragThreshold)
                {
                    _mode = DragMode.Create;
                    _selection = FromCorners(_dragStart, pos);
                }
                break;
        }
        Changed?.Invoke();
    }

    public void PointerReleased()
    {
        if (_mode == DragMode.None)
            return;
        var mode = _mode;
        _mode = DragMode.None;
        if (mode == DragMode.Create
            && (_selection.Width < _dragThreshold || _selection.Height < _dragThreshold))
        {
            // 位移过小视为误点击，恢复原选区（不清除默认全屏标记）
            _selection = _dragStartRect;
        }
        else
        {
            _isDefault = false;
        }
        Changed?.Invoke();
        DragCompleted?.Invoke(_selection);
    }

    /// <summary>测试钩子：直接设定选区（虚拟屏幕物理像素）。</summary>
    public void SetSelection(PixelRect rect)
    {
        _selection = rect;
        _isDefault = false;
        Changed?.Invoke();
        DragCompleted?.Invoke(_selection);
    }

    /// <returns>命中的手柄下标，未命中返回 -1。hitRadius 为物理像素。</returns>
    public int HitHandle(PixelPoint pos, double hitRadius)
    {
        if (_selection.Width <= 0 || _selection.Height <= 0)
            return -1;
        for (var i = 0; i < HandleAnchors.Length; i++)
        {
            var center = HandleCenter(i);
            if (Math.Abs(pos.X - center.X) <= hitRadius && Math.Abs(pos.Y - center.Y) <= hitRadius)
                return i;
        }
        return -1;
    }

    /// <summary>手柄中心（虚拟屏幕物理像素）。</summary>
    public Point HandleCenter(int index)
    {
        var (ax, ay) = HandleAnchors[index];
        return new Point(_selection.X + ax * _selection.Width, _selection.Y + ay * _selection.Height);
    }

    private PixelPoint Clamp(PixelPoint p) => new(
        Math.Clamp(p.X, _virtualBounds.X, _virtualBounds.Right),
        Math.Clamp(p.Y, _virtualBounds.Y, _virtualBounds.Bottom));

    private static PixelRect FromCorners(PixelPoint a, PixelPoint b) => new(
        new PixelPoint(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)),
        new PixelSize(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)));

    // 注意不能用 Rect.Union——它对零尺寸矩形（单点）有空矩形特判会原样返回
    private static PixelRect BoundingBox(PixelRect rect, PixelPoint p)
    {
        var left = Math.Min(rect.X, p.X);
        var top = Math.Min(rect.Y, p.Y);
        var right = Math.Max(rect.Right, p.X);
        var bottom = Math.Max(rect.Bottom, p.Y);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private PixelRect ResizeByHandle(PixelPoint pos)
    {
        var (ax, ay) = HandleAnchors[_handleIndex];
        int left = _dragStartRect.X, top = _dragStartRect.Y;
        int right = _dragStartRect.Right, bottom = _dragStartRect.Bottom;
        if (ax == 0) left = pos.X;
        else if (ax == 1) right = pos.X;
        if (ay == 0) top = pos.Y;
        else if (ay == 1) bottom = pos.Y;
        // 允许拖拽越过对边，自动翻转
        return new PixelRect(
            Math.Min(left, right), Math.Min(top, bottom),
            Math.Abs(right - left), Math.Abs(bottom - top));
    }
}

/// <summary>选区交互的 DIP 尺寸常量（换算物理像素时乘以窗口缩放）。</summary>
public static class SelectionMetrics
{
    public const double DragThreshold = 3;
    public const double HandleSize = 8;
    public const double HandleHitRadius = 10;
}
