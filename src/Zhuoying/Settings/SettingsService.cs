using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zhuoying.Settings;

/// <summary>settings.json 读写，目录 %AppData%/Zhuoying/（REQUIREMENTS §9）。</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Zhuoying", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(_filePath), JsonOptions);
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
        File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
