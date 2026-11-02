using System;
using System.Threading.Tasks;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Models;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>
/// 卡片右键菜单的统一处理（收藏 / 喜欢 / 下载），各列表页共用。
/// </summary>
public static class IllustrationActions
{
    public static async Task HandleAsync(IllustrationGrid grid, CardActionEventArgs e)
    {
        switch (e.Action)
        {
            case CardAction.Bookmark:
            case CardAction.Like:
                await ToggleBookmarkAsync(grid, e.Item, e.Action == CardAction.Like);
                break;

            case CardAction.Download:
                await DownloadAsync(grid, e.Item);
                break;
        }
    }

    public static async Task ToggleBookmarkAsync(IllustrationGrid grid, Illustration item, bool isPrivate)
    {
        var value = !item.IsBookmarked;
        bool ok;

        try
        {
            ok = await AppServices.Pixiv.SetBookmarkAsync(item.Id, value, isPrivate);
        }
        catch (Exception)
        {
            ok = false;
        }

        if (ok)
        {
            item.IsBookmarked = value;
            item.BookmarkIsPrivate = value && isPrivate;
        }

        grid.ShowToast(ok
            ? value
                ? isPrivate ? "已加入私密收藏（喜欢）" : "已收藏"
                : "已取消收藏"
            : "操作失败：请检查登录状态或网络");
    }

    public static async Task DownloadAsync(IllustrationGrid grid, Illustration item)
    {
        grid.ShowToast("开始下载…");

        try
        {
            var folder = await AppServices.Downloads.DownloadIllustrationAsync(item);
            grid.ShowToast(folder is null ? "下载失败：没有可用的图片地址" : $"已下载到 {folder}");
        }
        catch (Exception ex)
        {
            grid.ShowToast($"下载失败：{ex.Message}");
        }
    }
}
