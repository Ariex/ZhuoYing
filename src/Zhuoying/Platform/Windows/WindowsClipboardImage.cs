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
        var dib = BuildDib(bitmap);

        // 独立 STA 线程 OLE 写入
        Exception? error = null;
        var writer = new Thread(() =>
        {
            try
            {
                WriteClipboardOle(dib);
            }
            catch (Exception e)
            {
                error = e;
            }
        }) { Name = "Zhuoying.ClipboardWriter" };
        writer.SetApartmentState(ApartmentState.STA);
        writer.Start();
        writer.Join();
        if (error != null)
            throw error;
    }

    private static void WriteClipboardOle(byte[] dib)
    {
        var hr = Win32.OleInitialize(IntPtr.Zero);
        if (hr < 0)
            throw new InvalidOperationException($"OleInitialize 失败: 0x{hr:X8}");
        try
        {
            var dataObject = new ImageDataObject(((short)Win32.CF_DIB, dib));
            Marshal.ThrowExceptionForHR(Win32.OleSetClipboard(dataObject));
            // 立即固化数据到系统剪贴板，使其不依赖本进程存活
            Marshal.ThrowExceptionForHR(Win32.OleFlushClipboard());
        }
        finally
        {
            Win32.OleUninitialize();
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

/// <summary>向 PNG 字节流写入/替换 pHYs（物理分辨率）块。</summary>
internal static class PngDpiWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] WithDpi(byte[] png, double dpiX, double dpiY)
    {
        // PNG 签名 8 字节 + IHDR 块（定长 13：8+4+4+13+4 = 33）
        if (png.Length < 33 || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(12)) != 0x49484452u /* IHDR */)
            return png;

        var phys = BuildPhysChunk(dpiX, dpiY);
        using var ms = new MemoryStream(png.Length + phys.Length);
        ms.Write(png, 0, 33); // 签名 + IHDR
        ms.Write(phys);

        // 拷贝其余块，跳过原有 pHYs
        var pos = 33;
        while (pos + 12 <= png.Length)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 4));
            var chunkTotal = 12 + len;
            if (pos + chunkTotal > png.Length)
                break;
            if (type != 0x70485973u /* pHYs */)
                ms.Write(png, pos, chunkTotal);
            pos += chunkTotal;
        }
        return ms.ToArray();
    }

    private static byte[] BuildPhysChunk(double dpiX, double dpiY)
    {
        var chunk = new byte[21]; // 4 长度 + 4 类型 + 9 数据 + 4 CRC
        BinaryPrimitives.WriteInt32BigEndian(chunk, 9);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), 0x70485973u); // "pHYs"
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), (uint)Math.Round(dpiX * 1000.0 / 25.4));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), (uint)Math.Round(dpiY * 1000.0 / 25.4));
        chunk[16] = 1; // 单位：米
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(17), Crc32(chunk.AsSpan(4, 13)));
        return chunk;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
