using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Zhuoying.Capture;

namespace Zhuoying.Annotations;

/// <summary>图章样式。OutlineWidth 为物理像素。</summary>
public sealed record StampStyle
{
    public double RotationDeg { get; init; }
    public double Opacity { get; init; } = 100;
    public bool OutlineEnabled { get; init; }
    public Color OutlineColor { get; init; } = Colors.White;
    public double OutlineWidth { get; init; } = 4;
}

/// <summary>
/// 图章元素：素材图片（SVG/PNG，路径入快照）贴入截图，支持移动/缩放/旋转。
/// 渲染为**单张合成位图**：轮廓描边（alpha 距离变换）铺底、主体预乘盖上、
/// 整体透明度烘入像素——单图元零重叠（TROUBLESHOOTING §11）。
/// SVG 按当前尺寸光栅化缓存，尺寸偏离超 25% 重新光栅化保持清晰。
/// </summary>
public sealed class StampElement : BoxedElement
{
    /// <summary>素材文件路径（库内文件；快照仅存路径）。</summary>
    public string SourcePath { get; set; } = "";

    public StampStyle Style { get; set; } = new();

    private WriteableBitmap? _composite;
    private byte[]? _alpha;          // 合成结果 alpha（命中采样）
    private int _rasterW, _rasterH;  // 合成位图尺寸（含描边外扩）
    private int _pad;                // 描边外扩（合成位图像素），保证描边不被裁剪
    private (string Path, StampStyle Style) _cacheKey;

    public override double RotationDeg
    {
        get => Style.RotationDeg;
        set => Style = Style with { RotationDeg = value };
    }

    public override object CaptureState() => (Bounds, Style, SourcePath);

    public override void RestoreState(object state)
    {
        var (bounds, style, path) = ((PixelRect, StampStyle, string))state;
        Bounds = bounds;
        Style = style;
        SourcePath = path;
    }

