using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixiv_Viewer_WinUI.Helpers;
using Pixiv_Viewer_WinUI.Models;
using Pixiv_Viewer_WinUI.Services;

namespace Pixiv_Viewer_WinUI.Views;

/// <summary>
/// 作品详情页的视图模型：负责加载详情 / 评论、翻页、收藏与关注状态。
/// </summary>
public sealed class IllustrationDetailViewModel : ObservableObject
{
    private Illustration? _item;
    private int _currentIndex;
    private bool _isBookmarked;
    private bool _isFollowed;
    private bool _isDescriptionExpanded;
    private bool _isLoading;
    private string _newCommentText = string.Empty;
    private string? _currentImageUrl;
    private Brush? _placeholderBrush;
    private bool _bookmarkIsPrivate;
    private bool _commentsUnavailable;
    private bool _isDescriptionTranslated;
    private bool _isTranslatingDescription;
    private string _translatedDescription = string.Empty;

    public Illustration? Item => _item;

    public bool HasItem => _item is not null;

    public string Id => _item?.Id ?? string.Empty;

    public string Title => _item?.Title ?? "未选择作品";

    public string Description => string.IsNullOrWhiteSpace(_item?.Description)
        ? "这位作者还没有填写作品说明。"
        : _item!.Description;

    public string AuthorName => _item?.Author.Name ?? "—";

    public string AuthorAccount => _item?.Author.AccountText ?? string.Empty;

    public string? AuthorAvatarUrl => _item?.Author.AvatarUrl;

    public IReadOnlyList<IllustTag> Tags => _item?.Tags ?? Array.Empty<IllustTag>();

    public bool HasTags => Tags.Count > 0;

    public string LikeText => _item?.LikeText ?? "0";

    public string BookmarkText => _item?.BookmarkText ?? "0";

    public string ViewText => _item?.ViewText ?? "0";

    public string CreatedText => _item is null ? string.Empty : $"投稿于 {_item.CreatedText}";

    public int PageCount => Math.Max(1, _item?.PageCount ?? 1);

    public bool HasMultiplePages => PageCount > 1;

    public string PageCountText => $"{PageCount} 页";

    public int CurrentIndex => _currentIndex;

    public string PageIndicator => $"{_currentIndex + 1} / {PageCount}";

    public bool CanGoPrevious => _currentIndex > 0;

    public bool CanGoNext => _currentIndex < PageCount - 1;

    public string? CurrentImageUrl
    {
        get => _currentImageUrl;
        private set
        {
            if (SetProperty(ref _currentImageUrl, value))
            {
                OnPropertyChanged(nameof(HasImage));
                OnPropertyChanged(nameof(CurrentOriginalUrl));
            }
        }
    }

    /// <summary>当前页原图地址（「另存为」/ 高清加载用）。</summary>
    public string? CurrentOriginalUrl
    {
        get
        {
            if (_item is null)
            {
                return null;
            }

            return _item.OriginalUrls.Count > _currentIndex
                ? _item.OriginalUrls[_currentIndex]
                : _currentImageUrl;
        }
    }

    /// <summary>当前页的 Pixiv 源链接（i.pximg.net，未走反代，仅展示）。</summary>
    public string CurrentSourceUrl
        => _item is not null && _item.SourceUrls.Count > _currentIndex
            ? _item.SourceUrls[_currentIndex]
            : string.Empty;

    public bool HasImage => !string.IsNullOrWhiteSpace(_currentImageUrl);

    /// <summary>当前作品被设置屏蔽（R-18 / R-18G / AI），图片区显示占位。</summary>
    public bool IsBlocked { get; private set; }

    public string BlockReason { get; private set; } = string.Empty;

    /// <summary>未接入图片时的渐变占位（同一作品同一页颜色固定）。</summary>
    public Brush CurrentPlaceholderBrush => _placeholderBrush ??= PlaceholderPalette.GetBrush($"{Id}-{_currentIndex}");

    public List<PageThumbnail> Pages { get; } = new();

    public ObservableCollection<Comment> Comments { get; } = new();

    public string CommentsHeader => Comments.Count > 0
        ? $"评论 ({Comments.Count})"
        : "评论";

    public bool HasComments => Comments.Count > 0;

