using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Zhuoying.Agent;

/// <summary>
/// 极简 PNG 编码器（BGRA → 8bit RGB，滤波 0，zlib/Fastest，含 pHYs DPI 块）。
/// 无头 Agent API 进程不初始化 Avalonia 平台，不能用 WriteableBitmap.Save——
/// 自带编码器让 --api-capture 保持零框架依赖、启动即抓即退。
/// </summary>
internal static unsafe class MiniPng
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(byte* bgra, int width, int height, int stride, double dpi)
    {
        // 原始扫描线：每行 1 字节滤波类型(0) + RGB
        var raw = new byte[(long)height * (width * 3 + 1)];
        var p = 0;
        for (var y = 0; y < height; y++)
        {
            raw[p++] = 0;
            var row = bgra + (long)y * stride;
            for (var x = 0; x < width; x++)
            {
                raw[p++] = row[x * 4 + 2];
                raw[p++] = row[x * 4 + 1];
                raw[p++] = row[x * 4 + 0];
            }
        }

        // zlib 包装：78 9C 头 + deflate + Adler-32
        byte[] idat;
        using (var ms = new MemoryStream())
        {
            ms.WriteByte(0x78);
            ms.WriteByte(0x9C);
            using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                ds.Write(raw, 0, raw.Length);
            Span<byte> adler = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(raw));
            ms.Write(adler);
            idat = ms.ToArray();
        }

        using var png = new MemoryStream();
        // 注意不能写成 "\x89PNG..."u8——u8 是 UTF-8 编码，\x89 会变成两个字节
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // 位深
        ihdr[9] = 2;  // 颜色类型 RGB
        WriteChunk(png, "IHDR"u8, ihdr);

        Span<byte> phys = stackalloc byte[9];
        var ppm = (uint)Math.Round(dpi / 0.0254);
        BinaryPrimitives.WriteUInt32BigEndian(phys, ppm);
        BinaryPrimitives.WriteUInt32BigEndian(phys[4..], ppm);
        phys[8] = 1; // 单位：米
        WriteChunk(png, "pHYs"u8, phys);

        WriteChunk(png, "IDAT"u8, idat);
        WriteChunk(png, "IEND"u8, default);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFFu);
        s.Write(crcBytes);
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var t in data)
        {
            a = (a + t) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
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
}
