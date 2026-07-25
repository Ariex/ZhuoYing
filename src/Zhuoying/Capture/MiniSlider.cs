using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Zhuoying.Capture;

/// <summary>
/// 紧凑滑条（细轨道 + 小圆钮，PixPin 风格）。整数取值；
/// 暴露 IsDragging 供弹层的关闭逻辑判断（拖拽中不许关闭）。
/// </summary>
public sealed class MiniSlider : Control
{
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly IBrush FillBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xF0));
    private static readonly Pen ThumbPen = new(FillBrush, 1.5);

    private const double TrackHeight = 3;
    private const double ThumbRadius = 5.5;

    private double _value;
    private bool _dragging;

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;

    public double Value
    {
        get => _value;
        set
        {
            var v = Math.Round(Math.Clamp(value, Minimum, Maximum));
            if (Math.Abs(v - _value) < 0.5 && _value != 0)
            {
                if ((int)v == (int)_value)
                    return;
            }
            _value = v;
            InvalidateVisual();
        }
    }

    public bool IsDragging => _dragging;

    /// <summary>用户交互引起的取值变化（代码赋值不触发）。</summary>
    public event Action<double>? ValueChanged;

    /// <summary>一次拖拽结束（含轨道点击松开）。</summary>
    public event Action? DragEnded;

    public MiniSlider()
    {
        Width = 150;
        Height = 20;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private void SetFromPointer(Point p)
    {
        var usable = Math.Max(1, Bounds.Width - ThumbRadius * 2);
        var t = Math.Clamp((p.X - ThumbRadius) / usable, 0, 1);
        var v = Math.Round(Minimum + t * (Maximum - Minimum));
        if ((int)v == (int)_value)
            return;
        _value = v;
        InvalidateVisual();
        ValueChanged?.Invoke(v);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragging = true;
        SetFromPointer(e.GetPosition(this));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
            SetFromPointer(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
        DragEnded?.Invoke();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var v = Math.Clamp(_value + (e.Delta.Y > 0 ? 1 : -1), Minimum, Maximum);
        if ((int)v != (int)_value)
        {
            _value = v;
            InvalidateVisual();
            ValueChanged?.Invoke(v);
        }
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var cy = Bounds.Height / 2;
        var usable = Math.Max(1, w - ThumbRadius * 2);
        var t = Maximum > Minimum ? (_value - Minimum) / (Maximum - Minimum) : 0;
        var thumbX = ThumbRadius + t * usable;

        // 命中底
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        // 轨道 + 已填充段
        context.DrawRectangle(TrackBrush, null,
            new Rect(ThumbRadius, cy - TrackHeight / 2, usable, TrackHeight), TrackHeight / 2, TrackHeight / 2);
        context.DrawRectangle(FillBrush, null,
            new Rect(ThumbRadius, cy - TrackHeight / 2, Math.Max(0, thumbX - ThumbRadius), TrackHeight),
            TrackHeight / 2, TrackHeight / 2);
        // 圆钮
        context.DrawEllipse(Brushes.White, ThumbPen, new Point(thumbX, cy), ThumbRadius, ThumbRadius);
    }
}
