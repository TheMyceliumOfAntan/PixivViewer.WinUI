using System;
using Pixiv_Viewer_WinUI.Helpers;

namespace Pixiv_Viewer_WinUI.Models;

/// <summary>
/// 作品评论。
/// </summary>
public sealed class Comment : ObservableObject
{
    private string _translatedText = string.Empty;
    private bool _isTranslated;
    private bool _isTranslating;

    public string Id { get; set; } = string.Empty;

    public PixivUser User { get; set; } = new();

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public int LikeCount { get; set; }

    /// <summary>印章（スタンプ）评论的贴纸 ID，普通文字评论为空。</summary>
    public string StampId { get; set; } = string.Empty;

    public bool HasStamp => !string.IsNullOrEmpty(StampId);

    /// <summary>印章图片地址；由服务层按反代地址（/static/...）填充，s.pximg.net 在国内不可直连。</summary>
    public string? StampUrl { get; set; }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public string TimeText => Format.RelativeTime(CreatedAt);

    public string LikeText => LikeCount > 0 ? Format.Compact(LikeCount) : string.Empty;

    /// <summary>译文（翻译完成后填充）。</summary>
    public string TranslatedText
    {
        get => _translatedText;
        set => SetProperty(ref _translatedText, value);
    }

    /// <summary>当前显示译文还是原文。</summary>
    public bool IsTranslated
    {
        get => _isTranslated;
        set
        {
            if (SetProperty(ref _isTranslated, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(TranslateButtonText));
            }
        }
    }

    public bool IsTranslating
    {
        get => _isTranslating;
        set => SetProperty(ref _isTranslating, value);
    }

    /// <summary>当前显示的文本（原文或译文）。贴纸占位符由 EmojiTextBlock 渲染成图片。</summary>
    public string DisplayText => IsTranslated && !string.IsNullOrEmpty(TranslatedText) ? TranslatedText : Text;

    public string TranslateButtonText => IsTranslated ? "显示原文" : "翻译";
}
