using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Zhuoying.Platform;

namespace Zhuoying.Settings;

/// <summary>设置窗口。当前仅支持修改截屏快捷键。</summary>
public sealed class SettingsWindow : Window
{
    private static readonly IBrush BoxBorderIdle = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));
    private static readonly IBrush BoxBorderFocused = new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xF0));

    private readonly HotkeySetting _original;
    private readonly Func<HotkeySetting, bool> _tryApply;
    private readonly Action<HotkeySetting> _save;

    private readonly Border _hotkeyBox;
    private readonly TextBlock _hotkeyText;
    private readonly TextBlock _errorText;
    private HotkeySetting? _pending;

    public SettingsWindow(HotkeySetting current, Func<HotkeySetting, bool> tryApply, Action<HotkeySetting> save)
    {
        _original = current;
        _tryApply = tryApply;
        _save = save;

        Title = $"捉影 — 设置  v{AppVersion.Display}";
        Width = 380;
        Height = 230;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Zhuoying/Assets/icon.ico")));
        }
        catch (Exception)
        {
            // 图标加载失败不影响功能
        }

        _hotkeyText = new TextBlock
        {
            Text = current.Display,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _hotkeyBox = new Border
        {
            Height = 38,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = BoxBorderIdle,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)),
            Focusable = true,
            Child = _hotkeyText,
        };
        _hotkeyBox.PointerPressed += (_, _) => _hotkeyBox.Focus();
        _hotkeyBox.GotFocus += (_, _) => _hotkeyBox.BorderBrush = BoxBorderFocused;
        _hotkeyBox.LostFocus += (_, _) => _hotkeyBox.BorderBrush = BoxBorderIdle;
        _hotkeyBox.KeyDown += OnHotkeyBoxKeyDown;

        _errorText = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            FontSize = 12,
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap,
        };

        var saveButton = new Button { Content = "保存", Padding = new Thickness(20, 6) };
        saveButton.Click += (_, _) => OnSave();
        var cancelButton = new Button { Content = "取消", Padding = new Thickness(20, 6) };
        cancelButton.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "截屏快捷键", FontSize = 13 },
                _hotkeyBox,
                new TextBlock
                {
                    Text = "点击上方输入框，按下新的组合键（需含修饰键；F1–F12 可单独使用）",
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                },
                _errorText,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { saveButton, cancelButton },
                },
            },
        };

        Opened += (_, _) => _hotkeyBox.Focus();
    }

    private void OnHotkeyBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
            return; // 保留键盘导航

        e.Handled = true;

        if (IsModifierKey(e.Key))
        {
            _hotkeyText.Text = ModifiersDisplay(ToHotkeyModifiers(e.KeyModifiers)) + "...";
            return;
        }

        if (!TryMapKey(e.Key, out var vk, out var name))
            return; // 不支持的键，忽略

        var mods = ToHotkeyModifiers(e.KeyModifiers);
        var isFunctionKey = e.Key is >= Key.F1 and <= Key.F12;
        if (mods == HotkeyModifiers.None && !isFunctionKey)
        {
            _hotkeyText.Text = "需要包含修饰键（Ctrl/Shift/Alt/Win）";
            return;
        }

        _pending = new HotkeySetting { Modifiers = mods, VirtualKey = vk, KeyName = name };
        _hotkeyText.Text = _pending.Display;
        _errorText.IsVisible = false;
    }

    private void OnSave()
    {
        var candidate = _pending ?? _original;
        if (candidate.SameAs(_original))
        {
            Close();
            return;
        }
        if (_tryApply(candidate))
        {
            _save(candidate);
            Close();
            return;
        }
        _errorText.Text = $"热键 {candidate.Display} 注册失败（可能被其他程序占用），已恢复原热键";
        _errorText.IsVisible = true;
        _tryApply(_original);
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static HotkeyModifiers ToHotkeyModifiers(KeyModifiers modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Meta)) result |= HotkeyModifiers.Win;
        return result;
    }

    private static string ModifiersDisplay(HotkeyModifiers mods)
    {
        var s = "";
        if (mods.HasFlag(HotkeyModifiers.Control)) s += "Ctrl+";
        if (mods.HasFlag(HotkeyModifiers.Shift)) s += "Shift+";
        if (mods.HasFlag(HotkeyModifiers.Alt)) s += "Alt+";
        if (mods.HasFlag(HotkeyModifiers.Win)) s += "Win+";
        return s;
    }

    /// <summary>Avalonia Key → Win32 虚拟键码。支持字母、数字、小键盘数字、F1–F12。</summary>
    private static bool TryMapKey(Key key, out uint vk, out string name)
    {
        switch (key)
        {
            case >= Key.A and <= Key.Z:
                vk = (uint)('A' + (key - Key.A));
                name = ((char)vk).ToString();
                return true;
            case >= Key.D0 and <= Key.D9:
                vk = (uint)('0' + (key - Key.D0));
                name = ((char)vk).ToString();
                return true;
            case >= Key.NumPad0 and <= Key.NumPad9:
                vk = (uint)(0x60 + (key - Key.NumPad0));
                name = $"Num{key - Key.NumPad0}";
                return true;
            case >= Key.F1 and <= Key.F12:
                vk = (uint)(0x70 + (key - Key.F1));
                name = $"F{key - Key.F1 + 1}";
                return true;
            default:
                vk = 0;
                name = "";
                return false;
        }
    }
}
