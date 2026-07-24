using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Zhuoying.Capture;

internal static class BitmapUtil
{
    /// <summary>
    /// 从源位图裁出指定像素区域（自动与源范围求交）。
    /// dpi 写入结果位图元数据（输出用，来源显示器 96×缩放比），不影响像素。
    /// </summary>
    public static unsafe WriteableBitmap Crop(WriteableBitmap source, PixelRect rect, Vector dpi)
    {
        var full = new PixelRect(source.PixelSize);
        rect = rect.Intersect(full);
        if (rect.Width < 1 || rect.Height < 1)
            throw new ArgumentException($"裁剪区域为空：{rect}");

        var result = new WriteableBitmap(
            new PixelSize(rect.Width, rect.Height), dpi,
            PixelFormat.Bgra8888, AlphaFormat.Opaque);

        using var srcFb = source.Lock();
        using var dstFb = result.Lock();
        var srcBase = (byte*)srcFb.Address;
        var dstBase = (byte*)dstFb.Address;
        var rowBytes = rect.Width * 4;
        for (var y = 0; y < rect.Height; y++)
        {
            var srcRow = srcBase + (long)(rect.Y + y) * srcFb.RowBytes + (long)rect.X * 4;
            var dstRow = dstBase + (long)y * dstFb.RowBytes;
            Buffer.MemoryCopy(srcRow, dstRow, rowBytes, rowBytes);
        }
        return result;
    }
}
