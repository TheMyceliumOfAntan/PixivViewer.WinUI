using System;
using System.IO;
using System.Text.Json;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>settings.json 的落盘结构。新增字段给默认值，旧版本文件读不出也不会炸。</summary>
internal sealed class StoredSettings
{
    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>proxy = 自建反代；fronting = 域前置直连。</summary>
    public string ApiMode { get; set; } = "proxy";

    /// <summary>「域名 = IP, IP」多行文本；空则用内置默认。</summary>
    public string FrontingHostMap { get; set; } = string.Empty;

    /// <summary>反代失败时自动切域前置。</summary>
    public bool AutoFallback { get; set; } = true;
}

/// <summary>
/// 极简本地设置存储（%LOCALAPPDATA%\PixivViewer\settings.json）。
/// </summary>
internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixivViewer",
        "settings.json");

    public static StoredSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new StoredSettings();
            }

            return JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(FilePath), JsonOptions)
                   ?? new StoredSettings();
        }
        catch (Exception)
        {
            return new StoredSettings();
        }
    }

    public static void Save(StoredSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception)
        {
            // 写盘失败不影响本次会话
        }
    }
}
