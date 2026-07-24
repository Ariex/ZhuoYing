using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Zhuoying.Capture;

/// <summary>
/// 选区交互与渲染层：半透明暗化遮罩、选区边框、尺寸提示（物理像素）。
/// 坐标为窗口 DIP；对外换算物理像素时使用窗口 RenderScaling。
/// </summary>
public sealed class SelectionLayer : Control
{
    private static readonly IBrush MaskBrush = new SolidColorBrush(Color.FromArgb(0x77, 0, 0, 0));
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xF0)), 2);
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20));

    private Rect _selection;
    private Rect _preDragSelection;
    private Point _dragStart;
    private bool _dragging;

    public Rect Selection => _selection;
    public bool IsDragging => _dragging;

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
        _dragging = true;
        _preDragSelection = _selection;
        _dragStart = Clamp(e.GetPosition(this));
        _selection = new Rect(_dragStart, _dragStart);
        DragStarted?.Invoke();
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
            return;
        var pos = Clamp(e.GetPosition(this));
        _selection = new Rect(
            Math.Min(_dragStart.X, pos.X), Math.Min(_dragStart.Y, pos.Y),
            Math.Abs(pos.X - _dragStart.X), Math.Abs(pos.Y - _dragStart.Y));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging || e.InitialPressMouseButton != MouseButton.Left)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
        // 位移过小视为误点击，恢复原选区（对应 TOOLS-SPEC "< 3px 丢弃" 的同类规则）
        if (_selection.Width < 3 || _selection.Height < 3)
            _selection = _preDragSelection;
        InvalidateVisual();
        DragCompleted?.Invoke(_selection);
    }

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, 0, Bounds.Width),
        Math.Clamp(p.Y, 0, Bounds.Height));

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
        DrawSizeLabel(context, sel);
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
