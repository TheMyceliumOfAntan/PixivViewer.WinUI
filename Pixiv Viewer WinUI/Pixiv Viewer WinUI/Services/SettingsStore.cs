using System;
using System.IO;
using System.Text.Json;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>
/// 极简本地设置存储（%LOCALAPPDATA%\PixivViewer\settings.json）。
/// 目前只持久化「API 服务地址」——它是程序能否工作的前提，必须记住。
/// </summary>
internal static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixivViewer",
        "settings.json");

    public static string LoadApiBaseUrl()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return string.Empty;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            return document.RootElement.TryGetProperty("apiBaseUrl", out var value)
                ? (value.GetString() ?? string.Empty).Trim()
                : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public static void SaveApiBaseUrl(string value)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(new { apiBaseUrl = value ?? string.Empty });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception)
        {
            // 写盘失败不影响本次会话
        }
    }
}
