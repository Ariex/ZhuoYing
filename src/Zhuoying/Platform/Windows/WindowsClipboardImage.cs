using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Media.Imaging;

namespace Zhuoying.Platform.Windows;

public sealed class WindowsClipboardImage : IClipboardImage
{
    public void SetImage(WriteableBitmap bitmap)
    {
        // 仅写 CF_DIB（含正确 DPI 头）。实测在 Win11 画图上，剪贴板同时存在
        // "PNG" 自定义格式会触发其粘贴时把图像按 DPI 减半（画图并不能从纯 PNG
        // 格式粘贴，PNG 的存在只会干扰其格式选择），故不写 PNG 格式。
        //
        // 写入走纯 Win32 剪贴板 API（非 OLE/COM，NativeAOT 兼容）：
        // SetClipboardData 交出 HGLOBAL 所有权后数据归系统持有，
        // 与 OleFlushClipboard 一样不依赖本进程存活。
        WriteClipboard(BuildDib(bitmap));
    }

    private static void WriteClipboard(byte[] dib)
    {
        // 剪贴板可能被其他进程短暂占用，带重试
        var opened = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (Win32.OpenClipboard(IntPtr.Zero))
            {
                opened = true;
                break;
            }
            Thread.Sleep(50);
        }
        if (!opened)
            throw new InvalidOperationException("OpenClipboard 失败（剪贴板被占用）");

        try
        {
            if (!Win32.EmptyClipboard())
                throw new InvalidOperationException("EmptyClipboard 失败");

            var hGlobal = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (nuint)dib.Length);
            if (hGlobal == IntPtr.Zero)
                throw new OutOfMemoryException("GlobalAlloc 失败");
            var ptr = Win32.GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero)
            {
                Win32.GlobalFree(hGlobal);
                throw new InvalidOperationException("GlobalLock 失败");
            }
            try
            {
                Marshal.Copy(dib, 0, ptr, dib.Length);
            }
            finally
            {
                Win32.GlobalUnlock(hGlobal);
            }

            if (Win32.SetClipboardData(Win32.CF_DIB, hGlobal) == IntPtr.Zero)
            {
                // 设置失败时所有权未转移，需要自行释放
                Win32.GlobalFree(hGlobal);
                throw new InvalidOperationException("SetClipboardData 失败");
            }
            // 成功后 hGlobal 归系统所有，不得再释放
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    /// <summary>BITMAPINFOHEADER + 自底向上 32bpp BGRA 像素。</summary>
    private static unsafe byte[] BuildDib(WriteableBitmap bitmap)
    {
        var size = bitmap.PixelSize;
        int w = size.Width, h = size.Height;
        var pelsPerMeter = (int)Math.Round(bitmap.Dpi.X * 1000.0 / 25.4);
        var headerSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>();
        var buffer = new byte[headerSize + (long)w * h * 4];

        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)headerSize,
            biWidth = w,
            biHeight = h, // 正值 = bottom-up
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Win32.BI_RGB,
            biSizeImage = (uint)(w * h * 4),
            biXPelsPerMeter = pelsPerMeter,
            biYPelsPerMeter = pelsPerMeter,
        };
        MemoryMarshal.Write(buffer, in header);

        using var fb = bitmap.Lock();
        fixed (byte* dstBase = buffer)
        {
            var src = (byte*)fb.Address;
            for (var y = 0; y < h; y++)
            {
                var srcRow = src + (long)y * fb.RowBytes;
                var dstRow = dstBase + headerSize + (long)(h - 1 - y) * w * 4;
                Buffer.MemoryCopy(srcRow, dstRow, w * 4, w * 4);
            }
        }
        return buffer;
    }
}
