using System;
using Avalonia;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// Linux 侧的实现选择：同一套 Linux 构建要同时伺候 X11 会话与 Wayland 会话，
/// 二者的抓屏、帧源、热键是**完全不同的两条路**，在此按会话类型分流。
///
/// （Windows 侧不需要这一层：那边一个平台只有一套实现。）
/// </summary>
internal static class LinuxServices
{
    private static readonly Lazy<IScreenCapture> LazyCapture = new(() =>
        X11Interop.IsWayland ? new WaylandScreenCapture() : new X11ScreenCapture());

    /// <summary>抓屏实现（Wayland 下同时持有共享的 portal 会话）。</summary>
    internal static IScreenCapture ScreenCapture => LazyCapture.Value;

    /// <summary>录屏帧源。Wayland 下复用截屏那条 portal 会话，不再弹第二次授权框。</summary>
    internal static IFrameSource CreateFrameSource(PixelRect region) =>
        ScreenCapture is WaylandScreenCapture wayland
            ? new WaylandFrameSource(wayland.Portal, region)
            : new X11FrameSource(region);

    /// <summary>全局热键。Wayland 没有 XGrabKey，只能走 portal GlobalShortcuts。</summary>
    internal static IHotkeyService CreateHotkeyService() =>
        X11Interop.IsWayland ? new PortalHotkeyService() : new X11HotkeyService();
}
