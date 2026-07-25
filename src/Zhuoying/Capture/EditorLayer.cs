using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Zhuoying.Annotations;

namespace Zhuoying.Capture;

/// <summary>
/// 编辑器输入层（最上层，接收全部画布指针输入）+ 选中元素的手柄渲染。
/// 输入路由优先级：旋转手柄 → 圆角手柄 → 缩放手柄 → 元素本体（选中/移动）→
/// 选区交互（委托 SelectionController）。形状工具激活时拖拽创建新元素。
/// 元素可旋转：手柄位置在未旋转坐标系中计算、绕元素中心旋转后命中/绘制；
/// 拖拽调整时把指针逆旋转回未旋转坐标系再套用原有几何逻辑。
/// </summary>
public sealed class EditorLayer : Control
{
    private enum Op
    {
        None,
        CreateShape,
        MoveElement,
        ResizeElement,
        RadiusElement,
        RotateElement,
        Selection, // 委托给 SelectionController 的选区交互
    }

    /// <summary>圆角手柄所在的四个角（锚点系数）。</summary>
    private static readonly (double X, double Y)[] RadiusCorners = [(0, 0), (1, 0), (1, 1), (0, 1)];

    private static readonly StandardCursorType[] HandleCursors =
    [
        StandardCursorType.TopLeftCorner, StandardCursorType.TopSide, StandardCursorType.TopRightCorner,
        StandardCursorType.RightSide, StandardCursorType.BottomRightCorner, StandardCursorType.BottomSide,
        StandardCursorType.BottomLeftCorner, StandardCursorType.LeftSide,
    ];

