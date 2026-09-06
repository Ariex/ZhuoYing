using System;
using Avalonia;
#if ZY_WINDOWS
using Zhuoying.Platform.Windows;
#else
using Zhuoying.Platform.Linux;
#endif

namespace Zhuoying.Platform;

/// <summary>
/// 平台实现的唯一取用口。上层（Capture/Agent/Settings/App）一律经此拿服务，
/// 不直接引用 <c>Platform.Windows</c> / <c>Platform.Linux</c> 下的具体类型——
/// 这是 Linux 移植的接口收敛边界（见 DEVPLAN 阶段十八）。
/// 具体实现由编译期符号 ZY_WINDOWS / ZY_LINUX 选定，两平台的文件在 csproj 中互斥编译。
/// </summary>
public static class PlatformServices
{
    private static readonly Lazy<IScreenCapture> LazyScreenCapture = new(() =>
#if ZY_WINDOWS
        new WindowsScreenCapture()
#else
        LinuxServices.ScreenCapture
#endif
    );

    private static readonly Lazy<IClipboardImage> LazyClipboard = new(() =>
#if ZY_WINDOWS
        new WindowsClipboardImage()
#else
        new LinuxClipboardImage()
#endif
    );

    private static readonly Lazy<IStartupManager> LazyStartup = new(() =>
#if ZY_WINDOWS
        new WindowsStartupManager()
#else
        new LinuxStartupManager()
#endif
    );

    private static readonly Lazy<IWindowEffects> LazyWindowEffects = new(() =>
#if ZY_WINDOWS
        new WindowsWindowEffects()
#else
        new X11WindowEffects()
#endif
    );

    /// <summary>抓屏与显示器信息（无状态单例）。</summary>
    public static IScreenCapture ScreenCapture => LazyScreenCapture.Value;

    /// <summary>剪贴板图片写入。</summary>
    public static IClipboardImage ClipboardImage => LazyClipboard.Value;

    /// <summary>开机自启。</summary>
    public static IStartupManager Startup => LazyStartup.Value;

    /// <summary>录制期窗口特效。</summary>
    public static IWindowEffects WindowEffects => LazyWindowEffects.Value;

    /// <summary>全局热键服务（持有专用线程/资源，由调用方 Dispose）。</summary>
    public static IHotkeyService CreateHotkeyService() =>
#if ZY_WINDOWS
        new WindowsHotkeyService();
#else
        LinuxServices.CreateHotkeyService();
#endif

    /// <summary>录屏帧源（固定区域重复采样）。</summary>
    public static IFrameSource CreateFrameSource(PixelRect region) =>
#if ZY_WINDOWS
        new WindowsFrameSource(region);
#else
        LinuxServices.CreateFrameSource(region);
#endif

    /// <summary>MP4 视频编码器。</summary>
    public static IVideoEncoder CreateVideoEncoder(PixelRect region, int fps, string path) =>
#if ZY_WINDOWS
        new MediaFoundationVideoEncoder(region, fps, path);
#else
        new FfmpegVideoEncoder(region, fps, path);
#endif

    /// <summary>视频编码器的分辨率上限。</summary>
    public static VideoEncoderLimits VideoLimits =>
#if ZY_WINDOWS
        MediaFoundationVideoEncoder.Limits;
#else
        FfmpegVideoEncoder.Limits;
#endif

    /// <summary>解码视频前若干帧、报告尺寸/帧数与指定像素的颜色序列
    ///（--test-mp4 自测用；两平台报告格式一致）。</summary>
    public static string ProbeVideo(string path, int x, int y) =>
#if ZY_WINDOWS
        MediaFoundationVideoProbe.Probe(path, x, y);
#else
        FfmpegVideoProbe.Probe(path, x, y);
#endif

    /// <summary>平台名（诊断输出用）。</summary>
    public static string PlatformName =>
#if ZY_WINDOWS
        "windows";
#else
        X11Interop.SessionKind;
#endif
}
