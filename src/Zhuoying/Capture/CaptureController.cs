using System;
using Avalonia;
using Zhuoying.Platform;

namespace Zhuoying.Capture;

/// <summary>
/// 截屏会话控制器：触发 → 冻结抓取 → 覆盖窗口 → 输出。
/// 一期为单屏（鼠标所在显示器）；二期扩展为全部显示器各覆盖一个窗口。
/// </summary>
public sealed class CaptureController
{
    private readonly IScreenCapture _screenCapture;
    private readonly IClipboardImage _clipboard;
    private CaptureOverlayWindow? _overlay;

    public CaptureController(IScreenCapture screenCapture, IClipboardImage clipboard)
    {
        _screenCapture = screenCapture;
        _clipboard = clipboard;
    }

    public void StartCapture()
    {
        if (_overlay != null)
            return; // 会话进行中，忽略再次触发

        var cursor = _screenCapture.GetCursorPosition();
        var monitor = _screenCapture.GetMonitorAt(cursor);
        // 先冻结再显示遮罩，后续操作全部基于冻结帧（REQUIREMENTS §4.1）
        var frame = _screenCapture.CaptureRegion(monitor.Bounds);

        _overlay = new CaptureOverlayWindow(frame, monitor,
            physicalRect => CopyToClipboard(frame, monitor, physicalRect));
        _overlay.Closed += (_, _) =>
        {
            _overlay = null;
            frame.Dispose();
        };
        _overlay.Show();
    }

    private void CopyToClipboard(
        Avalonia.Media.Imaging.WriteableBitmap frame, MonitorInfo monitor, PixelRect physicalRect)
    {
        try
        {
            var dpi = 96.0 * monitor.Scaling;
            var cropped = BitmapUtil.Crop(frame, physicalRect, new Vector(dpi, dpi));
            // 先关遮罩再写剪贴板：进程持有高 DPI 全屏窗口时写入，
            // 剪贴板位图会被打上高 DPI 虚拟化标签，导致部分粘贴目标缩小图像
            _overlay?.Close();
            try
            {
                _clipboard.SetImage(cropped);
            }
            finally
            {
                cropped.Dispose();
            }
        }
        catch (Exception)
        {
            // 一期：输出失败不崩溃；托盘气泡提示留到生命周期完善阶段
            _overlay?.Close();
        }
    }
}
