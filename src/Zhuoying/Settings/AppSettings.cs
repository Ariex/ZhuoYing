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
