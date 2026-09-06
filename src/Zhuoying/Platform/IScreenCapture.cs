using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;

namespace Zhuoying.Platform;

/// <summary>显示器信息。坐标一律为虚拟屏幕物理像素。</summary>
public sealed record MonitorInfo(PixelRect Bounds, PixelRect WorkArea, double Scaling, bool IsPrimary);

public interface IScreenCapture
{
    /// <summary>当前鼠标位置（虚拟屏幕物理像素）。</summary>
    PixelPoint GetCursorPosition();

    /// <summary>包含（或最接近）指定点的显示器。</summary>
    MonitorInfo GetMonitorAt(PixelPoint point);

    /// <summary>全部显示器（二期跨屏抓取用）。</summary>
    IReadOnlyList<MonitorInfo> GetAllMonitors();

    /// <summary>
    /// 抓取虚拟屏幕上指定物理像素区域，返回 Bgra8888 位图（DPI 标记 96）。
    /// 注意：位图保持 96 DPI 供 Avalonia 正常显示；输出（剪贴板/文件）时
    /// 再按来源显示器缩放重新标记 DPI（见 BitmapUtil.Crop）。
    /// </summary>
    WriteableBitmap CaptureRegion(PixelRect physicalRegion);

    /// <summary>
    /// 抓取区域直写调用方缓冲（top-down BGRA，dst 指向 region 左上角像素）。
    /// 与 <see cref="CaptureRegion"/> 同一条抓屏管线，只是省掉 WriteableBitmap——
    /// 供 Agent API / MCP 这类"抓完立刻编码 PNG"的路径使用。
    /// </summary>
    unsafe void CaptureInto(PixelRect region, byte* dst, int dstStride);

    /// <summary>
    /// 当前可见顶层窗口矩形快照（自顶向下 Z 序，物理像素，已去阴影/已过滤
    /// 最小化与隐身窗口）。抓屏瞬间调用，供选区的"窗口吸附"检测。
    /// </summary>
    IReadOnlyList<PixelRect> GetVisibleWindowRects();

    /// <summary>带标题的可见顶层窗口快照（Agent API / MCP 用，
    /// 过滤与 Z 序规则同 <see cref="GetVisibleWindowRects"/>）。</summary>
    IReadOnlyList<(string Title, PixelRect Rect)> GetVisibleWindowsWithTitles();
}
