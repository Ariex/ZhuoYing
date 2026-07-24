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
}
