using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Pixiv_Viewer_WinUI.Helpers;
using Pixiv_Viewer_WinUI.Services;

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
        var image = new Image
        {
            Width = 20,
            Height = 20,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _ = LoadStickerAsync(url, image);
        return image;
    }

    /// <summary>
    /// 贴纸统一走 <see cref="ImageCache"/>：它会用当前接口方式的 HttpClient
    /// （域前置时自动连固定 IP + 补 Referer），直接 new BitmapImage(url) 会被 SNI 拦掉。
    /// </summary>
    private static async Task LoadStickerAsync(string url, Image image)
    {
        try
        {
            var mapped = AppServices.MapStaticUrl(url);
            var bitmap = await ImageCache.GetAsync(mapped, 48);

            if (bitmap is not null)
            {
                image.Source = bitmap;
            }
        }
        catch (Exception)
        {
            // 单张图失败就留空，不影响其它内容
        }
    }
}
