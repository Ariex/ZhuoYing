using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Zhuoying.Annotations;

public enum TextHAlign
{
    Left,
    Center,
    Right,
}

public enum TextVAlign
{
    Top,
    Center,
    Bottom,
}

/// <summary>
/// 文字样式。FontSize / Padding / StrokeThickness 为物理像素；
/// 外框（背景矩形）与描边各自带独立颜色与透明度（透明度直接烘入颜色 alpha，
/// 单图元绘制，遵守"元素内零重叠"原则，见 TROUBLESHOOTING §11）。
/// </summary>
public sealed record TextStyle
{
    public string FontFamily { get; init; } = "Microsoft YaHei UI";
    public double FontSize { get; init; } = 28;
    public Color Color { get; init; } = Color.FromRgb(0xFF, 0x3B, 0x30);
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public double RotationDeg { get; init; }
    public TextHAlign HAlign { get; init; } = TextHAlign.Left;
    public TextVAlign VAlign { get; init; } = TextVAlign.Top;

    /// <summary>文本到矩形边缘的内边距（物理像素）。</summary>
    public double Padding { get; init; } = 8;

    public bool BoxEnabled { get; init; }
    public Color BoxColor { get; init; } = Colors.White;
    /// <summary>外框圆角 0–100%（同形状语义）。</summary>
    public double BoxRadiusPercent { get; init; } = 10;
    public double BoxOpacity { get; init; } = 100;

    /// <summary>文字描边粗细（0 = 无描边）。</summary>
    public double StrokeThickness { get; init; }
    public Color StrokeColor { get; init; } = Colors.White;
    public double StrokeOpacity { get; init; } = 100;
}

/// <summary>
/// 文字元素：矩形内的多行纯文本（自动换行），支持对齐、外框、描边、旋转。
/// 编辑态由叠加的真实 TextBox 承担（IME/光标原生支持），
/// 此时本元素只画外框（IsEditing）。
/// </summary>
public sealed class TextElement : BoxedElement
{
    public string Text { get; set; } = "";

    public TextStyle Style { get; set; } = new();

    /// <summary>正在被叠加 TextBox 编辑（瞬态，不入撤销快照）：只画外框不画文字。</summary>
    public bool IsEditing { get; set; }

    public override double RotationDeg
    {
        get => Style.RotationDeg;
        set => Style = Style with { RotationDeg = value };
    }

    public override object CaptureState() => (Bounds, Style, Text);

    public override void RestoreState(object state)
    {
        var (bounds, style, text) = ((PixelRect, TextStyle, string))state;
        Bounds = bounds;
        Style = style;
        Text = text;
    }

    public override void Render(DrawingContext context, Func<Point, Point> toLocal, double scale)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var r = new Rect(
            toLocal(new Point(Bounds.X, Bounds.Y)),
            toLocal(new Point(Bounds.Right, Bounds.Bottom)));

        using (context.PushTransform(RotationMatrix(r.Center, Style.RotationDeg)))
        {
            if (Style.BoxEnabled)
            {
                var alpha = (byte)Math.Round(Math.Clamp(Style.BoxOpacity, 0, 100) / 100 * 255);
                var boxBrush = new SolidColorBrush(Color.FromArgb(
                    alpha, Style.BoxColor.R, Style.BoxColor.G, Style.BoxColor.B));
                var rx = Style.BoxRadiusPercent / 100 * r.Width / 2;
                var ry = Style.BoxRadiusPercent / 100 * r.Height / 2;
                context.DrawRectangle(boxBrush, null, r, rx, ry);
            }

            if (string.IsNullOrEmpty(Text))
                return;

            var pad = Style.Padding / scale;
            var inner = r.Deflate(Math.Min(pad, Math.Min(r.Width, r.Height) / 2 - 0.5));
            if (inner.Width < 1 || inner.Height < 1)
                return;

            var formatted = BuildFormattedText(inner.Width, scale);
            var y = Style.VAlign switch
            {
                TextVAlign.Center => inner.Y + (inner.Height - formatted.Height) / 2,
                TextVAlign.Bottom => inner.Bottom - formatted.Height,
                _ => inner.Y,
            };
            var origin = new Point(inner.X, y);

            using (context.PushClip(r))
            {
                if (Style.StrokeThickness > 0)
                {
                    var alpha = (byte)Math.Round(Math.Clamp(Style.StrokeOpacity, 0, 100) / 100 * 255);
                    var pen = new Pen(
                        new SolidColorBrush(Color.FromArgb(
                            alpha, Style.StrokeColor.R, Style.StrokeColor.G, Style.StrokeColor.B)),
                        Style.StrokeThickness / scale)
                    {
                        LineJoin = PenLineJoin.Round,
                    };
                    // 描边画在填充之下；半透明描边与文字填充的重叠被不透明填充覆盖，无叠加感。
                    // 编辑中也画描边（填充文字由叠加编辑框呈现，两者同一排版引擎、逐字对位）
                    var geometry = formatted.BuildGeometry(origin);
                    if (geometry != null)
                        context.DrawGeometry(null, pen, geometry);
                }
                if (!IsEditing)
                    context.DrawText(formatted, origin);
            }
        }
    }

    /// <summary>构建排版对象（宽度约束 + 对齐；目标坐标系单位）。</summary>
    public FormattedText BuildFormattedText(double maxWidth, double scale)
    {
        var formatted = new FormattedText(
            Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily(Style.FontFamily),
                Style.Italic ? FontStyle.Italic : FontStyle.Normal,
                Style.Bold ? FontWeight.Bold : FontWeight.Normal),
            Math.Max(1, Style.FontSize / scale),
            new SolidColorBrush(Style.Color))
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            TextAlignment = Style.HAlign switch
            {
                TextHAlign.Center => TextAlignment.Center,
                TextHAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
        };
        return formatted;
    }

    /// <summary>命中测试：旋转后的矩形内（含边框拖拽带）。</summary>
    public override bool HitTest(PixelPoint p, double slop)
    {
        var lp = ToUnrotated(new Point(p.X, p.Y));
        return lp.X >= Bounds.X - slop && lp.X <= Bounds.Right + slop
               && lp.Y >= Bounds.Y - slop && lp.Y <= Bounds.Bottom + slop;
    }

    /// <summary>点是否落在边框拖拽带上（矩形边缘向内 border、向外 slop 的环带）。</summary>
    public bool IsOnBorder(PixelPoint p, double border, double slop)
    {
        var lp = ToUnrotated(new Point(p.X, p.Y));
        var outer = lp.X >= Bounds.X - slop && lp.X <= Bounds.Right + slop
                    && lp.Y >= Bounds.Y - slop && lp.Y <= Bounds.Bottom + slop;
        if (!outer)
            return false;
        var inner = lp.X >= Bounds.X + border && lp.X <= Bounds.Right - border
                    && lp.Y >= Bounds.Y + border && lp.Y <= Bounds.Bottom - border;
        return !inner;
    }
}
