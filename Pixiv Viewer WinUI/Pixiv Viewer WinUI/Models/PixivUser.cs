using Pixiv_Viewer_WinUI.Helpers;

namespace Pixiv_Viewer_WinUI.Models;

/// <summary>
/// Pixiv 用户（作者 / 关注对象）。
/// </summary>
public sealed class PixivUser
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Account { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    public bool IsFollowed { get; set; }

    public int IllustrationCount { get; set; }

    public int FollowerCount { get; set; }

    public int FollowingCount { get; set; }

    /// <summary>公开收藏数（/v1/user/detail 的 profile.total_illust_bookmarks_public）。</summary>
    public int BookmarkCount { get; set; }

    /// <summary>个人简介。</summary>
    public string Biography { get; set; } = string.Empty;

    public string AccountText => string.IsNullOrEmpty(Account) ? string.Empty : "@" + Account;

    public string FollowerText => Format.Compact(FollowerCount);

    public string FollowingText => Format.Compact(FollowingCount);

    public string IllustrationCountText => Format.Compact(IllustrationCount);

    public string BookmarkText => Format.Compact(BookmarkCount);
}
