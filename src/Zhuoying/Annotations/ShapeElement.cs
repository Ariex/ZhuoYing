using System;
using Avalonia;
using Avalonia.Media;

namespace Zhuoying.Annotations;

/// <summary>
/// 矩形形状元素（圆角可调，100% 圆角 = 椭圆）。
/// 几何为虚拟屏幕物理像素；渲染时由调用方提供物理 → 目标坐标换算。
/// </summary>
public sealed class ShapeElement : AnnotationElement
{
    /// <summary>包围矩形（虚拟屏幕物理像素，已规范化）。</summary>
    public PixelRect Bounds { get; set; }

    public ShapeStyle Style { get; set; } = new();

    public override object CaptureState() => (Bounds, Style);

    public override void RestoreState(object state)
    {
        var (bounds, style) = ((PixelRect, ShapeStyle))state;
        Bounds = bounds;
        Style = style;
    }

    public override void Render(DrawingContext context, Func<Point, Point> toLocal, double scale)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var r = new Rect(
            toLocal(new Point(Bounds.X, Bounds.Y)),
            toLocal(new Point(Bounds.Right, Bounds.Bottom)));
        var rx = Style.CornerRadiusPercent / 100 * r.Width / 2;
        var ry = Style.CornerRadiusPercent / 100 * r.Height / 2;
        var brush = new SolidColorBrush(Style.Color);
        var dashStyle = BuildDashStyle();
        var pen = new Pen(brush, Style.Thickness / scale)
        {
            DashStyle = dashStyle,
            // 虚线段圆头 + 拐角圆连接（PixPin 风格）；实线保持锐角
            LineCap = dashStyle == null ? PenLineCap.Flat : PenLineCap.Round,
            LineJoin = dashStyle == null ? PenLineJoin.Miter : PenLineJoin.Round,
        };
        using (context.PushTransform(RotationMatrix(r.Center, Style.RotationDeg)))
        using (context.PushOpacity(Math.Clamp(Style.Opacity, 0, 100) / 100))
        {
            // PushOpacity 按图元逐个混合，填充若延伸到描边下方，半透明时重叠区会变深。
            // 因此填充内缩到描边内沿，与描边不重叠（圆角相应减小）
            if (Style.Filled)
            {
                var inset = Style.Thickness / scale / 2;
                if (r.Width > inset * 2 && r.Height > inset * 2)
                {
                    context.DrawRectangle(brush, null, r.Deflate(inset),
                        Math.Max(0, rx - inset), Math.Max(0, ry - inset));
                }
            }
            context.DrawRectangle(null, pen, r, rx, ry);
        }
    }

    /// <summary>绕 center 旋转 deg 度的变换矩阵。</summary>
    public static Matrix RotationMatrix(Point center, double deg)
    {
        if (deg == 0)
            return Matrix.Identity;
        var rad = deg * Math.PI / 180;
        return Matrix.CreateTranslation(-center.X, -center.Y)
               * Matrix.CreateRotation(rad)
               * Matrix.CreateTranslation(center.X, center.Y);
    }

    /// <summary>绕 center 把点旋转 deg 度。</summary>
    public static Point RotatePoint(Point p, Point center, double deg)
    {
        if (deg == 0)
            return p;
        var rad = deg * Math.PI / 180;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = p.X - center.X;
        var dy = p.Y - center.Y;
        return new Point(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    /// <summary>包围盒中心（物理像素）。</summary>
    public Point Center => new(Bounds.X + Bounds.Width / 2.0, Bounds.Y + Bounds.Height / 2.0);

    /// <summary>把物理像素点逆旋转到元素未旋转坐标系。</summary>
    public Point ToUnrotated(Point p) => RotatePoint(p, Center, -Style.RotationDeg);

    private DashStyle? BuildDashStyle()
    {
        var dashes = LineStyles.All[Math.Clamp(Style.LineStyleIndex, 0, LineStyles.All.Length - 1)].Dashes;
        return dashes == null ? null : new DashStyle(dashes, 0);
    }

    /// <summary>
    /// 命中测试（物理像素，已考虑旋转）。整个形状内部均可命中
    ///（未填充也一样——点击形状内拖拽应移动形状而非操作选区）。
    /// </summary>
    public override bool HitTest(PixelPoint p, double slop)
    {
        var half = Style.Thickness / 2 + slop;
        return ContainsRounded(ToUnrotated(new Point(p.X, p.Y)), half);
    }

    /// <summary>点（未旋转坐标系）是否在按 expand 外扩（负值内缩）后的圆角矩形内。</summary>
    private bool ContainsRounded(Point p, double expand)
    {
        double x = Bounds.X - expand, y = Bounds.Y - expand;
        double w = Bounds.Width + expand * 2, h = Bounds.Height + expand * 2;
        if (w <= 0 || h <= 0)
            return false;
        if (p.X < x || p.X > x + w || p.Y < y || p.Y > y + h)
            return false;
        // 圆角（椭圆角）区域：角带内按椭圆方程判定
        var rx = Math.Clamp(Style.CornerRadiusPercent / 100 * Bounds.Width / 2 + expand, 0, w / 2);
        var ry = Math.Clamp(Style.CornerRadiusPercent / 100 * Bounds.Height / 2 + expand, 0, h / 2);
        if (rx <= 0 || ry <= 0)
            return true;
        var cx = Math.Max(x + rx - p.X, p.X - (x + w - rx));
        var cy = Math.Max(y + ry - p.Y, p.Y - (y + h - ry));
        if (cx <= 0 || cy <= 0)
            return true;
        return cx * cx / (rx * rx) + cy * cy / (ry * ry) <= 1;
    }
}
