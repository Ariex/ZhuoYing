using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace Zhuoying.Annotations;

/// <summary>
/// 笔迹带状几何（画笔荧光裁剪 / 橡皮擦除区共用）：
/// 折线两侧等距偏移 + 折点 bevel + 两端半圆帽，NonZero 填充规则保证
/// 自交（来回涂抹）不产生孔洞。
/// </summary>
internal static class StrokeGeometry
{
    /// <summary>由物理像素点列构建目标坐标系的带状填充几何。</summary>
    public static Geometry BuildBand(
        IReadOnlyList<PixelPoint> points, Func<Point, Point> toLocal, double half)
    {
        // 折算到目标坐标系并去除重合点
        var pts = new List<Point>(points.Count);
        foreach (var p in points)
        {
            var lp = toLocal(new Point(p.X, p.Y));
            if (pts.Count == 0 || Math.Abs(lp.X - pts[^1].X) > 0.01 || Math.Abs(lp.Y - pts[^1].Y) > 0.01)
                pts.Add(lp);
        }
        if (pts.Count == 0)
            return new EllipseGeometry();
        if (pts.Count == 1)
            return new EllipseGeometry(new Rect(
                pts[0].X - half, pts[0].Y - half, half * 2, half * 2));

        var left = new List<Point>();
        var right = new List<Point>();
        for (var i = 0; i < pts.Count; i++)
        {
            if (i > 0)
            {
                var n = Normal(pts[i - 1], pts[i]);
                left.Add(pts[i] + n * half);
                right.Add(pts[i] - n * half);
            }
            if (i < pts.Count - 1)
            {
                var n = Normal(pts[i], pts[i + 1]);
                left.Add(pts[i] + n * half);
                right.Add(pts[i] - n * half);
            }
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.SetFillRule(FillRule.NonZero);
            ctx.BeginFigure(right[0], true);
            AppendCapArc(ctx, pts[0], right[0], left[0]);      // 起端半圆帽
            foreach (var p in left)
                ctx.LineTo(p);
            AppendCapArc(ctx, pts[^1], left[^1], right[^1]);   // 末端半圆帽
            for (var i = right.Count - 1; i >= 0; i--)
                ctx.LineTo(right[i]);
            ctx.EndFigure(true);
        }
        return geometry;
    }

    private static Vector Normal(Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-6 ? new Vector(0, 0) : new Vector(-dy / len, dx / len);
    }

    /// <summary>绕 center 从 from 到 to 补一段半圆（8 段折线近似，向外凸）。</summary>
    private static void AppendCapArc(StreamGeometryContext ctx, Point center, Point from, Point to)
    {
        var a0 = Math.Atan2(from.Y - center.Y, from.X - center.X);
        var a1 = Math.Atan2(to.Y - center.Y, to.X - center.X);
        while (a1 > a0)
            a1 -= Math.PI * 2;
        var r = Math.Sqrt(Math.Pow(from.X - center.X, 2) + Math.Pow(from.Y - center.Y, 2));
        const int steps = 8;
        for (var i = 1; i <= steps; i++)
        {
            var a = a0 + (a1 - a0) * i / steps;
            ctx.LineTo(new Point(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a)));
        }
    }
}
