using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Zhuoying.Capture;
using Zhuoying.Platform;
using Zhuoying.Platform.Windows;
using Zhuoying.Settings;

namespace Zhuoying;

public partial class App : Application
{
    private CaptureController? _captureController;
    private IHotkeyService? _hotkey;
    private SettingsService? _settingsService;
    private AppSettings _appSettings = new();
    private SettingsWindow? _settingsWindow;

    public bool HotkeyRegistered { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 无主窗口，驻留托盘（REQUIREMENTS §3）
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _settingsService = new SettingsService();
            _appSettings = _settingsService.Load();

            _captureController = new CaptureController(
                new WindowsScreenCapture(), new WindowsClipboardImage(),
                () => new Zhuoying.Capture.EditorOptions(
                    GetAnnotationColors(),
                    Math.Max(1, _appSettings.FontSizeMin),
                    Math.Max(_appSettings.FontSizeMin, _appSettings.FontSizeMax)));

            _hotkey = new WindowsHotkeyService();
            TryApplyHotkey(_appSettings.Hotkey);

            desktop.Exit += (_, _) => _hotkey?.Dispose();

            // 开发自测：--test-capture 自动触发一次抓屏；--test-settings 打开设置窗口；
            // --test-copy x,y,w,h 在自动抓屏后直接按虚拟屏幕物理像素设选区并复制（免键鼠注入）
            var args = desktop.Args ?? [];
            if (Array.IndexOf(args, "--test-capture") >= 0)
                DispatcherTimer.RunOnce(() => _captureController!.StartCapture(),
                    TimeSpan.FromMilliseconds(1500));
            SetupTestRectHook(args, "--test-copy", r => _captureController!.TestCopy(r));
            SetupTestRectHook(args, "--test-select", r => _captureController!.TestSelect(r));
            SetupTestShapeHook(args);
            SetupTestLineHook(args);
            SetupTestTextHook(args);
            SetupTestNumberHook(args);
            SetupTestMosaicHook(args);
            if (Array.IndexOf(args, "--test-settings") >= 0)
                DispatcherTimer.RunOnce(OpenSettings, TimeSpan.FromMilliseconds(500));
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>解析形如 `--test-copy x,y,w,h` 的自测参数：1.5s 后自动抓屏，3s 后对选区执行动作。</summary>
    private void SetupTestRectHook(string[] args, string name, Action<Avalonia.PixelRect> action)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length)
            return;
        var parts = args[index + 1].Split(',');
        var rect = new Avalonia.PixelRect(
            int.Parse(parts[0]), int.Parse(parts[1]),
            int.Parse(parts[2]), int.Parse(parts[3]));
        DispatcherTimer.RunOnce(() => _captureController!.StartCapture(),
            TimeSpan.FromMilliseconds(1500));
        DispatcherTimer.RunOnce(() => action(rect), TimeSpan.FromMilliseconds(3000));
    }

    /// <summary>解析标注预设颜色（settings.json，非法项跳过，空则回落默认）。</summary>
    private System.Collections.Generic.IReadOnlyList<Avalonia.Media.Color> GetAnnotationColors()
    {
        var result = new System.Collections.Generic.List<Avalonia.Media.Color>();
        foreach (var hex in _appSettings.AnnotationColors)
        {
            if (result.Count >= AppSettings.MaxAnnotationColors)
                break;
            if (Avalonia.Media.Color.TryParse(hex, out var color))
                result.Add(color);
        }
        if (result.Count == 0)
            foreach (var hex in AppSettings.DefaultAnnotationColors())
                result.Add(Avalonia.Media.Color.Parse(hex));
        return result;
    }

    /// <summary>解析 `--test-shape x,y,w,h[,radius%[,filled(0/1)[,thickness[,rotation°]]]]`：2.2s 时添加形状标注。</summary>
    private void SetupTestShapeHook(string[] args)
    {
        var index = Array.IndexOf(args, "--test-shape");
        if (index < 0 || index + 1 >= args.Length)
            return;
        var p = args[index + 1].Split(',');
        var rect = new Avalonia.PixelRect(
            int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
        var radius = p.Length > 4 ? double.Parse(p[4]) : 0;
        var filled = p.Length > 5 && p[5] == "1";
        var thickness = p.Length > 6 ? double.Parse(p[6]) : 10;
        var rotation = p.Length > 7 ? double.Parse(p[7]) : 0;
        var lineStyle = p.Length > 8 ? int.Parse(p[8]) : 0;
        var opacity = p.Length > 9 ? double.Parse(p[9]) : 100;
        DispatcherTimer.RunOnce(
            () => _captureController!.TestAddShape(
                rect, radius, filled, thickness, rotation, lineStyle, opacity),
            TimeSpan.FromMilliseconds(2200));
    }

    /// <summary>
    /// 解析 `--test-line "x:y;x:y;...[,起端,末端,起粗,末粗,样条0/1,线形,透明度]"`：
    /// 2.2s 时添加线/箭头标注（端头为 LineCapKind 枚举序号）。
    /// </summary>
    private void SetupTestLineHook(string[] args)
    {
        var index = Array.IndexOf(args, "--test-line");
        if (index < 0 || index + 1 >= args.Length)
            return;
        var p = args[index + 1].Split(',');
        var points = new System.Collections.Generic.List<Avalonia.PixelPoint>();
        foreach (var pair in p[0].Split(';'))
        {
            var xy = pair.Split(':');
            points.Add(new Avalonia.PixelPoint(int.Parse(xy[0]), int.Parse(xy[1])));
        }
        var startCap = (Zhuoying.Annotations.LineCapKind)(p.Length > 1 ? int.Parse(p[1]) : 0);
        var endCap = (Zhuoying.Annotations.LineCapKind)(p.Length > 2 ? int.Parse(p[2]) : 0);
        var startT = p.Length > 3 ? double.Parse(p[3]) : 5;
        var endT = p.Length > 4 ? double.Parse(p[4]) : 5;
        var spline = p.Length > 5 && p[5] == "1";
        var lineStyle = p.Length > 6 ? int.Parse(p[6]) : 0;
        var opacity = p.Length > 7 ? double.Parse(p[7]) : 100;
        DispatcherTimer.RunOnce(
            () => _captureController!.TestAddLine(
                points, startCap, endCap, startT, endT, spline, lineStyle, opacity),
            TimeSpan.FromMilliseconds(2200));
    }

    /// <summary>解析 `--test-text "x:y:w:h:字号:旋转:外框0/1:描边粗|文本"`：2.2s 时添加文字标注。</summary>
    private void SetupTestTextHook(string[] args)
    {
        var index = Array.IndexOf(args, "--test-text");
        if (index < 0 || index + 1 >= args.Length)
            return;
        var raw = args[index + 1];
        var sep = raw.IndexOf('|');
        // 命令行参数在空格处会被切开，文本里用 %20 表示空格、%0A 表示换行
        var text = (sep >= 0 ? raw[(sep + 1)..] : "测试文字")
            .Replace("%20", " ").Replace("%0A", "\n");
        var p = (sep >= 0 ? raw[..sep] : raw).Split(':');
        var rect = new Avalonia.PixelRect(
            int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
        var size = p.Length > 4 ? double.Parse(p[4]) : 28;
        var rotation = p.Length > 5 ? double.Parse(p[5]) : 0;
        var boxed = p.Length > 6 && p[6] == "1";
        var stroke = p.Length > 7 ? double.Parse(p[7]) : 0;
        var opacity = p.Length > 8 ? double.Parse(p[8]) : 100;
        DispatcherTimer.RunOnce(
            () => _captureController!.TestAddText(rect, text, size, rotation, boxed, stroke, opacity),
            TimeSpan.FromMilliseconds(2200));
    }

    /// <summary>
    /// 解析 `--test-number "x:y[:值[:类型[:直径[:空心0/1[:RRGGBB]]]]][;下一个…]"`：
    /// 2.2s 时添加编号徽章（类型为 NumberKind 枚举序号，分号分隔可加多个）。
    /// </summary>
    private void SetupTestNumberHook(string[] args)
    {
        var index = Array.IndexOf(args, "--test-number");
        if (index < 0 || index + 1 >= args.Length)
            return;
        var groups = args[index + 1].Split(';');
        DispatcherTimer.RunOnce(() =>
        {
            foreach (var group in groups)
            {
                var p = group.Split(':');
                var center = new Avalonia.PixelPoint(int.Parse(p[0]), int.Parse(p[1]));
                var value = p.Length > 2 ? int.Parse(p[2]) : 1;
                var kind = (Zhuoying.Annotations.NumberKind)(p.Length > 3 ? int.Parse(p[3]) : 0);
                var diameter = p.Length > 4 ? double.Parse(p[4]) : 48;
                var hollow = p.Length > 5 && p[5] == "1";
                Avalonia.Media.Color? color =
                    p.Length > 6 && Avalonia.Media.Color.TryParse("#" + p[6], out var c) ? c : null;
                _captureController!.TestAddNumber(center, value, kind, diameter, hollow, color);
            }
        }, TimeSpan.FromMilliseconds(2200));
    }

    /// <summary>
    /// 解析 `--test-mosaic "x,y,w,h[,模糊0/1[,强度[,旋转°]]][;下一个…]"`：
    /// 2.2s 时添加区域模糊（强度 = 像素大小或模糊半径，默认 10）。
    /// </summary>
    private void SetupTestMosaicHook(string[] args)
    {
        var index = Array.IndexOf(args, "--test-mosaic");
        if (index < 0 || index + 1 >= args.Length)
            return;
        var groups = args[index + 1].Split(';');
        DispatcherTimer.RunOnce(() =>
        {
            foreach (var group in groups)
            {
                var p = group.Split(',');
                var rect = new Avalonia.PixelRect(
                    int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                var blur = p.Length > 4 && p[4] == "1";
                var amount = p.Length > 5 ? double.Parse(p[5]) : 10;
                var rotation = p.Length > 6 ? double.Parse(p[6]) : 0;
                _captureController!.TestAddMosaic(rect, blur, amount, rotation);
            }
        }, TimeSpan.FromMilliseconds(2200));
    }

    /// <summary>注册（或改绑）截屏热键并同步托盘提示文案。</summary>
    private bool TryApplyHotkey(HotkeySetting hotkey)
    {
        if (_hotkey == null)
            return false;
        HotkeyRegistered = _hotkey.TryRegister(hotkey.Modifiers, hotkey.VirtualKey,
            () => Dispatcher.UIThread.Post(() => _captureController!.StartCapture()));

        var icons = TrayIcon.GetIcons(this);
        if (icons is { Count: > 0 })
            icons[0].ToolTipText = HotkeyRegistered
                ? $"捉影 v{AppVersion.Display} — {hotkey.Display} 截屏"
                : $"捉影 v{AppVersion.Display} — 热键 {hotkey.Display} 被占用，请在设置中改键";
        return HotkeyRegistered;
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(
            _appSettings.Hotkey,
            _appSettings.AnnotationColors,
            TryApplyHotkey,
            (hotkey, colors) =>
            {
                _appSettings.Hotkey = hotkey;
                _appSettings.AnnotationColors = colors;
                _settingsService!.Save(_appSettings);
            });
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OnTrayCapture(object? sender, EventArgs e)
    {
        // 稍等托盘菜单收起，避免菜单被截进冻结帧
        DispatcherTimer.RunOnce(() => _captureController?.StartCapture(),
            TimeSpan.FromMilliseconds(250));
    }

    private void OnTraySettings(object? sender, EventArgs e) => OpenSettings();

    private void OnTrayExit(object? sender, EventArgs e) =>
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
}
