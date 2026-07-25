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

            _captureController = new CaptureController(
                new WindowsScreenCapture(), new WindowsClipboardImage());

            _settingsService = new SettingsService();
            _appSettings = _settingsService.Load();

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
            TryApplyHotkey,
            hotkey =>
            {
                _appSettings.Hotkey = hotkey;
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
