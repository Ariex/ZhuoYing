using System;
using Avalonia.Controls;

namespace Zhuoying.Platform.Windows;

/// <summary>
/// 录制期窗口特效的 Windows 实现：SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)
/// 把窗口从任何截屏路径排除（Win10 2004+），可选 WS_EX_TRANSPARENT 整窗点击穿透。
/// </summary>
public sealed class WindowsWindowEffects : IWindowEffects
{
    public bool SupportsCaptureExclusion => true;

    public void ExcludeFromCapture(Window window, bool clickThrough)
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
}
