using System;
using System.Collections.Generic;

namespace Pixiv_Viewer_WinUI.Models;

/// <summary>一条版本更新记录。</summary>
public sealed class ChangelogEntry
{
    public string Version { get; init; } = string.Empty;

    public string Date { get; init; } = string.Empty;

    public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();

    /// <summary>是否为当前版本（显示「当前版本」徽章）。</summary>
    public bool IsLatest { get; init; }

    /// <summary>是否在条目下方画分割线（最后一条不画）。</summary>
    public bool ShowDivider { get; init; } = true;
}
