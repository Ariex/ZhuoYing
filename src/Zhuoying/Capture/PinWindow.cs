using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Zhuoying.Platform;

namespace Zhuoying.Capture;

/// <summary>
/// 钉屏贴图窗口：把截图（含标注合成）以物理像素 1:1 钉在屏幕上的置顶小窗，
/// 初始位置 = 选区原位。按住拖动移动、滚轮缩放（10% 步进）、Ctrl+滚轮调透明度、
/// 双击/中键/Esc 关闭、右键菜单（复制/另存为/关闭/关闭全部）。
/// 独立于截屏会话，应用退出时随窗口体系销毁；数量无人为上限（内存约束）。
/// </summary>
public sealed class PinWindow : Window
{
    private static readonly List<PinWindow> All = [];

    private readonly WriteableBitmap _bitmap; // 96 DPI（显示用，§2）
    private readonly double _sourceDpi;       // 来源屏幕 DPI（复制/保存时标记）
    private readonly IClipboardImage _clipboard;
    private readonly PixelPoint _physicalOrigin;
    private readonly TextBlock _zoomHint;
    private readonly Border _hintBox;
    private readonly Image _image;
    private double _zoom = 1.0;
    private DispatcherTimer? _hintTimer;

    public PinWindow(
        WriteableBitmap bitmap, PixelPoint physicalTopLeft, double sourceDpi,
        IClipboardImage clipboard)
    {
        _bitmap = bitmap;
        _sourceDpi = sourceDpi;
        _clipboard = clipboard;
        _physicalOrigin = physicalTopLeft;

        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = physicalTopLeft;
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.SizeAll);

        _image = new Image { Source = bitmap, Stretch = Stretch.Fill };
        // 1:1 显示用最近邻（像素级无损）；缩放后切高质量插值
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.None);
        _zoomHint = new TextBlock
        {
            FontSize = 13,
            Foreground = Brushes.White,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        _hintBox = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsVisible = false,
            Child = _zoomHint,
        };
        // 边框做成叠加层而非容器：不挤占布局，位图与屏幕保持像素级 1:1
        Content = new Panel
        {
            Children =
            {
                _image,
                new Border
                {
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0x2D, 0x8C, 0xF0)),
                    IsHitTestVisible = false,
                },
                _hintBox,
            },
        };

        Opened += (_, _) => ApplySize();
        ScalingChanged += (_, _) => ApplySize(); // 跨屏拖动保持物理像素尺寸
        Closed += (_, _) =>
        {
            All.Remove(this);
            _bitmap.Dispose();
        };
        All.Add(this);

        PointerPressed += OnPointerPressedHandler;
        DoubleTapped += (_, _) => Close();
        PointerWheelChanged += OnWheel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        BuildContextMenu();
    }

    /// <summary>关闭全部贴图。</summary>
    public static void CloseAll()
    {
        foreach (var pin in All.ToArray())
            pin.Close();
    }

    /// <summary>按当前屏缩放与缩放倍率把窗口尺寸对齐到物理像素。</summary>
    private void ApplySize()
    {
        var s = RenderScaling;
        Width = Math.Max(8, _bitmap.PixelSize.Width * _zoom / s);
        Height = Math.Max(8, _bitmap.PixelSize.Height * _zoom / s);
    }

    private void OnPointerPressedHandler(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed)
        {
            Close();
            e.Handled = true;
        }
        else if (props.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl+滚轮：透明度 15%–100%
            Opacity = Math.Clamp(Opacity + (e.Delta.Y > 0 ? 0.1 : -0.1), 0.15, 1.0);
            ShowHint($"透明度 {Opacity * 100:0}%");
        }
        else
        {
            // 滚轮：缩放 10% 步进；回到 100% 附近时吸附到精确 1:1
            _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1), 0.1, 8.0);
            if (Math.Abs(_zoom - 1) < 0.05)
                _zoom = 1;
            RenderOptions.SetBitmapInterpolationMode(_image,
                _zoom == 1 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
            ApplySize();
            ShowHint($"{_zoom * 100:0}%");
        }
        e.Handled = true;
    }

    private void ShowHint(string text)
    {
        _zoomHint.Text = text;
        _hintBox.IsVisible = true;
        _hintTimer?.Stop();
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintBox.IsVisible = false;
            _hintTimer?.Stop();
        };
        _hintTimer.Start();
    }

    private unsafe WriteableBitmap CloneWithSourceDpi()
    {
        var clone = new WriteableBitmap(
            _bitmap.PixelSize, new Vector(_sourceDpi, _sourceDpi),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using var src = _bitmap.Lock();
        using var dst = clone.Lock();
        for (var y = 0; y < _bitmap.PixelSize.Height; y++)
        {
            Buffer.MemoryCopy(
                (byte*)src.Address + (long)y * src.RowBytes,
                (byte*)dst.Address + (long)y * dst.RowBytes,
                dst.RowBytes, _bitmap.PixelSize.Width * 4);
        }
        return clone;
    }

    private void BuildContextMenu()
    {
        var copy = new MenuItem { Header = "复制到剪贴板" };
        copy.Click += (_, _) =>
        {
            // 剪贴板需要带来源 DPI 的位图（显示位图固定 96）——拷一份换标记
            using var clone = CloneWithSourceDpi();
            _clipboard.SetImage(clone);
        };
        var saveAs = new MenuItem { Header = "另存为…" };
        saveAs.Click += async (_, _) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "另存为",
                SuggestedFileName = $"捉影_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExtension = "png",
                FileTypeChoices = [new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }],
            });
            if (file?.TryGetLocalPath() is not { } path)
                return;
            try
            {
                using var ms = new System.IO.MemoryStream();
                _bitmap.Save(ms);
                System.IO.File.WriteAllBytes(path,
                    Zhuoying.Platform.PngDpiWriter.WithDpi(
                        ms.ToArray(), _sourceDpi, _sourceDpi));
                NotificationToast.Show($"已保存：{path}");
            }
            catch (Exception)
            {
                NotificationToast.Show("另存为失败：目标位置无法写入");
            }
        };
        var close = new MenuItem { Header = "关闭" };
        close.Click += (_, _) => Close();
        var closeAll = new MenuItem { Header = "关闭全部贴图" };
        closeAll.Click += (_, _) => CloseAll();

        ContextMenu = new ContextMenu
        {
            Items = { copy, saveAs, new Separator(), close, closeAll },
        };
    }
}
