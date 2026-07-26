using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Zhuoying;

/// <summary>
/// 轻量通知气泡：主屏右下角的置顶小提示窗，2.5 秒自动消失，不抢焦点。
/// 自绘实现（系统气泡需要真实 NotifyIcon 句柄，Avalonia 托盘不外露；
/// Toast/WinRT 在 NativeAOT 下引入成本高）。新通知替换旧通知。
/// </summary>
public static class NotificationToast
{
    private static Window? _current;

    public static void Show(string message)
    {
        _current?.Close();
        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            Topmost = true,
            CanResize = false,
            ShowActivated = false, // 不抢焦点
            WindowStartupLocation = WindowStartupLocation.Manual,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Background = Brushes.Transparent,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x28, 0x28, 0x28)),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                Padding = new Thickness(16, 10),
                Margin = new Thickness(8),
                BoxShadow = BoxShadows.Parse("0 2 10 0 #50000000"),
                Child = new TextBlock
                {
                    Text = message,
                    FontSize = 13,
                    Foreground = Brushes.White,
                    MaxWidth = 420,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        window.Opened += (_, _) =>
        {
            // 主屏工作区右下角（物理像素）
            var screen = window.Screens.Primary ?? window.Screens.ScreenFromPoint(default);
            if (screen != null)
            {
                var s = window.DesktopScaling;
                var w = (int)(window.ClientSize.Width * s);
                var h = (int)(window.ClientSize.Height * s);
                window.Position = new PixelPoint(
                    screen.WorkingArea.Right - w - (int)(8 * s),
                    screen.WorkingArea.Bottom - h - (int)(8 * s));
            }
        };
        window.Closed += (_, _) =>
        {
            if (_current == window)
                _current = null;
        };
        _current = window;
        window.Show();
        DispatcherTimer.RunOnce(() =>
        {
            if (_current == window)
                window.Close();
        }, TimeSpan.FromMilliseconds(2500));
    }
}
