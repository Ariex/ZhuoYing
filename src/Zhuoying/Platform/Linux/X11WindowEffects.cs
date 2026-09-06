using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// 录制期窗口特效的 X11 实现。
///
/// 与 Windows 的关键差异：X11 **没有** WDA_EXCLUDEFROMCAPTURE 的等价物——
/// 任何窗口对 XGetImage 都可见，录制红框/控制条无法从帧里排除。
/// <see cref="SupportsCaptureExclusion"/> 因此为 false，
/// 由 <c>RecordingController</c> 改走"控件避开录制区"的规避策略。
///
/// 点击穿透用 XShape 的 **input shape**（置空输入区）实现，
/// 语义等价于 WS_EX_TRANSPARENT，且不影响窗口可见性。
/// </summary>
public sealed class X11WindowEffects : IWindowEffects
{
    public bool SupportsCaptureExclusion => false;

    public void ExcludeFromCapture(Window window, bool clickThrough)
    {
        if (!clickThrough)
            return; // 排除截屏在 X11 上无解，只能忽略

        var handle = window.TryGetPlatformHandle();
        if (handle == null || handle.Handle == IntPtr.Zero)
            return;

        X11Display.With<object?>(d =>
        {
            if (!X11Interop.XShapeQueryExtension(d, out _, out _))
                return null;
            // 空矩形列表 = 输入区为空 = 所有指针事件穿透到下层窗口
            X11Interop.XShapeCombineRectangles(d, handle.Handle,
                X11Interop.ShapeInput, 0, 0, IntPtr.Zero, 0, X11Interop.ShapeSet, 0);
            X11Interop.XFlush(d);
            return null;
        });
    }
}
