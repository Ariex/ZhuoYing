using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Zhuoying.Platform;

namespace Zhuoying.Capture;

/// <summary>
/// 录屏会话 UI：区域红框（点击穿透）+ 控制条（时长/大小/停止/取消）。
/// 活屏录制（非冻结帧模式），录制期间用户正常操作。
///
/// 红框有两种画法，按平台能力二选一（<see cref="IWindowEffects.SupportsCaptureExclusion"/>）：
/// - **能排除捕获**（Windows：WDA_EXCLUDEFROMCAPTURE）：一个覆盖整个区域的透明窗口，
///   边框画在外扩 2px 处，双保险；
/// - **不能排除捕获**（Linux：X11/Wayland 都没有等价物）：拆成上下左右**四条实心边条**
///   窗口，完全不覆盖录制区。这样既不入镜，也不依赖合成器的窗口透明——
///   无合成器的 X11 下 TransparencyLevelHint 会失效，透明区渲染成白色实心块，
///   整块盖住录制区（Xvfb 实测，见 docs/LINUX-PORT.md）。
/// 控制条同理：不能排除捕获时若停不进区域外，宁可隐藏也不入镜。
/// </summary>
public enum RecordFormat
{
    Gif,
    Mp4,
}

public sealed class RecordingController
{
    private static RecordingController? _active;

    private readonly IScreenRecorder _recorder;
    private readonly string _ext;
    private readonly string _tempPath;
    private readonly string _saveDir;
    /// <summary>红框窗口：能排除捕获时 1 个覆盖式，否则 4 条边条。</summary>
    private readonly Window[] _borders;

    private readonly Window _bar;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _uiTimer;
    private bool _finished;

    /// <summary>开始录制（同一时刻只允许一场；重复调用忽略）。</summary>
    public static void Start(PixelRect region, int fps, string saveDir,
        RecordFormat format = RecordFormat.Gif)
    {
        if (_active != null)
            return;
        _active = new RecordingController(region, fps, saveDir, format);
    }

    /// <summary>是否正在录制（避免录制中再开截屏会话时误操作，目前仅诊断用）。</summary>
    public static bool IsRecording => _active != null;

    /// <summary>结束当前录制（等效点击控制条「完成」；自测钩子用）。</summary>
    internal static void FinishActive() => _active?.Finish();

