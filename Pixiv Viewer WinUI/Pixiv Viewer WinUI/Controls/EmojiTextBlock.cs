using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixiv_Viewer_WinUI.Helpers;

namespace Pixiv_Viewer_WinUI.Controls;

/// <summary>
/// 支持 pixiv 贴纸表情的文本控件：把 <c>(heart)</c> 这类占位符渲染成官方贴纸图片，
/// 和 pixiv 网页显示一致（纯文本控件只能显示成字面量，容易产生歧义）。
/// </summary>
public sealed class EmojiTextBlock : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(EmojiTextBlock),
        new PropertyMetadata(string.Empty, OnTextChanged));

    /// <summary>贴纸图片缓存：同一张图在几十条评论里复用同一个 BitmapImage。</summary>
    private static readonly Dictionary<string, BitmapImage> ImageCache = new(StringComparer.Ordinal);

    private readonly RichTextBlock _host = new()
    {
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    public EmojiTextBlock()
    {
        Content = _host;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((EmojiTextBlock)sender).Render(args.NewValue as string);

    private void Render(string? text)
    {
        _host.Blocks.Clear();

        var paragraph = new Paragraph();

        foreach (var (segment, imageUrl) in PixivEmoji.Split(text))
        {
            if (imageUrl is null)
            {
                if (segment.Length > 0)
                {
                    paragraph.Inlines.Add(new Run { Text = segment });
                }
            }
            else
            {
                paragraph.Inlines.Add(new InlineUIContainer { Child = CreateSticker(imageUrl) });
            }
        }

        _host.Blocks.Add(paragraph);
    }

    private static Image CreateSticker(string url)
    {
        if (!ImageCache.TryGetValue(url, out var bitmap))
        {
            bitmap = new BitmapImage(new Uri(url)) { DecodePixelWidth = 48 };
            ImageCache[url] = bitmap;
        }

        return new Image
        {
            Source = bitmap,
            Width = 20,
            Height = 20,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
