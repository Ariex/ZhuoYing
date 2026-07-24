using System;

namespace Zhuoying.Platform;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>
/// 全局热键。回调可能在非 UI 线程触发，调用方自行调度到 UI 线程。
/// </summary>
public interface IHotkeyService : IDisposable
{
    /// <returns>注册失败（如热键冲突）返回 false。</returns>
    bool TryRegister(HotkeyModifiers modifiers, uint virtualKey, Action callback);
}
