using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixiv_Viewer_WinUI.Services;

namespace Pixiv_Viewer_WinUI.Controls;

/// <summary>
/// Image / PersonPicture 的附加属性：给一个 URL，自动走 <see cref="ImageCache"/>（磁盘 + 内存缓存）。
/// 详情页用 <c>HighQualityUrl</c> 做渐进加载：先出 large，再换原图。
/// </summary>
public static class CachedImage
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url",
        typeof(string),
        typeof(CachedImage),
        new PropertyMetadata(null, OnUrlChanged));

    /// <summary>解码宽度（逻辑像素）。列表用小值，详情页用大值。</summary>
    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth",
        typeof(int),
        typeof(CachedImage),
        new PropertyMetadata(480));

    /// <summary>高清地址：先用 <see cref="UrlProperty"/> 快速显示，高清图加载完再替换。</summary>
    public static readonly DependencyProperty HighQualityUrlProperty = DependencyProperty.RegisterAttached(
        "HighQualityUrl",
        typeof(string),
        typeof(CachedImage),
        new PropertyMetadata(null, OnHighQualityUrlChanged));

    public static readonly DependencyProperty ProfileUrlProperty = DependencyProperty.RegisterAttached(
        "ProfileUrl",
        typeof(string),
        typeof(CachedImage),
        new PropertyMetadata(null, OnProfileUrlChanged));

    public static string? GetUrl(DependencyObject element) => (string?)element.GetValue(UrlProperty);

    public static void SetUrl(DependencyObject element, string? value) => element.SetValue(UrlProperty, value);

    public static int GetDecodeWidth(DependencyObject element) => (int)element.GetValue(DecodeWidthProperty);

    public static void SetDecodeWidth(DependencyObject element, int value) => element.SetValue(DecodeWidthProperty, value);

    public static string? GetHighQualityUrl(DependencyObject element) => (string?)element.GetValue(HighQualityUrlProperty);

    public static void SetHighQualityUrl(DependencyObject element, string? value) => element.SetValue(HighQualityUrlProperty, value);

    public static string? GetProfileUrl(DependencyObject element) => (string?)element.GetValue(ProfileUrlProperty);

    public static void SetProfileUrl(DependencyObject element, string? value) => element.SetValue(ProfileUrlProperty, value);

    /// <summary>图片真正挂上 Source 后触发（ImageOpened 在某些解码路径下不触发，页面用它关闭骨架/loading）。</summary>
    public static event EventHandler<Image>? SourceApplied;

    private static void OnUrlChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is Image image)
        {
            image.Source = null;
            image.Opacity = 0;
            _ = LoadAsync(image);
        }
    }

    private static void OnHighQualityUrlChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is Image image)
        {
            _ = LoadAsync(image);
        }
    }

    /// <summary>
    /// 先加载预览图（快），再后台加载高清图替换。
    /// 两次调用可能并发，这里用「已显示的位图宽度」做保护，避免小图覆盖大图。
    /// </summary>
    private static async Task LoadAsync(Image image)
    {
        var url = GetUrl(image);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var preview = await ImageCache.GetAsync(url, GetDecodeWidth(image));

        // 加载期间 URL 可能已经变了（列表复用 / 翻页），避免把旧图贴到新卡片上
        if (GetUrl(image) != url)
        {
            return;
        }

        if (preview is not null && !IsWiderThanCurrent(image, preview))
        {
            image.Source = preview;
            image.Opacity = 1;
            SourceApplied?.Invoke(null, image);
        }

        var highQualityUrl = GetHighQualityUrl(image);
        if (string.IsNullOrWhiteSpace(highQualityUrl) || highQualityUrl == url)
        {
            return;
        }

        var highQuality = await ImageCache.GetAsync(highQualityUrl, Math.Max(GetDecodeWidth(image) * 2, 1600));

        if (highQuality is null || GetUrl(image) != url || GetHighQualityUrl(image) != highQualityUrl)
        {
            return;
        }

        image.Source = highQuality;
        image.Opacity = 1;
        SourceApplied?.Invoke(null, image);
    }

    private static bool IsWiderThanCurrent(Image image, BitmapImage candidate)
    {
        try
        {
            return image.Source is BitmapImage current && current.PixelWidth >= candidate.PixelWidth;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async void OnProfileUrlChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not PersonPicture picture)
        {
            return;
        }

        var url = args.NewValue as string;
        picture.ProfilePicture = null;

        var bitmap = await ImageCache.GetAsync(url, 96);
        if (GetProfileUrl(picture) == url)
        {
            picture.ProfilePicture = bitmap;
        }
    }
}
