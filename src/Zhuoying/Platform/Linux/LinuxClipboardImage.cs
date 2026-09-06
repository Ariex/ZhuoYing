using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Media.Imaging;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// Linux 剪贴板图片写入：PNG（含 pHYs DPI）交给会话剪贴板守护进程。
///
/// 与 Windows 的关键差异：CLAUDE.md 记的"只写 CF_DIB、绝不同时写 PNG"是
/// Win11 画图的专属坑，Linux 上不适用——X11/Wayland 的图片交换本来就是
/// image/png MIME，DPI 元数据只能走 PNG 的 pHYs 块。
///
/// 实现走外部剪贴板工具而非自己当 selection owner：X11 的 selection 协议要求
/// 所有者进程持续在线响应 SelectionRequest，自己实现等于在热键线程之外再养一个
/// 事件循环；wl-copy/xclip 自带 fork 常驻，且能被 GNOME 正确桥接到两侧剪贴板。
/// </summary>
public sealed class LinuxClipboardImage : IClipboardImage
{
    /// <summary>候选工具，按会话类型排序后依次尝试。</summary>
    private static readonly (string Exe, string[] Args)[] WaylandTools =
    [
        ("wl-copy", ["--type", "image/png"]),
        ("xclip", ["-selection", "clipboard", "-t", "image/png"]),
    ];

    private static readonly (string Exe, string[] Args)[] X11Tools =
    [
        ("xclip", ["-selection", "clipboard", "-t", "image/png"]),
        ("wl-copy", ["--type", "image/png"]),
    ];

    public void SetImage(WriteableBitmap bitmap)
    {
        var png = EncodePng(bitmap);
        var tools = X11Interop.IsWayland ? WaylandTools : X11Tools;

        var errors = new System.Text.StringBuilder();
        foreach (var (exe, args) in tools)
        {
            try
            {
                if (TryPipe(exe, args, png))
                    return;
            }
            catch (Exception ex)
            {
                errors.Append($"{exe}: {ex.Message}; ");
            }
        }
        throw new InvalidOperationException(
            $"写剪贴板失败，未找到可用工具（请安装 wl-clipboard 或 xclip）。{errors}");
    }

    /// <summary>位图 → PNG 字节流，pHYs 写入位图自身的真实 DPI。</summary>
    private static byte[] EncodePng(WriteableBitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        return PngDpiWriter.WithDpi(ms.ToArray(), bitmap.Dpi.X, bitmap.Dpi.Y);
    }

    private static bool TryPipe(string exe, string[] args, byte[] data)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // 工具没装：换下一个候选
        }
        if (p == null)
            return false;

        using (p)
        {
            using (var stdin = p.StandardInput.BaseStream)
                stdin.Write(data, 0, data.Length);
            // wl-copy/xclip 会 fork 出常驻进程持有 selection 后立刻返回；
            // 等它退出即可确认数据已交接，超时按失败处理换下一个工具
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { /* 已退出 */ }
                return false;
            }
            return p.ExitCode == 0;
        }
    }
}
