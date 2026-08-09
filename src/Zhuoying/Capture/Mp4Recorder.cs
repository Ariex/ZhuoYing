using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia;
using Zhuoying.Platform.Windows;

namespace Zhuoying.Capture;

/// <summary>
/// MP4 (H.264) 录屏：Media Foundation SinkWriter，输入 RGB32 帧，颜色转换与
/// 编码器（优先硬件）由 SinkWriter 自动插入，WriteSample 内部异步不阻塞采样。
/// 单线程即可（对比 GifRecorder 无需相同帧合并——编码器对静止画面近零码率）；
/// 时间戳用真实流逝时间，回放忠于实际。
/// </summary>
internal sealed class Mp4Recorder : IScreenRecorder
{
    /// <summary>Windows 自带 H.264 编码器的分辨率上限（Level 5.2）。</summary>
    public const int MaxWidth = 4096;
    public const int MaxHeight = 2304;

    private readonly PixelRect _region;
    private readonly int _fps;
    private readonly string _path;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private IntPtr _writer;
    private readonly uint _stream;
    private volatile bool _stopping;
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

        MediaFoundation.Startup(); // Windows N 无媒体功能包时在此失败
        var attrs = IntPtr.Zero;
        var outType = IntPtr.Zero;
        var inType = IntPtr.Zero;
        try
        {
            MediaFoundation.Check(
                MediaFoundation.MFCreateAttributes(out attrs, 1), "MFCreateAttributes");
            MediaFoundation.SetU32(attrs,
                MediaFoundation.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            MediaFoundation.Check(
                MediaFoundation.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out _writer),
                "MFCreateSinkWriterFromURL");

            outType = MediaFoundation.CreateVideoType(
                MediaFoundation.MFVideoFormat_H264, _region.Width, _region.Height, _fps);
            // 码率经验值：0.1 bpp × 像素率，钳位 1–25 Mbps（屏幕内容足够清晰）
            var bitrate = (uint)Math.Clamp(
                (long)(_region.Width * (double)_region.Height * _fps * 0.1), 1_000_000, 25_000_000);
            MediaFoundation.SetU32(outType, MediaFoundation.MF_MT_AVG_BITRATE, bitrate);
            _stream = MediaFoundation.AddStream(_writer, outType);

            inType = MediaFoundation.CreateVideoType(
                MediaFoundation.MFVideoFormat_RGB32, _region.Width, _region.Height, _fps);
            // 正 stride = 顶朝下（RGB 默认底朝上，不声明会整帧垂直翻转）
            MediaFoundation.SetU32(inType,
                MediaFoundation.MF_MT_DEFAULT_STRIDE, (uint)(_region.Width * 4));
            MediaFoundation.SetInputMediaType(_writer, _stream, inType);
            MediaFoundation.BeginWriting(_writer);
        }
        catch
        {
            MediaFoundation.Release(ref _writer);
            MediaFoundation.Shutdown();
            throw;
        }
        finally
        {
            MediaFoundation.Release(ref attrs);
            MediaFoundation.Release(ref outType);
            MediaFoundation.Release(ref inType);
        }

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
        if (_stopping)
            return;
        _stopping = true;
        _thread.Join();
        if (_writer != IntPtr.Zero)
        {
            try
            {
                MediaFoundation.FinalizeWriter(_writer);
            }
            finally
            {
                MediaFoundation.Release(ref _writer);
                MediaFoundation.Shutdown();
            }
        }
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
        using var source = new RegionFrameSource(_region);
        var intervalMs = 1000.0 / _fps;
        var durationNs100 = 10_000_000L / _fps;
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
            var sample = MediaFoundation.CreateFrameSample(
                source.Bits, source.ByteLength,
                _clock.ElapsedMilliseconds * 10_000, durationNs100);
            try
            {
                MediaFoundation.WriteSample(_writer, _stream, sample);
                _frameCount++;
            }
            catch (InvalidOperationException)
            {
                // 编码失败（磁盘满/设备丢失）：停止采样，Stop 时 Finalize 尽力收尾
                _stopping = true;
            }
            finally
            {
                MediaFoundation.Release(ref sample);
            }
        }
    }
}
