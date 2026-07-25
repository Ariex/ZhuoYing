using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Zhuoying.Capture;

/// <summary>
/// 选区视图层（铺满覆盖整个虚拟屏幕的截屏窗口）：半透明暗化遮罩、选区边框、
/// 8 手柄、尺寸提示。选区状态与交互逻辑在 <see cref="SelectionController"/>
///（虚拟屏幕物理像素）；本层负责指针事件 DIP → 物理像素上报、选区物理像素 → DIP 渲染。
/// 换算一律手工进行（虚拟屏幕原点 + RenderScaling），与渲染定义上自洽。
/// </summary>
public sealed class SelectionLayer : Control
{
    private static readonly IBrush MaskBrush = new SolidColorBrush(Color.FromArgb(0x77, 0, 0, 0));
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xF0)), 2);
    private static readonly Pen HandlePen = new(new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xF0)), 1.5);
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20));

    private static readonly StandardCursorType[] HandleCursors =
    [
        StandardCursorType.TopLeftCorner, StandardCursorType.TopSide, StandardCursorType.TopRightCorner,
        StandardCursorType.RightSide, StandardCursorType.BottomRightCorner, StandardCursorType.BottomSide,
        StandardCursorType.BottomLeftCorner, StandardCursorType.LeftSide,
    ];

    private readonly SelectionController _controller;
    private readonly PixelPoint _origin; // 虚拟屏幕包围盒左上角（物理像素）
    private StandardCursorType _currentCursor = StandardCursorType.Cross;
    private bool _dragging;

    public SelectionLayer(SelectionController controller, PixelPoint origin)
    {
        _controller = controller;
        _origin = origin;
        _controller.Changed += InvalidateVisual;
    }

    private double Scaling => (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;

    /// <summary>指针位置 → 虚拟屏幕物理像素（DIP × RenderScaling + 原点）。</summary>
    private PixelPoint ToPhysical(PointerEventArgs e)
    {
        var s = Scaling;
        var p = e.GetPosition(this);
        return new PixelPoint(
            _origin.X + (int)Math.Round(p.X * s),
            _origin.Y + (int)Math.Round(p.Y * s));
    }

    /// <summary>虚拟屏幕物理像素矩形 → 窗口 DIP。</summary>
    private Rect ToLocal(PixelRect r)
    {
        var s = Scaling;
        return new Rect(
            (r.X - _origin.X) / s, (r.Y - _origin.Y) / s,
            r.Width / s, r.Height / s);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _controller.IsDragging)
            return;
        _controller.PointerPressed(ToPhysical(e), Scaling);
        _dragging = true;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
            _controller.PointerMoved(ToPhysical(e));
        else if (!_controller.IsDragging)
            UpdateCursor(ToPhysical(e));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging || e.InitialPressMouseButton != MouseButton.Left)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
        _controller.PointerReleased();
        UpdateCursor(ToPhysical(e));
    }

    private void UpdateCursor(PixelPoint physical)
    {
        var handle = _controller.HitHandle(physical, SelectionMetrics.HandleHitRadius * Scaling);
        var type = handle >= 0 ? HandleCursors[handle]
            : _controller.Selection.Contains(physical) && !_controller.IsDefaultSelection
                ? StandardCursorType.SizeAll
                : StandardCursorType.Cross;
        if (type == _currentCursor)
            return;
        _currentCursor = type;
        Cursor = new Cursor(type);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);

        // 透明底：保证选区镂空后整层仍可命中指针事件
        context.DrawRectangle(Brushes.Transparent, null, bounds);

        var physical = _controller.Selection;
        var hasSelection = physical.Width > 0 && physical.Height > 0;
        var sel = hasSelection ? ToLocal(physical) : default;

        // 遮罩：全屏暗化，选区镂空（EvenOdd）
        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry(bounds));
        if (hasSelection)
            mask.Children.Add(new RectangleGeometry(sel));
        context.DrawGeometry(MaskBrush, null, mask);

        if (!hasSelection)
            return;

        context.DrawRectangle(null, BorderPen, sel);
        DrawHandles(context, sel);
        DrawSizeLabel(context, sel, physical);
    }

    private void DrawHandles(DrawingContext context, Rect sel)
    {
        // 新建拖拽过程中不画手柄，避免与十字光标视觉冲突
        if (_controller.IsCreating)
            return;
        const double size = SelectionMetrics.HandleSize;
        foreach (var (ax, ay) in SelectionController.HandleAnchors)
        {
            var center = new Point(sel.X + ax * sel.Width, sel.Y + ay * sel.Height);
            var rect = new Rect(center.X - size / 2, center.Y - size / 2, size, size);
            context.DrawRectangle(Brushes.White, HandlePen, rect, 1.5, 1.5);
        }
    }

    private void DrawSizeLabel(DrawingContext context, Rect sel, PixelRect physical)
    {
        // 选区本身就是物理像素，直接显示，与输出尺寸严格一致
        var text = new FormattedText(
            $"{physical.Width} × {physical.Height}",
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
