using System;
using Avalonia;

namespace Zhuoying.Platform.Windows;

/// <summary>
/// MP4 (H.264) 编码的 Windows 实现：Media Foundation SinkWriter，输入 RGB32 帧，
/// 颜色转换与编码器（优先硬件）由 SinkWriter 自动插入，WriteSample 内部异步不阻塞采样。
/// </summary>
public sealed class MediaFoundationVideoEncoder : IVideoEncoder
{
    /// <summary>Windows 自带 H.264 编码器的分辨率上限（Level 5.2）。</summary>
    public static VideoEncoderLimits Limits => new(4096, 2304);

    private readonly long _durationNs100;
    private IntPtr _writer;
    private readonly uint _stream;
    private bool _finished;

    public MediaFoundationVideoEncoder(PixelRect region, int fps, string path)
    {
        _durationNs100 = 10_000_000L / fps;

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
                MediaFoundation.MFVideoFormat_H264, region.Width, region.Height, fps);
            // 码率经验值：0.1 bpp × 像素率，钳位 1–25 Mbps（屏幕内容足够清晰）
            var bitrate = (uint)Math.Clamp(
                (long)(region.Width * (double)region.Height * fps * 0.1), 1_000_000, 25_000_000);
            MediaFoundation.SetU32(outType, MediaFoundation.MF_MT_AVG_BITRATE, bitrate);
            _stream = MediaFoundation.AddStream(_writer, outType);

            inType = MediaFoundation.CreateVideoType(
                MediaFoundation.MFVideoFormat_RGB32, region.Width, region.Height, fps);
            // 正 stride = 顶朝下（RGB 默认底朝上，不声明会整帧垂直翻转）
            MediaFoundation.SetU32(inType,
                MediaFoundation.MF_MT_DEFAULT_STRIDE, (uint)(region.Width * 4));
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
    }

    public void WriteFrame(IntPtr bgra, int byteLength, long timestampMs)
    {
        // SinkWriter 按样本自带时间戳排帧，无需补齐等间隔帧
        var sample = MediaFoundation.CreateFrameSample(
            bgra, byteLength, timestampMs * 10_000, _durationNs100);
        try
        {
            MediaFoundation.WriteSample(_writer, _stream, sample);
        }
        finally
        {
            MediaFoundation.Release(ref sample);
        }
    }

    public void Finish()
    {
        if (_finished || _writer == IntPtr.Zero)
            return;
        _finished = true;
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

    public void Dispose() => Finish();
}
