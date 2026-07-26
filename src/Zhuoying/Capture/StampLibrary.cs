using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Platform;

namespace Zhuoying.Capture;

/// <summary>
/// 图章素材库：目录即库（%AppData%\Zhuoying\stamps\，*.svg|*.png|*.jpg）。
/// 首次运行把内置素材（Assets/stamps/*.svg）拷入目录（.initialized 标记，
/// 用户删掉内置素材后不会被重新拷回）。不做库内管理 UI——用户直接管理目录。
/// </summary>
internal static class StampLibrary
{
    private static readonly string[] Extensions = [".svg", ".png", ".jpg", ".jpeg"];

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Zhuoying", "stamps");

    /// <summary>库内全部素材路径（文件名排序，内置素材靠前因名字固定）。</summary>
    public static IReadOnlyList<string> GetStamps()
    {
        EnsureBuiltins();
        try
        {
            return System.IO.Directory.EnumerateFiles(Directory)
                .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>导入素材：复制进库目录（重名自动加序号），返回库内路径。</summary>
    public static string? Import(string sourcePath)
    {
        try
        {
            EnsureBuiltins();
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            var ext = Path.GetExtension(sourcePath);
            var target = Path.Combine(Directory, name + ext);
            for (var i = 2; File.Exists(target); i++)
                target = Path.Combine(Directory, $"{name}-{i}{ext}");
            File.Copy(sourcePath, target);
            return target;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void EnsureBuiltins()
    {
        var marker = Path.Combine(Directory, ".initialized");
        if (File.Exists(marker))
            return;
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var name in new[] { "check", "cross", "star", "warning", "heart", "arrow" })
        {
            var target = Path.Combine(Directory, $"{name}.svg");
            if (File.Exists(target))
                continue;
            using var src = AssetLoader.Open(new Uri($"avares://Zhuoying/Assets/stamps/{name}.svg"));
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }
        File.WriteAllText(marker, "");
    }
}
