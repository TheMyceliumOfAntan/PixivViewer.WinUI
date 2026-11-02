namespace Pixiv_Viewer_WinUI.Models;

/// <summary>
/// 作品标签。pixiv 会在 <c>translated_name</c> 里给官方翻译（跟随请求头 Accept-Language），
/// 显示用译文，但搜索/跳转必须用原文，否则搜不到东西。
/// </summary>
public sealed class IllustTag
{
    /// <summary>原文（日文/作者填写）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>官方翻译（可能为空）。</summary>
    public string Translated { get; init; } = string.Empty;

    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translated);

    /// <summary>界面上显示的文本。</summary>
    public string Display => HasTranslation ? Translated : Name;

    public string Tooltip => HasTranslation ? $"原文：{Name}" : Name;

    /// <summary>「原文（译文）」，用于复制信息等纯文本场景。</summary>
    public override string ToString() => HasTranslation ? $"{Name}（{Translated}）" : Name;
}
