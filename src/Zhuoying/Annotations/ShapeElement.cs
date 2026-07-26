using System;
using Avalonia;
using Avalonia.Media;

namespace Zhuoying.Annotations;

/// <summary>
/// 矩形形状元素（圆角可调，100% 圆角 = 椭圆）。
/// 几何为虚拟屏幕物理像素；渲染时由调用方提供物理 → 目标坐标换算。
/// </summary>
public sealed class ShapeElement : BoxedElement
{
    public ShapeStyle Style { get; set; } = new();

    /// <summary>冻结帧（"反色"颜色的采样来源，创建时注入；普通颜色不使用）。</summary>
    public Avalonia.Media.Imaging.WriteableBitmap? Frame { get; set; }

    /// <summary>冻结帧左上角对应的虚拟屏幕物理坐标。</summary>
    public PixelPoint FrameOrigin { get; set; }

    private Avalonia.Media.Imaging.WriteableBitmap? _invCache;
    private PixelRect _invRegion;
    private (PixelRect Bounds, ShapeStyle Style) _invKey;

    public override double RotationDeg
    {
        get => Style.RotationDeg;
        set => Style = Style with { RotationDeg = value };
    }

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
        if (InvertPaint.IsInvert(Style.Color))
        {
            RenderInverted(context, toLocal, scale);
            return;
        }
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


    /// <summary>
    /// "反色"渲染：采样冻结帧 AABB → 逐像素取反 → 按描边环带（填充时整块）
    /// 圆角矩形几何裁剪绘制。线形/透明度在反色下不适用（几何裁剪无虚线语义）。
    /// </summary>
    private void RenderInverted(DrawingContext context, Func<Point, Point> toLocal, double scale)
    {
        if (Frame == null)
            return;
        var frameRect = new PixelRect(FrameOrigin, Frame.PixelSize);
        var half = (int)Math.Ceiling(Style.Thickness / 2) + 1;
        var ra = RotatedAabb();
        var aabb = new PixelRect(
            ra.X - half, ra.Y - half, ra.Width + half * 2, ra.Height + half * 2)
            .Intersect(frameRect);
        if (aabb.Width <= 0 || aabb.Height <= 0)
            return;
        EnsureInvertCache(aabb);
        if (_invCache == null)
            return;

        var r = new Rect(
            toLocal(new Point(Bounds.X, Bounds.Y)),
            toLocal(new Point(Bounds.Right, Bounds.Bottom)));
        var dest = new Rect(
            toLocal(new Point(aabb.X, aabb.Y)),
            toLocal(new Point(aabb.Right, aabb.Bottom)));
        var t = Style.Thickness / scale;
        var rx = Style.CornerRadiusPercent / 100 * r.Width / 2;
        var ry = Style.CornerRadiusPercent / 100 * r.Height / 2;

        // 描边环带 = 外扩半线宽的圆角矩形 − 内缩半线宽的圆角矩形；填充 = 外块整体
        var outer = new RectangleGeometry(r.Inflate(t / 2))
        {
            RadiusX = rx + t / 2,
            RadiusY = ry + t / 2,
        };
        Geometry clip = outer;
        if (!Style.Filled)
        {
            var innerRect = r.Deflate(t / 2);
            if (innerRect.Width > 0 && innerRect.Height > 0)
            {
                clip = new CombinedGeometry(GeometryCombineMode.Exclude, outer,
                    new RectangleGeometry(innerRect)
                    {
                        RadiusX = Math.Max(0, rx - t / 2),
                        RadiusY = Math.Max(0, ry - t / 2),
                    });
            }
        }
        clip.Transform = new MatrixTransform(RotationMatrix(r.Center, Style.RotationDeg));
        using (context.PushGeometryClip(clip))
        {
            context.DrawImage(_invCache,
                new Rect(0, 0, _invRegion.Width, _invRegion.Height), dest);
        }
    }

    private unsafe void EnsureInvertCache(PixelRect region)
    {
        if (_invCache != null && _invKey == (Bounds, Style) && _invRegion == region)
            return;
        _invKey = (Bounds, Style);
        _invCache?.Dispose();
        _invCache = null;

        var buf = new byte[(long)region.Width * region.Height * 4];
        using (var fb = Frame!.Lock())
        {
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
        }
        for (var i = 0; i < buf.Length; i += 4)
        {
            buf[i] = (byte)(255 - buf[i]);
            buf[i + 1] = (byte)(255 - buf[i + 1]);
            buf[i + 2] = (byte)(255 - buf[i + 2]);
            buf[i + 3] = 0xFF;
        }

        var bmp = new Avalonia.Media.Imaging.WriteableBitmap(
            new PixelSize(region.Width, region.Height), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            for (var y = 0; y < region.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    buf, y * region.Width * 4,
                    IntPtr.Add(fb.Address, y * fb.RowBytes), region.Width * 4);
        }
        _invCache = bmp;
        _invRegion = region;
    }

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
