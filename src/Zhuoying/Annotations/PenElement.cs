using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Zhuoying.Annotations;

/// <summary>画笔样式。Thickness 为物理像素。</summary>
public sealed record PenStyle
{
    public Color Color { get; init; } = Color.FromRgb(0xFF, 0x3B, 0x30);
    public double Thickness { get; init; } = 6;

    /// <summary>荧光笔：笔迹与**底图（冻结帧）**做正片叠底（乘法混合）——
    /// 黄荧光划过黑字，字仍是黑的、白底变黄，即真实荧光笔效果。</summary>
    public bool Highlight { get; init; }
}

/// <summary>
/// 画笔元素：自由笔迹（拖拽采点，圆头圆拐角）。
/// 荧光模式：Avalonia 绘图无混合模式，用区域模糊同款思路——采样冻结帧、
/// 逐像素乘上画笔颜色、按笔迹带状几何裁剪绘制（缓存位图，移动时重新取样；
/// 只与底图叠底，压住其下的其他标注，同马赛克语义）。
/// </summary>
public sealed class PenElement : AnnotationElement
{
    public List<PixelPoint> Points { get; } = [];

    public PenStyle Style { get; set; } = new();

    /// <summary>冻结帧（荧光采样来源，生命周期归会话管理）。</summary>
    public WriteableBitmap? Frame { get; set; }

    /// <summary>冻结帧左上角对应的虚拟屏幕物理坐标。</summary>
    public PixelPoint FrameOrigin { get; set; }

    private WriteableBitmap? _cache;
    private PixelRect _cacheRegion;
    private (PenStyle Style, int Count, PixelPoint First, PixelPoint Last) _cacheKey;

    public override object CaptureState() => (Points.ToArray(), Style);

    public override void RestoreState(object state)
    {
        var (points, style) = ((PixelPoint[], PenStyle))state;
        Points.Clear();
        Points.AddRange(points);
        Style = style;
    }

    /// <summary>笔迹包围盒（含线宽半径，物理像素）。</summary>
    public PixelRect Bounds
    {
        get
        {
            if (Points.Count == 0)
                return default;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var p in Points)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }
            var half = (int)Math.Ceiling(Style.Thickness / 2) + 1;
            return new PixelRect(minX - half, minY - half,
                maxX - minX + half * 2, maxY - minY + half * 2);
        }
    }

    public override void Render(DrawingContext context, Func<Point, Point> toLocal, double scale)
    {
        if (Points.Count == 0)
            return;
        var t = Math.Max(1, Style.Thickness) / scale;

        if (!Style.Highlight)
        {
            var brush = new SolidColorBrush(Style.Color);
            if (Points.Count == 1)
            {
                context.DrawEllipse(brush, null, toLocal(ToPoint(Points[0])), t / 2, t / 2);
                return;
            }
            var pen = new Pen(brush, t)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            context.DrawGeometry(null, pen, BuildPath(toLocal));
            return;
        }

        // ---- 荧光：底图 × 颜色 的位图按笔迹带裁剪 ----
        if (Frame == null)
            return;
        var frameRect = new PixelRect(FrameOrigin, Frame.PixelSize);
        var region = Bounds.Intersect(frameRect);
        if (region.Width <= 0 || region.Height <= 0)
            return;
        EnsureCache(region);
        if (_cache == null)
            return;
        var dest = new Rect(
            toLocal(new Point(region.X, region.Y)),
            toLocal(new Point(region.Right, region.Bottom)));
        using (context.PushGeometryClip(StrokeGeometry.BuildBand(Points, toLocal, t / 2)))
        {
            context.DrawImage(_cache,
                new Rect(0, 0, _cacheRegion.Width, _cacheRegion.Height), dest);
        }
    }

    private static Point ToPoint(PixelPoint p) => new(p.X, p.Y);

    /// <summary>折线路径（实心笔迹用 Pen 圆头圆拐角描）。</summary>
    private StreamGeometry BuildPath(Func<Point, Point> toLocal)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(toLocal(ToPoint(Points[0])), false);
        for (var i = 1; i < Points.Count; i++)
            ctx.LineTo(toLocal(ToPoint(Points[i])));
        ctx.EndFigure(false);
        return geometry;
    }

    /// <summary>底图 × 画笔颜色（正片叠底）位图缓存。</summary>
    private void EnsureCache(PixelRect region)
    {
        var key = (Style, Points.Count,
            Points.Count > 0 ? Points[0] : default,
            Points.Count > 0 ? Points[^1] : default);
        if (_cache != null && _cacheKey == key && _cacheRegion == region)
            return;
        _cacheKey = key;
        _cache?.Dispose();
        _cache = null;

        var buf = CopyRegion(region);
        int mb = Style.Color.B, mg = Style.Color.G, mr = Style.Color.R;
        for (var i = 0; i < buf.Length; i += 4)
        {
            buf[i] = (byte)(buf[i] * mb / 255);
            buf[i + 1] = (byte)(buf[i + 1] * mg / 255);
            buf[i + 2] = (byte)(buf[i + 2] * mr / 255);
            buf[i + 3] = 0xFF;
        }

        var bmp = new WriteableBitmap(
            new PixelSize(region.Width, region.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            for (var y = 0; y < region.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    buf, y * region.Width * 4,
                    IntPtr.Add(fb.Address, y * fb.RowBytes), region.Width * 4);
        }
        _cache = bmp;
        _cacheRegion = region;
    }

    private unsafe byte[] CopyRegion(PixelRect region)
    {
        var buf = new byte[(long)region.Width * region.Height * 4];
        using var fb = Frame!.Lock();
        var srcX = region.X - FrameOrigin.X;
        var srcY = region.Y - FrameOrigin.Y;
        fixed (byte* dst = buf)
        {
            for (var y = 0; y < region.Height; y++)
            {
                Buffer.MemoryCopy(
                    (byte*)fb.Address + (long)(srcY + y) * fb.RowBytes + (long)srcX * 4,
                    dst + (long)y * region.Width * 4,
                    region.Width * 4, region.Width * 4);
            }
        }
        return buf;
    }

    /// <summary>命中测试：到任一线段的距离 ≤ 半线宽 + slop。</summary>
    public override bool HitTest(PixelPoint p, double slop)
    {
        if (Points.Count == 0)
            return false;
        var half = Style.Thickness / 2 + slop;
        var b = Bounds;
        if (p.X < b.X - slop || p.X > b.Right + slop || p.Y < b.Y - slop || p.Y > b.Bottom + slop)
            return false;
        if (Points.Count == 1)
        {
            double dx = p.X - Points[0].X, dy = p.Y - Points[0].Y;
            return dx * dx + dy * dy <= half * half;
        }
        for (var i = 0; i < Points.Count - 1; i++)
        {
            if (DistanceToSegment(p, Points[i], Points[i + 1]) <= half)
                return true;
        }
        return false;
    }

    private static double DistanceToSegment(PixelPoint p, PixelPoint a, PixelPoint b)
    {
        double ax = a.X, ay = a.Y;
        double dx = b.X - ax, dy = b.Y - ay;
        var lenSq = dx * dx + dy * dy;
        var t = lenSq < 1e-6 ? 0 : Math.Clamp(((p.X - ax) * dx + (p.Y - ay) * dy) / lenSq, 0, 1);
        double px = ax + t * dx - p.X, py = ay + t * dy - p.Y;
        return Math.Sqrt(px * px + py * py);
    }
}
