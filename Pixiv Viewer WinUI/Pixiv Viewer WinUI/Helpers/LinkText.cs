using System;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.System;

namespace Pixiv_Viewer_WinUI.Helpers;

/// <summary>
/// 把纯文本里的 http(s) 链接渲染成可点击的超链接（Pixiv 站内链接可交给调用方做应用内跳转）。
/// </summary>
public static class LinkText
{
    private static readonly Regex UrlRegex = new(@"https?://[^\s<>""]+", RegexOptions.Compiled);

    public static void Fill(RichTextBlock target, string? text, Func<string, bool>? tryNavigate = null)
    {
        target.Blocks.Clear();

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var paragraph = new Paragraph();
        var index = 0;

        foreach (Match match in UrlRegex.Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ')', '。', '、', '!', '?');

            if (match.Index > index)
            {
                paragraph.Inlines.Add(new Run { Text = text[index..match.Index] });
            }

            var link = new Hyperlink();
            link.Inlines.Add(new Run { Text = url });
            link.Click += async (_, _) => await OpenAsync(url, tryNavigate);
            paragraph.Inlines.Add(link);

            index = match.Index + url.Length;
        }

        if (index < text.Length)
        {
            paragraph.Inlines.Add(new Run { Text = text[index..] });
        }

        target.Blocks.Add(paragraph);
    }

    private static async System.Threading.Tasks.Task OpenAsync(string url, Func<string, bool>? tryNavigate)
    {
        if (tryNavigate?.Invoke(url) == true)
        {
            return;
        }

        try
        {
            await Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception)
        {
            // 忽略无法打开的链接
        }
    }
}
