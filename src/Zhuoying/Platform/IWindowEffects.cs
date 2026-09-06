using Avalonia.Controls;

namespace Zhuoying.Platform;

/// <summary>录制期窗口特效：把控制条/红框从截屏路径排除，并可整窗点击穿透。
/// 平台无等价能力时可空实现（录制画面会含控件，不影响主流程）。</summary>
public interface IWindowEffects
{
    /// <summary>把窗口从任何截屏路径排除，可选整窗点击穿透。</summary>
    void ExcludeFromCapture(Window window, bool clickThrough);

    /// <summary>该平台是否真的能把窗口排除出截屏（false 时调用方需自行规避，
    /// 例如录制帧源跳过控件区域或临时隐藏）。</summary>
    bool SupportsCaptureExclusion { get; }
}
