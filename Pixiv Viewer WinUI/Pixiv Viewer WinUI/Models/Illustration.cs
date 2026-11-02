using System;
using System.Collections.Generic;
using Pixiv_Viewer_WinUI.Helpers;

namespace Pixiv_Viewer_WinUI.Models;

/// <summary>
/// 一件作品（插画 / 漫画）。UI 只读取这些字段，数据来源由 IPixivService 决定。
/// </summary>
public sealed class Illustration : ObservableObject
{
    private bool _isBookmarked;
    private bool _isLiked;

    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public PixivUser Author { get; set; } = new();

    /// <summary>illust / manga / ugoira。</summary>
    public string Type { get; set; } = "illust";

    /// <summary>总页数（多图作品 &gt; 1）。</summary>
    public int PageCount { get; set; } = 1;

    /// <summary>封面宽高比（宽 / 高），瀑布流用它决定卡片高度。</summary>
    public double AspectRatio { get; set; } = 0.75;

    public IReadOnlyList<IllustTag> Tags { get; set; } = Array.Empty<IllustTag>();

    /// <summary>用于展示的图片地址（反代后的原图，详情页主图用）。</summary>
    public IReadOnlyList<string> PageUrls { get; set; } = Array.Empty<string>();

    /// <summary>每页的小图（缩略图条用，避免为了缩略图去拉原图）。</summary>
    public IReadOnlyList<string> PreviewUrls { get; set; } = Array.Empty<string>();

    /// <summary>原图地址（i.pximg.net 源链接，未走反代，仅用于展示）。</summary>
    public IReadOnlyList<string> SourceUrls { get; set; } = Array.Empty<string>();

    /// <summary>原图宽度（像素）。</summary>
    public int Width { get; set; }

    /// <summary>原图高度（像素）。</summary>
    public int Height { get; set; }

    /// <summary>原图地址（详情页才有），用于「另存为」和下载。</summary>
    public IReadOnlyList<string> OriginalUrls { get; set; } = Array.Empty<string>();

    public string? ThumbnailUrl { get; set; }

    public int LikeCount { get; set; }

    public int BookmarkCount { get; set; }

    public int ViewCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>0 = 全年龄，1 = R-18，2 = R-18G。</summary>
    public int XRestrict { get; set; }

    /// <summary>Pixiv 的 illust_ai_type：2 = AI 生成。</summary>
    public bool IsAi { get; set; }

    public bool IsBookmarked
    {
        get => _isBookmarked;
        set => SetProperty(ref _isBookmarked, value);
    }

    /// <summary>当前收藏是否为私密收藏（只用于右键菜单文案，不参与通知）。</summary>
    public bool BookmarkIsPrivate { get; set; }

    public bool IsLiked
    {
        get => _isLiked;
        set => SetProperty(ref _isLiked, value);
    }

    /// <summary>排行榜名次，0 表示不显示徽章。</summary>
    public int Rank { get; set; }

    public bool IsR18 => XRestrict == 1;

    public bool IsR18G => XRestrict == 2;

    public bool HasRating => XRestrict > 0;

    public string RatingText => XRestrict switch
    {
        2 => "R-18G",
        1 => "R-18",
        _ => string.Empty,
    };

    public string LikeText => Format.Compact(LikeCount);

    public string BookmarkText => Format.Compact(BookmarkCount);

    public string ViewText => Format.Compact(ViewCount);

    public string PageText => PageCount > 1 ? $"{PageCount}P" : string.Empty;

    public string CreatedText => Format.Date(CreatedAt);

    public bool HasRank => Rank > 0;

    public string RankText => Rank.ToString();
}
