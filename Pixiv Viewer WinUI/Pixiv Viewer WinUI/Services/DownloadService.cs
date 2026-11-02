using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Pixiv_Viewer_WinUI.Models;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>
/// 下载实现：作品整包下载到设置里的目录（默认「图片\Pixiv」，支持并发）；
/// 单图走「另存为」对话框。列表/暂停等任务队列接口仍保留为空实现。
/// </summary>
public sealed class DownloadService : IDownloadService
{
    private static readonly string[] ImageExtensions = { ".jpg", ".png", ".webp", ".gif" };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly List<DownloadTask> _tasks = new();
    private readonly object _taskGate = new();

    private static readonly string TaskFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixivViewer",
        "downloads.json");

    private bool _tasksLoaded;

    public string DownloadFolder
    {
        get
        {
            var configured = AppServices.Settings.DownloadFolder;
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pixiv")
                : configured;
        }
    }

    public async Task<string?> DownloadIllustrationAsync(Illustration item, CancellationToken cancellationToken = default)
    {
        var urls = item.OriginalUrls.Count > 0 ? item.OriginalUrls : item.PageUrls;
        if (urls.Count == 0)
        {
            return null;
        }

        var folder = Path.Combine(DownloadFolder, SanitizeFileName($"{item.Id}_{item.Title}"));
        Directory.CreateDirectory(folder);

        // 并发下载（可在设置里调），避免一次性把反代打满
        var concurrency = Math.Clamp(AppServices.Settings.DownloadConcurrency, 1, 8);
        using var gate = new SemaphoreSlim(concurrency);

        var tasks = urls.Select(async (url, index) =>
        {
            var target = Path.Combine(folder, $"{item.Id}_p{index}{ExtensionFor(url)}");
            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                return;
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                var bytes = await _http.GetByteArrayAsync(url, cancellationToken);
                await File.WriteAllBytesAsync(target, bytes, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        // 记录一条已完成任务，下载页才能看到（之前只下载不记录，管理页永远是空的）
        long totalBytes = 0;
        try
        {
            totalBytes = new DirectoryInfo(folder).EnumerateFiles().Sum(f => f.Length);
        }
        catch (Exception)
        {
            // 统计失败不影响下载结果
        }

        lock (_taskGate)
        {
            EnsureTasksLoaded();
            _tasks.Insert(0, new DownloadTask
            {
                Id = Guid.NewGuid().ToString("N"),
                IllustrationId = item.Id,
                Title = item.Title,
                AuthorName = item.Author.Name,
                PlaceholderSeed = item.Id,
                ThumbnailUrl = item.ThumbnailUrl,
                PageCount = urls.Count,
                CurrentPage = urls.Count,
                State = DownloadState.Completed,
                Progress = 100,
                TotalBytes = totalBytes,
                BytesReceived = totalBytes,
                TargetPath = folder,
            });

            SaveTasks();
        }

        return folder;
    }

    public async Task<bool> SaveImageAsync(string url, string suggestedFileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = SanitizeFileName(suggestedFileName),
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeChoices.Add("图片", ImageExtensions.ToList());

        // WinUI 3 桌面应用必须把 picker 绑到窗口句柄，否则会抛 COM 异常。
        if (App.MainWindow is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return false;
        }

        var bytes = await _http.GetByteArrayAsync(url, cancellationToken);
        await FileIO.WriteBytesAsync(file, bytes);
        return true;
    }

    /// <summary>选择下载目录（设置页用）。</summary>
    public async Task<string?> PickDownloadFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");

        if (App.MainWindow is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public Task<IReadOnlyList<DownloadTask>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        lock (_taskGate)
        {
            EnsureTasksLoaded();
            return Task.FromResult<IReadOnlyList<DownloadTask>>(_tasks.ToList());
        }
    }

    /// <summary>首次访问时从磁盘恢复任务列表（下载记录跨进程保留）。</summary>
    private void EnsureTasksLoaded()
    {
        if (_tasksLoaded)
        {
            return;
        }

        _tasksLoaded = true;

        try
        {
            if (!File.Exists(TaskFile))
            {
                return;
            }

            var json = File.ReadAllText(TaskFile);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<DownloadTask>>(json);
            if (list is not null)
            {
                _tasks.AddRange(list);
            }
        }
        catch (Exception)
        {
            // 记录损坏就当作空列表
        }
    }

    private void SaveTasks()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TaskFile)!);
            File.WriteAllText(TaskFile, System.Text.Json.JsonSerializer.Serialize(_tasks));
        }
        catch (Exception)
        {
            // 写盘失败不影响下载
        }
    }

    public Task<bool> PauseAsync(DownloadTask task, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> ResumeAsync(DownloadTask task, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> CancelAsync(DownloadTask task, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> RetryAsync(DownloadTask task, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> DeleteAsync(DownloadTask task, bool deleteFiles = true, CancellationToken cancellationToken = default)
    {
        lock (_taskGate)
        {
            EnsureTasksLoaded();
            _tasks.Remove(task);
            SaveTasks();
        }

        if (deleteFiles)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(task.TargetPath) && Directory.Exists(task.TargetPath))
                {
                    Directory.Delete(task.TargetPath, true);
                }
            }
            catch (Exception)
            {
                // 文件被占用时只删任务记录
            }
        }

        return Task.FromResult(true);
    }

    public Task<bool> PauseAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> ResumeAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> ClearCompletedAsync(CancellationToken cancellationToken = default)
    {
        lock (_taskGate)
        {
            EnsureTasksLoaded();
            _tasks.RemoveAll(t => t.IsCompleted);
            SaveTasks();
        }

        return Task.FromResult(true);
    }

    public Task<bool> OpenDownloadFolderAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(DownloadFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", DownloadFolder) { UseShellExecute = true });
            return Task.FromResult(true);
        }
        catch (Exception)
        {
            return Task.FromResult(false);
        }
    }

    private static string ExtensionFor(string url)
    {
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        return ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) ? ext : ".jpg";
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "pixiv" : cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
