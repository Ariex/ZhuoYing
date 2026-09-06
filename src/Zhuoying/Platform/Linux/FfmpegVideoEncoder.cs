using System;
using System.Diagnostics;
using System.IO;
using Avalonia;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// MP4 (H.264) 编码的 Linux 实现：ffmpeg 子进程，stdin 喂 rawvideo BGRA。
///
/// 与 Media Foundation 的关键差异：rawvideo 输入**没有每帧时间戳**，
/// ffmpeg 按固定 -framerate 均匀排帧。上层的采样循环会在系统繁忙时跳拍
/// （慢帧丢帧），若照直喂给 ffmpeg 会让回放变快、时长短于实际。
/// 故本实现按 <c>timestampMs</c> 补帧：空档处重复上一帧，保证
/// "第 n 帧 = 第 n/fps 秒"，回放时长忠于真实流逝时间。
///
/// 选 ffmpeg 而非 VAAPI/纯托管编码器：libx264 无硬件依赖、各发行版都有，
/// 且 GIF 路径本就纯托管，MP4 是唯一需要外部编码器的场景。
/// </summary>
public sealed class FfmpegVideoEncoder : IVideoEncoder
{
    /// <summary>libx264 Level 5.2 的分辨率上限，与 Windows 侧取齐。</summary>
    public static VideoEncoderLimits Limits => new(4096, 2304);

    private readonly int _fps;
    private readonly int _byteLength;
    private readonly byte[] _lastFrame;
    private readonly Process _ffmpeg;
    private readonly Stream _stdin;
    private long _framesWritten;
    private bool _hasFrame;

    /// <summary>stdin 已关闭或管道已断，不能再喂帧。</summary>
    private bool _inputClosed;

    /// <summary>Finish 已执行过（收尾只做一次）。</summary>
    private bool _finished;

    public FfmpegVideoEncoder(PixelRect region, int fps, string path)
    {
        _fps = fps;
        _byteLength = region.Width * region.Height * 4;
        _lastFrame = new byte[_byteLength];

        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "-s", $"{region.Width}x{region.Height}",
            "-framerate", fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", "-",
            "-c:v", "libx264",
            "-preset", "veryfast",
            // 屏幕内容优化：静止画面码率近零，文字边缘锐利
            "-tune", "stillimage",
            "-crf", "23",
            // yuv420p 才是各播放器/浏览器通吃的像素格式
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            "-y", path,
        })
            psi.ArgumentList.Add(a);

        try
        {
            _ffmpeg = Process.Start(psi)
                      ?? throw new InvalidOperationException("无法启动 ffmpeg");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                "MP4 录制需要 ffmpeg，请先安装（Ubuntu: sudo apt install ffmpeg）", ex);
        }
        // stderr 必须持续排空，否则管道缓冲写满会让 ffmpeg 卡死
        _ffmpeg.ErrorDataReceived += (_, _) => { };
        _ffmpeg.BeginErrorReadLine();
        _stdin = _ffmpeg.StandardInput.BaseStream;
    }

    public unsafe void WriteFrame(IntPtr bgra, int byteLength, long timestampMs)
    {
        if (_inputClosed || byteLength != _byteLength)
            return;

        // 该时间戳应落在第几帧（0 基），据此补齐中间被跳过的拍子
        var targetIndex = timestampMs * _fps / 1000;
        if (_hasFrame)
            while (_framesWritten <= targetIndex - 1)
                WriteRaw(_lastFrame);

        new ReadOnlySpan<byte>((void*)bgra, byteLength).CopyTo(_lastFrame);
        _hasFrame = true;
        WriteRaw(_lastFrame);
    }

    private void WriteRaw(byte[] frame)
    {
        try
        {
            _stdin.Write(frame, 0, frame.Length);
            _framesWritten++;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // ffmpeg 已退出（磁盘满/编码失败）：停止喂帧，Finish 时收集退出码
            _inputClosed = true;
        }
    }

    public void Finish()
    {
        // 收尾只做一次。Dispose 也会调本方法，此处不短路就会对已关闭的
        // stdin 再 Flush 一次，抛 ObjectDisposedException 并把异常甩给调用方
        // （实测让 Mp4Recorder.Stop 中断、整个录制流程挂死）。
        if (_finished)
            return;
        _finished = true;
        try
        {
            if (!_inputClosed)
                _stdin.Flush();
            _stdin.Dispose(); // 关 stdin = 告诉 ffmpeg 输入结束
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // 已断开
        }
        finally
        {
            _inputClosed = true;
        }
        // 封装收尾（moov 前置）可能耗时，给足时间
        if (!_ffmpeg.WaitForExit(30_000))
        {
            try { _ffmpeg.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* 已退出 */ }
        }
    }

    public void Dispose()
    {
        Finish();
        _ffmpeg.Dispose();
    }
}