    private RecordingController(PixelRect region, int fps, string saveDir, RecordFormat format)
    {
        _saveDir = saveDir;
        _ext = format == RecordFormat.Mp4 ? "mp4" : "gif";
        _tempPath = Path.Combine(Path.GetTempPath(),
            $"zhuoying-rec-{DateTime.Now:yyyyMMdd-HHmmss}.{_ext}");
        // MP4 的偶数边长收缩同步应用到红框，框住的即录到的
        if (format == RecordFormat.Mp4)
            region = Mp4Recorder.EvenRegion(region);
        _recorder = format == RecordFormat.Mp4
            ? new Mp4Recorder(region, fps, _tempPath)
            : new GifRecorder(region, fps, _tempPath);

        var effects = PlatformServices.WindowEffects;
        _borders = effects.SupportsCaptureExclusion
            ? [CreateOverlayBorder(region)]
            : CreateEdgeBorders(region);
        foreach (var w in _borders)
            effects.ExcludeFromCapture(w, clickThrough: true);

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
        effects.ExcludeFromCapture(_bar, clickThrough: false);

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

    private static readonly SolidColorBrush BorderRed =
        new(Color.FromRgb(0xE5, 0x3E, 0x3E));

    private const int BorderThicknessPx = 2;

    /// <summary>覆盖式红框（仅用于能排除捕获的平台）：一个透明窗口罩住整个区域，
    /// 边框画在外扩 2px 处。依赖合成器支持窗口透明。</summary>
    private static Window CreateOverlayBorder(PixelRect region)
    {
        var window = new Window
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
                BorderBrush = BorderRed,
                BorderThickness = new Thickness(BorderThicknessPx),
                Background = Brushes.Transparent,
            },
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(region.X - BorderThicknessPx, region.Y - BorderThicknessPx),
        };
        window.Show();
        var w = region.Width + BorderThicknessPx * 2;
        var h = region.Height + BorderThicknessPx * 2;
        SizeToPhysical(window, w, h);
        window.ScalingChanged += (_, _) => SizeToPhysical(window, w, h);
        return window;
    }

    /// <summary>
    /// 四条实心边条（用于不能排除捕获的平台）：贴在录制区外沿，
    /// 一个像素也不覆盖录制区，因此天然不入镜、也不需要窗口透明。
    /// </summary>
    private static Window[] CreateEdgeBorders(PixelRect region)
    {
        const int t = BorderThicknessPx;
        // 上下两条横跨含拐角，左右两条只占区域高度，四条拼成完整回字边
        var rects = new[]
        {
            new PixelRect(region.X - t, region.Y - t, region.Width + t * 2, t), // 上
            new PixelRect(region.X - t, region.Bottom, region.Width + t * 2, t), // 下
            new PixelRect(region.X - t, region.Y, t, region.Height),            // 左
            new PixelRect(region.Right, region.Y, t, region.Height),            // 右
        };
        var windows = new Window[rects.Length];
        for (var i = 0; i < rects.Length; i++)
        {
            var r = rects[i];
            var window = new Window
            {
                SystemDecorations = SystemDecorations.None,
                ShowInTaskbar = false,
                Topmost = true,
                CanResize = false,
                ShowActivated = false,
                Background = BorderRed,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(r.X, r.Y),
            };
            window.Show();
            SizeToPhysical(window, r.Width, r.Height);
            window.ScalingChanged += (_, _) => SizeToPhysical(window, r.Width, r.Height);
            windows[i] = window;
        }
        return windows;
    }

    /// <summary>按物理像素设定窗口尺寸（跨 DPI 屏一致；同 PinWindow 方案）。</summary>
    private static void SizeToPhysical(Window window, int physicalW, int physicalH)
    {
        var s = window.RenderScaling;
        window.Width = physicalW / s;
        window.Height = physicalH / s;
    }

    /// <summary>
    /// 控制条停靠：区域下方外侧优先，放不下再试上方外侧。
    /// 能排除捕获时（Windows）实在没地方就翻进区域内右下角——反正不入镜；
    /// 不能排除捕获时（Linux）宁可隐藏也不进区域，否则会录进画面。
    /// </summary>
    private void PositionBar(PixelRect region)
    {
        var s = _bar.RenderScaling;
        var barH = (int)Math.Round(34 * s);
        var margin = (int)Math.Round(6 * s);
        var y = region.Bottom + margin;
        // 越出虚拟屏幕底部则改停区域上方外侧
        var screenBottom = int.MinValue;
        var screenTop = int.MaxValue;
        foreach (var screen in _bar.Screens.All)
        {
            screenBottom = Math.Max(screenBottom, screen.Bounds.Bottom);
            screenTop = Math.Min(screenTop, screen.Bounds.Y);
        }
        if (y + barH > screenBottom)
        {
            var above = region.Y - barH - margin;
            if (above >= screenTop)
            {
                y = above;
            }
            else if (PlatformServices.WindowEffects.SupportsCaptureExclusion)
            {
                y = region.Bottom - barH - margin; // 进区域内，但不入镜
            }
            else
            {
                // 区域上下都放不下，且控件会入镜：隐藏控制条，
                // 录制仍可从托盘菜单结束（NotificationToast 告知用户）
                _bar.Hide();
                NotificationToast.Show("录制中：控制条会录进画面，已隐藏——请从托盘菜单结束录制");
                return;
            }
        }
        // 宽度 SizeToContent 待布局，右对齐用估算宽后到 Opened 再校正
        _bar.Position = new PixelPoint(Math.Max(region.X, region.Right - (int)(220 * s)), y);
        _bar.Opened += (_, _) =>
        {
            var w = (int)Math.Round(_bar.Bounds.Width * _bar.RenderScaling);
            _bar.Position = new PixelPoint(Math.Max(region.X, region.Right - w), y);
        };
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
            var path = UniquePath(_saveDir, $"捉影_{DateTime.Now:yyyyMMdd_HHmmss}.{_ext}");
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
        foreach (var w in _borders)
            w.Close();
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
