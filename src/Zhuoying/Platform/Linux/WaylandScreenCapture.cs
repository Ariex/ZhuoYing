using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// Wayland 抓屏：xdg-desktop-portal ScreenCast + PipeWire。
///
/// 为什么不能沿用 <see cref="X11ScreenCapture"/>：Wayland 会话里应用经 Xwayland
/// 运行，X11 的 root window 只含 X 客户端，合成器与原生 Wayland 窗口一律抓不到
/// （实测 1920×1080 里 2073458 像素纯黑）。
///
/// 坐标系：portal 给每路流报告 position/size，即该显示器在合成器全局坐标中的
/// 位置——项目"虚拟屏物理像素为唯一真源"的约定在这里仍然成立，
/// 只是真源从 X11 root window 换成了 portal 报告的流布局。
/// </summary>
public sealed class WaylandScreenCapture : IScreenCapture
{
    private readonly PortalScreenCast _portal = new();

    /// <summary>指针位置仍走 X11（Xwayland 的指针跟随合成器，坐标与全局一致）。
    /// Wayland 本身没有"查询全局指针"的协议，portal 的 RemoteDesktop 能做但要另一次授权。</summary>
    private readonly X11ScreenCapture _x11 = new();

    public PixelPoint GetCursorPosition() => _x11.GetCursorPosition();

    public MonitorInfo GetMonitorAt(PixelPoint point)
    {
        var all = GetAllMonitors();
        foreach (var m in all)
            if (m.Bounds.Contains(point))
                return m;
        return all.Count > 0 ? all[0] : throw new InvalidOperationException("未检测到显示器");
    }

    /// <summary>
    /// 显示器列表。会话已建立时用 portal 报告的流布局（权威）；
    /// 未建立时退回 X11/XRandR——Xwayland 从合成器拿到的输出布局是对的，
    /// 抓不到的只是像素内容，显示器几何本身可信。
    /// </summary>
    public IReadOnlyList<MonitorInfo> GetAllMonitors()
    {
        var streams = _portal.Streams;
        if (streams.Count == 0)
            return _x11.GetAllMonitors();

        var scaling = X11Display.ReadXftDpi() / 96.0;
        if (scaling <= 0)
            scaling = 1.0;
        var list = new List<MonitorInfo>(streams.Count);
        for (var i = 0; i < streams.Count; i++)
            list.Add(new MonitorInfo(streams[i].Bounds, streams[i].Bounds, scaling, i == 0));
        return list;
    }

    public unsafe WriteableBitmap CaptureRegion(PixelRect region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException($"非法抓取区域 {region}");
        var bitmap = new WriteableBitmap(
            new PixelSize(region.Width, region.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        CaptureInto(region, (byte*)fb.Address, fb.RowBytes);
        return bitmap;
    }

    public unsafe void CaptureInto(PixelRect region, byte* dst, int dstStride)
    {
        _portal.EnsureSession();
        var streams = _portal.Streams;
        if (streams.Count == 0)
            throw new InvalidOperationException("portal 会话没有可用视频流");

        // multiple:false 只会有一路流；多屏跨屏抓取见 docs/LINUX-PORT.md「待办」
        var stream = streams[0];
        var size = new PixelSize(stream.Bounds.Width, stream.Bounds.Height);
        if (size.Width <= 0 || size.Height <= 0)
            throw new InvalidOperationException($"portal 报告的流尺寸非法 {size}");

        var frame = new byte[(long)size.Width * size.Height * 4];
        using (var reader = new PipeWireFrameReader(stream.NodeId, size))
        {
            if (!reader.ReadFirstFrame(frame))
            {
                // 流建立后立刻断掉，通常是授权被撤销/会话被合成器关闭
                _portal.Reset();
                throw new InvalidOperationException("PipeWire 流读取失败（授权可能已被撤销）");
            }
        }

        BlitRegion(frame, stream.Bounds, region, dst, dstStride);
    }

    /// <summary>把整屏帧里的 region 部分拷进 dst；越界部分留黑。</summary>
    private static unsafe void BlitRegion(byte[] frame, PixelRect streamBounds,
        PixelRect region, byte* dst, int dstStride)
    {
        var srcStride = streamBounds.Width * 4;
        // region 是全局坐标，减去本屏原点得到流内坐标
        var offsetX = region.X - streamBounds.X;
        var offsetY = region.Y - streamBounds.Y;

        fixed (byte* src = frame)
        {
            for (var y = 0; y < region.Height; y++)
            {
                var sy = offsetY + y;
                var dRow = (uint*)(dst + (long)y * dstStride);
                if (sy < 0 || sy >= streamBounds.Height)
                {
                    for (var x = 0; x < region.Width; x++)
                        dRow[x] = 0xFF000000u;
                    continue;
                }
                var sRow = (uint*)(src + (long)sy * srcStride);
                for (var x = 0; x < region.Width; x++)
                {
                    var sx = offsetX + x;
                    // 合成器给的 alpha 不可靠，与 X11/GDI 两侧一样强制不透明
                    dRow[x] = sx >= 0 && sx < streamBounds.Width
                        ? sRow[sx] | 0xFF000000u
                        : 0xFF000000u;
                }
            }
        }
    }

    /// <summary>
    /// Wayland 下**无法**枚举其他应用的窗口——合成器不向客户端暴露
    /// 全局窗口列表，这是安全模型的硬限制，没有 portal 接口可绕。
    /// 返回空列表即"没有可吸附的窗口"，选区的窗口吸附功能在 Wayland 上自然降级。
    /// </summary>
    public IReadOnlyList<PixelRect> GetVisibleWindowRects() => [];

    public IReadOnlyList<(string Title, PixelRect Rect)> GetVisibleWindowsWithTitles() => [];

    /// <summary>供帧源复用同一个 portal 会话（录屏不必再授权一次）。</summary>
    internal PortalScreenCast Portal => _portal;
}
