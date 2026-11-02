using System;
using System.Collections.Generic;

namespace Pixiv_Viewer_WinUI.Models;

/// <summary>
/// 标签搜索候选。pixiv 的搜索必须用原文（多为日文）标签，
/// 这里把中文/英文输入映射到原始标签，并同时展示中英译名。
/// </summary>
public sealed class TagSuggestion
{
    /// <summary>实际用于搜索的标签（原文，通常为日文）。</summary>
    public string Tag { get; init; } = string.Empty;

    public string Chinese { get; init; } = string.Empty;

    public string English { get; init; } = string.Empty;

    /// <summary>副标题：中文 · English（与 Tag 相同或为空的项会被过滤）。</summary>
    public string TranslationText { get; init; } = string.Empty;

    public bool HasTranslationText => TranslationText.Length > 0;

    public static TagSuggestion Create(string tag, string? chinese, string? english)
    {
        var parts = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(chinese) && !string.Equals(chinese, tag, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(chinese);
        }

        if (!string.IsNullOrWhiteSpace(english) && !string.Equals(english, tag, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(english);
        }

        return new TagSuggestion
        {
            Tag = tag,
            Chinese = chinese ?? string.Empty,
            English = english ?? string.Empty,
            TranslationText = string.Join(" · ", parts),
        };
    }

    public override string ToString() => Tag;
}
