using System;

namespace Pixiv_Viewer_WinUI.Models;

/// <summary>
/// 详情页缩略图条中的一项。
/// </summary>
public sealed class PageThumbnail
{
    public int Index { get; init; }

    public string Seed { get; init; } = string.Empty;

    public string? ImageUrl { get; init; }

    public string Caption => (Index + 1).ToString();
}
