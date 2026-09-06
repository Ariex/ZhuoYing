using System;
using System.Buffers.Binary;
using System.IO;

namespace Zhuoying.Platform;

/// <summary>向 PNG 字节流写入/替换 pHYs（物理分辨率）块。</summary>
public static class PngDpiWriter
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
