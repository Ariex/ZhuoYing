using System;
using System.Runtime.InteropServices;
using Avalonia;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// 录屏帧源的 X11 实现：固定区域 XGetImage 采样 + XFixes 光标补绘
/// （对应 Windows 侧 <c>WindowsFrameSource</c> 的 BitBlt + DrawIconEx）。
///
/// 缓冲复用整个录制生命周期；Bits 指向 top-down BGRA。
/// </summary>
public sealed unsafe class X11FrameSource : IFrameSource
{
    private readonly PixelRect _region;
    private readonly IntPtr _buffer;
    private readonly int _stride;
    private readonly bool _hasXFixes;
    private bool _disposed;

    public IntPtr Bits => _buffer;

    public int ByteLength => _region.Width * _region.Height * 4;

    public X11FrameSource(PixelRect region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException($"非法录制区域 {region}");
        _region = region;
        _stride = region.Width * 4;
        _buffer = Marshal.AllocHGlobal(ByteLength);
        NativeMemory.Clear((void*)_buffer, (nuint)ByteLength);
        _hasXFixes = X11Display.With(d =>
            X11Interop.XFixesQueryExtension(d, out _, out _));
    }

    public bool Capture()
    {
        if (_disposed)
            return false;
        try
        {
            X11ScreenCapture.CaptureIntoCore(_region, (byte*)_buffer, _stride);
        }
        catch (Exception)
        {
            return false; // 帧保留上次内容（接口约定）
        }
        DrawCursor();
        return true;
    }

    /// <summary>把当前光标合成进帧（XGetImage 不含光标）。</summary>
    private void DrawCursor()
    {
        if (!_hasXFixes)
            return;

        X11Display.With<object?>(d =>
        {
            var imgPtr = X11Interop.XFixesGetCursorImage(d);
            if (imgPtr == IntPtr.Zero)
                return null;
            try
            {
                var ci = Marshal.PtrToStructure<X11Interop.XFixesCursorImage>(imgPtr);
                // 光标左上角在录制区内的坐标（X/Y 已是屏幕坐标含热点偏移）
                var ox = ci.X - ci.XHot - _region.X;
                var oy = ci.Y - ci.YHot - _region.Y;

                // XFixes 的 Pixels 是 unsigned long*：64 位机上每像素 8 字节，
                // 低 32 位才是预乘 ARGB（规范如此，按 uint* 读会错位成花屏）
                var px = (nuint*)ci.Pixels;
                for (var y = 0; y < ci.Height; y++)
                {
                    var dy = oy + y;
                    if (dy < 0 || dy >= _region.Height)
                        continue;
                    var dstRow = (uint*)((byte*)_buffer + (long)dy * _stride);
                    for (var x = 0; x < ci.Width; x++)
                    {
                        var dx = ox + x;
                        if (dx < 0 || dx >= _region.Width)
                            continue;
                        var s = (uint)px[y * ci.Width + x];
                        var sa = s >> 24;
                        if (sa == 0)
                            continue;
                        if (sa == 255)
                        {
                            dstRow[dx] = s | 0xFF000000u;
                            continue;
                        }
                        // 源为预乘 alpha：dst = src + dst*(1-a)
                        var dpx = dstRow[dx];
                        var inv = 255 - sa;
                        var b = ((s & 0xFF) + (dpx & 0xFF) * inv / 255) & 0xFF;
                        var g = (((s >> 8) & 0xFF) + ((dpx >> 8) & 0xFF) * inv / 255) & 0xFF;
                        var r = (((s >> 16) & 0xFF) + ((dpx >> 16) & 0xFF) * inv / 255) & 0xFF;
                        dstRow[dx] = 0xFF000000u | (r << 16) | (g << 8) | b;
                    }
                }
            }
            finally
            {
                X11Interop.XFree(imgPtr);
            }
            return null;
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Marshal.FreeHGlobal(_buffer);
    }
}
