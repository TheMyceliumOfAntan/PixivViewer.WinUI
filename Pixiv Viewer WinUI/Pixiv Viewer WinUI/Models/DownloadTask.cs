using Pixiv_Viewer_WinUI.Helpers;

namespace Pixiv_Viewer_WinUI.Models;

public enum DownloadState
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// 一条下载任务。UI 只读取状态与进度，真正的下载由 IDownloadService 负责。
/// </summary>
public sealed class DownloadTask : ObservableObject
{
    private DownloadState _state;
    private double _progress;
    private long _bytesReceived;
    private long _totalBytes;
    private double _bytesPerSecond;
    private int _currentPage = 1;
    private string? _errorMessage;

    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string AuthorName { get; set; } = string.Empty;

    public string IllustrationId { get; set; } = string.Empty;

    /// <summary>缩略图 URL；为空时用渐变占位。</summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>渐变占位种子（一般用作品 Id）。</summary>
    public string PlaceholderSeed { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public int PageCount { get; set; } = 1;

    public DownloadState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertiesChanged(nameof(StateText), nameof(CanPause), nameof(CanResume),
                    nameof(CanCancel), nameof(CanRetry), nameof(CanOpen), nameof(IsActive),
                    nameof(IsCompleted), nameof(IsFailed), nameof(SpeedText));
            }
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (SetProperty(ref _progress, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        set
        {
            if (SetProperty(ref _bytesReceived, value))
            {
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetProperty(ref _totalBytes, value))
            {
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public double BytesPerSecond
    {
        get => _bytesPerSecond;
        set
        {
            if (SetProperty(ref _bytesPerSecond, value))
            {
                OnPropertyChanged(nameof(SpeedText));
            }
        }
    }

    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(PageText));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public bool IsActive => State is DownloadState.Downloading or DownloadState.Queued;

    public bool IsCompleted => State == DownloadState.Completed;

    public bool IsFailed => State == DownloadState.Failed;

    public bool CanPause => State is DownloadState.Downloading or DownloadState.Queued;

    public bool CanResume => State == DownloadState.Paused;

    public bool CanCancel => State != DownloadState.Completed;

    public bool CanRetry => State == DownloadState.Failed;

    public bool CanOpen => State == DownloadState.Completed;

    public string StateText => State switch
    {
        DownloadState.Queued => "排队中",
        DownloadState.Downloading => "下载中",
        DownloadState.Paused => "已暂停",
        DownloadState.Completed => "已完成",
        DownloadState.Failed => string.IsNullOrEmpty(ErrorMessage) ? "失败" : $"失败 · {ErrorMessage}",
        _ => string.Empty,
    };

    public string ProgressText => $"{Progress:0}%";

    public string SizeText => TotalBytes > 0
        ? $"{Format.Bytes(BytesReceived)} / {Format.Bytes(TotalBytes)}"
        : Format.Bytes(BytesReceived);

    public string SpeedText => State == DownloadState.Downloading && BytesPerSecond > 0
        ? $"{Format.Bytes((long)BytesPerSecond)}/s"
        : string.Empty;

    public string PageText => PageCount > 1 ? $"第 {CurrentPage}/{PageCount} 页" : string.Empty;
}
