using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Pixiv_Viewer_WinUI.Helpers;

/// <summary>
/// pixiv 评论里的表情：服务端返回的原始文本是 <c>(heart)(excited)</c> 这样的 slug，
/// 网页端会渲染成官方贴纸图片。这里同时提供：
///   · slug → 贴纸图片 URL（渲染用，与网站一致）
///   · slug → Unicode 表情（翻译时保持语义，避免把占位符送去机翻）
///
/// slug / id 对照来自官方 <c>GET /v1/emoji</c>（emoji_definitions）。
/// </summary>
public static class PixivEmoji
{
    /// <summary>slug → emoji id。</summary>
    private static readonly Dictionary<string, int> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["normal"] = 101,
        ["surprise"] = 102,
        ["serious"] = 103,
        ["heaven"] = 104,
        ["happy"] = 105,
        ["excited"] = 106,
        ["sing"] = 107,
        ["cry"] = 108,

        ["normal2"] = 201,
        ["shame2"] = 202,
        ["love2"] = 203,
        ["interesting2"] = 204,
        ["blush2"] = 205,
        ["fire2"] = 206,
        ["angry2"] = 207,
        ["shine2"] = 208,
        ["panic2"] = 209,

        ["normal3"] = 301,
        ["satisfaction3"] = 302,
        ["surprise3"] = 303,
        ["smile3"] = 304,
        ["shock3"] = 305,
        ["gaze3"] = 306,
        ["wink3"] = 307,
        ["happy3"] = 308,
        ["excited3"] = 309,
        ["love3"] = 310,

        ["normal4"] = 401,
        ["surprise4"] = 402,
        ["serious4"] = 403,
        ["love4"] = 404,
        ["shine4"] = 405,
        ["sweat4"] = 406,
        ["shame4"] = 407,
        ["sleep4"] = 408,

        ["heart"] = 501,
        ["teardrop"] = 502,
        ["star"] = 503,
    };

    /// <summary>全部表情（slug + 贴纸 URL），按 id 排序，供表情选择面板使用。</summary>
    public static IReadOnlyList<(string Slug, string Url)> All { get; } = Ids
        .OrderBy(pair => pair.Value)
        .Select(pair => (pair.Key, BuildUrl(pair.Value)))
        .ToList();

    public static int IdForSlug(string slug)
        => Ids.TryGetValue(slug, out var id) ? id : 0;

    /// <summary>同一批表情的 Unicode 近似值（仅用于翻译时保留语义）。</summary>
    private static readonly Dictionary<string, string> Unicode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["normal"] = "😊",
        ["surprise"] = "😲",
        ["serious"] = "😐",
        ["heaven"] = "😇",
        ["happy"] = "😄",
        ["excited"] = "😆",
        ["sing"] = "🎵",
        ["cry"] = "😭",
        ["normal2"] = "🙂",
        ["shame2"] = "😳",
        ["love2"] = "😍",
        ["interesting2"] = "🤔",
        ["blush2"] = "☺️",
        ["fire2"] = "🔥",
        ["angry2"] = "😠",
        ["shine2"] = "✨",
        ["panic2"] = "😱",
        ["normal3"] = "😐",
        ["satisfaction3"] = "😌",
        ["surprise3"] = "😮",
        ["smile3"] = "😊",
        ["shock3"] = "😲",
        ["gaze3"] = "👀",
        ["wink3"] = "😉",
        ["happy3"] = "😄",
        ["excited3"] = "🤩",
        ["love3"] = "😘",
        ["normal4"] = "😐",
        ["surprise4"] = "😯",
        ["serious4"] = "😑",
        ["love4"] = "❤️",
        ["shine4"] = "✨",
        ["sweat4"] = "😅",
        ["shame4"] = "😳",
        ["sleep4"] = "😴",
        ["heart"] = "❤️",
        ["teardrop"] = "💧",
        ["star"] = "⭐",
    };

    private static readonly Regex Placeholder = new(
        @"\(([a-z0-9_]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Unicode 表情 → 贴纸 URL（译文里只剩 Unicode 时也能还原成贴纸）。</summary>
    private static readonly Dictionary<string, string> ByUnicode = BuildUnicodeLookup();

    /// <summary>Unicode 匹配用的正则（长的放前面，避免 ❤️ 先匹配到 ❤）。</summary>
    private static readonly Regex UnicodePattern = BuildUnicodePattern();

    /// <summary>把 (slug) 换成 Unicode 表情，供翻译使用。</summary>
    public static string ToPlainEmoji(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return Placeholder.Replace(text, match =>
            Unicode.TryGetValue(match.Groups[1].Value, out var emoji) ? emoji : match.Value);
    }

    public static string? UrlForSlug(string slug)
        => Ids.TryGetValue(slug, out var id) ? BuildUrl(id) : null;

    public static string? UrlForUnicode(string emoji)
        => ByUnicode.TryGetValue(emoji, out var url) ? url : null;

    private static string BuildUrl(int id)
        => $"https://s.pximg.net/common/images/emoji/128x128/{id}_128x128.png";

    /// <summary>
    /// 把评论文本切成「纯文本」与「贴纸」片段，供 RichTextBlock 拼 Inline。
    /// 同时识别 (slug) 占位符和译文里残留的 Unicode 表情。
    /// </summary>
    public static IEnumerable<(string Text, string? ImageUrl)> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var segments = new List<(int Start, int Length, string Url)>();

        foreach (Match match in Placeholder.Matches(text))
        {
            var url = UrlForSlug(match.Groups[1].Value);
            if (url is not null)
            {
                segments.Add((match.Index, match.Length, url));
            }
        }

        foreach (Match match in UnicodePattern.Matches(text))
        {
            var url = UrlForUnicode(match.Value);
            if (url is not null)
            {
                segments.Add((match.Index, match.Length, url));
            }
        }

        if (segments.Count == 0)
        {
            yield return (text, null);
            yield break;
        }

        // 占位符与 Unicode 可能重叠，按起点排序后跳过已被覆盖的部分
        segments.Sort((a, b) => a.Start.CompareTo(b.Start));

        var cursor = 0;
        foreach (var segment in segments)
        {
            if (segment.Start < cursor)
            {
                continue;
            }

            if (segment.Start > cursor)
            {
                yield return (text[cursor..segment.Start], null);
            }

            yield return (string.Empty, segment.Url);
            cursor = segment.Start + segment.Length;
        }

        if (cursor < text.Length)
        {
            yield return (text[cursor..], null);
        }
    }

    private static Dictionary<string, string> BuildUnicodeLookup()
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in Ids)
        {
            if (Unicode.TryGetValue(pair.Key, out var emoji))
            {
                // 同一个 Unicode 对应多个 id 时，保留先出现的那个
                lookup.TryAdd(emoji, BuildUrl(pair.Value));
            }
        }

        return lookup;
    }

    private static Regex BuildUnicodePattern()
    {
        var keys = ByUnicode.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape);
        return new Regex(string.Join("|", keys), RegexOptions.Compiled);
    }
}
