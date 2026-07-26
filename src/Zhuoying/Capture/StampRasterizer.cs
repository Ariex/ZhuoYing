using System;
using System.IO;
using SkiaSharp;
using Svg.Skia;

namespace Zhuoying.Capture;

/// <summary>光栅化结果：预乘 BGRA 像素（图章合成/命中采样用）。</summary>
internal sealed record RasterStamp(byte[] Pixels, int Width, int Height);

/// <summary>
/// 图章素材光栅化：SVG 走 Svg.Skia（矢量，按目标尺寸渲染永远清晰），
/// PNG/JPG 走 SKBitmap 解码 + 高质量缩放。统一输出预乘 BGRA 字节。
/// </summary>
internal static class StampRasterizer
{
    /// <summary>素材固有宽高比（宽/高），加载失败返回 null。</summary>
    public static double? GetAspect(string path)
    {
        try
        {
            if (IsSvg(path))
            {
                using var svg = new SKSvg();
                var picture = svg.Load(path);
                if (picture == null || picture.CullRect.Height <= 0)
                    return null;
                return picture.CullRect.Width / picture.CullRect.Height;
            }
            using var codec = SKCodec.Create(path);
            if (codec == null || codec.Info.Height <= 0)
                return null;
            return (double)codec.Info.Width / codec.Info.Height;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>按目标像素尺寸光栅化（SVG 矢量渲染 / 位图重采样）。失败返回 null。</summary>
    public static RasterStamp? Load(string path, int width, int height)
    {
        width = Math.Clamp(width, 1, 8192);
        height = Math.Clamp(height, 1, 8192);
        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var target = new SKBitmap(info);

            if (IsSvg(path))
            {
                using var svg = new SKSvg();
                var picture = svg.Load(path);
                if (picture == null || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
                    return null;
                using var canvas = new SKCanvas(target);
                canvas.Clear(SKColors.Transparent);
                canvas.Scale(
                    width / picture.CullRect.Width,
                    height / picture.CullRect.Height);
                canvas.Translate(-picture.CullRect.Left, -picture.CullRect.Top);
                canvas.DrawPicture(picture);
                canvas.Flush();
            }
            else
            {
                using var decoded = SKBitmap.Decode(path);
                if (decoded == null)
                    return null;
                using var converted = decoded.Copy(SKColorType.Bgra8888);
                var source = converted ?? decoded;
                if (!source.ScalePixels(target, new SKSamplingOptions(SKCubicResampler.Mitchell)))
                    return null;
            }

            var pixels = new byte[width * height * 4];
            System.Runtime.InteropServices.Marshal.Copy(
                target.GetPixels(), pixels, 0, pixels.Length);
            return new RasterStamp(pixels, width, height);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsSvg(string path) =>
        Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase);
}