    private static readonly Pen HandlePen = new(new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xF0)), 1.5);
    private static readonly Pen RadiusHandlePen = new(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), 1.5);
    private static readonly Pen RotationStemPen = new(new SolidColorBrush(Color.FromArgb(0xAA, 0x2D, 0x8C, 0xF0)), 1);
    private static readonly Pen SelectionOutlinePen = new(
        new SolidColorBrush(Color.FromArgb(0xAA, 0x2D, 0x8C, 0xF0)), 1)
    { DashStyle = new DashStyle([4, 3], 0) };

    /// <summary>旋转手柄到包围盒上边中点的距离（DIP）。</summary>
    private const double RotationHandleOffset = 24;

    /// <summary>圆角手柄离元素中心的最小间距（DIP），避免 100% 时四个手柄重合。</summary>
    private const double RadiusHandleCenterGap = 12;

    private readonly EditorState _state;
    private readonly AnnotationModel _model;
    private readonly SelectionController _selection;
    private readonly PixelPoint _origin;

    private Op _op;
    private ShapeElement? _liveElement;   // 创建中/编辑中的元素
    private PixelPoint _dragStart;
    private PixelRect _dragStartBounds;
    private ShapeStyle _dragStartStyle = new();
    private int _handleIndex;
    private StandardCursorType _currentCursor = StandardCursorType.Cross;

    public EditorLayer(
        EditorState state, SelectionController selection, PixelPoint origin)
    {
        _state = state;
        _model = state.Model;
        _selection = selection;
        _origin = origin;
        _model.Changed += InvalidateVisual;
        _state.ToolChanged += () =>
        {
            AbortCreate();
            InvalidateVisual();
        };
    }

    private double Scaling => (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;

    private PixelPoint ToPhysical(PointerEventArgs e)
    {
        var s = Scaling;
        var p = e.GetPosition(this);
        return new PixelPoint(
            _origin.X + (int)Math.Round(p.X * s),
            _origin.Y + (int)Math.Round(p.Y * s));
    }

    private Rect ToLocal(PixelRect r)
    {
        var s = Scaling;
        return new Rect(
            (r.X - _origin.X) / s, (r.Y - _origin.Y) / s,
            r.Width / s, r.Height / s);
    }

    private Point ToLocal(Point physical)
    {
        var s = Scaling;
        return new Point((physical.X - _origin.X) / s, (physical.Y - _origin.Y) / s);
    }

    /// <summary>Esc：优先取消创建中元素，其次取消选中；都没有则返回 false（由上层关会话）。</summary>
    public bool HandleEscape()
    {
        if (_op == Op.CreateShape)
        {
            AbortCreate();
            return true;
        }
        if (_state.Tool == EditorTool.Shape)
        {
            _state.Tool = EditorTool.Select;
            return true;
        }
        if (_model.Selected != null)
        {
            _model.Selected = null;
            return true;
        }
        return false;
    }

    public void DeleteSelected()
    {
        if (_model.Selected is not { } el)
            return;
        var index = _model.Elements.IndexOf(el);
        _model.Elements.Remove(el);
        _model.Selected = null;
        _model.Push(new RemoveElementCommand(_model, el, index));
    }

    /// <summary>双击是否落在空白处（用于双击复制的判定）。</summary>
    public bool IsBlankAt(PixelPoint physical) =>
        _state.Tool == EditorTool.Select
        && _model.HitTest(physical, 4 * Scaling) == null
        && HitElementHandle(physical) < 0
        && HitRadiusHandle(physical) < 0
        && !HitRotationHandle(physical);

    private void AbortCreate()
    {
        if (_op == Op.CreateShape && _liveElement != null)
        {
            _model.Elements.Remove(_liveElement);
            _model.RaiseChanged();
        }
        if (_op == Op.CreateShape)
            _op = Op.None;
        _liveElement = null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _op != Op.None)
            return;
        var phys = ToPhysical(e);
        var s = Scaling;

        if (_state.Tool == EditorTool.Shape)
        {
            _liveElement = new ShapeElement
            {
                Bounds = new PixelRect(phys, new PixelSize(0, 0)),
                // 新元素继承当前样式，但旋转角归零
                Style = _state.CurrentStyle with { RotationDeg = 0 },
            };
            _model.Elements.Add(_liveElement);
            _op = Op.CreateShape;
            _dragStart = phys;
            _model.RaiseChanged();
        }
        else // Select 工具
        {
            if (_model.Selected is { } sel && HitRotationHandle(phys))
            {
                _op = Op.RotateElement;
                _liveElement = sel;
                _dragStartBounds = sel.Bounds;
                _dragStartStyle = sel.Style;
            }
            else if (_model.Selected is { } selR && HitRadiusHandle(phys) is var radius && radius >= 0)
            {
                _op = Op.RadiusElement;
                _handleIndex = radius;
                _liveElement = selR;
                _dragStartBounds = selR.Bounds;
                _dragStartStyle = selR.Style;
            }
            else if (_model.Selected is { } selH && HitElementHandle(phys) is var handle && handle >= 0)
            {
                _op = Op.ResizeElement;
                _handleIndex = handle;
                _liveElement = selH;
                _dragStartBounds = selH.Bounds;
                _dragStartStyle = selH.Style;
            }
            else if (_model.HitTest(phys, 4 * s) is { } hit)
            {
                _model.Selected = hit;
                _state.SyncStyleFromSelection();
                _op = Op.MoveElement;
                _liveElement = hit;
                _dragStart = phys;
                _dragStartBounds = hit.Bounds;
                _dragStartStyle = hit.Style;
            }
            else
            {
                if (_model.Selected != null)
                    _model.Selected = null;
                _op = Op.Selection;
                _selection.PointerPressed(phys, s);
            }
        }

        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var phys = ToPhysical(e);
        switch (_op)
        {
            case Op.None:
                UpdateCursor(phys);
                return;
            case Op.CreateShape when _liveElement != null:
                _liveElement.Bounds = FromCorners(_dragStart, phys);
                _model.RaiseChanged();
                break;
            case Op.ResizeElement when _liveElement != null:
            {
                // 指针逆旋转回未旋转坐标系（绕拖拽起始包围盒中心）再做锚点缩放
                var center = new Point(
                    _dragStartBounds.X + _dragStartBounds.Width / 2.0,
                    _dragStartBounds.Y + _dragStartBounds.Height / 2.0);
                var lp = ShapeElement.RotatePoint(
                    new Point(phys.X, phys.Y), center, -_liveElement.Style.RotationDeg);
                _liveElement.Bounds = ResizeByHandle(lp);
                _model.RaiseChanged();
                break;
            }
            case Op.MoveElement when _liveElement != null:
                _liveElement.Bounds = new PixelRect(
                    new PixelPoint(
                        _dragStartBounds.X + (phys.X - _dragStart.X),
                        _dragStartBounds.Y + (phys.Y - _dragStart.Y)),
                    _dragStartBounds.Size);
                _model.RaiseChanged();
                break;
            case Op.RadiusElement when _liveElement != null:
            {
                var lp = _liveElement.ToUnrotated(new Point(phys.X, phys.Y));
                _liveElement.Style = _liveElement.Style with
                {
                    CornerRadiusPercent = RadiusPercentFor(_liveElement.Bounds, lp, _handleIndex),
                };
                _model.RaiseChanged();
                _state.SyncStyleFromSelection();
                break;
            }
            case Op.RotateElement when _liveElement != null:
            {
                var c = _liveElement.Center;
                // 手柄位于元素上方（未旋转时朝向 -90°），故角度需 +90°
                var deg = Math.Atan2(phys.Y - c.Y, phys.X - c.X) * 180 / Math.PI + 90;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    deg = Math.Round(deg / 15) * 15; // Shift 吸附 15°（TOOLS-SPEC §0.1）
                deg = Math.Round(deg);
                while (deg > 180) deg -= 360;
                while (deg < -180) deg += 360;
                _liveElement.Style = _liveElement.Style with { RotationDeg = deg };
                _model.RaiseChanged();
                _state.SyncStyleFromSelection();
                break;
            }
            case Op.Selection:
                _selection.PointerMoved(phys);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_op == Op.None || e.InitialPressMouseButton != MouseButton.Left)
            return;
        var op = _op;
        _op = Op.None;
        e.Pointer.Capture(null);

        switch (op)
        {
            case Op.CreateShape when _liveElement != null:
                var threshold = 3 * Scaling;
                if (_liveElement.Bounds.Width < threshold || _liveElement.Bounds.Height < threshold)
                {
                    _model.Elements.Remove(_liveElement);
                    _model.RaiseChanged();
                }
                else
                {
                    _model.Push(new AddElementCommand(_model, _liveElement));
                    _model.Selected = _liveElement;
                    _state.Tool = EditorTool.Select; // 画完自动回到选择工具（TOOLS-SPEC §1）
                }
                break;
            case Op.MoveElement:
            case Op.ResizeElement:
            case Op.RadiusElement:
            case Op.RotateElement:
                if (_liveElement != null
                    && (_liveElement.Bounds != _dragStartBounds || _liveElement.Style != _dragStartStyle))
                    _model.Push(new MutateElementCommand(
                        _liveElement, _dragStartBounds, _dragStartStyle,
                        _liveElement.Bounds, _liveElement.Style));
                break;
            case Op.Selection:
                _selection.PointerReleased();
                break;
        }
        _liveElement = null;
        UpdateCursor(ToPhysical(e));
        InvalidateVisual();
    }

    private static PixelRect FromCorners(PixelPoint a, PixelPoint b) => new(
        new PixelPoint(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)),
        new PixelSize(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)));

    private PixelRect ResizeByHandle(Point pos)
    {
        var (ax, ay) = SelectionController.HandleAnchors[_handleIndex];
        double left = _dragStartBounds.X, top = _dragStartBounds.Y;
        double right = _dragStartBounds.Right, bottom = _dragStartBounds.Bottom;
        if (ax == 0) left = pos.X;
        else if (ax == 1) right = pos.X;
        if (ay == 0) top = pos.Y;
        else if (ay == 1) bottom = pos.Y;
        return new PixelRect(
            (int)Math.Round(Math.Min(left, right)), (int)Math.Round(Math.Min(top, bottom)),
            (int)Math.Round(Math.Abs(right - left)), (int)Math.Round(Math.Abs(bottom - top)));
    }

    /// <summary>
    /// 由指针位置（未旋转坐标系）计算圆角百分比：按所属角的有向内缩距离，
    /// 越过角（向外）钳在 0、越过中心（向内）钳在 100，不会回弹。
    /// </summary>
    private static double RadiusPercentFor(PixelRect b, Point pos, int corner)
    {
        if (b.Width <= 0 || b.Height <= 0)
            return 0;
        var (ax, ay) = RadiusCorners[corner];
        var dx = ax == 0 ? pos.X - b.X : b.Right - pos.X;
        var dy = ay == 0 ? pos.Y - b.Y : b.Bottom - pos.Y;
        var nx = Math.Clamp(dx / (b.Width / 2.0), 0, 1);
        var ny = Math.Clamp(dy / (b.Height / 2.0), 0, 1);
        return Math.Round(Math.Clamp((nx + ny) / 2 * 100, 0, 100));
    }

    /// <returns>选中元素被命中的缩放手柄下标，未命中 -1。</returns>
    private int HitElementHandle(PixelPoint pos)
    {
        if (_model.Selected is not { } el)
            return -1;
        var hit = SelectionMetrics.HandleHitRadius * Scaling;
        for (var i = 0; i < SelectionController.HandleAnchors.Length; i++)
        {
            var c = ResizeHandleCenter(el, i);
            if (Math.Abs(pos.X - c.X) <= hit && Math.Abs(pos.Y - c.Y) <= hit)
                return i;
        }
        return -1;
    }

    /// <returns>选中元素被命中的圆角手柄下标，未命中 -1。</returns>
    private int HitRadiusHandle(PixelPoint pos)
    {
        if (_model.Selected is not { } el)
            return -1;
        var hit = SelectionMetrics.HandleHitRadius * Scaling;
        for (var i = 0; i < RadiusCorners.Length; i++)
        {
            var c = ShapeElement.RotatePoint(
                RadiusHandleCenterUnrotated(el, i), el.Center, el.Style.RotationDeg);
            if (Math.Abs(pos.X - c.X) <= hit && Math.Abs(pos.Y - c.Y) <= hit)
                return i;
        }
        return -1;
    }

    private bool HitRotationHandle(PixelPoint pos)
    {
        if (_model.Selected is not { } el)
            return false;
        var hit = SelectionMetrics.HandleHitRadius * Scaling;
        var c = ShapeElement.RotatePoint(
            RotationHandleCenterUnrotated(el), el.Center, el.Style.RotationDeg);
        return Math.Abs(pos.X - c.X) <= hit && Math.Abs(pos.Y - c.Y) <= hit;
    }

    /// <summary>缩放手柄中心（物理像素，已含旋转）。</summary>
    private Point ResizeHandleCenter(ShapeElement el, int index)
    {
        var (ax, ay) = SelectionController.HandleAnchors[index];
        var p = new Point(el.Bounds.X + ax * el.Bounds.Width, el.Bounds.Y + ay * el.Bounds.Height);
        return ShapeElement.RotatePoint(p, el.Center, el.Style.RotationDeg);
    }

    /// <summary>
    /// 圆角手柄中心（未旋转坐标系）：从角沿对角线内缩反映当前圆角大小；
    /// 显示位置钳在 [最小内缩, 距中心一段间距]，100% 时四个手柄互不重合。
    /// </summary>
    private Point RadiusHandleCenterUnrotated(ShapeElement el, int corner)
    {
        var (ax, ay) = RadiusCorners[corner];
        var b = el.Bounds;
        var s = Scaling;
        var rx = el.Style.CornerRadiusPercent / 100 * b.Width / 2.0;
        var ry = el.Style.CornerRadiusPercent / 100 * b.Height / 2.0;
        var minInset = 18 * s;
        var gap = RadiusHandleCenterGap * s;
        var ox = Math.Min(Math.Clamp(rx, minInset, Math.Max(minInset, b.Width / 2.0 - gap)), b.Width / 2.0);
        var oy = Math.Min(Math.Clamp(ry, minInset, Math.Max(minInset, b.Height / 2.0 - gap)), b.Height / 2.0);
        var cx = ax == 0 ? b.X + ox : b.Right - ox;
        var cy = ay == 0 ? b.Y + oy : b.Bottom - oy;
        return new Point(cx, cy);
    }

    /// <summary>旋转手柄中心（未旋转坐标系）：包围盒上边中点上方。</summary>
    private Point RotationHandleCenterUnrotated(ShapeElement el) => new(
        el.Bounds.X + el.Bounds.Width / 2.0,
        el.Bounds.Y - RotationHandleOffset * Scaling);

    private void UpdateCursor(PixelPoint phys)
    {
        StandardCursorType type;
        if (_state.Tool == EditorTool.Shape)
        {
            type = StandardCursorType.Cross;
        }
        else if (HitRotationHandle(phys) || HitRadiusHandle(phys) >= 0)
        {
            type = StandardCursorType.Hand;
        }
        else if (HitElementHandle(phys) is var h && h >= 0)
        {
            type = HandleCursors[h];
        }
        else if (_model.HitTest(phys, 4 * Scaling) != null)
        {
            type = StandardCursorType.SizeAll;
        }
        else
        {
            // 选区交互的光标
            var handle = _selection.HitHandle(phys, SelectionMetrics.HandleHitRadius * Scaling);
            type = handle >= 0 ? HandleCursors[handle]
                : _selection.Selection.Contains(phys) && !_selection.IsDefaultSelection
                    ? StandardCursorType.SizeAll
                    : StandardCursorType.Cross;
        }
        if (type == _currentCursor)
            return;
        _currentCursor = type;
        Cursor = new Cursor(type);
    }

    public override void Render(DrawingContext context)
    {
        // 透明底保证整层可命中
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        if (_model.Selected is not { } el || _op == Op.CreateShape)
            return;

        var local = ToLocal(el.Bounds);
        const double hs = SelectionMetrics.HandleSize;

        // 所有手柄都画在旋转后的坐标系里（与元素同步旋转）
        using (context.PushTransform(ShapeElement.RotationMatrix(local.Center, el.Style.RotationDeg)))
        {
            context.DrawRectangle(null, SelectionOutlinePen, local);

            // 旋转手柄：包围盒上边中点向上引出一条短线 + 圆钮
            var stemTop = new Point(local.Center.X, local.Y - RotationHandleOffset);
            context.DrawLine(RotationStemPen, new Point(local.Center.X, local.Y), stemTop);
            context.DrawEllipse(Brushes.White, HandlePen, stemTop, hs / 2 + 1, hs / 2 + 1);

            // 8 个缩放手柄（方形）
            foreach (var (ax, ay) in SelectionController.HandleAnchors)
            {
                var c = new Point(local.X + ax * local.Width, local.Y + ay * local.Height);
                context.DrawRectangle(Brushes.White, HandlePen,
                    new Rect(c.X - hs / 2, c.Y - hs / 2, hs, hs), 1.5, 1.5);
            }

            // 4 个圆角手柄（圆形，橙色描边）
            for (var i = 0; i < RadiusCorners.Length; i++)
            {
                var c = ToLocal(RadiusHandleCenterUnrotated(el, i));
                context.DrawEllipse(Brushes.White, RadiusHandlePen, c, hs / 2, hs / 2);
            }
        }
    }
}
