using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Zhuoying.Annotations;

public enum MosaicMode
{
    Pixelate, // 像素化（马赛克块）
    Blur,     // 模糊化
}

/// <summary>区域模糊样式。BlockSize / BlurRadius 为物理像素（1–50）。</summary>
public sealed record MosaicStyle
{
    public MosaicMode Mode { get; init; } = MosaicMode.Pixelate;
    public double BlockSize { get; init; } = 10;
    public double BlurRadius { get; init; } = 10;
    public double RotationDeg { get; init; }
}

/// <summary>
/// 区域模糊元素：矩形区域内对**底图（冻结帧）**做像素化或模糊化。
/// 无描边；永远渲染在所有其他标注之下（模型列表头部的前缀组，仅在底图之上）。
/// 为抵抗去马赛克工具（Depix 等按块均值匹配还原）：像素化前先按元素固定
/// 随机种子做**跨块**像素交换（块内乱序不改变均值，必须跨块才污染均值）；
/// 模糊化后叠加轻微随机噪声破坏反卷积。种子随元素固定，重绘稳定不闪烁。
/// </summary>
public sealed class MosaicElement : BoxedElement
{
    public MosaicStyle Style { get; set; } = new();

    /// <summary>随机种子（创建时生成，决定交换/噪声模式；不入撤销快照，从不改变）。</summary>
    public int Seed { get; set; } = 1;

    /// <summary>冻结帧（仅采样来源，生命周期归会话管理）。</summary>
    public WriteableBitmap? Frame { get; set; }

    /// <summary>冻结帧左上角对应的虚拟屏幕物理坐标。</summary>
    public PixelPoint FrameOrigin { get; set; }

    private WriteableBitmap? _cache;
    private PixelRect _cacheRegion;
    private (PixelRect Bounds, MosaicStyle Style) _cacheKey;

    public override double RotationDeg
    {
        get => Style.RotationDeg;
        set => Style = Style with { RotationDeg = value };
    }

    public override object CaptureState() => (Bounds, Style);

    public override void RestoreState(object state)
    {
        var (bounds, style) = ((PixelRect, MosaicStyle))state;
        Bounds = bounds;
        Style = style;
    }

    /// <summary>命中测试：旋转后的矩形内。</summary>
    public override bool HitTest(PixelPoint p, double slop)
    {
        var lp = ToUnrotated(new Point(p.X, p.Y));
        return lp.X >= Bounds.X - slop && lp.X <= Bounds.Right + slop
               && lp.Y >= Bounds.Y - slop && lp.Y <= Bounds.Bottom + slop;
    }

