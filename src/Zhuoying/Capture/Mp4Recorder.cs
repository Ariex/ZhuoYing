using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia;
using Zhuoying.Platform;

namespace Zhuoying.Capture;

/// <summary>
/// MP4 (H.264) 录屏：固定帧率采样 + 平台视频编码器（<see cref="IVideoEncoder"/>）。
/// 单线程即可（对比 GifRecorder 无需相同帧合并——编码器对静止画面近零码率）；
/// 时间戳用真实流逝时间，回放忠于实际。
///
/// 本类自身纯托管：编码走 Windows 的 Media Foundation 或 Linux 的 ffmpeg，
/// 帧源走 X11/GDI，两者都由 PlatformServices 选定。
/// </summary>
internal sealed class Mp4Recorder : IScreenRecorder
{
    /// <summary>平台 H.264 编码器的分辨率上限。</summary>
    public static int MaxWidth => PlatformServices.VideoLimits.MaxWidth;
    public static int MaxHeight => PlatformServices.VideoLimits.MaxHeight;

    private readonly PixelRect _region;
    private readonly int _fps;
    private readonly string _path;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private readonly IVideoEncoder _encoder;
    private volatile bool _stopping;
    private bool _stopped;
    private int _frameCount;

    /// <summary>H.264 要求偶数边长：区域宽高向下取偶（至多牺牲 1px）。</summary>
    public static PixelRect EvenRegion(PixelRect region) =>
        new(region.X, region.Y, Math.Max(2, region.Width & ~1), Math.Max(2, region.Height & ~1));

    public Mp4Recorder(PixelRect region, int fps, string path)
    {
        _region = EvenRegion(region);
        if (_region.Width > MaxWidth || _region.Height > MaxHeight)
            throw new InvalidOperationException(
                $"区域 {_region.Width}×{_region.Height} 超出 H.264 编码上限 {MaxWidth}×{MaxHeight}");
        _fps = Math.Clamp(fps, 1, 60);
        _path = path;

        // 编码器创建失败（Windows N 缺媒体功能包 / Linux 缺 ffmpeg）直接抛给调用方
        _encoder = PlatformServices.CreateVideoEncoder(_region, _fps, path);

        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "mp4-capture" };
        _thread.Start();
    }

    public TimeSpan Elapsed => _clock.Elapsed;

    public long BytesWritten
    {
        get
        {
            try
            {
                return new FileInfo(_path).Length;
            }
            catch (IOException)
            {
                return 0;
            }
        }
    }

    public int FrameCount => _frameCount;

    public void Stop()
    {
        if (_stopped)
            return;
        _stopped = true;
        _stopping = true;
        _thread.Join();
        _encoder.Finish();
        _encoder.Dispose();
    }

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

    private void CaptureLoop()
    {
        using var source = PlatformServices.CreateFrameSource(_region);
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
            tick = (long)(_clock.ElapsedMilliseconds / intervalMs) + 1; // 慢帧跳拍

            if (!source.Capture())
                continue;
            try
            {
                _encoder.WriteFrame(source.Bits, source.ByteLength, _clock.ElapsedMilliseconds);
                _frameCount++;
            }
            catch (InvalidOperationException)
            {
                // 编码失败（磁盘满/设备丢失）：停止采样，Stop 时尽力收尾
                _stopping = true;
            }
        }
    }
}
