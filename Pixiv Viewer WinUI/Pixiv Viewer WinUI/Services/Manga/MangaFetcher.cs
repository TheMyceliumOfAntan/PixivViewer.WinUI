using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pixiv_Viewer_WinUI.Services.Pixiv;

namespace Pixiv_Viewer_WinUI.Services.Manga;

/// <summary>
/// 开发自检用：把指定作品的每一页原图抓到本地目录，方便离线跑流水线。
/// 用法：程序 --manga-fetch &lt;作品ID&gt; &lt;输出目录&gt;
/// </summary>
internal static class MangaFetcher
{
    public static async Task<string> FetchAsync(string illustId, string outputDirectory, Action<string> log)
    {
        Directory.CreateDirectory(outputDirectory);

        if (!await AppServices.Pixiv.SignInAsync())
        {
            return "登录失败（没有可用的登录态）";
        }

        var illustration = await AppServices.Pixiv.GetIllustrationAsync(illustId);
        if (illustration is null)
        {
            return "拿不到作品信息";
        }

        log($"title={illustration.Title} pages={illustration.PageCount} originalUrls={illustration.OriginalUrls.Count} pageUrls={illustration.PageUrls.Count}");

        var saved = new List<string>();
        var total = Math.Max(1, illustration.PageCount);

        for (var i = 0; i < total; i++)
        {
            var url = i < illustration.OriginalUrls.Count && !string.IsNullOrWhiteSpace(illustration.OriginalUrls[i])
                ? illustration.OriginalUrls[i]
                : i < illustration.PageUrls.Count ? illustration.PageUrls[i] : null;

            if (string.IsNullOrWhiteSpace(url))
            {
                log($"page {i + 1}: no url");
                continue;
            }

            var bytes = await ImageCache.GetBytesAsync(url, CancellationToken.None);
            if (bytes is null || bytes.Length == 0)
            {
                log($"page {i + 1}: download failed ({url})");
                continue;
            }

            var path = Path.Combine(outputDirectory, $"page{i}.img");
            await File.WriteAllBytesAsync(path, bytes);
            saved.Add(path);
            log($"page {i + 1}: {bytes.Length / 1024} KB -> {Path.GetFileName(path)}");
        }

        return saved.Count == 0 ? "没有抓到任何页面" : $"ok pages={saved.Count}";
    }
}
