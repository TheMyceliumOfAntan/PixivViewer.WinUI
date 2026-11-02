using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pixiv_Viewer_WinUI.Models;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>
/// Pixiv 数据访问契约。UI 只依赖此接口：
/// 接入真实 API 时新增一个实现类替换 <see cref="PixivService"/> 即可，页面代码无需改动。
/// </summary>
public interface IPixivService
{
    bool IsLoggedIn { get; }

    PixivUser? CurrentUser { get; }

    /// <summary>用已保存的 refresh_token 登录（首次启动 / 恢复会话）。</summary>
    Task<bool> SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>用新的 refresh_token 登录并保存到本机。</summary>
    Task<bool> SignInWithTokenAsync(string refreshToken, CancellationToken cancellationToken = default);

    void SignOut();

    Task<PagedResult<Illustration>> GetRecommendedAsync(int offset, int limit, CancellationToken cancellationToken = default);

    Task<PagedResult<Illustration>> GetRankingAsync(RankingMode mode, int offset, int limit, CancellationToken cancellationToken = default);

    Task<PagedResult<Illustration>> SearchAsync(SearchFilter filter, int offset, int limit, CancellationToken cancellationToken = default);

    Task<PagedResult<Illustration>> GetBookmarksAsync(bool isPrivate, string? cursor, int limit, CancellationToken cancellationToken = default);

    Task<PagedResult<Illustration>> GetMyWorksAsync(int offset, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PixivUser>> GetFollowingUsersAsync(int offset, int limit, CancellationToken cancellationToken = default);

    Task<PixivUser?> GetUserAsync(string userId, CancellationToken cancellationToken = default);

    Task<PagedResult<Illustration>> GetUserIllustrationsAsync(string userId, int offset, int limit, CancellationToken cancellationToken = default);

    Task<Illustration?> GetIllustrationAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>获取评论；返回 null 表示接口不可用（Pixiv 已关闭第三方评论读取）。</summary>
    Task<IReadOnlyList<Comment>?> GetCommentsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>标签搜索候选：把中文/英文输入映射到原始（日文）标签。不会返回 null。</summary>
    Task<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(string word, CancellationToken cancellationToken = default);

    Task<bool> SetBookmarkAsync(string id, bool isBookmarked, bool isPrivate = false, CancellationToken cancellationToken = default);

    Task<bool> SetFollowAsync(string userId, bool isFollowed, CancellationToken cancellationToken = default);

    Task<Comment?> PostCommentAsync(string id, string text, CancellationToken cancellationToken = default);
}
