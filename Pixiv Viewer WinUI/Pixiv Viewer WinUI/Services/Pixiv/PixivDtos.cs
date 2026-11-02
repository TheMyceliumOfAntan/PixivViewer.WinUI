using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pixiv_Viewer_WinUI.Services.Pixiv;

/// <summary>Pixiv 不同接口里 id 有时是数字、有时是字符串，这里统一吃下。</summary>
internal sealed class FlexibleLongConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt64(out var number) ? number : 0,
            JsonTokenType.String => long.TryParse(reader.GetString(), out var parsed) ? parsed : 0,
            _ => 0,
        };

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

internal sealed class PixivAuthResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }

    [JsonPropertyName("user")] public PixivUserDto? User { get; set; }
}

internal sealed class PixivImageUrlsDto
{
    [JsonPropertyName("square_medium")] public string? SquareMedium { get; set; }

    [JsonPropertyName("medium")] public string? Medium { get; set; }

    [JsonPropertyName("large")] public string? Large { get; set; }

    [JsonPropertyName("original")] public string? Original { get; set; }
}

internal sealed class PixivUserDto
{
    [JsonPropertyName("id"), JsonConverter(typeof(FlexibleLongConverter))] public long Id { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("account")] public string? Account { get; set; }

    [JsonPropertyName("comment")] public string? Comment { get; set; }

    [JsonPropertyName("is_followed")] public bool IsFollowed { get; set; }

    [JsonPropertyName("profile_image_urls")] public PixivImageUrlsDto? ProfileImageUrls { get; set; }
}

internal sealed class PixivTagDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("translated_name")] public string? TranslatedName { get; set; }
}

internal sealed class PixivMetaPageDto
{
    [JsonPropertyName("image_urls")] public PixivImageUrlsDto? ImageUrls { get; set; }
}

internal sealed class PixivIllustDto
{
    [JsonPropertyName("id"), JsonConverter(typeof(FlexibleLongConverter))] public long Id { get; set; }

    [JsonPropertyName("title")] public string? Title { get; set; }

    [JsonPropertyName("caption")] public string? Caption { get; set; }

    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("page_count")] public int PageCount { get; set; }

    [JsonPropertyName("width")] public int Width { get; set; }

    [JsonPropertyName("height")] public int Height { get; set; }

    [JsonPropertyName("total_view")] public int TotalView { get; set; }

    [JsonPropertyName("total_bookmarks")] public int TotalBookmarks { get; set; }

    [JsonPropertyName("like_count")] public int? LikeCount { get; set; }

    [JsonPropertyName("create_date")] public string? CreateDate { get; set; }

    [JsonPropertyName("is_bookmarked")] public bool IsBookmarked { get; set; }

    /// <summary>0 = 全年龄，1 = R-18，2 = R-18G。</summary>
    [JsonPropertyName("x_restrict")] public int XRestrict { get; set; }

    [JsonPropertyName("illust_ai_type")] public int IllustAiType { get; set; }

    [JsonPropertyName("image_urls")] public PixivImageUrlsDto? ImageUrls { get; set; }

    [JsonPropertyName("meta_pages")] public List<PixivMetaPageDto>? MetaPages { get; set; }

    [JsonPropertyName("user")] public PixivUserDto? User { get; set; }

    [JsonPropertyName("tags")] public List<PixivTagDto>? Tags { get; set; }
}

internal sealed class PixivIllustListResponse
{
    [JsonPropertyName("illusts")] public List<PixivIllustDto>? Illusts { get; set; }

    [JsonPropertyName("next_url")] public string? NextUrl { get; set; }
}

internal sealed class PixivIllustDetailResponse
{
    [JsonPropertyName("illust")] public PixivIllustDto? Illust { get; set; }
}

internal sealed class PixivProfileDto
{
    [JsonPropertyName("total_illusts")] public int TotalIllusts { get; set; }

    [JsonPropertyName("total_follow_users")] public int TotalFollowUsers { get; set; }

    [JsonPropertyName("total_illust_bookmarks_public")] public int TotalIllustBookmarksPublic { get; set; }

    [JsonPropertyName("webpage")] public string? Webpage { get; set; }

    [JsonPropertyName("twitter_account")] public string? TwitterAccount { get; set; }
}

internal sealed class PixivUserDetailResponse
{
    [JsonPropertyName("user")] public PixivUserDto? User { get; set; }

