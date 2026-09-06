using System;
using System.Runtime.InteropServices;
using Avalonia;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// 录屏帧源的 Wayland 实现：复用截屏已建立的 portal ScreenCast 会话，
/// 整个录制期间保持一条 PipeWire 流常驻（对比截屏是按需启停）。
///
/// 光标由合成器直接画进帧（SelectSources 时选了 CURSOR_MODE_EMBEDDED），
/// 不需要像 X11 侧那样用 XFixes 手工合成。
/// </summary>
public sealed unsafe class WaylandFrameSource : IFrameSource
{
    private readonly PixelRect _region;
    private readonly PixelRect _streamBounds;
    private readonly IntPtr _buffer;
    private readonly int _stride;
    private readonly byte[] _frame;
    private readonly PipeWireFrameReader _reader;
    private bool _disposed;

    public IntPtr Bits => _buffer;

    public int ByteLength => _region.Width * _region.Height * 4;

    internal WaylandFrameSource(PortalScreenCast portal, PixelRect region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException($"非法录制区域 {region}");
        _region = region;
        _stride = region.Width * 4;
        _buffer = Marshal.AllocHGlobal(ByteLength);
        NativeMemory.Clear((void*)_buffer, (nuint)ByteLength);

        portal.EnsureSession();
        var streams = portal.Streams;
        if (streams.Count == 0)
            throw new InvalidOperationException("portal 会话没有可用视频流");
        _streamBounds = streams[0].Bounds;
        var size = new PixelSize(_streamBounds.Width, _streamBounds.Height);
        _frame = new byte[(long)size.Width * size.Height * 4];
        _reader = new PipeWireFrameReader(streams[0].NodeId, size);
    }

    public bool Capture()
    {
        if (_disposed || !_reader.ReadFrame(_frame))
            return false; // 帧保留上次内容（接口约定）

        var srcStride = _streamBounds.Width * 4;
        var offsetX = _region.X - _streamBounds.X;
        var offsetY = _region.Y - _streamBounds.Y;

        fixed (byte* src = _frame)
        {
            for (var y = 0; y < _region.Height; y++)
            {
                var sy = offsetY + y;
                var dRow = (uint*)((byte*)_buffer + (long)y * _stride);
                if (sy < 0 || sy >= _streamBounds.Height)
                {
                    for (var x = 0; x < _region.Width; x++)
                        dRow[x] = 0xFF000000u;
                    continue;
                }
                var sRow = (uint*)(src + (long)sy * srcStride);
                for (var x = 0; x < _region.Width; x++)
                {
                    var sx = offsetX + x;
                    dRow[x] = sx >= 0 && sx < _streamBounds.Width
                        ? sRow[sx] | 0xFF000000u
                        : 0xFF000000u;
                }
            }
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _reader.Dispose();
        Marshal.FreeHGlobal(_buffer);
    }
}
