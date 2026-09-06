using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia;
using Zhuoying.Platform;
#if ZY_WINDOWS
using Zhuoying.Platform.Windows;
#endif
using Zhuoying.Settings;

namespace Zhuoying.Agent;

/// <summary>
/// Agent API 无头命令行（v0.15）：供 AI Agent（Claude 等）获取屏幕信息。
///
///   --api-monitors                     显示器拓扑 JSON（物理像素 + 缩放比）
///   --api-windows                      可见顶层窗口 JSON（标题 + 矩形，Z 序自顶向下）
///   --api-capture "x,y,w,h|full" [--out 路径]   区域/全屏截图存 PNG（携带 DPI）
///
/// 特性：第二进程免 UI 即抓即退，不触碰单实例互斥、托盘与热键；坐标一律为
/// 虚拟屏幕物理像素（与 --test-* 一致）；抓取复用 DDA→BitBlt 产线管线。
/// 安全边界：只"看"不"动"；默认关闭，需在设置勾选「允许 Agent API」。
/// MCP 服务不在此处：由托盘常驻实例内置 HTTP 服务承载（McpHttpServer，
/// 设置开关即时启停，无需为 MCP 另起进程）。
/// </summary>
internal static class AgentCli
{
    /// <summary>识别并执行 Agent API 参数；返回 true 表示本进程为 API 调用（应直接退出）。</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        var isApi = Array.Exists(args, a =>
            a is "--api-monitors" or "--api-windows" or "--api-capture");
        if (!isApi)
        {
            exitCode = 0;
            return false;
        }

#if ZY_WINDOWS
        // WinExe 无控制台：附加到父进程控制台（重定向管道时无需，失败无妨）。
        // Linux 上进程本就是 Exe、stdout 直连终端，无需补偿。
        Win32.AttachConsole(Win32.ATTACH_PARENT_PROCESS);
#endif

        if (!new SettingsService().Load().AgentApiEnabled)
        {
            Console.Error.WriteLine("捉影 Agent API 未启用：请在托盘菜单 → 设置 中勾选「允许 Agent API」");
            Console.WriteLine("""{"error":"agent_api_disabled"}""");
            exitCode = 2;
            return true;
        }

        try
        {
            if (Array.IndexOf(args, "--api-monitors") >= 0)
                Console.WriteLine(MonitorsJson());
            else if (Array.IndexOf(args, "--api-windows") >= 0)
                Console.WriteLine(WindowsJson());
            else
                RunCapture(args);
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonText(w =>
            {
                w.WriteStartObject();
                w.WriteString("error", ex.Message);
                w.WriteEndObject();
            }));
            exitCode = 1;
        }
        return true;
    }

    private static void RunCapture(string[] args)
    {
        var index = Array.IndexOf(args, "--api-capture");
        if (index < 0 || index + 1 >= args.Length)
            throw new ArgumentException("用法：--api-capture \"x,y,w,h\"（或 full） [--out 路径]");
        var region = ParseRegion(args[index + 1]);

        var outIndex = Array.IndexOf(args, "--out");
        var outPath = outIndex >= 0 && outIndex + 1 < args.Length
            ? Path.GetFullPath(args[outIndex + 1])
            : Path.Combine(Path.GetTempPath(),
                $"zhuoying-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

        var png = CapturePng(region, out var dpi);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllBytes(outPath, png);

        Console.WriteLine(JsonText(w =>
        {
            w.WriteStartObject();
            w.WriteString("path", outPath);
            w.WriteNumber("x", region.X);
            w.WriteNumber("y", region.Y);
            w.WriteNumber("width", region.Width);
            w.WriteNumber("height", region.Height);
            w.WriteNumber("dpi", dpi);
            w.WriteEndObject();
        }));
    }

    /// <summary>解析 "x,y,w,h" 或 "full"（整个虚拟屏幕）。</summary>
    internal static PixelRect ParseRegion(string spec)
    {
        if (string.Equals(spec, "full", StringComparison.OrdinalIgnoreCase))
            return VirtualScreen();
        var p = spec.Split(',');
        if (p.Length != 4)
            throw new ArgumentException($"非法区域 \"{spec}\"：应为 x,y,w,h 或 full");
        var rect = new PixelRect(
            int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentException($"非法区域 {rect}");
        return rect;
    }

    internal static PixelRect VirtualScreen()
    {
        PixelRect union = default;
        var first = true;
        foreach (var mon in PlatformServices.ScreenCapture.GetAllMonitors())
        {
            union = first ? mon.Bounds : Union(union, mon.Bounds);
            first = false;
        }
        if (first)
            throw new InvalidOperationException("未检测到显示器");
        return union;
    }

    private static PixelRect Union(PixelRect a, PixelRect b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new PixelRect(x, y,
            Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    /// <summary>抓取区域并编码 PNG（DDA 优先 + BitBlt 回退，同产线管线）。</summary>
    internal static unsafe byte[] CapturePng(PixelRect region, out double dpi)
    {
        var stride = region.Width * 4;
        var buffer = new byte[(long)stride * region.Height];
        fixed (byte* dst = buffer)
        {
            // 与产线抓屏同一条管线（Windows: DDA + BitBlt 回退；Linux: XGetImage），
            // 平台内部各自处理与 MCP 服务线程的互斥
            PlatformServices.ScreenCapture.CaptureInto(region, dst, stride);

            // DPI 取与选区重叠面积最大的显示器（与保存/复制一致）
            double scaling = 1;
            long best = -1;
            foreach (var mon in PlatformServices.ScreenCapture.GetAllMonitors())
            {
                var i = region.Intersect(mon.Bounds);
                var area = (long)Math.Max(0, i.Width) * Math.Max(0, i.Height);
                if (area > best)
                {
                    best = area;
                    scaling = mon.Scaling;
                }
            }
            dpi = 96 * scaling;
            return MiniPng.Encode(dst, region.Width, region.Height, stride, dpi);
        }
    }

    internal static string MonitorsJson() => JsonText(w =>
    {
        w.WriteStartArray();
        var index = 0;
        foreach (var mon in PlatformServices.ScreenCapture.GetAllMonitors())
        {
            w.WriteStartObject();
            w.WriteNumber("index", index++);
            w.WriteNumber("x", mon.Bounds.X);
            w.WriteNumber("y", mon.Bounds.Y);
            w.WriteNumber("width", mon.Bounds.Width);
            w.WriteNumber("height", mon.Bounds.Height);
            w.WriteNumber("workX", mon.WorkArea.X);
            w.WriteNumber("workY", mon.WorkArea.Y);
            w.WriteNumber("workWidth", mon.WorkArea.Width);
            w.WriteNumber("workHeight", mon.WorkArea.Height);
            w.WriteNumber("scaling", mon.Scaling);
            w.WriteBoolean("primary", mon.IsPrimary);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    internal static string WindowsJson() => JsonText(w =>
    {
        w.WriteStartArray();
        foreach (var (title, rect) in PlatformServices.ScreenCapture.GetVisibleWindowsWithTitles())
        {
            w.WriteStartObject();
            w.WriteString("title", title);
            w.WriteNumber("x", rect.X);
            w.WriteNumber("y", rect.Y);
            w.WriteNumber("width", rect.Width);
            w.WriteNumber("height", rect.Height);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    internal static string JsonText(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            write(w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
