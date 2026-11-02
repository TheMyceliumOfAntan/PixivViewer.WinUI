using System;
using System.Collections.Generic;

namespace Pixiv_Viewer_WinUI.Models;

/// <summary>
/// 分页结果。<see cref="NextOffset"/> 用于「加载更多」，由服务层计算（已跳过被过滤的条目）。
/// </summary>
public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    public int Offset { get; init; }

    public int NextOffset { get; init; }

    /// <summary>游标式分页（收藏用 max_bookmark_id）。</summary>
    public string? NextCursor { get; init; }

    public bool HasMore { get; init; }

    public static PagedResult<T> Empty { get; } = new();
}
