using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pixiv_Viewer_WinUI.Models;

/// <summary>
/// Pixiv 收藏集（https://www.pixiv.net/collections/{id}）。
/// </summary>
public sealed class PixivCollection
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public PixivUser Author { get; set; } = new();

    public IReadOnlyList<Illustration> Illustrations { get; set; } = Array.Empty<Illustration>();

    public string WorksText => $"{Illustrations.Count} 件作品";
}
