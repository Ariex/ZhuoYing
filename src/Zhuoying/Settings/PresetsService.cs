using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Zhuoying.Annotations;
using Zhuoying.Capture;

namespace Zhuoying.Settings;

/// <summary>
/// presets.json 的数据形状：各工具"上次使用的样式"（对应 StyleMemory）。
/// 注意：样式 record 的 init 属性经源生成反序列化时，缺失字段会得到
/// default(T) 而非属性初始化器值——本文件始终由程序全字段写出，
/// 手工编辑时请保留全部字段（缺 Opacity 会变 0 = 元素隐形）。
/// </summary>
public sealed class StylePresets
{
    public ShapeStyle? Shape { get; set; }
    public LineStyle? Arrow { get; set; }
    public LineStyle? Polyline { get; set; }
    public TextStyle? Text { get; set; }
    public NumberStyle? Number { get; set; }
    public MosaicStyle? Mosaic { get; set; }
    public PenStyle? Pen { get; set; }
    public StampStyle? Stamp { get; set; }
    public string? StampPath { get; set; }
    public double? Eraser { get; set; }
}

/// <summary>Avalonia Color ↔ "#AARRGGBB" 字符串（Color 属性只读，需自定义转换）。</summary>
public sealed class ColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Color.TryParse(reader.GetString() ?? "", out var color) ? color : Colors.Red;

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}");
}

/// <summary>presets.json 源生成上下文（NativeAOT 兼容；Color 走自定义转换器）。</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true, UseStringEnumConverter = true,
    Converters = [typeof(ColorJsonConverter)])]
[JsonSerializable(typeof(StylePresets))]
internal sealed partial class PresetsJsonContext : JsonSerializerContext;

/// <summary>
/// 样式记忆持久化：启动时读 presets.json 填充 <see cref="StyleMemory"/>，
/// 每次截屏会话结束（及退出）时写回——调过的颜色/粗细/字号等重启后保持。
/// </summary>
public static class PresetsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Zhuoying", "presets.json");

    public static void LoadIntoMemory()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            var presets = JsonSerializer.Deserialize(
                File.ReadAllText(FilePath), PresetsJsonContext.Default.StylePresets);
            if (presets == null)
                return;
            StyleMemory.Shape = presets.Shape;
            StyleMemory.Arrow = presets.Arrow;
            StyleMemory.Polyline = presets.Polyline;
            StyleMemory.Text = presets.Text;
            StyleMemory.Number = presets.Number;
            StyleMemory.Mosaic = presets.Mosaic;
            StyleMemory.Pen = presets.Pen;
            StyleMemory.Stamp = presets.Stamp;
            StyleMemory.StampPath = presets.StampPath;
            StyleMemory.Eraser = presets.Eraser;
        }
        catch (Exception)
        {
            // 预设损坏时忽略（回落默认样式）
        }
    }

    public static void SaveFromMemory()
    {
        try
        {
            var presets = new StylePresets
            {
                Shape = StyleMemory.Shape,
                Arrow = StyleMemory.Arrow,
                Polyline = StyleMemory.Polyline,
                Text = StyleMemory.Text,
                Number = StyleMemory.Number,
                Mosaic = StyleMemory.Mosaic,
                Pen = StyleMemory.Pen,
                Stamp = StyleMemory.Stamp,
                StampPath = StyleMemory.StampPath,
                Eraser = StyleMemory.Eraser,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(presets, PresetsJsonContext.Default.StylePresets));
        }
        catch (Exception)
        {
            // 写盘失败不影响使用
        }
    }
}