    public override void Render(DrawingContext context, Func<Point, Point> toLocal, double scale)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        EnsureComposite();
        if (_composite == null)
            return;
        // 目标矩形按描边外扩量放大（位图内主体区域 ↔ Bounds 对齐）
        var (padX, padY) = PadPhysical();
        var r = new Rect(
            toLocal(new Point(Bounds.X - padX, Bounds.Y - padY)),
            toLocal(new Point(Bounds.Right + padX, Bounds.Bottom + padY)));
        var center = new Rect(
            toLocal(new Point(Bounds.X, Bounds.Y)),
            toLocal(new Point(Bounds.Right, Bounds.Bottom))).Center;
        using (context.PushTransform(RotationMatrix(center, Style.RotationDeg)))
        {
            context.DrawImage(_composite, new Rect(0, 0, _rasterW, _rasterH), r);
        }
    }

    /// <summary>描边外扩量换算到物理像素（x/y 按各自缩放比）。</summary>
    private (double X, double Y) PadPhysical()
    {
        var innerW = _rasterW - _pad * 2;
        var innerH = _rasterH - _pad * 2;
        if (_pad <= 0 || innerW <= 0 || innerH <= 0)
            return (0, 0);
        return ((double)_pad * Bounds.Width / innerW, (double)_pad * Bounds.Height / innerH);
    }

    /// <summary>命中测试：旋转矩形内且落点素材（含描边）不透明——透明区点击穿透。</summary>
    public override bool HitTest(PixelPoint p, double slop)
    {
        var (padX, padY) = PadPhysical();
        var lp = ToUnrotated(new Point(p.X, p.Y));
        if (lp.X < Bounds.X - padX - slop || lp.X > Bounds.Right + padX + slop
            || lp.Y < Bounds.Y - padY - slop || lp.Y > Bounds.Bottom + padY + slop)
            return false;
        if (_alpha == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return true;
        var x = (int)((lp.X - (Bounds.X - padX)) / (Bounds.Width + padX * 2) * _rasterW);
        var y = (int)((lp.Y - (Bounds.Y - padY)) / (Bounds.Height + padY * 2) * _rasterH);
        x = Math.Clamp(x, 0, _rasterW - 1);
        y = Math.Clamp(y, 0, _rasterH - 1);
        // slop 范围内做粗采样，边缘细线不难点中
        var radius = Math.Max(1, (int)(slop / Math.Max(1e-3, Bounds.Width) * _rasterW));
        for (var dy = -radius; dy <= radius; dy += Math.Max(1, radius))
        {
            for (var dx = -radius; dx <= radius; dx += Math.Max(1, radius))
            {
                var sx = Math.Clamp(x + dx, 0, _rasterW - 1);
                var sy = Math.Clamp(y + dy, 0, _rasterH - 1);
                if (_alpha[sy * _rasterW + sx] > 12)
                    return true;
            }
        }
        return false;
    }

    // ---- 合成（物理像素） ----

    private void EnsureComposite()
    {
        var needW = Math.Max(1, Bounds.Width);
        var needH = Math.Max(1, Bounds.Height);
        var innerW = _rasterW - _pad * 2; // 主体区域（不含描边外扩）
        var innerH = _rasterH - _pad * 2;
        var sizeOk = innerW > 0 && innerH > 0
            && Math.Abs(needW - innerW) <= innerW * 0.25
            && Math.Abs(needH - innerH) <= innerH * 0.25;
        if (_composite != null && _cacheKey == (SourcePath, Style) && sizeOk)
            return;
        _cacheKey = (SourcePath, Style);
        _composite?.Dispose();
        _composite = null;
        _alpha = null;

        var raster = StampRasterizer.Load(SourcePath, needW, needH);
        if (raster == null)
            return;

        // 描边启用时合成画布向外扩边，保证贴边素材的描边不被裁掉
        var outlineWidth = Math.Clamp(Style.OutlineWidth, 1, 40);
        _pad = Style.OutlineEnabled ? (int)Math.Ceiling(outlineWidth) + 2 : 0;
        var w = raster.Width + _pad * 2;
        var h = raster.Height + _pad * 2;
        var body = new byte[w * h * 4]; // 预乘 BGRA，主体居中
        for (var y = 0; y < raster.Height; y++)
            Array.Copy(raster.Pixels, y * raster.Width * 4,
                body, ((y + _pad) * w + _pad) * 4, raster.Width * 4);

        var overall = Math.Clamp(Style.Opacity, 0, 100) / 100.0;
        byte[]? outline = null;
        if (Style.OutlineEnabled)
            outline = BuildOutline(body, w, h, outlineWidth, Style.OutlineColor, overall);

        // 合成：描边铺底，主体（× 整体透明度）premul over
        var outPixels = outline ?? new byte[w * h * 4];
        for (var i = 0; i < outPixels.Length; i += 4)
        {
            var ba = body[i + 3] * overall;
            var inv = 1 - ba / 255.0;
            outPixels[i] = (byte)Math.Min(255, body[i] * overall + outPixels[i] * inv);
            outPixels[i + 1] = (byte)Math.Min(255, body[i + 1] * overall + outPixels[i + 1] * inv);
            outPixels[i + 2] = (byte)Math.Min(255, body[i + 2] * overall + outPixels[i + 2] * inv);
            outPixels[i + 3] = (byte)Math.Min(255, ba + outPixels[i + 3] * inv);
        }

        var bmp = new WriteableBitmap(
            new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            for (var y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    outPixels, y * w * 4, IntPtr.Add(fb.Address, y * fb.RowBytes), w * 4);
        }
        _composite = bmp;
        _rasterW = w;
        _rasterH = h;
        _alpha = new byte[w * h];
        for (var i = 0; i < _alpha.Length; i++)
            _alpha[i] = outPixels[i * 4 + 3];
    }

    /// <summary>
    /// 轮廓描边层（预乘 BGRA）：对主体 alpha 做 chamfer 距离变换（两遍 O(n)），
    /// 距离 ≤ 宽度的透明像素填描边色，最外 1px 按小数渐隐抗锯齿。
    /// </summary>
    private static byte[] BuildOutline(
        byte[] body, int w, int h, double width, Color color, double overall)
    {
        const float diag = 1.4142f;
        var dist = new float[w * h];
        for (var i = 0; i < dist.Length; i++)
            dist[i] = body[i * 4 + 3] > 12 ? 0 : float.MaxValue;

        // 正向扫描（左上 → 右下）
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (dist[i] == 0)
                    continue;
                var d = dist[i];
                if (x > 0) d = Math.Min(d, dist[i - 1] + 1);
                if (y > 0)
                {
                    d = Math.Min(d, dist[i - w] + 1);
                    if (x > 0) d = Math.Min(d, dist[i - w - 1] + diag);
                    if (x < w - 1) d = Math.Min(d, dist[i - w + 1] + diag);
                }
                dist[i] = d;
            }
        }
        // 反向扫描（右下 → 左上）
        for (var y = h - 1; y >= 0; y--)
        {
            for (var x = w - 1; x >= 0; x--)
            {
                var i = y * w + x;
                if (dist[i] == 0)
                    continue;
                var d = dist[i];
                if (x < w - 1) d = Math.Min(d, dist[i + 1] + 1);
                if (y < h - 1)
                {
                    d = Math.Min(d, dist[i + w] + 1);
                    if (x < w - 1) d = Math.Min(d, dist[i + w + 1] + diag);
                    if (x > 0) d = Math.Min(d, dist[i + w - 1] + diag);
                }
                dist[i] = d;
            }
        }

        var outline = new byte[w * h * 4];
        for (var i = 0; i < dist.Length; i++)
        {
            var d = dist[i];
            if (d <= 0 || d > width + 1)
                continue;
            var coverage = d <= width ? 1.0 : width + 1 - d; // 外缘 1px 渐隐
            var a = coverage * overall;
            var o = i * 4;
            outline[o] = (byte)(color.B * a);
            outline[o + 1] = (byte)(color.G * a);
            outline[o + 2] = (byte)(color.R * a);
            outline[o + 3] = (byte)(255 * a);
        }
        return outline;
    }
}
