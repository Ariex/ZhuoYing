using System;
using System.IO;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// 开机自启：XDG autostart 规范的 .desktop 文件
/// （对应 Windows 侧 HKCU\...\Run；文件存在即持久态，不进 settings.json 避免双源）。
/// </summary>
public sealed class LinuxStartupManager : IStartupManager
{
    private const string FileName = "zhuoying.desktop";

    private static string AutostartDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart");

    private static string FilePath => Path.Combine(AutostartDir, FileName);

    public bool IsEnabled()
    {
        try
        {
            return File.Exists(FilePath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
                return;
            }
            if (Environment.ProcessPath is not { } exe)
                return;
            Directory.CreateDirectory(AutostartDir);
            // Exec 里的可执行路径按 .desktop 规范转义（空格/引号）
            var escaped = exe.Replace("\\", "\\\\").Replace("\"", "\\\"");
            File.WriteAllText(FilePath,
                $"""
                [Desktop Entry]
                Type=Application
                Name=捉影
                Comment=截屏标注工具
                Exec="{escaped}"
                Terminal=false
                X-GNOME-Autostart-enabled=true

                """);
        }
        catch (Exception)
        {
            // 目录不可写等：静默失败，复选框状态下次打开会如实反映
        }
    }
}
