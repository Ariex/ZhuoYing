using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia;
using Zhuoying.Platform.Windows;

namespace Zhuoying.Capture;

/// <summary>
/// GIF 录屏核心：抓帧线程（BitBlt 逐 tick 采样 + 光标补绘）与编码线程
///（时长合并 + GifWriter 流式写盘）的生产者-消费者管线，UI 无关。
///
/// 帧源用 BitBlt 而非 DDA/WGC：录制区域通常不大，BitBlt 每 tick 数毫秒，
/// 且天然尊重 WDA_EXCLUDEFROMCAPTURE（录制边框/控制条不入镜）；帧源
/// 后续可替换为 DDA 持久会话（DEVPLAN 后续阶段）。
///
/// 时间轴：相同帧不重复写——挂起当前帧直到内容变化，把真实流逝时间
///（1/100 秒取整、误差滚动进位）写为该帧 delay，回放速度忠于实际。
/// </summary>
internal sealed unsafe class GifRecorder : IDisposable
{
    private readonly PixelRect _region;
    private readonly int _fps;
    private readonly string _path;
    private readonly FileStream _file;
    private readonly GifWriter _writer;

    private readonly ConcurrentQueue<byte[]> _pool = new();
    private readonly BlockingCollection<(byte[] Frame, long Ms)> _queue = new(8);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _captureThread;
    private readonly Thread _encodeThread;
    private volatile bool _stopping;
    private int _pooledBuffers;

    public GifRecorder(PixelRect region, int fps, string path)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException($"非法录制区域 {region}");
        _region = region;
        _fps = Math.Clamp(fps, 1, 60);
        _path = path;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new GifWriter(_file, region.Width, region.Height);

        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "gif-capture" };
        _encodeThread = new Thread(EncodeLoop) { IsBackground = true, Name = "gif-encode" };
        _captureThread.Start();
        _encodeThread.Start();
    }

    public TimeSpan Elapsed => _clock.Elapsed;

    public long BytesWritten => _file.CanWrite ? _file.Length : 0;

    public int FrameCount => _writer.FrameCount;

    /// <summary>结束录制并完成文件（阻塞至编码收尾）。</summary>
    public void Stop()
    {
        if (_stopping)
            return;
        _stopping = true;
        _captureThread.Join();
        _queue.CompleteAdding();
        _encodeThread.Join();
        _writer.Finish();
        _file.Dispose();
    }

    /// <summary>取消录制并删除文件。</summary>
    public void Cancel()
    {
        Stop();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose() => Stop();

    // ---------- 抓帧线程 ----------

    private void CaptureLoop()
    {
        int w = _region.Width, h = _region.Height;
        var screenDc = Win32.GetDC(IntPtr.Zero);
        var memDc = Win32.CreateCompatibleDC(screenDc);
        var bmi = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Win32.BI_RGB,
        };
        var hBitmap = Win32.CreateDIBSection(memDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = Win32.SelectObject(memDc, hBitmap);
        try
        {
            var intervalMs = 1000.0 / _fps;
            var tick = 0L;
            while (!_stopping)
            {
                var target = (long)(tick * intervalMs);
                var wait = target - _clock.ElapsedMilliseconds;
                if (wait > 1)
                    Thread.Sleep((int)Math.Min(wait, 50));
                if (_clock.ElapsedMilliseconds < target)
                    continue;
                tick = (long)(_clock.ElapsedMilliseconds / intervalMs) + 1; // 慢帧跳拍不追帧

                if (!Win32.BitBlt(memDc, 0, 0, w, h, screenDc, _region.X, _region.Y,
                        Win32.SRCCOPY | Win32.CAPTUREBLT))
                    continue;
                DrawCursor(memDc);

                // 编码队列满（编码落后）则丢帧：时长合并机制会自然补齐时间轴
                if (!_pool.TryDequeue(out var buffer))
                {
                    if (_pooledBuffers >= 6)
                        continue;
                    buffer = new byte[(long)w * h * 4];
                    _pooledBuffers++;
                }
                new ReadOnlySpan<byte>((void*)bits, w * h * 4).CopyTo(buffer);
                if (!_queue.TryAdd((buffer, _clock.ElapsedMilliseconds)))
                    _pool.Enqueue(buffer);
            }
        }
        finally
        {
            Win32.SelectObject(memDc, old);
            Win32.DeleteObject(hBitmap);
            Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>把当前光标补绘进帧（BitBlt 不含光标；坐标为物理像素）。</summary>
    private void DrawCursor(IntPtr memDc)
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
        Win32.DrawIconEx(memDc,
            ci.ptScreenPos.X - _region.X - (int)hotX,
            ci.ptScreenPos.Y - _region.Y - (int)hotY,
            ci.hCursor, 0, 0, 0, IntPtr.Zero, Win32.DI_NORMAL);
    }

    // ---------- 编码线程 ----------

    private void EncodeLoop()
    {
        byte[]? current = null;
        long currentMs = 0;
        double carryCs = 0;
        long lastMs = 0;

        foreach (var (frame, ms) in _queue.GetConsumingEnumerable())
        {
            lastMs = ms;
            if (current == null)
            {
                current = frame;
                currentMs = ms;
                continue;
            }
            if (frame.AsSpan().SequenceEqual(current))
            {
                _pool.Enqueue(frame); // 无变化：时长并入当前帧
                continue;
            }
            WritePending(current, ms - currentMs, ref carryCs);
            _pool.Enqueue(current);
            current = frame;
            currentMs = ms;
        }
        if (current != null)
            WritePending(current, Math.Max(lastMs, _clock.ElapsedMilliseconds) - currentMs, ref carryCs);
    }

    private void WritePending(byte[] frame, long durationMs, ref double carryCs)
    {
        var exact = durationMs / 10.0 + carryCs;
        var delay = (int)Math.Round(exact);
        carryCs = exact - delay;
        _writer.AddFrame(frame, delay);
    }
}
