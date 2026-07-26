using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Zhuoying.Annotations;

namespace Zhuoying.Capture;

/// <summary>
/// 编辑器输入层（最上层，接收全部画布指针输入）+ 选中元素的手柄渲染。
/// 输入路由优先级：元素手柄（旋转/圆角/缩放/线控制点）→ 元素本体（选中/移动）→
/// 选区交互（委托 SelectionController）。
/// 工具：形状/箭头拖拽创建；折线逐次点击加点、双击结束（期间指针不捕获）。
/// </summary>
public sealed class EditorLayer : Control
{
    private enum Op
    {
        None,
        CreateShape,
        CreateArrow,
        CreatePolyline,
        CreateNumber,
        MoveElement,
        ResizeElement,
        RadiusElement,
        RotateElement,
        MoveLinePoint,
        MoveLine,
        Selection, // 委托给 SelectionController 的选区交互
    }

    /// <summary>请求开始文字就地编辑（元素，是否新建）。由窗口接到 TextEditController。</summary>
    public event Action<TextElement, bool>? TextEditRequested;

    /// <summary>画布收到按下时先提交进行中的文字编辑（由窗口注入）。</summary>
    public Action? CommitTextEdit { get; set; }

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
    private AnnotationElement? _liveElement;   // 创建中/编辑中的元素
    private PixelPoint _dragStart;
    private PixelRect _dragStartBounds;
    private PixelPoint _dragStartCenter;
    private object? _dragBeforeState;
    private PixelPoint[] _dragStartPoints = [];
    private int _handleIndex;
    private bool _dragMutated;
    private string _currentCursorTag = "";
    private static Cursor? s_rotateCursor;
    private static double s_rotateCursorScale;

