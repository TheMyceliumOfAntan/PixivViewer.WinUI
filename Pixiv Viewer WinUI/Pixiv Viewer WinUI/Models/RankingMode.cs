using System;
using System.Collections.Generic;

namespace Pixiv_Viewer_WinUI.Models;

public enum RankingMode
{
    Daily,
    Weekly,
    Monthly,
    Rookie,
    Original,

    /// <summary>R-18 今日榜（mode=day_r18）。</summary>
    R18Daily,

    /// <summary>R-18G 本周榜（mode=week_r18g）。</summary>
    R18GWeekly,
}

public enum SearchSort
{
    Popular,
    Latest,
}

public enum SearchTarget
{
    /// <summary>不限类型。</summary>
    All,
    Illustrations,
    Manga,
    Ugoira,
}

/// <summary>
/// 搜索条件。
/// </summary>
public sealed class SearchFilter
{
    public string Keyword { get; set; } = string.Empty;

    /// <summary>多标签（AND）：每个标签都必须命中。</summary>
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    public SearchSort Sort { get; set; } = SearchSort.Popular;

    public SearchTarget Target { get; set; } = SearchTarget.All;

    public static SearchFilter Empty { get; } = new();
}

public static class PixivEnumLabels
{
    public static string ToLabel(this RankingMode mode) => mode switch
    {
        RankingMode.Daily => "今日",
        RankingMode.Weekly => "本周",
        RankingMode.Monthly => "本月",
        RankingMode.Rookie => "新人",
        RankingMode.Original => "原创",
        RankingMode.R18Daily => "R-18 今日",
        RankingMode.R18GWeekly => "R-18G 本周",
        _ => mode.ToString(),
    };

    public static string ToLabel(this SearchSort sort) => sort switch
    {
        SearchSort.Popular => "按人气",
        SearchSort.Latest => "按最新",
        _ => sort.ToString(),
    };

    public static string ToLabel(this SearchTarget target) => target switch
    {
        SearchTarget.All => "全部",
        SearchTarget.Illustrations => "插画",
        SearchTarget.Manga => "漫画",
        SearchTarget.Ugoira => "动图",
        _ => target.ToString(),
    };

    /// <summary>Pixiv 返回的 type 字段与枚举的对应关系。</summary>
    public static bool Matches(this SearchTarget target, string? illustType) => target switch
    {
        SearchTarget.All => true,
        SearchTarget.Manga => string.Equals(illustType, "manga", StringComparison.OrdinalIgnoreCase),
        SearchTarget.Ugoira => string.Equals(illustType, "ugoira", StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(illustType, "illust", StringComparison.OrdinalIgnoreCase),
    };
}
