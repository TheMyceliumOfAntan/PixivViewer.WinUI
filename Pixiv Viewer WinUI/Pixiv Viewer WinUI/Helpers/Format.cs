using System;

namespace Pixiv_Viewer_WinUI.Helpers;

/// <summary>
/// 展示层格式化（数字、字节、时间）。
/// </summary>
public static class Format
{
    /// <summary>1.2万 / 3.4亿 / 1,234</summary>
    public static string Compact(int value) => value switch
    {
        >= 100_000_000 => (value / 100_000_000d).ToString("0.#") + "亿",
        >= 10_000 => (value / 10_000d).ToString("0.#") + "万",
        _ => value.ToString("N0"),
    };

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / 1073741824d).ToString("0.##") + " GB",
        >= 1L << 20 => (bytes / 1048576d).ToString("0.#") + " MB",
        >= 1024 => (bytes / 1024d).ToString("0.#") + " KB",
        _ => bytes + " B",
    };

    public static string RelativeTime(DateTimeOffset time)
    {
        var span = DateTimeOffset.Now - time;
        if (span.TotalSeconds < 60)
        {
            return "刚刚";
        }

        if (span.TotalMinutes < 60)
        {
            return $"{(int)span.TotalMinutes} 分钟前";
        }

        if (span.TotalHours < 24)
        {
            return $"{(int)span.TotalHours} 小时前";
        }

        if (span.TotalDays < 7)
        {
            return $"{(int)span.TotalDays} 天前";
        }

        return time.LocalDateTime.ToString("yyyy-MM-dd");
    }

    public static string Date(DateTimeOffset time) => time.LocalDateTime.ToString("yyyy年M月d日 HH:mm");
}