    /// <summary>
    /// 旋转手柄光标：环形箭头（Windows 无内置旋转光标，运行时渲染 ↻ 生成）。
    /// 位图按屏幕缩放渲染，尺寸与系统十字/斜拉光标一致（32 × 缩放）。
    /// </summary>
    private static Cursor RotateCursor(double scaling)
    {
        if (s_rotateCursor != null && Math.Abs(s_rotateCursorScale - scaling) < 0.01)
            return s_rotateCursor;
        var size = (int)Math.Round(32 * scaling);
        var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            var text = new FormattedText(
                "↻", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
                24 * scaling, Brushes.Black);
            var origin = new Point((size - text.Width) / 2, (size - text.Height) / 2);
            var geometry = text.BuildGeometry(origin);
            if (geometry != null)
            {
                // 白描黑字，深浅背景都可辨
                ctx.DrawGeometry(null,
                    new Pen(Brushes.White, 3 * scaling) { LineJoin = PenLineJoin.Round }, geometry);
                ctx.DrawGeometry(Brushes.Black, null, geometry);
            }
        }
        s_rotateCursor = new Cursor(rtb, new PixelPoint(size / 2, size / 2));
        s_rotateCursorScale = scaling;
        return s_rotateCursor;
    }

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
            CancelInProgress();
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

    private Point ToLocal(PixelPoint physical) => ToLocal(new Point(physical.X, physical.Y));

    /// <summary>
    /// 右键落在元素上时弹出图层菜单（上移/下移/置顶/置底），弹出了返回 true；
    /// 未命中元素返回 false（由上层执行取消逻辑）。
    /// </summary>
    public bool TryShowContextMenu(PixelPoint phys)
    {
        if (_op != Op.None || _model.HitTest(phys, 4 * Scaling) is not { } hit)
            return false;
        _model.Selected = hit;
        _state.SyncStyleFromSelection();

        // 层序调整限定在所属组内：区域模糊只能在底层前缀组里排，
        // 普通标注只能在其上方排（模糊永远压底，仅在底图之上）
        var isMosaic = hit is MosaicElement;
        var groupBottom = isMosaic ? 0 : LeadingMosaicCount();
        var groupTop = isMosaic ? LeadingMosaicCount() - 1 : _model.Elements.Count - 1;
        var index = _model.Elements.IndexOf(hit);
        var flyout = new MenuFlyout();

        void Add(string header, int target, bool enabled)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) =>
            {
                var bottom = isMosaic ? 0 : LeadingMosaicCount();
                var top = isMosaic ? LeadingMosaicCount() - 1 : _model.Elements.Count - 1;
                var from = _model.Elements.IndexOf(hit);
                var to = Math.Clamp(target, bottom, top);
                if (from < 0 || from == to)
                    return;
                _model.Elements.RemoveAt(from);
                _model.Elements.Insert(to, hit);
                _model.Push(new ReorderElementCommand(_model, hit, from, to));
            };
            flyout.Items.Add(item);
        }

        Add("上移一层", index + 1, index < groupTop);
        Add("下移一层", index - 1, index > groupBottom);
        Add("移到顶层", groupTop, index < groupTop);
        Add("移到底层", groupBottom, index > groupBottom);
        flyout.ShowAt(this, true);
        return true;
    }

    /// <summary>列表头部连续的区域模糊元素个数（"永远在底层"前缀组的边界）。</summary>
    private int LeadingMosaicCount()
    {
        var n = 0;
        while (n < _model.Elements.Count && _model.Elements[n] is MosaicElement)
            n++;
        return n;
    }

    /// <summary>取消进行中的创建（形状拖拽/折线逐点/编号放置）。取消了返回 true。</summary>
    public bool CancelInProgress()
    {
        if (_op is not (Op.CreateShape or Op.CreateArrow or Op.CreatePolyline or Op.CreateNumber)
            || _liveElement == null)
            return false;
        if (_liveElement is NumberElement number) // 序列回退，下次放置沿用本编号
            _state.SetNextNumber(number.Style.Kind, number.Value);
        _model.Elements.Remove(_liveElement);
        _liveElement = null;
        _op = Op.None;
        _model.RaiseChanged();
        return true;
    }

    /// <summary>Esc：优先取消创建中元素，其次退出工具/取消选中；都没有则返回 false（由上层关会话）。</summary>
    public bool HandleEscape()
    {
        if (CancelInProgress())
            return true;
        if (_state.Tool != EditorTool.Select)
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

    /// <summary>双击是否落在空白处（用于双击复制的判定；折线创建中不算空白）。</summary>
    public bool IsBlankAt(PixelPoint physical) =>
        _state.Tool == EditorTool.Select
        && _op == Op.None
        && _model.HitTest(physical, 4 * Scaling) == null
        && HitElementHandle(physical) < 0
        && HitRadiusHandle(physical) < 0
        && !HitRotationHandle(physical)
        && HitLineVertex(physical) < 0;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed)
            return;
        var phys = ToPhysical(e);
        var s = Scaling;

        // 点击画布任意处先提交进行中的文字编辑（点击落在编辑框内时不会到达本层）
        CommitTextEdit?.Invoke();

        // 折线创建：后续点击加点/双击结束（指针不捕获，点击间自由移动）
        if (_op == Op.CreatePolyline && _liveElement is LineElement poly)
        {
            if (e.ClickCount >= 2)
            {
                FinishPolyline(poly);
            }
            else
            {
                poly.Points[^1] = phys;      // 固化预览点
                poly.Points.Add(phys);       // 新预览点
                _model.RaiseChanged();
            }
            e.Handled = true;
            return;
        }

        if (_op != Op.None)
            return;

        switch (_state.Tool)
        {
            case EditorTool.Shape:
            {
                var el = new ShapeElement
                {
                    Bounds = new PixelRect(phys, new PixelSize(0, 0)),
                    // 新元素继承当前样式，但旋转角归零
                    Style = _state.CurrentStyle with { RotationDeg = 0 },
                };
                _model.Elements.Add(el);
                _liveElement = el;
                _op = Op.CreateShape;
                _dragStart = phys;
                _model.RaiseChanged();
                e.Pointer.Capture(this);
                break;
            }
            case EditorTool.Arrow:
            {
                var el = new LineElement
                {
                    Style = _state.LineStyleFor(EditorTool.Arrow),
                    IsArrowTool = true,
                };
                el.Points.Add(phys);
                el.Points.Add(phys);
                _model.Elements.Add(el);
                _liveElement = el;
                _op = Op.CreateArrow;
                _dragStart = phys;
                _model.RaiseChanged();
                e.Pointer.Capture(this);
                break;
            }
            case EditorTool.Polyline:
            {
                var el = new LineElement { Style = _state.LineStyleFor(EditorTool.Polyline) };
                el.Points.Add(phys);
                el.Points.Add(phys); // 预览点
                _model.Elements.Add(el);
                _liveElement = el;
                _op = Op.CreatePolyline;
                _model.RaiseChanged();
                // 不捕获指针：折线靠点击序列而非拖拽
                break;
            }
            case EditorTool.Pixelate:
            case EditorTool.Blur:
            {
                // 区域模糊：拖拽创建（同形状），但插入到列表头部的"模糊前缀组"——
                // 永远在其他标注之下、仅在底图之上
                var el = new MosaicElement
                {
                    Bounds = new PixelRect(phys, new PixelSize(0, 0)),
                    Style = _state.MosaicStyleFor(_state.Tool),
                    Frame = _state.BackgroundFrame,
                    FrameOrigin = _state.BackgroundOrigin,
                    Seed = Random.Shared.Next(),
                };
                _model.Elements.Insert(LeadingMosaicCount(), el);
                _liveElement = el;
                _op = Op.CreateShape; // 复用形状创建的拖拽/落地逻辑
                _dragStart = phys;
                _model.RaiseChanged();
                e.Pointer.Capture(this);
                break;
            }
            case EditorTool.Number:
            {
                // 点到已有编号时选中它（可直接拖动），避免原地叠章
                for (var i = _model.Elements.Count - 1; i >= 0; i--)
                {
                    if (_model.Elements[i] is NumberElement existing && existing.HitTest(phys, 4 * s))
                    {
                        _model.Selected = existing;
                        _state.SyncStyleFromSelection();
                        _dragMutated = false;
                        BeginElementDrag(Op.MoveElement, existing, phys, e);
                        return;
                    }
                }
                // 点击放置徽章（按住可拖到准确位置再松开），工具保持激活以连续盖章
                var style = _state.CurrentNumberStyle;
                _model.Selected = null; // 放置期间不保留上一个徽章的选中框/按钮
                var el = new NumberElement
                {
                    Center = phys,
                    Style = style,
                    Value = _state.TakeNextNumber(style.Kind),
                };
                _model.Elements.Add(el);
                _liveElement = el;
                _op = Op.CreateNumber;
                _dragStart = phys;
                _model.RaiseChanged();
                e.Pointer.Capture(this);
                break;
            }
            case EditorTool.Text:
            {
                // 点击处创建默认大小文本框并立即进入编辑（TOOLS-SPEC §6）；
                // 元素暂不入撤销栈，编辑结束时按是否为空决定 Add 或丢弃
                var w = (int)(320 * s);
                var h = (int)(110 * s);
                var el = new TextElement
                {
                    Bounds = new PixelRect(phys.X, phys.Y, w, h),
                    Style = _state.CurrentTextStyle with { RotationDeg = 0 },
                };
                _model.Elements.Add(el);
                _model.Selected = el;
                _state.Tool = EditorTool.Select;
                _model.RaiseChanged();
                TextEditRequested?.Invoke(el, true);
                e.Handled = true;
                return; // 不捕获指针，焦点交给编辑框
            }
            default:
                PressSelectTool(phys, s, e);
                break;
        }
        InvalidateVisual();
    }

    private void PressSelectTool(PixelPoint phys, double s, PointerPressedEventArgs e)
    {
        _dragMutated = false;
        if (_model.Selected is BoxedElement boxed)
        {
            if (HitRotationHandle(phys))
            {
                BeginElementDrag(Op.RotateElement, boxed, phys, e);
                return;
            }
            if (boxed is ShapeElement && HitRadiusHandle(phys) is var radius && radius >= 0)
            {
                _handleIndex = radius;
                BeginElementDrag(Op.RadiusElement, boxed, phys, e);
                return;
            }
            if (HitElementHandle(phys) is var handle && handle >= 0)
            {
                _handleIndex = handle;
                BeginElementDrag(Op.ResizeElement, boxed, phys, e);
                return;
            }
        }
        if (_model.Selected is LineElement line && HitLineVertex(phys) is var vertex && vertex >= 0)
        {
            _handleIndex = vertex;
            BeginElementDrag(Op.MoveLinePoint, line, phys, e);
            return;
        }
        if (_model.HitTest(phys, 4 * s) is { } hit)
        {
            _model.Selected = hit;
            _state.SyncStyleFromSelection();
            // 文字元素：边框环带拖拽移动；内部点击进入就地编辑（纯文本框语义）
            if (hit is TextElement text && !text.IsOnBorder(phys, 8 * s, 4 * s))
            {
                TextEditRequested?.Invoke(text, false);
                e.Handled = true;
                return;
            }
            BeginElementDrag(hit is LineElement ? Op.MoveLine : Op.MoveElement, hit, phys, e);
            return;
        }
        if (_model.Selected != null)
            _model.Selected = null;
        _op = Op.Selection;
        _selection.PointerPressed(phys, s);
        e.Pointer.Capture(this);
    }

    private void BeginElementDrag(Op op, AnnotationElement el, PixelPoint phys, PointerPressedEventArgs e)
    {
        _op = op;
        _liveElement = el;
        _dragStart = phys;
        _dragBeforeState = el.CaptureState();
        if (el is BoxedElement boxed)
            _dragStartBounds = boxed.Bounds;
        if (el is LineElement line)
            _dragStartPoints = line.Points.ToArray();
        if (el is NumberElement number)
            _dragStartCenter = number.Center;
        e.Pointer.Capture(this);
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
            case Op.CreateShape when _liveElement is BoxedElement cs:
                cs.Bounds = FromCorners(_dragStart, phys);
                _model.RaiseChanged();
                break;
            case Op.CreateArrow when _liveElement is LineElement arrow:
                arrow.Points[1] = phys;
                _model.RaiseChanged();
                break;
            case Op.CreatePolyline when _liveElement is LineElement poly:
                poly.Points[^1] = phys; // 预览段跟随
                _model.RaiseChanged();
                break;
            case Op.CreateNumber when _liveElement is NumberElement cn:
                cn.Center = phys;
                _model.RaiseChanged();
                break;
            case Op.ResizeElement when _liveElement is BoxedElement rs:
            {
                var center = new Point(
                    _dragStartBounds.X + _dragStartBounds.Width / 2.0,
                    _dragStartBounds.Y + _dragStartBounds.Height / 2.0);
                var lp = BoxedElement.RotatePoint(
                    new Point(phys.X, phys.Y), center, -rs.RotationDeg);
                rs.Bounds = ResizeByHandle(lp);
                _dragMutated = true;
                _model.RaiseChanged();
                break;
            }
            case Op.MoveElement when _liveElement is NumberElement mn:
                mn.Center = new PixelPoint(
                    _dragStartCenter.X + (phys.X - _dragStart.X),
                    _dragStartCenter.Y + (phys.Y - _dragStart.Y));
                _dragMutated = true;
                _model.RaiseChanged();
                break;
            case Op.MoveElement when _liveElement is BoxedElement ms:
                ms.Bounds = new PixelRect(
                    new PixelPoint(
                        _dragStartBounds.X + (phys.X - _dragStart.X),
                        _dragStartBounds.Y + (phys.Y - _dragStart.Y)),
                    _dragStartBounds.Size);
                _dragMutated = true;
                _model.RaiseChanged();
                break;
            case Op.RadiusElement when _liveElement is ShapeElement rads:
            {
                var lp = rads.ToUnrotated(new Point(phys.X, phys.Y));
                rads.Style = rads.Style with
                {
                    CornerRadiusPercent = RadiusPercentFor(rads.Bounds, lp, _handleIndex),
                };
                _dragMutated = true;
                _model.RaiseChanged();
                _state.SyncStyleFromSelection();
                break;
            }
            case Op.RotateElement when _liveElement is BoxedElement rots:
            {
                var c = rots.Center;
                // 手柄位于元素上方（未旋转时朝向 -90°），故角度需 +90°
                var deg = Math.Atan2(phys.Y - c.Y, phys.X - c.X) * 180 / Math.PI + 90;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    deg = Math.Round(deg / 15) * 15; // Shift 吸附 15°（TOOLS-SPEC §0.1）
                deg = Math.Round(deg);
                while (deg > 180) deg -= 360;
                while (deg < -180) deg += 360;
                rots.RotationDeg = deg;
                _dragMutated = true;
                _model.RaiseChanged();
                _state.SyncStyleFromSelection();
                break;
            }
            case Op.MoveLinePoint when _liveElement is LineElement lp1:
                lp1.Points[_handleIndex] = phys;
                _dragMutated = true;
                _model.RaiseChanged();
                break;
            case Op.MoveLine when _liveElement is LineElement ml:
            {
                var dx = phys.X - _dragStart.X;
                var dy = phys.Y - _dragStart.Y;
                for (var i = 0; i < ml.Points.Count; i++)
                    ml.Points[i] = new PixelPoint(_dragStartPoints[i].X + dx, _dragStartPoints[i].Y + dy);
                _dragMutated = true;
                _model.RaiseChanged();
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
        if (_op is Op.None or Op.CreatePolyline || e.InitialPressMouseButton != MouseButton.Left)
            return; // 折线在点击间松开不结束创建
        var op = _op;
        _op = Op.None;
        e.Pointer.Capture(null);

        switch (op)
        {
            case Op.CreateShape when _liveElement is BoxedElement cs:
            {
                var threshold = 3 * Scaling;
                if (cs.Bounds.Width < threshold || cs.Bounds.Height < threshold)
                {
                    _model.Elements.Remove(cs);
                    _model.RaiseChanged();
                }
                else
                {
                    // 记录插入位置：区域模糊在底层前缀组，重做时保持层序
                    _model.Push(new AddElementCommand(_model, cs, _model.Elements.IndexOf(cs)));
                    _model.Selected = cs;
                    _state.Tool = EditorTool.Select; // 画完自动回到选择工具（TOOLS-SPEC §1）
                }
                break;
            }
            case Op.CreateArrow when _liveElement is LineElement arrow:
            {
                var a = arrow.Points[0];
                var b = arrow.Points[1];
                var len = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
                if (len < 3 * Scaling)
                {
                    _model.Elements.Remove(arrow);
                    _model.RaiseChanged();
                }
                else
                {
                    _model.Push(new AddElementCommand(_model, arrow));
                    _model.Selected = arrow;
                    _state.Tool = EditorTool.Select;
                }
                break;
            }
            case Op.CreateNumber when _liveElement is NumberElement cn:
                _model.Push(new AddElementCommand(_model, cn));
                _model.Selected = cn;
                // 编号工具保持激活：继续点击放置递增编号
                break;
            case Op.MoveElement:
            case Op.ResizeElement:
            case Op.RadiusElement:
            case Op.RotateElement:
            case Op.MoveLinePoint:
            case Op.MoveLine:
                if (_liveElement != null && _dragMutated && _dragBeforeState != null)
                    _model.Push(new MutateElementCommand(
                        _liveElement, _dragBeforeState, _liveElement.CaptureState()));
                break;
            case Op.Selection:
                _selection.PointerReleased();
                break;
        }
        _liveElement = null;
        _dragBeforeState = null;
        UpdateCursor(ToPhysical(e));
        InvalidateVisual();
    }

    /// <summary>结束折线：去掉预览点，过短则丢弃；保留折线工具供连续绘制。</summary>
    private void FinishPolyline(LineElement poly)
    {
        _op = Op.None;
        _liveElement = null;
        if (poly.Points.Count > 1)
            poly.Points.RemoveAt(poly.Points.Count - 1); // 预览点（与双击首击位置重合）
        double len = 0;
        for (var i = 0; i < poly.Points.Count - 1; i++)
            len += Math.Sqrt(
                Math.Pow(poly.Points[i + 1].X - poly.Points[i].X, 2)
                + Math.Pow(poly.Points[i + 1].Y - poly.Points[i].Y, 2));
        if (poly.Points.Count < 2 || len < 3 * Scaling)
        {
            _model.Elements.Remove(poly);
            _model.RaiseChanged();
            return;
        }
        _model.Push(new AddElementCommand(_model, poly));
        // 折线工具保持激活：下次点击开始新的折线
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

    // ---- 命中测试 ----

    /// <returns>选中盒状元素被命中的缩放手柄下标，未命中 -1。</returns>
    private int HitElementHandle(PixelPoint pos)
    {
        if (_model.Selected is not BoxedElement el)
            return -1;
        var hit = SelectionMetrics.HandleHitRadius * Scaling;
        for (var i = 0; i < SelectionController.HandleAnchors.Length; i++)
        {
            var (ax, ay) = SelectionController.HandleAnchors[i];
            var p = new Point(el.Bounds.X + ax * el.Bounds.Width, el.Bounds.Y + ay * el.Bounds.Height);
            var c = BoxedElement.RotatePoint(p, el.Center, el.RotationDeg);
            if (Math.Abs(pos.X - c.X) <= hit && Math.Abs(pos.Y - c.Y) <= hit)
                return i;
        }
        return -1;
    }

    /// <returns>选中形状被命中的圆角手柄下标，未命中 -1。</returns>
    private int HitRadiusHandle(PixelPoint pos)
    {
        if (_model.Selected is not ShapeElement el)
            return -1;
        var hit = SelectionMetrics.HandleHitRadius * Scaling;
        for (var i = 0; i < RadiusCorners.Length; i++)
        {
            var c = BoxedElement.RotatePoint(
                RadiusHandleCenterUnrotated(el, i), el.Center, el.RotationDeg);
            if (Math.Abs(pos.X - c.X) <= hit && Math.Abs(pos.Y - c.Y) <= hit)
                return i;
        }
        return -1;
    }

    private bool HitRotationHandle(PixelPoint pos)
    {
        if (_model.Selected is not BoxedElement el)
            return false;
        var hit = SelectionMetrics.HandleHitRadius * Scaling;
        var c = BoxedElement.RotatePoint(
            RotationHandleCenterUnrotated(el), el.Center, el.RotationDeg);
        return Math.Abs(pos.X - c.X) <= hit && Math.Abs(pos.Y - c.Y) <= hit;
    }

    /// <returns>选中线元素被命中的控制点下标，未命中 -1。</returns>
    private int HitLineVertex(PixelPoint pos)
    {
        if (_model.Selected is not LineElement el)
            return -1;
        var hit = SelectionMetrics.HandleHitRadius * Scaling;
        for (var i = 0; i < el.Points.Count; i++)
        {
            if (Math.Abs(pos.X - el.Points[i].X) <= hit && Math.Abs(pos.Y - el.Points[i].Y) <= hit)
                return i;
        }
        return -1;
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
    private Point RotationHandleCenterUnrotated(BoxedElement el) => new(
        el.Bounds.X + el.Bounds.Width / 2.0,
        el.Bounds.Y - RotationHandleOffset * Scaling);

    private void UpdateCursor(PixelPoint phys)
    {
        // 旋转手柄用自定义环形箭头光标，其余为标准光标
        if (_state.Tool == EditorTool.Select && HitRotationHandle(phys))
        {
            SetCursor("rotate", null);
            return;
        }

        StandardCursorType type;
        if (_state.Tool is EditorTool.Shape or EditorTool.Arrow or EditorTool.Polyline
            or EditorTool.Number or EditorTool.Pixelate or EditorTool.Blur)
        {
            type = StandardCursorType.Cross;
        }
        else if (HitLineVertex(phys) >= 0)
        {
            type = StandardCursorType.Cross; // 线端点/控制点：十字
        }
        else if (HitRadiusHandle(phys) >= 0)
        {
            type = StandardCursorType.Hand;
        }
        else if (HitElementHandle(phys) is var h && h >= 0)
        {
            type = HandleCursors[h];
        }
        else if (_model.HitTest(phys, 4 * Scaling) is { } hover)
        {
            // 文字元素内部为文本光标（点击进入编辑），边框环带为移动
            type = hover is TextElement text && !text.IsOnBorder(phys, 8 * Scaling, 4 * Scaling)
                ? StandardCursorType.Ibeam
                : StandardCursorType.SizeAll;
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
        SetCursor(type.ToString(), type);
    }

    private void SetCursor(string tag, StandardCursorType? type)
    {
        if (tag == _currentCursorTag)
            return;
        _currentCursorTag = tag;
        Cursor = type is { } t ? new Cursor(t) : RotateCursor(Scaling);
    }

    public override void Render(DrawingContext context)
    {
        // 透明底保证整层可命中
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        // 区域模糊的边界提示常显（含创建拖拽中）：处理后的画面与周围底图边界难辨。
        // 画在编辑层 = 仅编辑期可见，复制输出仍无描边
        foreach (var element in _model.Elements)
        {
            if (element is MosaicElement mosaic)
                RenderMosaicBoundary(context, mosaic);
        }

        if (_op is Op.CreateShape or Op.CreateArrow or Op.CreatePolyline)
            return;

        switch (_model.Selected)
        {
            // 文字编辑中也保持显示选中框与手柄（用户要求文本框常显）
            case BoxedElement boxed:
                RenderBoxedHandles(context, boxed);
                break;
            case LineElement line:
                RenderLineHandles(context, line);
                break;
            // 编号不可缩放/旋转：只画虚线选中框（四角按钮由 NumberActionsPanel 叠加）
            case NumberElement number:
            {
                var c = ToLocal(new Point(number.Center.X, number.Center.Y));
                var half = number.Style.Diameter / Scaling / 2 + NumberActionsPanel.OutlineMargin;
                context.DrawRectangle(null, SelectionOutlinePen,
                    new Rect(c.X - half, c.Y - half, half * 2, half * 2));
                break;
            }
        }
    }

    /// <summary>模糊区域边界：1 物理像素暗灰描边 + 向外渐弱的白色发光（深浅底图都可辨）。</summary>
    private void RenderMosaicBoundary(DrawingContext context, MosaicElement el)
    {
        if (el.Bounds.Width <= 0 || el.Bounds.Height <= 0)
            return;
        var local = ToLocal(el.Bounds);
        var s = Scaling;
        using (context.PushTransform(BoxedElement.RotationMatrix(local.Center, el.RotationDeg)))
        {
            for (var i = 3; i >= 1; i--)
            {
                byte alpha = i switch { 3 => 0x14, 2 => 0x2A, _ => 0x46 };
                context.DrawRectangle(null,
                    new Pen(new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF)), 1.6 / s),
                    local.Inflate(i * 1.3 / s));
            }
            context.DrawRectangle(null,
                new Pen(new SolidColorBrush(Color.FromArgb(0xC8, 0x3C, 0x3C, 0x3C)), 1 / s),
                local);
        }
    }

    private void RenderBoxedHandles(DrawingContext context, BoxedElement el)
    {
        var local = ToLocal(el.Bounds);
        const double hs = SelectionMetrics.HandleSize;

        // 所有手柄都画在旋转后的坐标系里（与元素同步旋转）
        using (context.PushTransform(BoxedElement.RotationMatrix(local.Center, el.RotationDeg)))
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

            // 4 个圆角手柄（圆形，橙色描边，仅形状）
            if (el is ShapeElement shape)
            {
                for (var i = 0; i < RadiusCorners.Length; i++)
                {
                    var c = ToLocal(RadiusHandleCenterUnrotated(shape, i));
                    context.DrawEllipse(Brushes.White, RadiusHandlePen, c, hs / 2, hs / 2);
                }
            }
        }
    }

    private void RenderLineHandles(DrawingContext context, LineElement el)
    {
        const double hs = SelectionMetrics.HandleSize;
        foreach (var p in el.Points)
        {
            var c = ToLocal(p);
            context.DrawEllipse(Brushes.White, HandlePen, c, hs / 2 + 0.5, hs / 2 + 0.5);
        }
    }
}