    [JsonPropertyName("profile")] public PixivProfileDto? Profile { get; set; }
}

internal sealed class PixivUserPreviewDto
{
    [JsonPropertyName("user")] public PixivUserDto? User { get; set; }
}

internal sealed class PixivFollowingResponse
{
    [JsonPropertyName("user_previews")] public List<PixivUserPreviewDto>? UserPreviews { get; set; }

    [JsonPropertyName("next_url")] public string? NextUrl { get; set; }
}

internal sealed class PixivCommentDto
{
    [JsonPropertyName("id"), JsonConverter(typeof(FlexibleLongConverter))] public long Id { get; set; }

    [JsonPropertyName("comment")] public string? Comment { get; set; }

    [JsonPropertyName("date")] public string? Date { get; set; }

    [JsonPropertyName("user")] public PixivUserDto? User { get; set; }
}

internal sealed class PixivCommentListResponse
{
    [JsonPropertyName("comments")] public List<PixivCommentDto>? Comments { get; set; }
}

// ---------------------------------------------------------------- 网页版 street 评论
// pixiv 把作品评论迁到了 street 体系：
//   GET /ajax/street/comments/roots?id={workId}&type=illust&offset=0&limit=20
// 旧的 /v1/illust/comments 与 /ajax/illust/{id}/comments 均已下线（404）。

internal sealed class PixivStreetCommentDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("userId")] public string? UserId { get; set; }

    [JsonPropertyName("userName")] public string? UserName { get; set; }

    [JsonPropertyName("img")] public string? Img { get; set; }

    [JsonPropertyName("comment")] public string? Comment { get; set; }

    /// <summary>印章（スタンプ）评论的贴纸 ID；普通文字评论为 null/空。</summary>
    [JsonPropertyName("stampId")] public string? StampId { get; set; }

    /// <summary>JST 的 "yyyy-MM-dd HH:mm"。</summary>
    [JsonPropertyName("commentDate")] public string? CommentDate { get; set; }

    [JsonPropertyName("hasReplies")] public bool HasReplies { get; set; }
}

internal sealed class PixivStreetCommentBody
{
    [JsonPropertyName("comments")] public List<PixivStreetCommentDto>? Comments { get; set; }

    [JsonPropertyName("hasNext")] public bool HasNext { get; set; }
}

internal sealed class PixivStreetCommentRootsResponse
{
    [JsonPropertyName("error")] public bool Error { get; set; }

    [JsonPropertyName("message")] public string? Message { get; set; }

    [JsonPropertyName("body")] public PixivStreetCommentBody? Body { get; set; }
}

// ---------------------------------------------------------------- 标签候选（网页版 ajax）
// GET /ajax/search/tags/{word}?lang=zh|en
//   body.tag                     命中的标签
//   body.pixpedia.parentTag      官方「父标签」（中文词 → 日文原文标签）
//   body.breadcrumbs.successor[] 相关标签 + 该语言的译名（lang=zh 给中文，lang=en 给英文）

internal sealed class PixivTagSuggestResponse
{
    [JsonPropertyName("error")] public bool Error { get; set; }

    [JsonPropertyName("body")] public PixivTagSuggestBody? Body { get; set; }
}

internal sealed class PixivTagSuggestBody
{
    [JsonPropertyName("tag")] public string? Tag { get; set; }

    [JsonPropertyName("pixpedia")] public PixivTagPixpedia? Pixpedia { get; set; }

    [JsonPropertyName("breadcrumbs")] public PixivTagBreadcrumbs? Breadcrumbs { get; set; }
}

internal sealed class PixivTagPixpedia
{
    [JsonPropertyName("parentTag")] public string? ParentTag { get; set; }
}

internal sealed class PixivTagBreadcrumbs
{
    [JsonPropertyName("successor")] public List<PixivTagBreadcrumb>? Successor { get; set; }
}

internal sealed class PixivTagBreadcrumb
{
    [JsonPropertyName("tag")] public string? Tag { get; set; }

    [JsonPropertyName("translation")] public PixivTagTranslation? Translation { get; set; }
}

internal sealed class PixivTagTranslation
{
    [JsonPropertyName("zh")] public string? Zh { get; set; }

    [JsonPropertyName("en")] public string? En { get; set; }
}

internal sealed class PixivCommentResponse
{
    [JsonPropertyName("comment")] public PixivCommentDto? Comment { get; set; }
}
