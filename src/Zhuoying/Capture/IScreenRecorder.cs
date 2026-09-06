using System;

namespace Zhuoying.Capture;

/// <summary>录屏器统一接口（GIF / MP4）。</summary>
internal interface IScreenRecorder : IDisposable
{
    TimeSpan Elapsed { get; }
    long BytesWritten { get; }
    int FrameCount { get; }

    /// <summary>结束录制并完成文件（阻塞至编码收尾）。</summary>
    void Stop();

    /// <summary>取消录制并删除文件。</summary>
    void Cancel();
}
