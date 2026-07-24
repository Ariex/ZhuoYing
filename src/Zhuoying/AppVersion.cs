using System.Reflection;

namespace Zhuoying;

public static class AppVersion
{
    /// <summary>产品版本字符串（如 "0.1+2026-07-25 14:32"），来自 Directory.Build.props。</summary>
    public static string Display { get; } =
        typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(2)
        ?? "?";
}