    public override void Render(DrawingContext context, Func<Point, Point> toLocal, double scale)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || Frame == null)
            return;
        EnsureCache();
        if (_cache == null)
            return;

        var dest = new Rect(
            toLocal(new Point(_cacheRegion.X, _cacheRegion.Y)),
            toLocal(new Point(_cacheRegion.Right, _cacheRegion.Bottom)));
        var localRect = new Rect(
            toLocal(new Point(Bounds.X, Bounds.Y)),
            toLocal(new Point(Bounds.Right, Bounds.Bottom)));

        // 处理区域可能大于元素矩形（模糊边缘取样余量 / 旋转 AABB），始终裁剪到元素形状
        var clip = new RectangleGeometry(localRect);
        if (Style.RotationDeg != 0)
            clip.Transform = new MatrixTransform(RotationMatrix(localRect.Center, Style.RotationDeg));
        using (context.PushGeometryClip(clip))
        {
            context.DrawImage(_cache,
                new Rect(0, 0, _cacheRegion.Width, _cacheRegion.Height), dest);
        }
    }

    // ---- 采样与处理（全部在物理像素空间） ----

    private void EnsureCache()
    {
        if (_cache != null && _cacheKey == (Bounds, Style))
            return;
        _cacheKey = (Bounds, Style);
        _cache?.Dispose();
        _cache = null;

        var frameRect = new PixelRect(FrameOrigin, Frame!.PixelSize);
        var aabb = RotatedAabb().Intersect(frameRect);
        if (aabb.Width <= 0 || aabb.Height <= 0)
            return;

        // 模糊需要边缘外的邻域参与，取样区域按半径外扩（钳在帧内）
        var margin = Style.Mode == MosaicMode.Blur
            ? Math.Clamp((int)Math.Round(Style.BlurRadius), 1, 50) : 0;
        var region = new PixelRect(
            aabb.X - margin, aabb.Y - margin,
            aabb.Width + margin * 2, aabb.Height + margin * 2).Intersect(frameRect);

        var buf = CopyRegion(region);
        if (Style.Mode == MosaicMode.Pixelate)
        {
            var block = Math.Clamp((int)Math.Round(Style.BlockSize), 1, 50);
            if (block > 1)
                CrossBlockSwap(buf, region.Width, region.Height, block, Seed);
            Pixelate(buf, region, block);
        }
        else
        {
            BoxBlur(ref buf, region.Width, region.Height, margin);
            BoxBlur(ref buf, region.Width, region.Height, margin);
            AddNoise(buf, Seed);
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

    /// <summary>旋转后四角的轴对齐包围盒（物理像素）。</summary>
    private PixelRect RotatedAabb()
    {
        if (Style.RotationDeg == 0)
            return Bounds;
        var c = Center;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        Span<Point> corners =
        [
            new(Bounds.X, Bounds.Y), new(Bounds.Right, Bounds.Y),
            new(Bounds.Right, Bounds.Bottom), new(Bounds.X, Bounds.Bottom),
        ];
        foreach (var corner in corners)
        {
            var p = RotatePoint(corner, c, Style.RotationDeg);
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
        return new PixelRect(
            (int)Math.Floor(minX), (int)Math.Floor(minY),
            (int)Math.Ceiling(maxX - minX), (int)Math.Ceiling(maxY - minY));
    }

    /// <summary>从冻结帧拷贝区域为紧凑 BGRA 缓冲。</summary>
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

    /// <summary>xorshift32（确定性，跨重绘稳定）。</summary>
    private static uint NextRandom(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    /// <summary>
    /// 跨块随机像素交换（约 1/4 像素与 ±block 范围内随机位置互换）。
    /// 均值对块内排列不变，交换必须跨块才能污染块均值、抵抗按均值匹配的还原工具。
    /// </summary>
    private static void CrossBlockSwap(byte[] buf, int w, int h, int block, int seed)
    {
        var state = (uint)seed | 1;
        var span = 2 * block + 1;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if ((NextRandom(ref state) & 3) != 0)
                    continue;
                var tx = Math.Clamp(x + (int)(NextRandom(ref state) % span) - block, 0, w - 1);
                var ty = Math.Clamp(y + (int)(NextRandom(ref state) % span) - block, 0, h - 1);
                var a = (y * w + x) * 4;
                var b = (ty * w + tx) * 4;
                for (var i = 0; i < 4; i++)
                    (buf[a + i], buf[b + i]) = (buf[b + i], buf[a + i]);
            }
        }
    }

    /// <summary>按块求均值填充。块网格锚定在元素矩形左上角（移动元素则重新取样）。</summary>
    private void Pixelate(byte[] buf, PixelRect region, int block)
    {
        static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
        var bx0 = FloorDiv(region.X - Bounds.X, block);
        var by0 = FloorDiv(region.Y - Bounds.Y, block);
        var bx1 = FloorDiv(region.Right - 1 - Bounds.X, block);
        var by1 = FloorDiv(region.Bottom - 1 - Bounds.Y, block);
        for (var by = by0; by <= by1; by++)
        {
            var y0 = Math.Max(Bounds.Y + by * block - region.Y, 0);
            var y1 = Math.Min(Bounds.Y + (by + 1) * block - region.Y, region.Height);
            for (var bx = bx0; bx <= bx1; bx++)
            {
                var x0 = Math.Max(Bounds.X + bx * block - region.X, 0);
                var x1 = Math.Min(Bounds.X + (bx + 1) * block - region.X, region.Width);
                long sb = 0, sg = 0, sr = 0;
                var n = (y1 - y0) * (x1 - x0);
                if (n <= 0)
                    continue;
                for (var y = y0; y < y1; y++)
                {
                    var row = (y * region.Width + x0) * 4;
                    for (var x = x0; x < x1; x++, row += 4)
                    {
                        sb += buf[row];
                        sg += buf[row + 1];
                        sr += buf[row + 2];
                    }
                }
                byte ab = (byte)(sb / n), ag = (byte)(sg / n), ar = (byte)(sr / n);
                for (var y = y0; y < y1; y++)
                {
                    var row = (y * region.Width + x0) * 4;
                    for (var x = x0; x < x1; x++, row += 4)
                    {
                        buf[row] = ab;
                        buf[row + 1] = ag;
                        buf[row + 2] = ar;
                        buf[row + 3] = 0xFF;
                    }
                }
            }
        }
    }

    /// <summary>可分离箱式模糊（水平 + 垂直滑动窗口，边缘钳位）。调用两遍近似高斯。</summary>
    private static void BoxBlur(ref byte[] buf, int w, int h, int radius)
    {
        if (radius < 1)
            return;
        var tmp = new byte[buf.Length];
        var win = 2 * radius + 1;
        // 水平
        for (var y = 0; y < h; y++)
        {
            var row = y * w * 4;
            int sb = 0, sg = 0, sr = 0;
            for (var i = -radius; i <= radius; i++)
            {
                var p = row + Math.Clamp(i, 0, w - 1) * 4;
                sb += buf[p];
                sg += buf[p + 1];
                sr += buf[p + 2];
            }
            for (var x = 0; x < w; x++)
            {
                var d = row + x * 4;
                tmp[d] = (byte)(sb / win);
                tmp[d + 1] = (byte)(sg / win);
                tmp[d + 2] = (byte)(sr / win);
                tmp[d + 3] = 0xFF;
                var add = row + Math.Clamp(x + radius + 1, 0, w - 1) * 4;
                var sub = row + Math.Clamp(x - radius, 0, w - 1) * 4;
                sb += buf[add] - buf[sub];
                sg += buf[add + 1] - buf[sub + 1];
                sr += buf[add + 2] - buf[sub + 2];
            }
        }
        // 垂直
        for (var x = 0; x < w; x++)
        {
            var col = x * 4;
            int sb = 0, sg = 0, sr = 0;
            for (var i = -radius; i <= radius; i++)
            {
                var p = Math.Clamp(i, 0, h - 1) * w * 4 + col;
                sb += tmp[p];
                sg += tmp[p + 1];
                sr += tmp[p + 2];
            }
            for (var y = 0; y < h; y++)
            {
                var d = y * w * 4 + col;
                buf[d] = (byte)(sb / win);
                buf[d + 1] = (byte)(sg / win);
                buf[d + 2] = (byte)(sr / win);
                buf[d + 3] = 0xFF;
                var add = Math.Clamp(y + radius + 1, 0, h - 1) * w * 4 + col;
                var sub = Math.Clamp(y - radius, 0, h - 1) * w * 4 + col;
                sb += tmp[add] - tmp[sub];
                sg += tmp[add + 1] - tmp[sub + 1];
                sr += tmp[add + 2] - tmp[sub + 2];
            }
        }
    }

    /// <summary>±2 级确定性噪声（破坏反卷积去模糊的数值稳定性，肉眼不可辨）。</summary>
    private static void AddNoise(byte[] buf, int seed)
    {
        var state = (uint)seed ^ 0x9E3779B9;
        for (var i = 0; i < buf.Length; i += 4)
        {
            var r = NextRandom(ref state);
            buf[i] = (byte)Math.Clamp(buf[i] + (int)(r % 5) - 2, 0, 255);
            buf[i + 1] = (byte)Math.Clamp(buf[i + 1] + (int)((r >> 8) % 5) - 2, 0, 255);
            buf[i + 2] = (byte)Math.Clamp(buf[i + 2] + (int)((r >> 16) % 5) - 2, 0, 255);
        }
    }
}
