using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Zhuoying.Platform.Windows;

namespace Zhuoying.Capture;

/// <summary>
/// GIF 录屏会话 UI：区域红框（点击穿透）+ 控制条（时长/大小/停止/取消）。
/// 两个窗口都设 WDA_EXCLUDEFROMCAPTURE——BitBlt 帧源不入镜；红框还画在
/// 区域外扩 2px 处双保险。活屏录制（非冻结帧模式），录制期间用户正常操作。
/// </summary>
public sealed class RecordingController
{
    private static RecordingController? _active;

    private readonly GifRecorder _recorder;
    private readonly string _tempPath;
    private readonly string _saveDir;
    private readonly Window _border;
    private readonly Window _bar;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _uiTimer;
    private bool _finished;

    /// <summary>开始录制（同一时刻只允许一场；重复调用忽略）。</summary>
    public static void Start(PixelRect region, int fps, string saveDir)
    {
        if (_active != null)
            return;
        _active = new RecordingController(region, fps, saveDir);
    }

    /// <summary>是否正在录制（避免录制中再开截屏会话时误操作，目前仅诊断用）。</summary>
    public static bool IsRecording => _active != null;

    /// <summary>结束当前录制（等效点击控制条「完成」；自测钩子用）。</summary>
    internal static void FinishActive() => _active?.Finish();

    private RecordingController(PixelRect region, int fps, string saveDir)
    {
        _saveDir = saveDir;
        _tempPath = Path.Combine(Path.GetTempPath(),
            $"zhuoying-rec-{DateTime.Now:yyyyMMdd-HHmmss}.gif");
        _recorder = new GifRecorder(region, fps, _tempPath);

        // 区域红框：外扩 2px、全窗点击穿透
        _border = new Window
        {
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            Topmost = true,
            CanResize = false,
            ShowActivated = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Content = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E)),
                BorderThickness = new Thickness(2),
                Background = Brushes.Transparent,
            },
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(region.X - 2, region.Y - 2),
        };
        _border.Show();
        SizeToPhysical(_border, region.Width + 4, region.Height + 4);
        _border.ScalingChanged += (_, _) => SizeToPhysical(_border, region.Width + 4, region.Height + 4);
        ExcludeFromCapture(_border, clickThrough: true);

        // 控制条
        _status = new TextBlock
        {
            Text = "● 00:00",
            Foreground = Brushes.White,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
        };
        var stopButton = BarButton("完成", true, Finish);
        var cancelButton = BarButton("✕", false, Cancel);
        _bar = new Window
        {
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            Topmost = true,
            CanResize = false,
            ShowActivated = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x28, 0x28, 0x28)),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Height = 34,
                Children = { _status, stopButton, cancelButton },
            },
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        _bar.Show();
        PositionBar(region);
        ExcludeFromCapture(_bar, clickThrough: false);

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) =>
        {
            var t = _recorder.Elapsed;
            var mb = _recorder.BytesWritten / 1024.0 / 1024.0;
            _status.Text = $"● {(int)t.TotalMinutes:00}:{t.Seconds:00}  {mb:0.0}MB";
        };
        _uiTimer.Start();
    }

    private static Button BarButton(string text, bool primary, Action click)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = primary
                ? new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xF0))
                : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Foreground = Brushes.White,
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>按物理像素设定窗口尺寸（跨 DPI 屏一致；同 PinWindow 方案）。</summary>
    private static void SizeToPhysical(Window window, int physicalW, int physicalH)
    {
        var s = window.RenderScaling;
        window.Width = physicalW / s;
        window.Height = physicalH / s;
    }

    /// <summary>控制条停靠：区域右下角外侧，越界翻到区域内侧。</summary>
    private void PositionBar(PixelRect region)
    {
        var s = _bar.RenderScaling;
        var barH = (int)Math.Round(34 * s);
        var margin = (int)Math.Round(6 * s);
        var y = region.Bottom + margin;
        // 越出虚拟屏幕底部则贴到区域内右下
        var screenBottom = int.MinValue;
        foreach (var screen in _bar.Screens.All)
            screenBottom = Math.Max(screenBottom, screen.Bounds.Bottom);
        if (y + barH > screenBottom)
            y = region.Bottom - barH - margin;
        // 宽度 SizeToContent 待布局，右对齐用估算宽后到 Opened 再校正
        _bar.Position = new PixelPoint(Math.Max(region.X, region.Right - (int)(220 * s)), y);
        _bar.Opened += (_, _) =>
        {
            var w = (int)Math.Round(_bar.Bounds.Width * _bar.RenderScaling);
            _bar.Position = new PixelPoint(Math.Max(region.X, region.Right - w), y);
        };
    }

    /// <summary>把窗口从任何截屏路径排除（Win10 2004+），可选整窗点击穿透。</summary>
    private static void ExcludeFromCapture(Window window, bool clickThrough)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle == null)
            return;
        Win32.SetWindowDisplayAffinity(handle.Handle, Win32.WDA_EXCLUDEFROMCAPTURE);
        if (!clickThrough)
            return;
        var ex = (long)Win32.GetWindowLongPtrW(handle.Handle, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtrW(handle.Handle, Win32.GWL_EXSTYLE,
            new IntPtr(ex | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED));
    }

    private void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        Close();
        try
        {
            _recorder.Stop();
            Directory.CreateDirectory(_saveDir);
            var path = UniquePath(_saveDir, $"捉影_{DateTime.Now:yyyyMMdd_HHmmss}.gif");
            File.Move(_tempPath, path);
            NotificationToast.Show($"录制完成：{path}");
        }
        catch (Exception ex)
        {
            NotificationToast.Show($"录制保存失败：{ex.Message}");
        }
    }

    private void Cancel()
    {
        if (_finished)
            return;
        _finished = true;
        Close();
        _recorder.Cancel();
        NotificationToast.Show("录制已取消");
    }

    private void Close()
    {
        _uiTimer.Stop();
        _border.Close();
        _bar.Close();
        _active = null;
    }

    private static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path))
            return path;
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            path = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(path))
                return path;
        }
    }
}
