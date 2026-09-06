using System;
using System.Diagnostics;
using System.IO;
using Avalonia;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// 从 portal 交出的 PipeWire node 读取原始帧。
///
/// 走 <c>gst-launch-1.0</c> 子进程而不是 libpipewire P/Invoke：PipeWire 的
/// C API 要手写 SPA POD 格式协商与一整套回调结构体，工作量与风险都远高于
/// 本项目其他互操作；而管道读原始帧的模式，项目里已有先例
/// （MP4 编码走 ffmpeg 子进程，见 <see cref="FfmpegVideoEncoder"/>）。
///
/// 生命周期：按需启停。常驻流意味着合成器持续推帧、videoconvert 持续转换，
/// 截屏工具平时不该有这个开销；代价是首帧要等一次 PipeWire 协商。
/// </summary>
internal sealed class PipeWireFrameReader : IDisposable
{
    private readonly Process _gst;
    private readonly Stream _stdout;
    private readonly int _frameBytes;
    private bool _disposed;

    internal PixelSize Size { get; }

    /// <summary>启动读取管道。<paramref name="size"/> 用 portal 报告的流尺寸，
    /// 管道里加 videoscale 强制成它，避免合成器给出别的尺寸时读帧错位。</summary>
    internal PipeWireFrameReader(uint nodeId, PixelSize size)
    {
        Size = size;
        _frameBytes = size.Width * size.Height * 4;

        var psi = new ProcessStartInfo("gst-launch-1.0")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
        {
            "-q",
            "pipewiresrc", $"path={nodeId}",
            "!", "videoconvert",
            "!", "videoscale",
            "!", $"video/x-raw,format=BGRA,width={size.Width},height={size.Height}",
            // sync=false 是关键：GstBaseSink 默认 sync=true，会按 buffer 时间戳
            // 等到"该播放的时刻"才吐帧。抓屏要的是立刻拿到当前画面，
            // 默认行为下实测首帧要等 15～57 秒（静止桌面更糟）。
            "!", "fdsink", "fd=1", "sync=false",
        })
            psi.ArgumentList.Add(a);

        try
        {
            _gst = Process.Start(psi)
                   ?? throw new InvalidOperationException("无法启动 gst-launch-1.0");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Wayland 抓屏需要 GStreamer 的 PipeWire 插件，请安装"
                + "（Ubuntu: sudo apt install gstreamer1.0-pipewire gstreamer1.0-plugins-base）", ex);
        }
        _gst.ErrorDataReceived += (_, _) => { };
        _gst.BeginErrorReadLine();
        _stdout = _gst.StandardOutput.BaseStream;
    }

    /// <summary>读满下一帧到 <paramref name="dst"/>（top-down BGRA）。
    /// 流结束或进程死亡返回 false。</summary>
    internal bool ReadFrame(Span<byte> dst)
    {
        if (_disposed || dst.Length < _frameBytes)
            return false;
        var read = 0;
        while (read < _frameBytes)
        {
            int n;
            try
            {
                n = _stdout.Read(dst[read.._frameBytes]);
            }
            catch (IOException)
            {
                return false;
            }
            if (n <= 0)
                return false;
            read += n;
        }
        return true;
    }

    /// <summary>
    /// 取一帧当前画面。
    ///
    /// **不要丢帧**：Windows 侧 DDA 的教训（TROUBLESHOOTING §12，首帧可能是
    /// 未播种的全黑帧，须丢弃重取）在 PipeWire 上反过来——合成器在流建立时就推
    /// 一帧当前内容，之后**只在画面变化时才推新帧**。静止桌面上多要一帧就是
    /// 无限期干等：实测丢一帧后读第二帧，屏幕有活动时要 8 秒，完全静止时
    /// 直接挂到超时。
    /// </summary>
    internal bool ReadFirstFrame(Span<byte> dst) => ReadFrame(dst);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // 直接杀，不等它优雅退出：这条管道是一次性的（抓屏取完帧即弃，
        // 录屏也是结束才走到这里），而 gst-launch 收到 stdin 关闭后
        // 要走完整套 EOS 流程，实测白等满 WaitForExit 的 2 秒——
        // 单次抓屏总耗时 2.25s 里有 2.0s 花在这儿。
        try
        {
            _stdout.Dispose();
        }
        catch (Exception)
        {
            // 管道已断
        }
        try
        {
            // 用 Kill() 而不是 Kill(entireProcessTree: true)：后者在 Linux 上要
            // 扫 /proc 重建整棵进程树，实测耗时抖动到 2 秒；gst-launch 不 fork
            // 子进程，杀它自己就够。
            if (!_gst.HasExited)
                _gst.Kill();
        }
        catch (Exception)
        {
            // 已退出或句柄失效
        }
        _gst.Dispose();
    }
}
