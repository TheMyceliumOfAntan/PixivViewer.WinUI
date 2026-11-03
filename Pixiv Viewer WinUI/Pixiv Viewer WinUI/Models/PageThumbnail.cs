using Pixiv_Viewer_WinUI.Helpers;

namespace Pixiv_Viewer_WinUI.Models;

/// <summary>
/// 详情页缩略图条中的一项。
/// </summary>
public sealed class PageThumbnail : ObservableObject
{
    private bool _isTranslated;

    public int Index { get; init; }

    public string Seed { get; init; } = string.Empty;

    public string? ImageUrl { get; init; }

    public string Caption => (Index + 1).ToString();

    /// <summary>该页是否已有漫画译文（缩略图角标「译」）。</summary>
    public bool IsTranslated
    {
        get => _isTranslated;
        set => SetProperty(ref _isTranslated, value);
    }
}
