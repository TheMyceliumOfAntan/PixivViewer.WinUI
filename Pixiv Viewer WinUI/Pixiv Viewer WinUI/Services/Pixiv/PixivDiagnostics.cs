using System;
using System.IO;

namespace Pixiv_Viewer_WinUI.Services.Pixiv;

/// <summary>
/// 极简诊断日志：只记录失败信息，写到 %LOCALAPPDATA%\PixivViewer\log.txt，方便排查反代/token 问题。
/// </summary>
internal static class PixivDiagnostics
{
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixivViewer",
        "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // 日志失败不影响主流程。
        }
    }
}
