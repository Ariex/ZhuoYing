using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Zhuoying.Platform;

namespace Zhuoying.Settings;

/// <summary>设置窗口：截屏快捷键 + 标注预设颜色。</summary>
public sealed class SettingsWindow : Window
{
    private static readonly IBrush BoxBorderIdle = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));
    private static readonly IBrush BoxBorderFocused = new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xF0));

    private readonly HotkeySetting _original;
    private readonly Func<HotkeySetting, bool> _tryApply;
    private readonly Action<HotkeySetting, System.Collections.Generic.List<string>, string> _save;

    private readonly Border _hotkeyBox;
    private readonly TextBlock _hotkeyText;
    private readonly TextBlock _errorText;
    private readonly TextBox _savePathBox;
    private readonly System.Collections.Generic.List<(TextBox Box, Border Preview)> _colorEditors = [];
    private HotkeySetting? _pending;

    public SettingsWindow(
        HotkeySetting current,
        System.Collections.Generic.IReadOnlyList<string> annotationColors,
        string savePath,
        Func<HotkeySetting, bool> tryApply,
        Action<HotkeySetting, System.Collections.Generic.List<string>, string> save)
    {
        _original = current;
        _tryApply = tryApply;
        _save = save;

        Title = $"捉影 — 设置  v{AppVersion.Display}";
        Width = 400;
        Height = 560;
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

        // 标注颜色编辑区：10 个 #RRGGBB 输入框 + 实时预览（留空表示不使用该槽位）
        var colorGrid = new WrapPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < AppSettings.MaxAnnotationColors; i++)
        {
            var box = new TextBox
            {
                Width = 84,
                Height = 30,
                FontSize = 12,
                Watermark = "#RRGGBB",
                Text = i < annotationColors.Count ? annotationColors[i] : "",
            };
            var preview = new Border
            {
                Width = 18, Height = 18,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = BoxBorderIdle,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 12, 0),
            };
            var b = box;
            var p = preview;
            box.TextChanged += (_, _) => UpdatePreview(b, p);
            UpdatePreview(box, preview);
            _colorEditors.Add((box, preview));
            // 取色器：选色写回十六进制框（预览随 TextChanged 联动）
            var picker = new Zhuoying.Capture.ColorPickButton(
                () => Color.TryParse(b.Text?.Trim() ?? "", out var c) ? c : Colors.White,
                c => b.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}",
                buttonSize: 24)
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            colorGrid.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 6),
                Children = { box, preview, picker },
            });
        }

        // 保存目录：文本框 + 浏览按钮（留空 = 默认"图片\捉影"）
        _savePathBox = new TextBox
        {
            Watermark = "留空 = 图片\\捉影",
            Text = savePath,
            FontSize = 12,
        };
        var browseButton = new Button { Content = "浏览…", Padding = new Thickness(10, 4) };
        browseButton.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "选择保存目录",
                    AllowMultiple = false,
                });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } local)
                _savePathBox.Text = local;
        };
        var savePathRow = new DockPanel { Children = { browseButton, _savePathBox } };
        DockPanel.SetDock(browseButton, Dock.Right);
        browseButton.Margin = new Thickness(6, 0, 0, 0);

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
                new TextBlock { Text = "保存目录（Ctrl+S）", FontSize = 13, Margin = new Thickness(0, 8, 0, 0) },
                savePathRow,
                new TextBlock { Text = "标注预设颜色（最多 10 个，留空跳过）", FontSize = 13, Margin = new Thickness(0, 8, 0, 0) },
                colorGrid,
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

    private static void UpdatePreview(TextBox box, Border preview)
    {
        preview.Background = Color.TryParse(box.Text?.Trim() ?? "", out var color)
            ? new SolidColorBrush(color)
            : Brushes.Transparent;
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
        var colors = new System.Collections.Generic.List<string>();
        foreach (var (box, _) in _colorEditors)
        {
            var text = box.Text?.Trim() ?? "";
            if (text.Length > 0 && Color.TryParse(text, out _))
                colors.Add(text);
        }
        if (colors.Count == 0)
            colors = AppSettings.DefaultAnnotationColors();

        var candidate = _pending ?? _original;
        if (!candidate.SameAs(_original) && !_tryApply(candidate))
        {
            _errorText.Text = $"热键 {candidate.Display} 注册失败（可能被其他程序占用），已恢复原热键";
            _errorText.IsVisible = true;
            _tryApply(_original);
            return;
        }
        _save(candidate, colors, _savePathBox.Text?.Trim() ?? "");
        Close();
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
