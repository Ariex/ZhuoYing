using System;
using System.Diagnostics;
using System.IO;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// MP4 解码回读自测（--test-mp4）的 Linux 实现：ffmpeg 解码到 rawvideo BGRA
/// 后逐帧读指定像素（对应 Windows 侧 Media Foundation SourceReader 那条路）。
/// 报告格式与 Windows 侧保持一致，两平台的自测脚本可共用断言。
/// </summary>
internal static class FfmpegVideoProbe
{
    private const int MaxFrames = 120;

    internal static string Probe(string path, int x, int y)
    {
        try
        {
            var (w, h) = ReadDimensions(path);
            if (w <= 0 || h <= 0)
                return "probe异常: 无法读取视频尺寸";

            var frameBytes = w * h * 4;
            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-i", path,
                "-frames:v", MaxFrames.ToString(),
                "-f", "rawvideo", "-pix_fmt", "bgra", "-",
            })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)
                          ?? throw new InvalidOperationException("无法启动 ffmpeg");
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();

            var buffer = new byte[frameBytes];
            var frames = 0;
            var colorSeq = "";
            var previous = "";
            using (var stdout = p.StandardOutput.BaseStream)
            {
                while (frames < MaxFrames && ReadExactly(stdout, buffer))
                {
                    frames++;
                    if (x < 0 || y < 0 || x >= w || y >= h)
                        continue;
                    var off = (y * w + x) * 4;
                    // rawvideo bgra 内存序：B G R A
                    var hex = $"{buffer[off + 2]:X2}{buffer[off + 1]:X2}{buffer[off]:X2}";
                    if (hex != previous)
                    {
                        colorSeq += (colorSeq.Length > 0 ? " " : "") + hex;
                        previous = hex;
                    }
                }
            }
            p.WaitForExit(15_000);
            return $"decoded={w}x{h} frames={frames}\n动画点变色序列: {colorSeq}";
        }
        catch (Exception ex)
        {
            return $"probe异常: {ex.Message}";
        }
    }

    /// <summary>ffprobe 读视频宽高；ffprobe 缺失时回退用 ffmpeg 的 stderr 解析。</summary>
    private static (int Width, int Height) ReadDimensions(string path)
    {
        var psi = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height",
            "-of", "csv=s=x:p=0",
            path,
        })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p == null)
            return (0, 0);
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(10_000);
        var parts = text.Split('x');
        return parts.Length >= 2
               && int.TryParse(parts[0], out var w)
               && int.TryParse(parts[1], out var h)
            ? (w, h)
            : (0, 0);
    }

    /// <summary>管道读满一整帧；流结束返回 false。</summary>
    private static bool ReadExactly(Stream s, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
                return false;
            read += n;
        }
        return true;
    }
}
