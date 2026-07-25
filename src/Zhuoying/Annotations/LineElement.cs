using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;

namespace Zhuoying.Annotations;

/// <summary>线段端头样式。</summary>
public enum LineCapKind
{
    None,
    SolidTriangle,
    SolidDiamond,
    SolidCircle,
    SolidSquare,
    HollowTriangle,
    HollowDiamond,
    HollowCircle,
    HollowSquare,
    /// <summary>两根线的箭头（开放 V 形）。</summary>
    LineArrow,
    /// <summary>破甲箭头（凹底三角）。</summary>
    Barbed,
}

/// <summary>
/// 线段/箭头样式。首尾粗细可不同（渐变带渲染，此时忽略线形——
/// 变宽路径上的虚线切段无良好定义）；等粗时支持全部线形。
/// </summary>
public sealed record LineStyle
{
    public Color Color { get; init; } = Color.FromRgb(0xFF, 0x3B, 0x30);
    public int LineStyleIndex { get; init; }
    public double StartThickness { get; init; } = 5;
    public double EndThickness { get; init; } = 5;
    public LineCapKind StartCap { get; init; }
    public LineCapKind EndCap { get; init; }
    /// <summary>弧线：用全部控制点画 Catmull-Rom 样条。</summary>
    public bool Spline { get; init; }
    public double Opacity { get; init; } = 100;
}

/// <summary>
/// 折线/箭头元素：N 个控制点（≥2），可样条化；两端可挂端头图形。
/// 箭头 = 两点折线 + 末端实心三角（工具层的差别，模型统一）。
/// </summary>
public sealed class LineElement : AnnotationElement
{
    public List<PixelPoint> Points { get; } = [];
    public LineStyle Style { get; set; } = new();

    /// <summary>由箭头工具创建（样式槽同步用）。</summary>
    public bool IsArrowTool { get; init; }

    public override object CaptureState() => (Points.ToArray(), Style);

    public override void RestoreState(object state)
    {
        var (points, style) = ((PixelPoint[], LineStyle))state;
        Points.Clear();
        Points.AddRange(points);
        Style = style;
    }

    public override void Render(DrawingContext context, Func<Point, Point> toLocal, double scale)
    {
        if (Points.Count < 2)
            return;
        var raw = Points.Select(p => toLocal(new Point(p.X, p.Y))).ToList();
        var pts = SamplePath(raw, Style.Spline);
        if (PathLength(pts) < 0.01)
            return;

        var t0 = Style.StartThickness / scale;
        var t1 = Style.EndThickness / scale;
        var color = Style.Color;
        var brush = new SolidColorBrush(color);

        // 端头几何参数与线身裁剪。等粗描边为圆头笔帽，端点外还凸出半线宽，
        // 有几何端头的一端需额外让位（空心端头再加其描边半宽），
        // 否则半透明时圆头扎进端头形成重叠加深
        var uniform = Math.Abs(t0 - t1) < 0.01;
        var startDir = Normalize(pts[0] - pts[1]);
        var endDir = Normalize(pts[^1] - pts[^2]);
        var startTip = pts[0];
        var endTip = pts[^1];
        var body = TrimPath(pts,
            CapInset(Style.StartCap, CapLength(t0)) + BodyExtraInset(Style.StartCap, t0, uniform),
            CapInset(Style.EndCap, CapLength(t1)) + BodyExtraInset(Style.EndCap, t1, uniform));

        using (context.PushOpacity(Math.Clamp(Style.Opacity, 0, 100) / 100))
        {
            // 渐变带时把实心三角/破甲端头融合进同一个多边形（单图元），
            // 端头与线身零重叠、零缝隙——半透明箭头的关键
            var fuseStart = !uniform && Fusible(Style.StartCap);
            var fuseEnd = !uniform && Fusible(Style.EndCap);

            if (body.Count >= 2)
            {
                if (uniform)
                {
                    var dashes = LineStyles.All[
                        Math.Clamp(Style.LineStyleIndex, 0, LineStyles.All.Length - 1)].Dashes;
                    var pen = new Pen(brush, Math.Max(t0, 0.5))
                    {
                        DashStyle = dashes == null ? null : new DashStyle(dashes, 0),
                        LineCap = PenLineCap.Round,
                        LineJoin = PenLineJoin.Round,
                    };
                    context.DrawGeometry(null, pen, BuildOpenPath(body));
                }
                else
                {
                    var w0 = ThicknessAt(pts, body[0], t0, t1);
                    var w1 = ThicknessAt(pts, body[^1], t0, t1);
                    context.DrawGeometry(brush, null, BuildTaperedGeometry(
                        body, w0, w1,
                        fuseStart ? (Style.StartCap, startTip, startDir, t0) : null,
                        fuseEnd ? (Style.EndCap, endTip, endDir, t1) : null));
                }
            }
            if (!fuseStart || body.Count < 2)
                DrawCap(context, startTip, startDir, t0, Style.StartCap, color);
            if (!fuseEnd || body.Count < 2)
                DrawCap(context, endTip, endDir, t1, Style.EndCap, color);
        }
    }

