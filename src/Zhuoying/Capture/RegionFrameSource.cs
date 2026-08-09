using System;
using Avalonia;
using Zhuoying.Platform.Windows;

namespace Zhuoying.Capture;

/// <summary>
/// 录屏共享帧源：固定区域的 BitBlt 采样 + 光标补绘（GIF 与 MP4 录制共用）。
/// DIB 复用整个录制生命周期；Bits 指向 top-down BGRA。
/// </summary>
internal sealed class RegionFrameSource : IDisposable
{
    private readonly PixelRect _region;
    private readonly IntPtr _screenDc;
    private readonly IntPtr _memDc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _oldBitmap;

    public IntPtr Bits { get; }

    public int ByteLength => _region.Width * _region.Height * 4;

    public RegionFrameSource(PixelRect region)
    {
        _region = region;
        _screenDc = Win32.GetDC(IntPtr.Zero);
        _memDc = Win32.CreateCompatibleDC(_screenDc);
        var bmi = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = region.Width,
            biHeight = -region.Height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Win32.BI_RGB,
        };
        _bitmap = Win32.CreateDIBSection(_memDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero)
            throw new InvalidOperationException("CreateDIBSection 失败");
        Bits = bits;
        _oldBitmap = Win32.SelectObject(_memDc, _bitmap);
    }

    /// <summary>采样一帧到 Bits（含光标）。失败返回 false（帧保留上次内容）。</summary>
    public bool Capture()
    {
        if (!Win32.BitBlt(_memDc, 0, 0, _region.Width, _region.Height,
                _screenDc, _region.X, _region.Y, Win32.SRCCOPY | Win32.CAPTUREBLT))
            return false;
        DrawCursor();
        return true;
    }

    /// <summary>把当前光标补绘进帧（BitBlt 不含光标；坐标为物理像素）。</summary>
    private void DrawCursor()
    {
        var ci = new Win32.CURSORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.CURSORINFO>(),
        };
        if (!Win32.GetCursorInfo(ref ci) || ci.flags != Win32.CURSOR_SHOWING
            || ci.hCursor == IntPtr.Zero)
            return;
        uint hotX = 0, hotY = 0;
        if (Win32.GetIconInfo(ci.hCursor, out var ii))
        {
            hotX = ii.xHotspot;
            hotY = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) Win32.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Win32.DeleteObject(ii.hbmColor);
        }
        Win32.DrawIconEx(_memDc,
            ci.ptScreenPos.X - _region.X - (int)hotX,
            ci.ptScreenPos.Y - _region.Y - (int)hotY,
            ci.hCursor, 0, 0, 0, IntPtr.Zero, Win32.DI_NORMAL);
    }

    public void Dispose()
    {
        Win32.SelectObject(_memDc, _oldBitmap);
        Win32.DeleteObject(_bitmap);
        Win32.DeleteDC(_memDc);
        Win32.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}

/// <summary>录屏器统一接口（GIF / MP4）。</summary>
internal interface IScreenRecorder : IDisposable
{
    TimeSpan Elapsed { get; }
    long BytesWritten { get; }
    int FrameCount { get; }

    /// <summary>结束录制并完成文件（阻塞至编码收尾）。</summary>
    void Stop();

    /// <summary>取消录制并删除文件。</summary>
    void Cancel();
}
