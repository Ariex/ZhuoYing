using System;
using System.Collections.Generic;
using System.IO;

namespace Zhuoying.Capture;

/// <summary>
/// 流式 GIF89a 编码器（录屏用）：逐帧写盘、内存占用与录制时长无关。
///
/// 每帧处理：与上一帧逐像素差分求脏矩形 → 只编码脏矩形（帧间差分是 GIF
/// 体积可用的前提，不是优化）；矩形内未变化像素写透明索引（处置法 1
/// "保留原图"），变化像素经八叉树量化到 ≤255 色局部色表；LZW 压缩。
/// 手写而非引库：ImageSharp 的动画 GIF 需全帧驻留内存且有许可条款，
/// 与零依赖 + 流式的目标不合。
/// </summary>
internal sealed class GifWriter : IDisposable
{
    private const int TransparentIndex = 255;

    private readonly Stream _stream;
    private readonly int _width;
    private readonly int _height;
    private byte[]? _prev; // 上一帧 BGRA（差分基准）
    private bool _finished;

    public GifWriter(Stream stream, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 65535 || height > 65535)
            throw new ArgumentException($"非法 GIF 尺寸 {width}x{height}");
        _stream = stream;
        _width = width;
        _height = height;

        _stream.Write("GIF89a"u8);
        Span<byte> lsd = stackalloc byte[7];
        WriteU16(lsd, width);
        WriteU16(lsd[2..], height);
        lsd[4] = 0x70; // 无全局色表，色彩分辨率 8bit
        _stream.Write(lsd);
        // NETSCAPE2.0 扩展：无限循环
        _stream.Write([0x21, 0xFF, 0x0B]);
        _stream.Write("NETSCAPE2.0"u8);
        _stream.Write([0x03, 0x01, 0x00, 0x00, 0x00]);
    }

    /// <summary>已写入的帧数（诊断/控制条显示用）。</summary>
    public int FrameCount { get; private set; }

    /// <summary>
    /// 写入一帧（BGRA，stride = width*4）。delayCs 为该帧的展示时长（1/100 秒，
    /// 下限 2——多数播放器把更小的值按 10cs 处理反而更慢）。
    /// 与上一帧完全相同的帧不应传入（调用方应合并进上一帧的 delay）。
    /// </summary>
    public void AddFrame(byte[] bgra, int delayCs)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        delayCs = Math.Max(2, delayCs);

        // 差分：脏矩形包围盒（首帧 = 全帧）
        int x0 = 0, y0 = 0, x1 = _width, y1 = _height;
        if (_prev != null && !FindDirtyRect(bgra, _prev, out x0, out y0, out x1, out y1))
        {
            // 内容无变化（调用方约定不该出现）：写 1×1 透明帧维持时间轴
            x0 = y0 = 0;
            x1 = y1 = 1;
        }
        int dw = x1 - x0, dh = y1 - y0;

        // 量化：只统计脏矩形内"变化了"的像素（未变化像素走透明索引）
        var quantizer = new OctreeQuantizer();
        for (var y = y0; y < y1; y++)
        {
            var row = y * _width * 4;
            for (var x = x0; x < x1; x++)
            {
                var p = row + x * 4;
                if (_prev != null && SamePixel(bgra, _prev, p))
                    continue;
                quantizer.Add(bgra[p + 2], bgra[p + 1], bgra[p]);
            }
        }
        var palette = quantizer.BuildPalette(TransparentIndex); // ≤255 色

        // 索引化
        var indices = new byte[dw * dh];
        var i = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = y * _width * 4;
            for (var x = x0; x < x1; x++)
            {
                var p = row + x * 4;
                indices[i++] = _prev != null && SamePixel(bgra, _prev, p)
                    ? (byte)TransparentIndex
                    : quantizer.GetIndex(bgra[p + 2], bgra[p + 1], bgra[p]);
            }
        }

        // 图形控制扩展：处置法 1（保留），透明索引启用
        Span<byte> gce = stackalloc byte[8];
        gce[0] = 0x21;
        gce[1] = 0xF9;
        gce[2] = 0x04;
        gce[3] = (1 << 2) | 1;
        WriteU16(gce[4..], delayCs);
        gce[6] = TransparentIndex;
        gce[7] = 0x00;
        _stream.Write(gce);

        // 图像描述符 + 256 项局部色表
        Span<byte> desc = stackalloc byte[10];
        desc[0] = 0x2C;
        WriteU16(desc[1..], x0);
        WriteU16(desc[3..], y0);
        WriteU16(desc[5..], dw);
        WriteU16(desc[7..], dh);
        desc[9] = 0x80 | 7; // 局部色表，2^(7+1)=256 项
        _stream.Write(desc);
        _stream.Write(palette);

        WriteLzw(indices);

        _prev ??= new byte[bgra.Length];
        bgra.AsSpan().CopyTo(_prev);
        FrameCount++;
    }

    public void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        _stream.WriteByte(0x3B); // trailer
        _stream.Flush();
    }

    public void Dispose() => Finish();

    private static bool SamePixel(byte[] a, byte[] b, int p) =>
        a[p] == b[p] && a[p + 1] == b[p + 1] && a[p + 2] == b[p + 2];

    /// <summary>求两帧差异包围盒（uint 逐像素比较，alpha 位一致可含）。无差异返回 false。</summary>
    private unsafe bool FindDirtyRect(byte[] cur, byte[] prev, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = _width;
        y0 = _height;
        x1 = 0;
        y1 = 0;
        fixed (byte* pc = cur, pp = prev)
        {
            var c = (uint*)pc;
            var p = (uint*)pp;
            for (var y = 0; y < _height; y++)
            {
                var row = y * _width;
                var rowDirty = false;
                for (var x = 0; x < _width; x++)
                {
                    if ((c[row + x] | 0xFF000000u) == (p[row + x] | 0xFF000000u))
                        continue;
                    if (x < x0) x0 = x;
                    if (x >= x1) x1 = x + 1;
                    rowDirty = true;
                }
                if (rowDirty)
                {
                    if (y < y0) y0 = y;
                    y1 = y + 1;
                }
            }
        }
        return x1 > x0;
    }

    private static void WriteU16(Span<byte> dst, int value)
    {
        dst[0] = (byte)value;
        dst[1] = (byte)(value >> 8);
    }

    // ---------- LZW（GIF 变长码，最小码宽 8） ----------

    private byte[] _block = new byte[255];
    private int _blockLen;
    private uint _bitBuffer;
    private int _bitCount;

    private void WriteLzw(byte[] indices)
    {
        _stream.WriteByte(8); // 最小码宽
        _bitBuffer = 0;
        _bitCount = 0;
        _blockLen = 0;

        const int clearCode = 256, eoiCode = 257;
        var codeSize = 9;
        var nextCode = 258;
        var dict = new Dictionary<int, int>(4096);

        OutputCode(clearCode, codeSize);
        int prefix = indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            var k = indices[i];
            var key = (prefix << 8) | k;
            if (dict.TryGetValue(key, out var code))
            {
                prefix = code;
                continue;
            }
            OutputCode(prefix, codeSize);
            if (nextCode < 4096)
            {
                dict[key] = nextCode++;
                // 升位宽要比"表长到达 2^codeSize"晚一个码：编码器每输出一个码就
                // 建一条新表项，而解码器读到的第一个数据码建不了表项（它还没有
                // 前缀），此后恒比编码器少一条。若按 nextCode == 2^codeSize 升位，
                // 编码器会提前一个码切到新位宽，解码器仍按旧位宽读 —— 从此整条
                // 码流比特错位，解出天文数字的非法码。
                // 症状极具迷惑性：小帧（差分帧只有几十个码）根本到不了 512 这个
                // 升位点，一路正常；只有数据量大的整帧才炸，且 ffmpeg/浏览器对
                // LZW 错误容错、照样显示，肉眼看不出来（Pillow 会如实报
                // "broken data stream"）。详见 docs/TROUBLESHOOTING.md §16。
                if (nextCode == (1 << codeSize) + 1 && codeSize < 12)
                    codeSize++;
            }
            else
            {
                OutputCode(clearCode, codeSize);
                dict.Clear();
                codeSize = 9;
                nextCode = 258;
            }
            prefix = k;
        }
        OutputCode(prefix, codeSize);
        OutputCode(eoiCode, codeSize);
        // 冲刷残余位与子块，写块终止符
        while (_bitCount > 0)
        {
            AppendByte((byte)_bitBuffer);
            _bitBuffer >>= 8;
            _bitCount -= 8;
        }
        FlushBlock();
        _stream.WriteByte(0x00);
    }

    private void OutputCode(int code, int codeSize)
    {
        _bitBuffer |= (uint)code << _bitCount;
        _bitCount += codeSize;
        while (_bitCount >= 8)
        {
            AppendByte((byte)_bitBuffer);
            _bitBuffer >>= 8;
            _bitCount -= 8;
        }
    }

    private void AppendByte(byte b)
    {
        _block[_blockLen++] = b;
        if (_blockLen == 255)
            FlushBlock();
    }

    private void FlushBlock()
    {
        if (_blockLen == 0)
            return;
        _stream.WriteByte((byte)_blockLen);
        _stream.Write(_block, 0, _blockLen);
        _blockLen = 0;
    }

    // ---------- 八叉树量化（≤255 色 + 保留透明索引） ----------

    private sealed class OctreeQuantizer
    {
        private const int MaxColors = 255;

        private sealed class Node
        {
            public Node?[] Children = new Node?[8];
            public bool IsLeaf;
            public long R, G, B, Count;
            public int Index;
        }

        private readonly Node _root = new();
        private readonly List<Node>[] _levels = new List<Node>[8];
        private int _leafCount;

        public OctreeQuantizer()
        {
            for (var i = 0; i < 8; i++)
                _levels[i] = [];
        }

        public void Add(byte r, byte g, byte b)
        {
            var node = _root;
            for (var depth = 0; depth < 8; depth++)
            {
                if (node.IsLeaf)
                    break;
                var bit = 7 - depth;
                var idx = (((r >> bit) & 1) << 2) | (((g >> bit) & 1) << 1) | ((b >> bit) & 1);
                var child = node.Children[idx];
                if (child == null)
                {
                    child = new Node();
                    node.Children[idx] = child;
                    if (depth == 7)
                    {
                        child.IsLeaf = true;
                        _leafCount++;
                    }
                    else
                    {
                        _levels[depth + 1].Add(child); // 记录为可归并层
                    }
                }
                node = child;
                if (node.IsLeaf)
                    break;
            }
            node.R += r;
            node.G += g;
            node.B += b;
            node.Count++;
            while (_leafCount > MaxColors && Reduce())
            {
            }
        }

        /// <summary>合并最深层的一个内部节点（其子叶并为一叶）。无可归并返回 false。</summary>
        private bool Reduce()
        {
            for (var level = 7; level >= 1; level--)
            {
                var list = _levels[level];
                while (list.Count > 0)
                {
                    var node = list[^1];
                    list.RemoveAt(list.Count - 1);
                    if (node.IsLeaf)
                        continue; // 已被更早的归并变成叶
                    var merged = 0;
                    foreach (var child in node.Children)
                    {
                        if (child == null)
                            continue;
                        // 子节点若还是内部节点，其下叶子也一并汇总（深层先归并，
                        // 正常不会出现；防御处理）
                        Accumulate(child, node);
                        merged++;
                    }
                    if (merged == 0)
                        continue;
                    node.IsLeaf = true;
                    Array.Clear(node.Children);
                    _leafCount++;
                    return true;
                }
            }
            return false;
        }

        private void Accumulate(Node child, Node into)
        {
            if (child.IsLeaf)
            {
                into.R += child.R;
                into.G += child.G;
                into.B += child.B;
                into.Count += child.Count;
                _leafCount--;
                return;
            }
            foreach (var grand in child.Children)
                if (grand != null)
                    Accumulate(grand, into);
        }

        /// <summary>生成 256 项色表（末项为透明占位），并给叶子分配索引。</summary>
        public byte[] BuildPalette(int transparentIndex)
        {
            var palette = new byte[256 * 3];
            var next = 0;
            AssignIndex(_root, palette, ref next, transparentIndex);
            return palette;
        }

        private static void AssignIndex(Node node, byte[] palette, ref int next, int transparentIndex)
        {
            if (node.IsLeaf)
            {
                if (next == transparentIndex)
                    next++; // 保留透明槽（约定 MaxColors=255 时不会到达）
                node.Index = Math.Min(next, 254);
                if (node.Count > 0 && next < 255)
                {
                    palette[next * 3 + 0] = (byte)(node.R / node.Count);
                    palette[next * 3 + 1] = (byte)(node.G / node.Count);
                    palette[next * 3 + 2] = (byte)(node.B / node.Count);
                }
                next++;
                return;
            }
            foreach (var child in node.Children)
                if (child != null)
                    AssignIndex(child, palette, ref next, transparentIndex);
        }

        /// <summary>颜色 → 色表索引（沿树下行到叶）。</summary>
        public byte GetIndex(byte r, byte g, byte b)
        {
            var node = _root;
            for (var depth = 0; depth < 8 && !node.IsLeaf; depth++)
            {
                var bit = 7 - depth;
                var idx = (((r >> bit) & 1) << 2) | (((g >> bit) & 1) << 1) | ((b >> bit) & 1);
                var child = node.Children[idx];
                if (child == null)
                    break; // 不该发生（查询颜色都 Add 过）；就近取当前节点
                node = child;
            }
            return (byte)node.Index;
        }
    }
}