    public override bool HitTest(PixelPoint p, double slop)
    {
        if (Points.Count < 2)
            return false;
        var pts = SamplePath(Points.Select(q => new Point(q.X, q.Y)).ToList(), Style.Spline);
        var half = Math.Max(Style.StartThickness, Style.EndThickness) / 2 + slop;
        var probe = new Point(p.X, p.Y);
        for (var i = 0; i < pts.Count - 1; i++)
        {
            if (DistToSegment(probe, pts[i], pts[i + 1]) <= half)
                return true;
        }
        // 端头区域（按端头长度放宽）
        var capR0 = CapLength(Style.StartThickness);
        var capR1 = CapLength(Style.EndThickness);
        return Dist(probe, pts[0]) <= capR0 + slop || Dist(probe, pts[^1]) <= capR1 + slop;
    }

    // ---- 路径 ----

    /// <summary>Catmull-Rom 样条采样（穿过全部控制点）；非样条或点数不足时原样返回。</summary>
    public static List<Point> SamplePath(List<Point> pts, bool spline)
    {
        if (!spline || pts.Count < 3)
            return pts;
        const int steps = 14;
        var result = new List<Point> { pts[0] };
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
            for (var s = 1; s <= steps; s++)
            {
                var t = (double)s / steps;
                var t2 = t * t;
                var t3 = t2 * t;
                result.Add(new Point(
                    0.5 * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2
                           + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                    0.5 * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2
                           + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)));
            }
        }
        return result;
    }

    private static double PathLength(List<Point> pts)
    {
        double len = 0;
        for (var i = 0; i < pts.Count - 1; i++)
            len += Dist(pts[i], pts[i + 1]);
        return len;
    }

    /// <summary>从两端各裁掉指定弧长（为端头让位）。裁完不足时返回空。</summary>
    private static List<Point> TrimPath(List<Point> pts, double startInset, double endInset)
    {
        if (startInset <= 0 && endInset <= 0)
            return pts;
        var total = PathLength(pts);
        if (startInset + endInset >= total)
            return [];
        var result = new List<Point>(pts);
        result = TrimFront(result, startInset);
        result.Reverse();
        result = TrimFront(result, endInset);
        result.Reverse();
        return result;
    }

    private static List<Point> TrimFront(List<Point> pts, double inset)
    {
        if (inset <= 0)
            return pts;
        var result = new List<Point>();
        double acc = 0;
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var seg = Dist(pts[i], pts[i + 1]);
            if (acc + seg <= inset)
            {
                acc += seg;
                continue;
            }
            if (result.Count == 0)
            {
                var t = seg <= 0 ? 0 : (inset - acc) / seg;
                result.Add(Lerp(pts[i], pts[i + 1], t));
            }
            result.Add(pts[i + 1]);
            acc += seg;
        }
        return result;
    }

    /// <summary>按点在原路径上的弧长位置插值粗细。</summary>
    private static double ThicknessAt(List<Point> full, Point p, double t0, double t1)
    {
        var total = PathLength(full);
        if (total <= 0)
            return t0;
        double acc = 0, best = 0, bestDist = double.MaxValue;
        double walked = 0;
        for (var i = 0; i < full.Count - 1; i++)
        {
            var seg = Dist(full[i], full[i + 1]);
            var d = DistToSegment(p, full[i], full[i + 1]);
            if (d < bestDist)
            {
                bestDist = d;
                var t = seg <= 0 ? 0 : Math.Clamp(Dot(p - full[i], full[i + 1] - full[i]) / (seg * seg), 0, 1);
                best = walked + seg * t;
            }
            walked += seg;
            acc = walked;
        }
        return t0 + (t1 - t0) * (best / total);
    }

    private static StreamGeometry BuildOpenPath(List<Point> pts)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(pts[0], false);
        for (var i = 1; i < pts.Count; i++)
            c.LineTo(pts[i]);
        c.EndFigure(false);
        return g;
    }

    /// <summary>可融合进渐变带的端头（实心尖形类）。</summary>
    private static bool Fusible(LineCapKind kind) =>
        kind is LineCapKind.None or LineCapKind.SolidTriangle or LineCapKind.Barbed;

    /// <summary>
    /// 渐变宽度实心带（可选融合两端的实心三角/破甲端头为同一多边形）。
    /// 沿路径两侧按弧长插值宽度偏移；锐角折点用斜切双点（bevel），
    /// 避免斜接偏移交叉产生自交尖刺。
    /// </summary>
    private static StreamGeometry BuildTaperedGeometry(
        List<Point> body, double w0, double w1,
        (LineCapKind Kind, Point Tip, Vector DirOut, double Thickness)? startCap,
        (LineCapKind Kind, Point Tip, Vector DirOut, double Thickness)? endCap)
    {
        var total = PathLength(body);
        var left = new List<Point>();
        var right = new List<Point>();
        double walked = 0;
        for (var i = 0; i < body.Count; i++)
        {
            if (i > 0)
                walked += Dist(body[i - 1], body[i]);
            var w = (w0 + (w1 - w0) * (total <= 0 ? 0 : walked / total)) / 2;
            var nPrev = i > 0 ? Normal(body[i - 1], body[i]) : Normal(body[i], body[i + 1]);
            var nNext = i < body.Count - 1 ? Normal(body[i], body[i + 1]) : nPrev;
            if (i > 0 && i < body.Count - 1 && Dot(nPrev, nNext) < 0.5)
            {
                // 锐角：斜切两点，防止斜接点越过对侧形成自交
                left.Add(body[i] + nPrev * w);
                left.Add(body[i] + nNext * w);
                right.Add(body[i] - nPrev * w);
                right.Add(body[i] - nNext * w);
            }
            else
            {
                var n = Normalize(nPrev + nNext);
                var miter = 1.0 / Math.Max(0.5, Dot(n, nNext));
                n *= Math.Min(miter, 1.5);
                left.Add(body[i] + n * w);
                right.Add(body[i] - n * w);
            }
        }

        // 端头翼点：与渐变带同一图形内衔接（left 侧法线 = 路径前进方向的左法线 = -dirOut 的左法线）
        var g = new StreamGeometry();
        using var c = g.Open();
        if (startCap is { } sc && sc.Kind != LineCapKind.None)
        {
            var l = CapLength(sc.Thickness);
            var half = sc.Kind == LineCapKind.Barbed ? l * 0.55 : l * 0.5;
            var back = sc.Tip - sc.DirOut * l;
            var n = new Vector(-sc.DirOut.Y, sc.DirOut.X);
            // 起端：路径前进方向 f = -DirOut，其左法线 = -n
            c.BeginFigure(sc.Tip, true);
            c.LineTo(back - n * half);
            for (var i = 0; i < left.Count; i++)
                c.LineTo(left[i]);
        }
        else
        {
            c.BeginFigure(left[0], true);
            for (var i = 1; i < left.Count; i++)
                c.LineTo(left[i]);
        }

        if (endCap is { } ec && ec.Kind != LineCapKind.None)
        {
            var l = CapLength(ec.Thickness);
            var half = ec.Kind == LineCapKind.Barbed ? l * 0.55 : l * 0.5;
            var back = ec.Tip - ec.DirOut * l;
            var n = new Vector(-ec.DirOut.Y, ec.DirOut.X);
            // 末端：DirOut 即路径前进方向，其左法线 = n，与 left[^1] 同侧
            c.LineTo(back + n * half);
            c.LineTo(ec.Tip);
            c.LineTo(back - n * half);
        }

        for (var i = right.Count - 1; i >= 0; i--)
            c.LineTo(right[i]);

        if (startCap is { } sc2 && sc2.Kind != LineCapKind.None)
        {
            var l = CapLength(sc2.Thickness);
            var half = sc2.Kind == LineCapKind.Barbed ? l * 0.55 : l * 0.5;
            var back = sc2.Tip - sc2.DirOut * l;
            var n = new Vector(-sc2.DirOut.Y, sc2.DirOut.X);
            c.LineTo(back + n * half);
        }
        c.EndFigure(true);
        return g;
    }

    // ---- 端头 ----

    /// <summary>端头长度（随该端线宽等比，有下限）。</summary>
    public static double CapLength(double thickness) => Math.Max(8, thickness * 3);

    /// <summary>圆头笔帽/空心描边导致的额外让位（等粗圆头凸出半线宽；渐变带为平头只需空心补偿）。</summary>
    private static double BodyExtraInset(LineCapKind kind, double thickness, bool roundCapStroke) => kind switch
    {
        LineCapKind.None or LineCapKind.LineArrow => 0,
        LineCapKind.HollowTriangle or LineCapKind.HollowDiamond
            or LineCapKind.HollowCircle or LineCapKind.HollowSquare =>
            (roundCapStroke ? thickness / 2 : 0) + thickness * 0.45,
        _ => roundCapStroke ? thickness / 2 : 0,
    };

    /// <summary>
    /// 线身需为端头让出的弧长。取端头背沿的精确距离（不重叠）——
    /// PushOpacity 按图元混合，半透明时任何重叠都会变深。
    /// </summary>
    public static double CapInset(LineCapKind kind, double capLength) => kind switch
    {
        LineCapKind.None => 0,
        LineCapKind.LineArrow => capLength * 0.17, // ≈ 线宽一半，避开 V 头交点
        LineCapKind.Barbed => capLength * 0.55,
        LineCapKind.SolidCircle or LineCapKind.HollowCircle => capLength * 0.7,
        LineCapKind.SolidSquare or LineCapKind.HollowSquare => capLength * 0.7,
        _ => capLength, // 三角/菱形：背沿
    };

    /// <summary>
    /// 绘制端头。tip = 线端点，dirOut = 指向线外侧的单位向量，thickness = 该端线宽。
    /// 空心变体为描边不填充。供元素渲染与工具栏端头预览共用。
    /// </summary>
    public static void DrawCap(
        DrawingContext ctx, Point tip, Vector dirOut, double thickness, LineCapKind kind, Color color)
    {
        if (kind == LineCapKind.None)
            return;
        var L = CapLength(thickness);
        var d = dirOut; // 已归一化
        var n = new Vector(-d.Y, d.X);
        var brush = new SolidColorBrush(color);
        var hollowPen = new Pen(brush, Math.Max(1.2, thickness * 0.7)) { LineJoin = PenLineJoin.Round };

        switch (kind)
        {
            case LineCapKind.SolidTriangle:
            case LineCapKind.HollowTriangle:
            {
                var back = tip - d * L;
                var geo = ClosedPolygon([tip, back + n * (L * 0.5), back - n * (L * 0.5)]);
                DrawSolidOrHollow(ctx, geo, kind == LineCapKind.SolidTriangle, brush, hollowPen);
                break;
            }
            case LineCapKind.Barbed:
            {
                var back = tip - d * L;
                var notch = tip - d * (L * 0.55);
                var geo = ClosedPolygon([tip, back + n * (L * 0.55), notch, back - n * (L * 0.55)]);
                ctx.DrawGeometry(brush, null, geo);
                break;
            }
            case LineCapKind.SolidDiamond:
            case LineCapKind.HollowDiamond:
            {
                var mid = tip - d * (L * 0.5);
                var geo = ClosedPolygon([tip, mid + n * (L * 0.35), tip - d * L, mid - n * (L * 0.35)]);
                DrawSolidOrHollow(ctx, geo, kind == LineCapKind.SolidDiamond, brush, hollowPen);
                break;
            }
            case LineCapKind.SolidCircle:
            case LineCapKind.HollowCircle:
            {
                var r = L * 0.35;
                var center = tip - d * r;
                if (kind == LineCapKind.SolidCircle)
                    ctx.DrawEllipse(brush, null, center, r, r);
                else
                    ctx.DrawEllipse(null, hollowPen, center, r, r);
                break;
            }
            case LineCapKind.SolidSquare:
            case LineCapKind.HollowSquare:
            {
                var half = L * 0.35;
                var center = tip - d * half;
                var geo = ClosedPolygon(
                [
                    center + d * half + n * half, center + d * half - n * half,
                    center - d * half - n * half, center - d * half + n * half,
                ]);
                DrawSolidOrHollow(ctx, geo, kind == LineCapKind.SolidSquare, brush, hollowPen);
                break;
            }
            case LineCapKind.LineArrow:
            {
                // 单条 V 形开放路径（一个图元），半透明时交点不会叠加变深
                var pen = new Pen(brush, Math.Max(1, thickness))
                {
                    LineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                var v = new StreamGeometry();
                using (var c = v.Open())
                {
                    c.BeginFigure(tip - d * L + n * (L * 0.55), false);
                    c.LineTo(tip);
                    c.LineTo(tip - d * L - n * (L * 0.55));
                    c.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, v);
                break;
            }
        }
    }

    private static void DrawSolidOrHollow(
        DrawingContext ctx, Geometry geo, bool solid, IBrush brush, IPen hollowPen)
    {
        if (solid)
            ctx.DrawGeometry(brush, null, geo);
        else
            ctx.DrawGeometry(null, hollowPen, geo);
    }

    private static StreamGeometry ClosedPolygon(Point[] pts)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(pts[0], true);
        for (var i = 1; i < pts.Length; i++)
            c.LineTo(pts[i]);
        c.EndFigure(true);
        return g;
    }

    // ---- 几何小工具 ----

    private static double Dist(Point a, Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static Point Lerp(Point a, Point b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static double Dot(Vector a, Vector b) => a.X * b.X + a.Y * b.Y;

    private static Vector Normalize(Vector v)
    {
        var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        return len < 1e-6 ? new Vector(1, 0) : v / len;
    }

    private static Vector Normal(Point a, Point b) => Normalize(new Vector(-(b.Y - a.Y), b.X - a.X));

    private static double DistToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var len2 = ab.X * ab.X + ab.Y * ab.Y;
        var t = len2 <= 0 ? 0 : Math.Clamp(Dot(p - a, ab) / len2, 0, 1);
        return Dist(p, a + ab * t);
    }
}
