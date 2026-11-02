using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Services.Pixiv;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>
/// 更新检查的调度：启动时每天最多一次 + 设置页手动检查。
/// </summary>
internal static class UpdateCoordinator
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>启动后调用；没有新版本 / 网络失败都静默。</summary>
    public static async Task CheckOnStartupAsync()
    {
        var settings = AppServices.Settings;

        if (!settings.AutoCheckUpdate)
        {
            return;
        }

        if (settings.LastUpdateCheck is { } last && DateTimeOffset.Now - last < CheckInterval)
        {
            return;
        }

        var root = App.MainWindow?.Content?.XamlRoot;
        if (root is null)
        {
            return;
        }

        await CheckCoreAsync(manual: false, root);
    }

    /// <summary>设置页手动检查；返回是否有新版本以及失败原因。</summary>
    public static async Task<(bool HasUpdate, string? Error)> CheckManuallyAsync(XamlRoot? xamlRoot)
    {
        if (xamlRoot is null)
        {
            return (false, "窗口还没有准备好");
        }

        return await CheckCoreAsync(manual: true, xamlRoot);
    }

    private static async Task<(bool HasUpdate, string? Error)> CheckCoreAsync(bool manual, XamlRoot root)
    {
        if (!await Gate.WaitAsync(0))
        {
            return (false, "正在检查更新，请稍候");
        }

        try
        {
            string? error = null;
            var info = await UpdateService.CheckAsync(default, message => error = message);
            var settings = AppServices.Settings;

            if (info is null)
            {
                // 网络正常且确实没有新版本时才记录时间；失败的话下次启动还会重试
                if (error is null)
                {
                    settings.LastUpdateCheck = DateTimeOffset.Now;
                }

                return (false, error);
            }

            settings.LastUpdateCheck = DateTimeOffset.Now;

            if (!manual &&
                string.Equals(settings.SkippedUpdateVersion, info.Version.ToString(), StringComparison.Ordinal))
            {
                return (true, null);
            }

            var dialog = new UpdateDialog(info) { XamlRoot = root };

            try
            {
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                // 已有其它 ContentDialog 打开时 ShowAsync 会抛，等下次再提示
                PixivDiagnostics.Write($"update dialog failed: {ex.Message}");
                return (true, null);
            }

            if (dialog.Skipped)
            {
                settings.SkippedUpdateVersion = info.Version.ToString();
                PixivDiagnostics.Write($"update: 用户跳过 {info.Version}");
            }

            return (true, null);
        }
        finally
        {
            Gate.Release();
        }
    }
}
