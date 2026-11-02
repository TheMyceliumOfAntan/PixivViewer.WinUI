using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pixiv_Viewer_WinUI.Models;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>
/// 空实现：所有方法返回空结果，UI 会进入「空状态」。
/// 接入真实 Pixiv API 时，逐个方法补上 HTTP 请求（注意 OAuth PKCE 登录、Referer 头、限流）。
/// </summary>
public sealed class PixivService : IPixivService
{
    // TODO: 接入 OAuth PKCE 后改为真实登录状态
    public bool IsLoggedIn => false;

    public PixivUser? CurrentUser => null;

    public Task<bool> SignInAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> SignInWithTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public void SignOut()
    {
    }

    // TODO: GET /v1/illust/recommended
    public Task<PagedResult<Illustration>> GetRecommendedAsync(int offset, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult(PagedResult<Illustration>.Empty);

    // TODO: GET /v1/illust/ranking?mode=day|week|month|day_rookie|day_original
    public Task<PagedResult<Illustration>> GetRankingAsync(RankingMode mode, int offset, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult(PagedResult<Illustration>.Empty);

    // TODO: GET /v1/search/illust
    public Task<PagedResult<Illustration>> SearchAsync(SearchFilter filter, int offset, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult(PagedResult<Illustration>.Empty);

    // TODO: GET /v1/user/bookmarks/illust?restrict=public|private
    public Task<PagedResult<Illustration>> GetBookmarksAsync(bool isPrivate, string? cursor, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult(PagedResult<Illustration>.Empty);

    // TODO: GET /v1/user/illusts
    public Task<PagedResult<Illustration>> GetMyWorksAsync(int offset, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult(PagedResult<Illustration>.Empty);

    // TODO: GET /v1/user/following
    public Task<IReadOnlyList<PixivUser>> GetFollowingUsersAsync(int offset, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PixivUser>>(new List<PixivUser>());

    // TODO: GET /v2/user/detail
    public Task<PixivUser?> GetUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult<PixivUser?>(null);

    // TODO: GET /v1/user/illusts?user_id={userId}
    public Task<PagedResult<Illustration>> GetUserIllustrationsAsync(string userId, int offset, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult(PagedResult<Illustration>.Empty);

    // TODO: GET /v1/illust/detail
    public Task<Illustration?> GetIllustrationAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult<Illustration?>(null);

    // TODO: GET /v2/illust/comment
    public Task<IReadOnlyList<Comment>?> GetCommentsAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Comment>?>(new List<Comment>());

    // TODO: GET /ajax/search/tags/{word}?lang=zh|en
    public Task<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(string word, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TagSuggestion>>(new List<TagSuggestion>());

    // TODO: POST /v2/illust/bookmark/add | delete
    public Task<bool> SetBookmarkAsync(string id, bool isBookmarked, bool isPrivate = false, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    // TODO: POST /v1/user/follow/add | delete
    public Task<bool> SetFollowAsync(string userId, bool isFollowed, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    // TODO: POST /v1/illust/comment/add
    public Task<Comment?> PostCommentAsync(string id, string text, CancellationToken cancellationToken = default)
        => Task.FromResult<Comment?>(null);

    public Task<Comment?> PostStampAsync(string id, string stampId, CancellationToken cancellationToken = default)
        => Task.FromResult<Comment?>(null);
}
