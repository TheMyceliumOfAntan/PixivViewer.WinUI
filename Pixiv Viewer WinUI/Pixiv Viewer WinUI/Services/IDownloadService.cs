using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Pixiv_Viewer_WinUI.Models;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>
/// 下载任务管理契约（暂停 / 继续 / 取消 / 重试 / 清空 / 打开目录）+ 作品下载 / 单图另存为。
/// </summary>
public interface IDownloadService
{
    /// <summary>作品下载根目录。</summary>
    string DownloadFolder { get; }

    /// <summary>下载整件作品（优先原图），返回保存目录；失败返回 null。</summary>
    Task<string?> DownloadIllustrationAsync(Illustration item, CancellationToken cancellationToken = default);

    /// <summary>弹出「另存为」对话框保存单张图片。</summary>
    Task<bool> SaveImageAsync(string url, string suggestedFileName, CancellationToken cancellationToken = default);

    /// <summary>弹出目录选择框（设置页配置下载路径用）。</summary>
    Task<string?> PickDownloadFolderAsync();

    Task<IReadOnlyList<DownloadTask>> GetTasksAsync(CancellationToken cancellationToken = default);

    Task<bool> PauseAsync(DownloadTask task, CancellationToken cancellationToken = default);

    Task<bool> ResumeAsync(DownloadTask task, CancellationToken cancellationToken = default);

    Task<bool> CancelAsync(DownloadTask task, CancellationToken cancellationToken = default);

    Task<bool> RetryAsync(DownloadTask task, CancellationToken cancellationToken = default);

    /// <summary>删除任务（默认同时删除已下载的文件）。</summary>
    Task<bool> DeleteAsync(DownloadTask task, bool deleteFiles = true, CancellationToken cancellationToken = default);

    Task<bool> PauseAllAsync(CancellationToken cancellationToken = default);

    Task<bool> ResumeAllAsync(CancellationToken cancellationToken = default);

    Task<bool> ClearCompletedAsync(CancellationToken cancellationToken = default);

    Task<bool> OpenDownloadFolderAsync(CancellationToken cancellationToken = default);
}