    /// <summary>评论接口不可用（Pixiv 已关闭第三方评论读取接口）。</summary>
    public bool CommentsUnavailable
    {
        get => _commentsUnavailable;
        private set
        {
            if (SetProperty(ref _commentsUnavailable, value))
            {
                OnPropertiesChanged(nameof(CommentsEmptyText), nameof(ShowCommentInput));
            }
        }
    }

    public string CommentsEmptyText => CommentsUnavailable
        ? "Pixiv 已关闭第三方评论读取接口，暂时无法获取评论。"
        : "还没有评论，来说点什么吧。";

    public bool ShowCommentInput => !CommentsUnavailable;

    /// <summary>作品说明是否正在显示译文。</summary>
    public bool IsDescriptionTranslated
    {
        get => _isDescriptionTranslated;
        private set
        {
            if (SetProperty(ref _isDescriptionTranslated, value))
            {
                OnPropertiesChanged(nameof(DescriptionTranslateButtonText), nameof(DescriptionDisplayText));
            }
        }
    }

    public bool IsTranslatingDescription
    {
        get => _isTranslatingDescription;
        private set
        {
            if (SetProperty(ref _isTranslatingDescription, value))
            {
                OnPropertyChanged(nameof(DescriptionTranslateButtonText));
            }
        }
    }

    public string TranslatedDescription
    {
        get => _translatedDescription;
        private set
        {
            if (SetProperty(ref _translatedDescription, value))
            {
                OnPropertyChanged(nameof(DescriptionDisplayText));
            }
        }
    }

    /// <summary>当前应显示的说明文本（原文或译文）。</summary>
    public string DescriptionDisplayText => IsDescriptionTranslated && TranslatedDescription.Length > 0
        ? TranslatedDescription
        : Description;

    public string DescriptionTranslateButtonText => IsTranslatingDescription
        ? "翻译中…"
        : IsDescriptionTranslated
            ? "显示原文"
            : "翻译";

    public bool CanExpandDescription => Description.Length > 90;

    public int DescriptionMaxLines => _isDescriptionExpanded ? 0 : 4;

    public string DescriptionToggleText => _isDescriptionExpanded ? "收起" : "展开";

    public bool IsBookmarked
    {
        get => _isBookmarked;
        set
        {
            if (SetProperty(ref _isBookmarked, value))
            {
                OnPropertiesChanged(
                    nameof(BookmarkActionText),
                    nameof(BookmarkMenuText));
            }
        }
    }

    /// <summary>收藏是否为私密收藏（决定右键/下拉菜单文案）。</summary>
    public bool BookmarkIsPrivate
    {
        get => _bookmarkIsPrivate;
        set
        {
            if (SetProperty(ref _bookmarkIsPrivate, value))
            {
                OnPropertyChanged(nameof(BookmarkMenuText));
            }
        }
    }

    /// <summary>主按钮文案。</summary>
    public string BookmarkActionText => IsBookmarked ? "已收藏" : "收藏";

    /// <summary>下拉菜单里的私密收藏文案。</summary>
    public string BookmarkMenuText => IsBookmarked && BookmarkIsPrivate ? "取消私密收藏" : "私密收藏";

    public bool IsFollowed
    {
        get => _isFollowed;
        set => SetProperty(ref _isFollowed, value);
    }

