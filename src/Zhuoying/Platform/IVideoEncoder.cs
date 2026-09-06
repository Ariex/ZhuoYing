using System;

namespace Zhuoying.Platform;

/// <summary>
/// 视频编码器（MP4/H.264 录屏用）。实现负责编码与封装，
/// 采样节奏与帧计数由上层 <c>Mp4Recorder</c> 统一驱动。
///
/// Windows：Media Foundation SinkWriter（手写 vtable 互操作）。
/// Linux：ffmpeg 子进程管道（rawvideo → libx264）。
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>写一帧 top-down BGRA。<paramref name="timestampMs"/> 为该帧相对
    /// 录制起点的真实流逝毫秒——实现需据此保证回放时长忠于实际
    /// （编码器若要求等间隔帧，由实现自行补帧）。</summary>
    void WriteFrame(IntPtr bgra, int byteLength, long timestampMs);

    /// <summary>收尾并完成文件（阻塞至封装写完）。</summary>
    void Finish();
}

/// <summary>视频编码器的平台能力约束。</summary>
public readonly record struct VideoEncoderLimits(int MaxWidth, int MaxHeight);
