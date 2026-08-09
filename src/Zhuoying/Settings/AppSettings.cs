using System.Text;
using System.Text.Json.Serialization;
using Zhuoying.Platform;

namespace Zhuoying.Settings;

public sealed class AppSettings
{
    public const int MaxAnnotationColors = 10;

    public HotkeySetting Hotkey { get; set; } = HotkeySetting.Default;

    /// <summary>标注预设颜色（#RRGGBB，最多 10 个）。默认：红黄绿蓝黑白。</summary>
    public System.Collections.Generic.List<string> AnnotationColors { get; set; } = DefaultAnnotationColors();

    /// <summary>文字工具字号范围（物理像素，直接改本文件生效）。</summary>
    public double FontSizeMin { get; set; } = 9;

    public double FontSizeMax { get; set; } = 100;

    /// <summary>保存（Ctrl+S）目标目录；空 = 默认"图片\捉影"。</summary>
    public string SavePath { get; set; } = "";

    /// <summary>允许 Agent API（--api-* 无头命令行，供 AI Agent / 脚本获取
    /// 屏幕信息）。涉及屏幕内容外读，默认关闭，需在设置中显式开启。</summary>
    public bool AgentApiEnabled { get; set; }

    /// <summary>启用 MCP 服务：托盘实例内置本机 HTTP 服务器
    ///（http://127.0.0.1:McpPort/mcp，Streamable HTTP 传输），开关即时生效
    /// 无需重启。默认关闭。</summary>
    public bool McpEnabled { get; set; }

    /// <summary>MCP 服务端口（仅绑定 127.0.0.1）。</summary>
    public int McpPort { get; set; } = 8990;

    /// <summary>解析实际保存目录（空值回落默认）。</summary>
    public string ResolveSavePath() => string.IsNullOrWhiteSpace(SavePath)
        ? System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), "捉影")
        : SavePath;

    public static System.Collections.Generic.List<string> DefaultAnnotationColors() =>
        ["#FF3B30", "#FFCC00", "#34C759", "#0A84FF", "#000000", "#FFFFFF"];
}

/// <summary>截屏热键：Win32 修饰键 + 虚拟键码 + 显示名。</summary>
public sealed class HotkeySetting
{
    public HotkeyModifiers Modifiers { get; set; }
    public uint VirtualKey { get; set; }
    public string KeyName { get; set; } = "";

    public static HotkeySetting Default => new()
    {
        Modifiers = HotkeyModifiers.Control,
        VirtualKey = 0x31, // '1'
        KeyName = "1",
    };

    [JsonIgnore]
    public string Display
    {
        get
        {
            var sb = new StringBuilder();
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) sb.Append("Ctrl+");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) sb.Append("Shift+");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) sb.Append("Alt+");
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) sb.Append("Win+");
            sb.Append(KeyName);
            return sb.ToString();
        }
    }

    public bool SameAs(HotkeySetting other) =>
        Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;
}