    public bool IsDescriptionExpanded
    {
        get => _isDescriptionExpanded;
        set
        {
            if (SetProperty(ref _isDescriptionExpanded, value))
            {
                OnPropertiesChanged(nameof(DescriptionMaxLines), nameof(DescriptionToggleText));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string NewCommentText
    {
        get => _newCommentText;
        set => SetProperty(ref _newCommentText, value);
    }

    public async Task LoadAsync(Illustration? item)
    {
        IsLoading = true;
        _item = item;

        if (item is not null)
        {
            var fresh = await AppServices.Pixiv.GetIllustrationAsync(item.Id);
            if (fresh is not null)
            {
                _item = fresh;
            }
        }

        _currentIndex = 0;
        _isBookmarked = _item?.IsBookmarked ?? false;
        _bookmarkIsPrivate = false;
        _isFollowed = _item?.Author.IsFollowed ?? false;
        _isDescriptionExpanded = false;
        _placeholderBrush = null;
        _isDescriptionTranslated = false;
        _translatedDescription = string.Empty;
        _commentsUnavailable = false;

        BuildPages();
        UpdateCurrentImage();
        UpdateBlockState();
        await LoadCommentsAsync();

        NotifyAll();
        IsLoading = false;
    }

    public void GoToPage(int index)
    {
        var clamped = Math.Clamp(index, 0, PageCount - 1);
        if (clamped == _currentIndex)
        {
            return;
        }

        _currentIndex = clamped;
        _placeholderBrush = null;
        UpdateCurrentImage();
        OnPropertiesChanged(nameof(CurrentIndex), nameof(PageIndicator), nameof(CanGoPrevious),
            nameof(CanGoNext), nameof(CurrentPlaceholderBrush));
    }

    public void NextPage() => GoToPage(_currentIndex + 1);

    public void PreviousPage() => GoToPage(_currentIndex - 1);

    public void ToggleDescription() => IsDescriptionExpanded = !IsDescriptionExpanded;

    public async Task<bool> ToggleBookmarkAsync(bool isPrivate = false)
    {
        var value = !IsBookmarked;
        var ok = await AppServices.Pixiv.SetBookmarkAsync(Id, value, isPrivate);
        if (ok)
        {
            IsBookmarked = value;
            BookmarkIsPrivate = value && isPrivate;
            if (_item is not null)
            {
                _item.IsBookmarked = value;
                _item.BookmarkIsPrivate = BookmarkIsPrivate;
            }
        }

        return ok;
    }

    /// <summary>在「原文 / 译文」之间切换作品说明，首次切换时才请求翻译。返回 false 表示翻译失败。</summary>
    public async Task<bool> ToggleDescriptionTranslationAsync()
    {
        if (IsDescriptionTranslated)
        {
            IsDescriptionTranslated = false;
            return true;
        }

        if (TranslatedDescription.Length == 0)
        {
            IsTranslatingDescription = true;
            try
            {
                var translated = await TranslationService.TranslateAsync(Description);
                TranslatedDescription = translated ?? string.Empty;
            }
            finally
            {
                IsTranslatingDescription = false;
            }

            if (TranslatedDescription.Length == 0)
            {
                return false;
            }
        }

        IsDescriptionTranslated = true;
        return true;
    }

    /// <summary>翻译单条评论（再点一次显示原文）。返回 false 表示翻译失败。</summary>
    public async Task<bool> ToggleCommentTranslationAsync(Comment comment)
    {
        if (comment is null || string.IsNullOrWhiteSpace(comment.Text))
        {
            return false;
        }

        if (comment.IsTranslated)
        {
            comment.IsTranslated = false;
            return true;
        }

        if (string.IsNullOrEmpty(comment.TranslatedText))
        {
            comment.IsTranslating = true;
            try
            {
                comment.TranslatedText = await TranslationService.TranslateAsync(PixivEmoji.ToPlainEmoji(comment.Text)) ?? string.Empty;
            }
            finally
            {
                comment.IsTranslating = false;
            }
        }

        if (string.IsNullOrEmpty(comment.TranslatedText))
        {
            return false;
        }

        comment.IsTranslated = true;
        return true;
    }

    public async Task<bool> ToggleFollowAsync()
    {
        var value = !IsFollowed;
        var ok = await AppServices.Pixiv.SetFollowAsync(_item?.Author.Id ?? string.Empty, value);
        if (ok)
        {
            IsFollowed = value;
        }

        return ok;
    }

    public async Task<bool> AddCommentAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var posted = await AppServices.Pixiv.PostCommentAsync(Id, text);
        if (posted is null && AppServices.Settings.UseSampleData)
        {
            posted = new Comment
            {
                Id = Guid.NewGuid().ToString("N"),
                User = SampleData.CurrentUser,
                Text = text,
                CreatedAt = DateTimeOffset.Now,
            };
        }

        if (posted is null)
        {
            return false;
        }

        Comments.Insert(0, posted);
        NewCommentText = string.Empty;
        OnPropertiesChanged(nameof(CommentsHeader), nameof(HasComments));
        return true;
    }

    /// <summary>发一张贴图评论。</summary>
    public async Task<bool> AddStampCommentAsync(string stampId)
    {
        if (string.IsNullOrWhiteSpace(stampId))
        {
            return false;
        }

        var posted = await AppServices.Pixiv.PostStampAsync(Id, stampId);
        if (posted is null)
        {
            return false;
        }

        Comments.Insert(0, posted);
        NewCommentText = string.Empty;
        OnPropertiesChanged(nameof(CommentsHeader), nameof(HasComments));
        return true;
    }

    private void BuildPages()
    {
        Pages.Clear();
        for (var i = 0; i < PageCount; i++)
        {
            Pages.Add(new PageThumbnail
            {
                Index = i,
                Seed = $"{Id}-{i}",
                ImageUrl = _item is not null && _item.PreviewUrls.Count > i
                    ? _item.PreviewUrls[i]
                    : _item is not null && _item.PageUrls.Count > i ? _item.PageUrls[i] : null,
            });
        }
    }

    private void UpdateCurrentImage()
    {
        CurrentImageUrl = _item is not null && _item.PageUrls.Count > _currentIndex
            ? _item.PageUrls[_currentIndex]
            : null;

        OnPropertyChanged(nameof(CurrentSourceUrl));
    }

    /// <summary>按设置判断当前作品是否应该被屏蔽（从链接/收藏直接进入时也会走到这里）。</summary>
    private void UpdateBlockState()
    {
        string? reason = null;

        if (_item is not null)
        {
            var settings = AppServices.Settings;

            if (_item.XRestrict == 1 && !settings.ShowR18)
            {
                reason = "设置里关闭了 R-18 作品的显示";
            }
            else if (_item.XRestrict == 2 && !settings.ShowR18G)
            {
                reason = "设置里关闭了 R-18G 作品的显示";
            }
            else if (_item.IsAi && !settings.ShowAi)
            {
                reason = "设置里关闭了 AI 生成作品的显示";
            }
        }

        BlockReason = reason ?? string.Empty;
        IsBlocked = reason is not null;
        OnPropertiesChanged(nameof(IsBlocked), nameof(BlockReason));
    }

    private async Task LoadCommentsAsync()
    {
        Comments.Clear();
        CommentsUnavailable = false;

        if (_item is null)
        {
            return;
        }

        var comments = await AppServices.Pixiv.GetCommentsAsync(_item.Id);

        if (comments is null)
        {
            // 接口不可用（Pixiv 已对第三方关闭评论读取）：不再假装「还没有评论」
            CommentsUnavailable = true;
            OnPropertiesChanged(nameof(CommentsHeader), nameof(HasComments));
            return;
        }

        IReadOnlyList<Comment> list = comments;

        if (list.Count == 0 && AppServices.Settings.UseSampleData)
        {
            list = SampleData.Comments;
        }

        foreach (var comment in list)
        {
            Comments.Add(comment);
        }

        OnPropertiesChanged(nameof(CommentsHeader), nameof(HasComments));
    }

    private void NotifyAll()
    {
        OnPropertiesChanged(
            nameof(Item), nameof(HasItem), nameof(Id), nameof(Title), nameof(Description),
            nameof(AuthorName), nameof(AuthorAccount), nameof(Tags), nameof(HasTags),
            nameof(LikeText), nameof(BookmarkText), nameof(ViewText), nameof(CreatedText),
            nameof(PageCount), nameof(HasMultiplePages), nameof(PageCountText),
            nameof(CurrentIndex), nameof(PageIndicator), nameof(CanGoPrevious), nameof(CanGoNext),
            nameof(CurrentImageUrl), nameof(HasImage), nameof(CurrentPlaceholderBrush),
            nameof(Comments), nameof(CommentsHeader), nameof(HasComments),
            nameof(CommentsUnavailable), nameof(CommentsEmptyText), nameof(ShowCommentInput),
            nameof(CanExpandDescription), nameof(DescriptionMaxLines), nameof(DescriptionToggleText),
            nameof(IsDescriptionTranslated), nameof(DescriptionDisplayText),
            nameof(DescriptionTranslateButtonText), nameof(IsTranslatingDescription),
            nameof(BookmarkActionText),
            nameof(BookmarkMenuText),
            nameof(IsBookmarked), nameof(IsFollowed), nameof(IsLoading));
    }
}
