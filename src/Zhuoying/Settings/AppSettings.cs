using System.Text;
using System.Text.Json.Serialization;
using Zhuoying.Platform;

namespace Zhuoying.Settings;

public sealed class AppSettings
{
    public HotkeySetting Hotkey { get; set; } = HotkeySetting.Default;
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
