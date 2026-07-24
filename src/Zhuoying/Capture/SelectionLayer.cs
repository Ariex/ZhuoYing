using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Zhuoying.Capture;

/// <summary>
/// 选区交互与渲染层：半透明暗化遮罩、选区边框、8 手柄调整、选区内拖拽移动、
/// 尺寸提示（物理像素）。坐标为窗口 DIP；对外换算物理像素时使用窗口 RenderScaling。
/// </summary>
public sealed class SelectionLayer : Control
{
    private static readonly IBrush MaskBrush = new SolidColorBrush(Color.FromArgb(0x77, 0, 0, 0));
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xF0)), 2);
    private static readonly Pen HandlePen = new(new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xF0)), 1.5);
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20));

    private const double HandleSize = 8;      // 手柄边长（DIP）
    private const double HandleHitRadius = 10; // 手柄命中半径（DIP）

    /// <summary>8 个手柄的锚点系数（x, y ∈ {0, 0.5, 1}），顺序：四角 + 四边中点。</summary>
    private static readonly (double X, double Y)[] HandleAnchors =
    [
        (0, 0), (0.5, 0), (1, 0),
        (1, 0.5), (1, 1), (0.5, 1),
        (0, 1), (0, 0.5),
    ];

    private static readonly StandardCursorType[] HandleCursors =
    [
        StandardCursorType.TopLeftCorner, StandardCursorType.TopSide, StandardCursorType.TopRightCorner,
        StandardCursorType.RightSide, StandardCursorType.BottomRightCorner, StandardCursorType.BottomSide,
        StandardCursorType.BottomLeftCorner, StandardCursorType.LeftSide,
    ];

    private enum DragMode
    {
        None,
        Create,
        Move,
        Resize,
        /// <summary>按在选区外：已预览"角扩展到按下点"，拖拽则转为 Create，直接松开则保留扩展结果。</summary>
        Expand,
    }

    private const double DragThreshold = 3;

    private Rect _selection;
    private Rect _dragStartRect;
    private Point _dragStart;
    private DragMode _mode;
    private int _handleIndex;
    private StandardCursorType _currentCursor = StandardCursorType.Cross;

    public Rect Selection => _selection;
    public bool IsDragging => _mode != DragMode.None;

    /// <summary>拖拽开始（用于隐藏工具条）。</summary>
    public event Action? DragStarted;

    /// <summary>拖拽结束，参数为最终选区。</summary>
    public event Action<Rect>? DragCompleted;

    public void SetSelection(Rect rect)
    {
        _selection = rect;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var pos = Clamp(e.GetPosition(this));
        _dragStart = pos;
        _dragStartRect = _selection;

        var handle = HitHandle(pos);
        if (handle >= 0)
        {
            _mode = DragMode.Resize;
            _handleIndex = handle;
        }
        else if (IsFullBounds(_selection))
        {
            // 默认全屏选区：内部按下即开始画新矩形（否则无法重新框选）
            _mode = DragMode.Create;
            _selection = new Rect(pos, pos);
        }
        else if (_selection.Contains(pos))
        {
            _mode = DragMode.Move;
        }
        else
        {
            // 选区外按下：先预览把最近的角/边扩展到按下点；
            // 后续拖拽超过阈值则转为画新矩形，直接松开则保留扩展结果。
            // 注意不能用 Rect.Union——它对零尺寸矩形（单点）有空矩形特判会原样返回
            _mode = DragMode.Expand;
            var left = Math.Min(_dragStartRect.X, pos.X);
            var top = Math.Min(_dragStartRect.Y, pos.Y);
            var right = Math.Max(_dragStartRect.Right, pos.X);
            var bottom = Math.Max(_dragStartRect.Bottom, pos.Y);
            _selection = new Rect(left, top, right - left, bottom - top);
        }

        DragStarted?.Invoke();
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = Clamp(e.GetPosition(this));

        switch (_mode)
        {
            case DragMode.None:
                UpdateCursor(pos);
                return;
            case DragMode.Create:
                _selection = new Rect(
                    Math.Min(_dragStart.X, pos.X), Math.Min(_dragStart.Y, pos.Y),
                    Math.Abs(pos.X - _dragStart.X), Math.Abs(pos.Y - _dragStart.Y));
                break;
            case DragMode.Move:
                var dx = Math.Clamp(pos.X - _dragStart.X, -_dragStartRect.X, Bounds.Width - _dragStartRect.Right);
                var dy = Math.Clamp(pos.Y - _dragStart.Y, -_dragStartRect.Y, Bounds.Height - _dragStartRect.Bottom);
                _selection = new Rect(_dragStartRect.X + dx, _dragStartRect.Y + dy,
                    _dragStartRect.Width, _dragStartRect.Height);
                break;
            case DragMode.Resize:
                _selection = ResizeByHandle(pos);
                break;
            case DragMode.Expand:
                if (Math.Abs(pos.X - _dragStart.X) >= DragThreshold
                    || Math.Abs(pos.Y - _dragStart.Y) >= DragThreshold)
                {
                    _mode = DragMode.Create;
                    _selection = new Rect(
                        Math.Min(_dragStart.X, pos.X), Math.Min(_dragStart.Y, pos.Y),
                        Math.Abs(pos.X - _dragStart.X), Math.Abs(pos.Y - _dragStart.Y));
                }
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mode == DragMode.None || e.InitialPressMouseButton != MouseButton.Left)
            return;
        var mode = _mode;
        _mode = DragMode.None;
        e.Pointer.Capture(null);
        // 新建时位移过小视为误点击，恢复原选区（Expand 模式松开则保留扩展结果）
        if (mode == DragMode.Create
            && (_selection.Width < DragThreshold || _selection.Height < DragThreshold))
            _selection = _dragStartRect;
        InvalidateVisual();
        UpdateCursor(Clamp(e.GetPosition(this)));
        DragCompleted?.Invoke(_selection);
    }

    private Rect ResizeByHandle(Point pos)
    {
        var (ax, ay) = HandleAnchors[_handleIndex];
        double left = _dragStartRect.X, top = _dragStartRect.Y;
        double right = _dragStartRect.Right, bottom = _dragStartRect.Bottom;
        if (ax == 0) left = pos.X;
        else if (ax == 1) right = pos.X;
        if (ay == 0) top = pos.Y;
        else if (ay == 1) bottom = pos.Y;
        // 允许拖拽越过对边，自动翻转
        return new Rect(
            Math.Min(left, right), Math.Min(top, bottom),
            Math.Abs(right - left), Math.Abs(bottom - top));
    }

    /// <returns>命中的手柄下标，未命中返回 -1。</returns>
    private int HitHandle(Point pos)
    {
        if (_selection.Width <= 0 || _selection.Height <= 0)
            return -1;
        for (var i = 0; i < HandleAnchors.Length; i++)
        {
            var center = HandleCenter(i);
            if (Math.Abs(pos.X - center.X) <= HandleHitRadius && Math.Abs(pos.Y - center.Y) <= HandleHitRadius)
                return i;
        }
        return -1;
    }

    private Point HandleCenter(int index)
    {
        var (ax, ay) = HandleAnchors[index];
        return new Point(_selection.X + ax * _selection.Width, _selection.Y + ay * _selection.Height);
    }

    private void UpdateCursor(Point pos)
    {
        var handle = HitHandle(pos);
        var type = handle >= 0 ? HandleCursors[handle]
            : _selection.Contains(pos) && !IsFullBounds(_selection) ? StandardCursorType.SizeAll
            : StandardCursorType.Cross;
        if (type == _currentCursor)
            return;
        _currentCursor = type;
        Cursor = new Cursor(type);
    }

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, 0, Bounds.Width),
        Math.Clamp(p.Y, 0, Bounds.Height));

    /// <summary>选区是否即整个屏幕（容差 0.5 DIP）。</summary>
    private bool IsFullBounds(Rect rect) =>
        rect.X <= 0.5 && rect.Y <= 0.5
        && rect.Right >= Bounds.Width - 0.5 && rect.Bottom >= Bounds.Height - 0.5;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var sel = _selection.Intersect(bounds);

        // 透明底：保证选区镂空后整层仍可命中指针事件
        context.DrawRectangle(Brushes.Transparent, null, bounds);

        // 遮罩：全屏暗化，选区镂空（EvenOdd）
        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry(bounds));
        if (sel.Width > 0 && sel.Height > 0)
            mask.Children.Add(new RectangleGeometry(sel));
        context.DrawGeometry(MaskBrush, null, mask);

        if (sel.Width <= 0 || sel.Height <= 0)
            return;

        context.DrawRectangle(null, BorderPen, sel);
        DrawHandles(context);
        DrawSizeLabel(context, sel);
    }

    private void DrawHandles(DrawingContext context)
    {
        // 新建拖拽过程中不画手柄，避免与十字光标视觉冲突
        if (_mode == DragMode.Create)
            return;
        for (var i = 0; i < HandleAnchors.Length; i++)
        {
            var center = HandleCenter(i);
            var rect = new Rect(
                center.X - HandleSize / 2, center.Y - HandleSize / 2, HandleSize, HandleSize);
            context.DrawRectangle(Brushes.White, HandlePen, rect, 1.5, 1.5);
        }
    }

    private void DrawSizeLabel(DrawingContext context, Rect sel)
    {
        var scaling = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        // 与裁剪取整方式保持一致，保证提示值与输出尺寸吻合
        var x1 = (int)Math.Round(sel.X * scaling);
        var y1 = (int)Math.Round(sel.Y * scaling);
        var x2 = (int)Math.Round(sel.Right * scaling);
        var y2 = (int)Math.Round(sel.Bottom * scaling);
        var text = new FormattedText(
            $"{x2 - x1} × {y2 - y1}",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 12, Brushes.White);

        const double padX = 6, padY = 3, gap = 4;
        var labelSize = new Size(text.Width + padX * 2, text.Height + padY * 2);
        var y = sel.Y - gap - labelSize.Height;
        if (y < 0)
            y = sel.Y + gap; // 上方放不下则放选区内部
        var x = Math.Clamp(sel.X, 0, Math.Max(0, Bounds.Width - labelSize.Width));

        var labelRect = new Rect(new Point(x, y), labelSize);
        context.DrawRectangle(LabelBackground, null, labelRect, 3, 3);
        context.DrawText(text, new Point(x + padX, y + padY));
    }
}
