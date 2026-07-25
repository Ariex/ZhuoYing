using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zhuoying.Settings;

/// <summary>
/// settings.json 的源生成序列化上下文（NativeAOT 兼容，编译期生成读写代码；
/// 文件格式与反射序列化完全一致：缩进、枚举字符串、未知字段忽略、缺失字段用默认值）。
/// 新增配置文件类型时在此追加一行 [JsonSerializable]。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>settings.json 读写，目录 %AppData%/Zhuoying/（REQUIREMENTS §9）。</summary>
public sealed class SettingsService
{
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Zhuoying", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var settings = JsonSerializer.Deserialize(
                    File.ReadAllText(_filePath), SettingsJsonContext.Default.AppSettings);
                if (settings is { Hotkey.KeyName.Length: > 0 })
                    return settings;
            }
        }
        catch (Exception)
        {
            // 配置损坏时回落到默认值
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath,
            JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
    }
}
