namespace Zhuoying.Platform;

/// <summary>开机自启开关。持久态由平台机制自身承载（注册表 / autostart 文件），
/// 不进 settings.json 避免双源。</summary>
public interface IStartupManager
{
    bool IsEnabled();

    /// <summary>设置失败（策略限制、目录不可写等）时静默忽略，
    /// 复选框状态下次打开会如实反映。</summary>
    void SetEnabled(bool enabled);
}
