using System;

namespace Zhuoying.Platform.Windows;

/// <summary>MP4 解码回读自测（--test-mp4）的 Windows 实现：Media Foundation SourceReader。</summary>
internal static class MediaFoundationVideoProbe
{
    /// <summary>解码 MP4 前若干帧，报告尺寸/帧数与指定像素的颜色序列。</summary>
    internal static string Probe(string path, int x, int y)
    {
        MediaFoundation.Startup();
        var reader = IntPtr.Zero;
        var request = IntPtr.Zero;
        var current = IntPtr.Zero;
        try
        {
            // 解码到 RGB32 需显式开视频处理（否则 SetCurrentMediaType 报
            // MF_E_INVALIDMEDIATYPE 0xC00D36B4）
            MediaFoundation.Check(
                MediaFoundation.MFCreateAttributes(out var attrs, 1), "MFCreateAttributes");
            MediaFoundation.SetU32(attrs,
                MediaFoundation.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
            var hrReader = MediaFoundation.MFCreateSourceReaderFromURL(path, attrs, out reader);
            MediaFoundation.Release(ref attrs);
            MediaFoundation.Check(hrReader, "MFCreateSourceReaderFromURL");
            MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out request), "MFCreateMediaType");
            MediaFoundation.SetGuid(request,
                MediaFoundation.MF_MT_MAJOR_TYPE, MediaFoundation.MFMediaType_Video);
            MediaFoundation.SetGuid(request,
                MediaFoundation.MF_MT_SUBTYPE, MediaFoundation.MFVideoFormat_RGB32);
            MediaFoundation.ReaderSetMediaType(reader,
                MediaFoundation.MF_SOURCE_READER_FIRST_VIDEO_STREAM, request);

            current = MediaFoundation.ReaderGetMediaType(reader,
                MediaFoundation.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
            MediaFoundation.TryGetU64(current, MediaFoundation.MF_MT_FRAME_SIZE, out var size);
            var w = (int)(size >> 32);
            var h = (int)(size & 0xFFFFFFFF);
            var strideTopDown = w * 4;
            if (MediaFoundation.TryGetU32(current, MediaFoundation.MF_MT_DEFAULT_STRIDE, out var st)
                && (int)st < 0)
                strideTopDown = -(int)st; // 负 stride = 底朝上，取行时翻转

            var frames = 0;
            var colorSeq = new System.Text.StringBuilder();
            string? previous = null;
            while (frames < 200)
            {
                MediaFoundation.Check(MediaFoundation.ReadSample(reader,
                    MediaFoundation.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    out var flags, out var sample), "ReadSample");
                if ((flags & MediaFoundation.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                    break;
                if (sample == IntPtr.Zero)
                    continue;
                var bytes = MediaFoundation.SampleToBytes(sample);
                MediaFoundation.Release(ref sample);
                frames++;
                var row = MediaFoundation.TryGetU32(current,
                        MediaFoundation.MF_MT_DEFAULT_STRIDE, out var s2) && (int)s2 < 0
                    ? h - 1 - y
                    : y;
                var offset = row * strideTopDown + x * 4;
                if (offset + 4 > bytes.Length)
                    continue;
                var hex = $"#{bytes[offset + 2]:X2}{bytes[offset + 1]:X2}{bytes[offset]:X2}";
                if (hex != previous)
                {
                    colorSeq.Append(hex).Append(' ');
                    previous = hex;
                }
            }
            return $"decoded={w}x{h} frames={frames}\n动画点变色序列: {colorSeq}";
        }
        finally
        {
            MediaFoundation.Release(ref current);
            MediaFoundation.Release(ref request);
            MediaFoundation.Release(ref reader);
            MediaFoundation.Shutdown();
        }
    }
}
