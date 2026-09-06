using System;

namespace Zhuoying.Platform;

/// <summary>
/// 录屏帧源：固定区域的重复采样（GIF 与 MP4 录制共用）。
/// 底层缓冲复用整个录制生命周期；<see cref="Bits"/> 指向 top-down BGRA，
/// 长度 <see cref="ByteLength"/>，每帧 <see cref="Capture"/> 后原地更新。
/// </summary>
public interface IFrameSource : IDisposable
{
    /// <summary>top-down BGRA 缓冲首地址（生命周期内地址稳定）。</summary>
    IntPtr Bits { get; }

    int ByteLength { get; }

    /// <summary>采样一帧到 <see cref="Bits"/>（含光标）。
    /// 失败返回 false，帧保留上次内容。</summary>
    bool Capture();
}
