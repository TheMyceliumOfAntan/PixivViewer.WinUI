using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Pixiv_Viewer_WinUI.Models;

namespace Pixiv_Viewer_WinUI.Services.Pixiv;

/// <summary>
/// 真实 Pixiv App API 实现：走自建反代 <c>https://pixiv.junsi233.top</c>（API / 图片 / OAuth 三通道）。
/// token 与登录态由 <see cref="PixivSession"/> 管理，界面层不需要关心。
/// </summary>
public sealed class PixivApiService : IPixivService
{
    public const string DefaultBaseUrl = "https://pixiv.junsi233.top";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly PixivSession _session;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    /// <summary>标签候选缓存：同一个输入 10 分钟内只打一次接口。</summary>
    private readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, IReadOnlyList<TagSuggestion> Items)> _tagSuggestCache = new();

    private HashSet<string>? _blockedSet;

    private sealed record CacheEntry(object Value, DateTimeOffset ExpiresAt);

    public PixivApiService(string? baseUrl = null)
    {
        var url = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        _session = new PixivSession(_http, url);
    }

    /// <summary>是否在列表中保留 R-18 作品。</summary>
    public bool ShowR18 { get; set; }

    /// <summary>是否在列表中保留 R-18G 作品。</summary>
    public bool ShowR18G { get; set; }

    /// <summary>是否显示 AI 生成作品（Pixiv 的 illust_ai_type == 2）。</summary>
    public bool ShowAi { get; set; } = true;

    /// <summary>黑名单标签（逗号/空格分隔），命中的作品在列表里隐藏。</summary>
    public string BlockedTags
    {
        get => _blockedTags;
        set
        {
            _blockedTags = value;
            _blockedSet = null;
        }
    }

    private string _blockedTags = string.Empty;

    public string BaseUrl => _session.BaseUrl;

    public bool IsLoggedIn => _session.IsAuthenticated;

    /// <summary>清空接口缓存（点「刷新」时用，否则会拿到 3 分钟内的旧结果）。</summary>
    public void ClearCache() => _cache.Clear();

    public PixivUser? CurrentUser => _session.User;

    public Task<bool> SignInAsync(CancellationToken cancellationToken = default)
        => _session.SignInAsync(cancellationToken);

    public Task<bool> SignInWithTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
        => _session.SignInWithTokenAsync(refreshToken, cancellationToken);

    public void SignOut() => _session.SignOut();

    // ---------------------------------------------------------------- 列表

    public async Task<PagedResult<Illustration>> GetRecommendedAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync<PixivIllustListResponse>("/v1/illust/recommended", new Dictionary<string, string>
        {
            ["illust_type"] = "illust",
            ["include_ranking_illusts"] = "true",
            ["include_privacy_policy"] = "true",
            ["filter"] = "for_ios",
        }, cancellationToken);

        return ToPaged(response?.Illusts, offset, response?.NextUrl is not null);
    }

    public async Task<PagedResult<Illustration>> GetRankingAsync(RankingMode mode, int offset, int limit, CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync<PixivIllustListResponse>("/v1/illust/ranking", new Dictionary<string, string>
        {
            ["mode"] = mode switch
            {
                RankingMode.Weekly => "week",
                RankingMode.Monthly => "month",
                RankingMode.Rookie => "day_rookie",
                RankingMode.Original => "day_original",
                RankingMode.R18Daily => "day_r18",
                RankingMode.R18GWeekly => "week_r18g",
                RankingMode.DailyMale => "day_male",
                RankingMode.DailyFemale => "day_female",
                RankingMode.DailyAi => "day_ai",
                _ => "day",
            },
            ["filter"] = "for_ios",
            ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
        }, cancellationToken);

        return ToPaged(response?.Illusts, offset, response?.NextUrl is not null, withRank: true);
    }

    public async Task<PagedResult<Illustration>> SearchAsync(SearchFilter filter, int offset, int limit, CancellationToken cancellationToken = default)
    {
        // 多标签：Pixiv 的 word 用空格分隔即「同时包含」，这里再用客户端 AND 过滤兜底。
        var keyword = filter.Tags.Count > 0
            ? string.Join(" ", filter.Tags)
            : filter.Keyword;

        if (string.IsNullOrWhiteSpace(keyword))
        {
            return PagedResult<Illustration>.Empty;
        }

        var items = new List<Illustration>();
        var pageOffset = offset;
        var hasMore = false;

        // 过滤（R-18 / 黑名单 / 多标签 / 类型）会吃掉条数，循环多拉几页直到凑够 limit
        for (var page = 0; page < 6 && items.Count < limit; page++)
        {
            var response = await GetJsonAsync<PixivIllustListResponse>("/v1/search/illust", new Dictionary<string, string>
            {
                ["word"] = keyword,
                ["search_target"] = "partial_match_for_tags",
                ["sort"] = filter.Sort == SearchSort.Popular ? "popular_desc" : "date_desc",
                ["filter"] = "for_ios",
                ["offset"] = pageOffset.ToString(CultureInfo.InvariantCulture),
            }, cancellationToken);

            var batch = response?.Illusts;
            if (batch is null || batch.Count == 0)
            {
                hasMore = false;
                break;
            }

            foreach (var dto in batch)
            {
                if (IsBlocked(dto) || !MatchesRequiredTags(dto, filter.Tags) || !filter.Target.Matches(dto.Type))
                {
                    continue;
                }

                items.Add(ToIllustration(dto));
                if (items.Count >= limit)
                {
                    break;
                }
            }

            pageOffset += batch.Count;
            hasMore = response?.NextUrl is not null;

            if (!hasMore)
            {
                break;
            }
        }

        // 非会员的 popular_desc 会被 Pixiv 静默降级成「最新」，这里本地按收藏数再排一次
        if (filter.Sort == SearchSort.Popular)
        {
            items = items.OrderByDescending(i => i.BookmarkCount).ToList();
        }

        return new PagedResult<Illustration>
        {
            Items = items,
            Offset = offset,
            NextOffset = pageOffset,
            HasMore = hasMore,
        };
    }

    public async Task<PagedResult<Illustration>> GetBookmarksAsync(bool isPrivate, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(cancellationToken);
        if (userId is null)
        {
            return PagedResult<Illustration>.Empty;
        }

        var items = new List<Illustration>();
        var maxBookmarkId = cursor;
        var hasMore = false;

        // 收藏按 max_bookmark_id 翻页；过滤会吃掉条数，循环补几页
        for (var page = 0; page < 6 && items.Count < limit; page++)
        {
            var query = new Dictionary<string, string>
            {
                ["user_id"] = userId,
                ["restrict"] = isPrivate ? "private" : "public",
                ["filter"] = "for_ios",
            };

            if (!string.IsNullOrEmpty(maxBookmarkId))
            {
                query["max_bookmark_id"] = maxBookmarkId;
            }

            var response = await GetJsonAsync<PixivIllustListResponse>("/v1/user/bookmarks/illust", query, cancellationToken);
            var batch = response?.Illusts;

            if (batch is null || batch.Count == 0)
            {
                hasMore = false;
                break;
            }

            foreach (var dto in batch)
            {
                if (IsBlocked(dto))
                {
                    continue;
                }

                items.Add(ToIllustration(dto));
                if (items.Count >= limit)
                {
                    break;
                }
            }

            maxBookmarkId = ExtractQueryParam(response?.NextUrl, "max_bookmark_id");
            hasMore = !string.IsNullOrEmpty(maxBookmarkId);

            if (!hasMore)
            {
                break;
            }
        }

        return new PagedResult<Illustration>
        {
            Items = items,
            HasMore = hasMore,
            NextCursor = maxBookmarkId,
        };
    }

    private static string? ExtractQueryParam(string? url, string name)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var index = url.IndexOf(name + "=", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = index + name.Length + 1;
        var end = url.IndexOf('&', start);
        return end < 0 ? url[start..] : url[start..end];
    }

    public async Task<PagedResult<Illustration>> GetMyWorksAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(cancellationToken);
        if (userId is null)
        {
            return PagedResult<Illustration>.Empty;
        }

        return await GetUserIllustrationsAsync(userId, offset, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<PixivUser>> GetFollowingUsersAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(cancellationToken);
        if (userId is null)
        {
            return Array.Empty<PixivUser>();
        }

        var response = await GetJsonAsync<PixivFollowingResponse>("/v1/user/following", new Dictionary<string, string>
        {
            ["user_id"] = userId,
            ["restrict"] = "public",
            ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
        }, cancellationToken);

        return response?.UserPreviews?
            .Select(p => p.User)
            .Where(u => u is not null)
            .Select(u => ToUser(u!))
            .ToList() ?? (IReadOnlyList<PixivUser>)Array.Empty<PixivUser>();
    }

    public async Task<PagedResult<Illustration>> GetUserIllustrationsAsync(string userId, int offset, int limit, CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync<PixivIllustListResponse>("/v1/user/illusts", new Dictionary<string, string>
        {
            ["user_id"] = userId,
            ["type"] = "illust",
            ["filter"] = "for_ios",
            ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
        }, cancellationToken);

        return ToPaged(response?.Illusts, offset, response?.NextUrl is not null);
    }

    // ---------------------------------------------------------------- 详情

    public async Task<Illustration?> GetIllustrationAsync(string id, CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync<PixivIllustDetailResponse>("/v1/illust/detail", new Dictionary<string, string>
        {
            ["illust_id"] = id,
        }, cancellationToken);

        return response?.Illust is null ? null : ToIllustration(response.Illust);
    }

    /// <summary>
    /// 获取作品评论。
    ///
    /// pixiv 已把评论迁到 street 体系，旧的 App API <c>/v1/illust/comments</c> 和
    /// 网页 <c>/ajax/illust/{id}/comments</c> 都已下线（404）。现在走：
    /// <c>GET /ajax/street/comments/roots?id={workId}&amp;type=illust&amp;offset=0&amp;limit=50</c>
    ///
    /// 返回 null 表示两个端点都不可用（UI 会明确提示，而不是假装「还没有评论」）。
    /// </summary>
    public async Task<IReadOnlyList<Comment>?> GetCommentsAsync(string id, CancellationToken cancellationToken = default)
    {
        // 1) street（网页版）—— 目前唯一可用的读取端点
        //    limit 上限是 20，传 50 会返回 400「不正なリクエストです」
        try
        {
            const int pageSize = 20;
            const int maxPages = 3;

            var all = new List<Comment>();

            for (var page = 0; page < maxPages; page++)
            {
                var offset = page * pageSize;
                var url = $"{_session.BaseUrl}/api/web/ajax/street/comments/roots" +
                          $"?id={Uri.EscapeDataString(id)}&type=illust&offset={offset}&limit={pageSize}";

                using var response = await SendWebAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    PixivDiagnostics.Write($"street comments {id} offset={offset} -> {(int)response.StatusCode}");

                    // 第一页就失败才算不可用；后续页失败就返回已拿到的
                    return all.Count > 0 ? all : null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var payload = JsonSerializer.Deserialize<PixivStreetCommentRootsResponse>(json, JsonOptions);

                if (payload is not { Error: false, Body: not null })
                {
                    PixivDiagnostics.Write($"street comments {id} offset={offset}: error payload {Truncate(json)}");
                    return all.Count > 0 ? all : null;
                }

                var batch = payload.Body.Comments;
                if (batch is null || batch.Count == 0)
                {
                    break;
                }

                all.AddRange(batch.Select(ToComment));

                if (!payload.Body.HasNext)
                {
                    break;
                }
            }

            return all;
        }
        catch (Exception ex)
        {
            PixivDiagnostics.Write($"street comments {id} failed: {ex.Message}");
        }

        // 2) 旧的 App API 兜底（若 pixiv 以后恢复）
        try
        {
            var response = await GetJsonAsync<PixivCommentListResponse>("/v1/illust/comments", new Dictionary<string, string>
            {
                ["illust_id"] = id,
            }, cancellationToken);

            return response?.Comments?
                .Select(c => new Comment
                {
                    Id = c.Id.ToString(CultureInfo.InvariantCulture),
                    Text = c.Comment ?? string.Empty,
                    CreatedAt = ParseDate(c.Date),
                    User = ToUser(c.User),
                })
                .ToList() ?? (IReadOnlyList<Comment>)Array.Empty<Comment>();
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return null;
        }
    }

    private Comment ToComment(PixivStreetCommentDto dto)
    {
        return new Comment
        {
            Id = dto.Id ?? string.Empty,
            Text = dto.Comment ?? string.Empty,
            StampId = dto.StampId ?? string.Empty,
            StampUrl = string.IsNullOrWhiteSpace(dto.StampId)
                ? null
                : ProxyStatic($"common/images/stamp/generated-stamps/{Uri.EscapeDataString(dto.StampId)}_s.jpg"),
            CreatedAt = ParseStreetDate(dto.CommentDate),
            User = new PixivUser
            {
                Id = dto.UserId ?? string.Empty,
                Name = dto.UserName ?? string.Empty,
                AvatarUrl = string.IsNullOrWhiteSpace(dto.Img) ? null : ProxyImage(dto.Img),
            },
        };
    }

    /// <summary>street 的 commentDate 是 JST 的 "yyyy-MM-dd HH:mm"（不带时区）。</summary>
    private static DateTimeOffset ParseStreetDate(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            DateTime.TryParseExact(
                value.Trim(),
                "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            var jst = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), TimeSpan.FromHours(9));
            return jst.ToLocalTime();
        }

        return DateTimeOffset.Now;
    }

    public async Task<PixivUser?> GetUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync<PixivUserDetailResponse>("/v1/user/detail", new Dictionary<string, string>
        {
            ["user_id"] = userId,
        }, cancellationToken);

        if (response?.User is null)
        {
            return null;
        }

        var user = ToUser(response.User);
        user.Biography = response.User.Comment ?? string.Empty;
        user.IllustrationCount = response.Profile?.TotalIllusts ?? 0;
        user.FollowingCount = response.Profile?.TotalFollowUsers ?? 0;
        user.BookmarkCount = response.Profile?.TotalIllustBookmarksPublic ?? 0;
        return user;
    }

    // ---------------------------------------------------------------- 网页版（收藏集）

    /// <summary>网页版登录 cookie（PHPSESSID）。</summary>
    public string SessionCookie => _session.SessionCookie;

    public void SaveSessionCookie(string cookie) => _session.SaveSessionCookie(cookie);

    public void SaveRefreshToken(string refreshToken) => _session.SaveRefreshToken(refreshToken);

    /// <summary>
    /// 收藏集：网页版是 Next.js SSR，数据在 __NEXT_DATA__ 里（公开收藏集不需要登录）。
    /// </summary>
    public async Task<PixivCollection?> GetCollectionAsync(string collectionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return null;
        }

        var url = $"{_session.BaseUrl}/api/web/collections/{collectionId}";
        using var response = await SendWebAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            PixivDiagnostics.Write($"collection {collectionId} -> {(int)response.StatusCode}");
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = ExtractNextData(html);
        PixivDiagnostics.Write($"collection {collectionId}: status={(int)response.StatusCode}, len={html.Length}, next={json is not null}");
        return json is null ? null : await ParseCollectionPageAsync(collectionId, json, cancellationToken);
    }

    /// <summary>
    /// 标签搜索候选。
    ///
    /// pixiv 的搜索只认原文标签（多为日文），直接拿中文去搜经常搜不到东西。
    /// 网页版有个「标签详情」接口可以给出映射：
    ///   GET /ajax/search/tags/{word}?lang=zh   → breadcrumbs.successor[].translation.zh
    ///   GET /ajax/search/tags/{word}?lang=en   → breadcrumbs.successor[].translation.en
    ///   body.pixpedia.parentTag                → 官方父标签（中文词对应的日文原文）
    /// 同时请求 zh / en 两份，按标签合并出一份「日文原文 + 中文 + 英文」的候选。
    /// </summary>
    public async Task<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(string word, CancellationToken cancellationToken = default)
    {
        var key = word.Trim();
        if (key.Length == 0)
        {
            return Array.Empty<TagSuggestion>();
        }

        if (_tagSuggestCache.TryGetValue(key, out var hit) && hit.ExpiresAt > DateTimeOffset.Now)
        {
            return hit.Items;
        }

        var zhTask = FetchTagSuggestionAsync(key, "zh", cancellationToken);
        var enTask = FetchTagSuggestionAsync(key, "en", cancellationToken);

        var zh = await zhTask.ConfigureAwait(false);
        var en = await enTask.ConfigureAwait(false);

        var items = new List<TagSuggestion>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void AddOne(string? tag, string? zhName, string? enName)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return;
            }

            tag = tag.Trim();

            if (index.TryGetValue(tag, out var existing))
            {
                var old = items[existing];
                items[existing] = TagSuggestion.Create(
                    old.Tag,
                    old.Chinese.Length > 0 ? old.Chinese : zhName,
                    old.English.Length > 0 ? old.English : enName);
                return;
            }

            index[tag] = items.Count;
            items.Add(TagSuggestion.Create(tag, zhName, enName));
        }

        // 1) 官方父标签最优先（中文「碧蓝档案」→ 日文「ブルーアーカイブ」）
        AddOne(zh?.Body?.Pixpedia?.ParentTag, null, null);
        AddOne(en?.Body?.Pixpedia?.ParentTag, null, null);

        // 2) 相关标签（顺带补齐中英译名）
        foreach (var successor in zh?.Body?.Breadcrumbs?.Successor ?? Enumerable.Empty<PixivTagBreadcrumb>())
        {
            AddOne(successor.Tag, successor.Translation?.Zh, null);
        }

        foreach (var successor in en?.Body?.Breadcrumbs?.Successor ?? Enumerable.Empty<PixivTagBreadcrumb>())
        {
            AddOne(successor.Tag, null, successor.Translation?.En);
        }

        // 3) 命中的标签本身
        AddOne(zh?.Body?.Tag, null, null);

        var result = items.Take(8).ToList();

        if (result.Count > 0)
        {
            if (_tagSuggestCache.Count > 200)
            {
                foreach (var pair in _tagSuggestCache.Where(p => p.Value.ExpiresAt <= DateTimeOffset.Now).ToList())
                {
                    _tagSuggestCache.TryRemove(pair.Key, out _);
                }
            }

            _tagSuggestCache[key] = (DateTimeOffset.Now.AddMinutes(10), result);
        }

        return result;
    }

    private async Task<PixivTagSuggestResponse?> FetchTagSuggestionAsync(
        string word,
        string lang,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{_session.BaseUrl}/api/web/ajax/search/tags/{Uri.EscapeDataString(word)}?lang={lang}";

            // 这个端点不需要登录；有趣的是带上登录 cookie 时 pixiv 只返回 1 个候选，
            // 匿名反而给完整的相关标签 + 中英译名，所以这里刻意不带 cookie。
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", PixivSession.UserAgent);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<PixivTagSuggestResponse>(json, JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }
    /// <summary>从 Next.js 页面里抠出 __NEXT_DATA__ 的 JSON。</summary>
    private static string? ExtractNextData(string html)
    {
        const string marker = "<script id=\"__NEXT_DATA__\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var jsonStart = html.IndexOf('>', start) + 1;
        if (jsonStart <= 0)
        {
            return null;
        }

        var jsonEnd = html.IndexOf("</script>", jsonStart, StringComparison.Ordinal);
        return jsonEnd <= jsonStart ? null : html[jsonStart..jsonEnd];
    }

    private async Task<PixivCollection?> ParseCollectionPageAsync(string collectionId, string json, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object ||
            !props.TryGetProperty("pageProps", out var pageProps) || pageProps.ValueKind != JsonValueKind.Object ||
            !pageProps.TryGetProperty("collection", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var collection = new PixivCollection
        {
            Id = collectionId,
            Name = GetString(info, "title"),
            Description = GetString(info, "caption"),
            Author = new PixivUser
            {
                Id = GetString(info, "userId"),
                Name = GetString(info, "userName"),
                AvatarUrl = ProxyImage(GetString(info, "profileImageUrl")),
            },
        };

        // 收藏集是拼图布局：tiles[].workId + layout.position(x, y)，本身不带作品详情
        var tiles = new List<(string WorkId, int X, int Y)>();

        if (info.TryGetProperty("tiles", out var tileArray) && tileArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var tile in tileArray.EnumerateArray())
            {
                if (tile.ValueKind != JsonValueKind.Object ||
                    !string.Equals(GetString(tile, "type"), "Work", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var workId = GetString(tile, "workId");
                if (string.IsNullOrEmpty(workId))
                {
                    continue;
                }

                var x = 0;
                var y = 0;

                if (tile.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object &&
                    layout.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Object)
                {
                    x = GetInt(position, "x");
                    y = GetInt(position, "y");
                }

                tiles.Add((workId, x, y));
            }
        }

        if (tiles.Count == 0)
        {
            collection.Illustrations = Array.Empty<Illustration>();
            return collection;
        }

        // 按拼图位置排序后逐个拉详情（并发 4，结果走缓存）
        var ordered = tiles.OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
        var results = new Illustration?[ordered.Count];
        using var gate = new SemaphoreSlim(4);

        var tasks = ordered.Select(async (tile, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                results[index] = await GetIllustrationAsync(tile.WorkId, cancellationToken);
            }
            catch (Exception)
            {
                // 单件失败跳过
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        collection.Illustrations = results
            .Where(item => item is not null && !IsHiddenBySettings(item))
            .Select(item => item!)
            .ToList();

        return collection;
    }
    private Illustration? ParseNextIllust(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(element, "id");
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var thumb = string.Empty;
        if (element.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Object)
        {
            thumb = GetString(urls, "540x540");
            if (string.IsNullOrEmpty(thumb))
            {
                thumb = GetString(urls, "360x360");
            }

            if (string.IsNullOrEmpty(thumb))
            {
                thumb = GetString(urls, "250x250");
            }
        }

        if (string.IsNullOrEmpty(thumb))
        {
            thumb = GetString(element, "url");
        }

        var width = GetInt(element, "width");
        var height = GetInt(element, "height");

        return new Illustration
        {
            Id = id,
            Title = GetString(element, "title"),
            ThumbnailUrl = ProxyImage(thumb),
            PageCount = Math.Max(1, GetInt(element, "pageCount")),
            AspectRatio = width > 0 && height > 0 ? (double)width / height : 0.75,
            XRestrict = GetInt(element, "xRestrict"),
            IsAi = GetInt(element, "aiType") == 2,
            Author = new PixivUser
            {
                Id = GetString(element, "userId"),
                Name = GetString(element, "userName"),
                AvatarUrl = ProxyImage(GetString(element, "profileImageUrl")),
            },
        };
    }

    private bool IsHiddenBySettings(Illustration item)
    {
        if (item.XRestrict == 1 && !ShowR18)
        {
            return true;
        }

        if (item.XRestrict == 2 && !ShowR18G)
        {
            return true;
        }

        return item.IsAi && !ShowAi;
    }

    private static int GetInt(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.TryGetInt32(out var number)
            ? number
            : 0;

    private static string GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private async Task<HttpResponseMessage> SendWebAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", PixivSession.UserAgent);

        // 反代会把 X-Pixiv-Cookie 转成 Cookie 发给 www.pixiv.net
        if (!string.IsNullOrWhiteSpace(_session.SessionCookie))
        {
            request.Headers.TryAddWithoutValidation("X-Pixiv-Cookie", _session.SessionCookie);
        }

        return await _http.SendAsync(request, cancellationToken);
    }

    // ---------------------------------------------------------------- 写操作

    public async Task<bool> SetBookmarkAsync(string id, bool isBookmarked, bool isPrivate = false, CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, string> { ["illust_id"] = id };

        if (isBookmarked)
        {
            fields["restrict"] = isPrivate ? "private" : "public";
            return await PostFormAsync("/v2/illust/bookmark/add", fields, cancellationToken);
        }

        return await PostFormAsync("/v1/illust/bookmark/delete", fields, cancellationToken);
    }

    public async Task<bool> SetFollowAsync(string userId, bool isFollowed, CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, string> { ["user_id"] = userId };

        if (isFollowed)
        {
            fields["restrict"] = "public";
            return await PostFormAsync("/v1/user/follow/add", fields, cancellationToken);
        }

        return await PostFormAsync("/v1/user/follow/delete", fields, cancellationToken);
    }

    public async Task<Comment?> PostCommentAsync(string id, string text, CancellationToken cancellationToken = default)
    {
        // App API 的 /v1/illust/comment/add 已下线；现在网页版作品页走的是
        //   POST https://www.pixiv.net/rpc/post_comment.php
        //   Content-Type: application/x-www-form-urlencoded
        //   type=comment&illust_id=…&author_user_id=…&comment=…
        // 需要网页登录态（PHPSESSID），并校验 Referer 是作品页。
        var authorId = _session.User?.Id;
        if (string.IsNullOrWhiteSpace(authorId) || string.IsNullOrWhiteSpace(_session.SessionCookie))
        {
            PixivDiagnostics.Write("post comment: 缺少登录态（需要 refresh_token + 网页 cookie）");
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_session.BaseUrl}/api/web/rpc/post_comment.php")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["type"] = "comment",
                    ["illust_id"] = id,
                    ["author_user_id"] = authorId,
                    ["comment"] = text,
                }),
            };

            request.Headers.TryAddWithoutValidation("User-Agent", PixivSession.UserAgent);
            request.Headers.TryAddWithoutValidation("X-Pixiv-Cookie", _session.SessionCookie);
            request.Headers.TryAddWithoutValidation("X-Pixiv-Referer", $"https://www.pixiv.net/artworks/{id}");

            using var response = await _http.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                PixivDiagnostics.Write($"post comment {id} -> {(int)response.StatusCode} {json}");
                return null;
            }

            var payload = JsonSerializer.Deserialize<PixivStreetCommentRootsResponse>(json, JsonOptions);
            if (payload is { Error: true })
            {
                PixivDiagnostics.Write($"post comment {id} error: {payload.Message}");
                return null;
            }

            return new Comment
            {
                Text = text,
                CreatedAt = DateTimeOffset.Now,
                User = _session.User ?? new PixivUser(),
            };
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- HTTP

    private async Task<T?> GetJsonAsync<T>(string path, Dictionary<string, string>? query, CancellationToken cancellationToken)
        where T : class
    {
        var url = BuildUrl(path, query);

        // 命中缓存直接返回：反代有风控风险，同样的请求 3 分钟内只打一次（详情 10 分钟）。
        if (_cache.TryGetValue(url, out var hit) && hit.ExpiresAt > DateTimeOffset.Now && hit.Value is T cached)
        {
            return cached;
        }

        using var response = await SendAsync(HttpMethod.Get, url, null, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            PixivDiagnostics.Write($"GET {path} -> {(int)response.StatusCode} {Truncate(body)}");

            // 400/401 基本都是没登录或 token 失效，给 UI 一个可读的原因
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                throw new PixivNotLoggedInException(
                    "未登录或登录已过期，请到「设置 → 账号」登录",
                    response.StatusCode);
            }

            throw new HttpRequestException(
                $"Pixiv API {(int)response.StatusCode} {path}",
                null,
                response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<T>(json, JsonOptions);

        if (result is not null)
        {
            var ttl = path.Contains("/illust/detail", StringComparison.Ordinal)
                ? TimeSpan.FromMinutes(10)
                : TimeSpan.FromMinutes(3);
            StoreCache(url, result, ttl);
        }

        return result;
    }

    private void StoreCache(string key, object value, TimeSpan ttl)
    {
        if (_cache.Count > 300)
        {
            foreach (var pair in _cache.Where(p => p.Value.ExpiresAt <= DateTimeOffset.Now).ToList())
            {
                _cache.TryRemove(pair.Key, out _);
            }

            if (_cache.Count > 400)
            {
                _cache.Clear();
            }
        }

        _cache[key] = new CacheEntry(value, DateTimeOffset.Now.Add(ttl));
    }

    private async Task<bool> PostFormAsync(string path, Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        var url = BuildUrl(path, null);
        using var response = await SendAsync(
            HttpMethod.Post,
            url,
            () => new FormUrlEncodedContent(fields),
            cancellationToken);

        return response.IsSuccessStatusCode;
    }

    private async Task<T?> PostJsonAsync<T>(string path, Dictionary<string, string> fields, CancellationToken cancellationToken)
        where T : class
    {
        var url = BuildUrl(path, null);
        using var response = await SendAsync(
            HttpMethod.Post,
            url,
            () => new FormUrlEncodedContent(fields),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Pixiv API {(int)response.StatusCode} {path}", null, response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        Func<HttpContent>? contentFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await _session.GetAccessTokenAsync(cancellationToken);

            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("User-Agent", PixivSession.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,ja;q=0.8");
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            }

            if (contentFactory is not null)
            {
                request.Content = contentFactory();
            }

            var response = await _http.SendAsync(request, cancellationToken);

            // access_token 失效：刷新后重试一次
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                _session.InvalidateAccessToken();
                continue;
            }

            return response;
        }
    }

    private string BuildUrl(string path, Dictionary<string, string>? query)
    {
        var builder = new StringBuilder(_session.BaseUrl).Append("/api").Append(path);

        if (query is null || query.Count == 0)
        {
            return builder.ToString();
        }

        builder.Append('?');
        var first = true;
        foreach (var (key, value) in query)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (!first)
            {
                builder.Append('&');
            }

            first = false;
            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    private async Task<string?> GetCurrentUserIdAsync(CancellationToken cancellationToken)
    {
        if (!_session.IsAuthenticated)
        {
            await _session.SignInAsync(cancellationToken);
        }

        return _session.User?.Id;
    }

    // ---------------------------------------------------------------- 映射

    private PagedResult<Illustration> ToPaged(
        List<PixivIllustDto>? dtos,
        int offset,
        bool hasNext,
        bool withRank = false,
        IReadOnlyList<string>? requiredTags = null,
        SearchTarget target = SearchTarget.All)
    {
        var items = new List<Illustration>();
        var rank = offset + 1;
        var consumed = 0;

        if (dtos is not null)
        {
            foreach (var dto in dtos)
            {
                consumed++;

                if (IsBlocked(dto) || !MatchesRequiredTags(dto, requiredTags) || !target.Matches(dto.Type))
                {
                    rank++;
                    continue;
                }

                var item = ToIllustration(dto);
                if (withRank)
                {
                    item.Rank = rank++;
                }

                items.Add(item);
            }
        }

        return new PagedResult<Illustration>
        {
            Items = items,
            Offset = offset,
            NextOffset = offset + consumed,
            HasMore = hasNext,
        };
    }

    private Illustration ToIllustration(PixivIllustDto dto)
    {
        var pages = new List<string>();
        var previews = new List<string>();
        var originals = new List<string>();
        var sources = new List<string>();

        if (dto.MetaPages is { Count: > 0 })
        {
            foreach (var page in dto.MetaPages)
            {
                // 预览用 large（600x1200，秒开）；原图后台加载后替换
                var previewLarge = page.ImageUrls?.Large ?? page.ImageUrls?.Medium;
                if (!string.IsNullOrEmpty(previewLarge))
                {
                    pages.Add(ProxyImage(previewLarge));
                }

                var original = page.ImageUrls?.Original ?? page.ImageUrls?.Large;
                if (!string.IsNullOrEmpty(original))
                {
                    originals.Add(ProxyImage(original));
                    sources.Add(original);
                }

                var preview = page.ImageUrls?.Medium ?? page.ImageUrls?.SquareMedium;
                if (!string.IsNullOrEmpty(preview))
                {
                    previews.Add(ProxyImage(preview));
                }
            }
        }

        if (pages.Count == 0)
        {
            var fallback = dto.ImageUrls?.Large ?? dto.ImageUrls?.Medium;
            if (!string.IsNullOrEmpty(fallback))
            {
                pages.Add(ProxyImage(fallback));
            }
        }

        if (originals.Count == 0)
        {
            var fallbackOriginal = dto.ImageUrls?.Original ?? dto.ImageUrls?.Large;
            if (!string.IsNullOrEmpty(fallbackOriginal))
            {
                originals.Add(ProxyImage(fallbackOriginal));
                sources.Add(fallbackOriginal);
            }
        }

        if (previews.Count == 0)
        {
            var fallbackPreview = dto.ImageUrls?.Medium ?? dto.ImageUrls?.SquareMedium;
            if (!string.IsNullOrEmpty(fallbackPreview))
            {
                previews.Add(ProxyImage(fallbackPreview));
            }
        }

        var tags = dto.Tags?
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => new IllustTag
            {
                Name = t.Name!,
                Translated = t.TranslatedName ?? string.Empty,
            })
            .ToList() ?? new List<IllustTag>();

        var aspect = dto.Width > 0 && dto.Height > 0 ? (double)dto.Width / dto.Height : 0.75;

        return new Illustration
        {
            Id = dto.Id.ToString(CultureInfo.InvariantCulture),
            Title = dto.Title ?? string.Empty,
            Description = ToPlainText(dto.Caption),
            Author = ToUser(dto.User),
            Type = dto.Type ?? "illust",
            PageCount = Math.Max(1, dto.PageCount),
            AspectRatio = aspect,
            Tags = tags,
            PageUrls = pages,
            PreviewUrls = previews,
            SourceUrls = sources,
            OriginalUrls = originals,
            Width = dto.Width,
            Height = dto.Height,
            // 列表用 540x540 的 medium 缩略图（大图只在详情页/下载用），显著降低流量与解码开销
            ThumbnailUrl = ProxyImage(dto.ImageUrls?.Medium ?? dto.ImageUrls?.SquareMedium),
            XRestrict = dto.XRestrict,
            IsAi = dto.IllustAiType == 2,
            // 排行榜接口不返回 like_count，用收藏数兜底，卡片上不至于全是 0
            LikeCount = dto.LikeCount ?? dto.TotalBookmarks,
            BookmarkCount = dto.TotalBookmarks,
            ViewCount = dto.TotalView,
            CreatedAt = ParseDate(dto.CreateDate),
            IsBookmarked = dto.IsBookmarked,
        };
    }

    private PixivUser ToUser(PixivUserDto? dto) => new()
    {
        Id = dto?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        Name = dto?.Name ?? string.Empty,
        Account = dto?.Account ?? string.Empty,
        AvatarUrl = ProxyImage(dto?.ProfileImageUrls?.Medium),
        IsFollowed = dto?.IsFollowed ?? false,
        Biography = dto?.Comment ?? string.Empty,
    };

    private bool IsBlocked(PixivIllustDto dto)
    {
        if (dto.XRestrict == 1 && !ShowR18)
        {
            return true;
        }

        if (dto.XRestrict == 2 && !ShowR18G)
        {
            return true;
        }

        if (dto.IllustAiType == 2 && !ShowAi)
        {
            return true;
        }

        var blocked = BlockedSet;
        if (blocked.Count > 0 && dto.Tags is not null)
        {
            foreach (var tag in dto.Tags)
            {
                if (!string.IsNullOrEmpty(tag.Name) && blocked.Contains(tag.Name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private HashSet<string> BlockedSet => _blockedSet ??= BlockedTags
        .Split(new[] { ',', '，', ' ', '、', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(t => t.Trim())
        .Where(t => t.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool MatchesRequiredTags(PixivIllustDto dto, IReadOnlyList<string>? requiredTags)
    {
        if (requiredTags is null || requiredTags.Count == 0)
        {
            return true;
        }

        if (dto.Tags is null)
        {
            return false;
        }

        var names = dto.Tags
            .Select(t => t.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return requiredTags.All(t => names.Contains(t));
    }

    private string ProxyImage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        return url.Replace("https://i.pximg.net", _session.BaseUrl + "/image", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>s.pximg.net 的静态资源（评论印章/表情）统一走反代的 /static/ 前缀。</summary>
    private string ProxyStatic(string path)
        => $"{_session.BaseUrl}/static/{path.TrimStart('/')}";

    private static DateTimeOffset ParseDate(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTimeOffset.Now;

    private static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        return text.Trim();
    }

    private static string Truncate(string value)
        => value.Length <= 300 ? value : value[..300];
}
