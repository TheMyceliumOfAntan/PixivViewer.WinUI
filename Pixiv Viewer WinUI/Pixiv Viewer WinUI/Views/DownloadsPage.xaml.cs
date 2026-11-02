using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Models;
using Pixiv_Viewer_WinUI.Services;

namespace Pixiv_Viewer_WinUI.Views;

public sealed partial class DownloadsPage : Page
{
    private readonly List<DownloadTask> _all = new();
    private string _filter = "all";

    public DownloadsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        LoadingRing.IsActive = true;

        try
        {
            var tasks = await AppServices.Downloads.GetTasksAsync();
            _all.Clear();

            if (tasks.Count == 0 && AppServices.Settings.UseSampleData)
            {
                _all.AddRange(SampleData.DownloadTasks);
            }
            else
            {
                _all.AddRange(tasks);
            }

            UpdateSummary();
            ApplyFilter();
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void UpdateSummary()
    {
        var active = _all.Count(t => t.IsActive);
        var completed = _all.Count(t => t.IsCompleted);
        var failed = _all.Count(t => t.IsFailed);

        SummaryText.Text = _all.Count == 0
            ? "暂无任务"
            : $"{active} 进行中 · {completed} 已完成 · {failed} 失败";

        OverallBar.Value = _all.Count == 0 ? 0 : _all.Average(t => t.Progress);
    }

    private void ApplyFilter()
    {
        IEnumerable<DownloadTask> items = _filter switch
        {
            "active" => _all.Where(t => t.IsActive || t.State == DownloadState.Paused),
            "completed" => _all.Where(t => t.IsCompleted),
            "failed" => _all.Where(t => t.IsFailed),
            _ => _all,
        };

        var list = items.ToList();
        TaskList.ItemsSource = list;

        EmptyTitle.Text = _filter switch
        {
            "active" => "没有进行中的任务",
            "completed" => "还没有完成的任务",
            "failed" => "没有失败的任务",
            _ => "还没有下载任务",
        };
        EmptyMessage.Text = _filter == "all"
            ? "在作品详情页点击「下载」，任务会出现在这里，可随时暂停或继续。"
            : "切换上方的筛选条件看看其他任务。";

        EmptyState.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Subtitle.Text = $"共 {_all.Count} 个任务 · 保存到 {AppServices.Downloads.DownloadFolder}";
        FolderText.Text = $"保存到 {AppServices.Downloads.DownloadFolder}";
    }

    private void OnFilterChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string filter)
        {
            _filter = filter;
            ApplyFilter();
        }
    }

    private async void OnPauseClick(object sender, RoutedEventArgs e) => await ToggleAsync(sender, isPause: true);

    private async void OnResumeClick(object sender, RoutedEventArgs e) => await ToggleAsync(sender, isPause: false);

    private async Task ToggleAsync(object sender, bool isPause)
    {
        if ((sender as FrameworkElement)?.Tag is not DownloadTask task)
        {
            return;
        }

        if (isPause)
        {
            await AppServices.Downloads.PauseAsync(task);
            task.State = DownloadState.Paused;
        }
        else
        {
            await AppServices.Downloads.ResumeAsync(task);
            task.State = DownloadState.Downloading;
        }

        UpdateSummary();
        ApplyFilter();
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadTask task)
        {
            await AppServices.Downloads.RetryAsync(task);
            task.State = DownloadState.Queued;
            task.ErrorMessage = null;
            UpdateSummary();
            ApplyFilter();
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DownloadTask task)
        {
            return;
        }

        await AppServices.Downloads.DeleteAsync(task);
        _all.Remove(task);
        UpdateSummary();
        ApplyFilter();
        Subtitle.Text = $"已删除任务和文件：{task.Title}";
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadTask task)
        {
            await AppServices.Downloads.CancelAsync(task);
            _all.Remove(task);
            UpdateSummary();
            ApplyFilter();
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadTask task)
        {
            Subtitle.Text = $"待接入：打开 {task.TargetPath}";
        }
    }

    private async void OnPauseAllClick(object sender, RoutedEventArgs e)
    {
        await AppServices.Downloads.PauseAllAsync();
        foreach (var task in _all.Where(t => t.IsActive))
        {
            task.State = DownloadState.Paused;
        }

        UpdateSummary();
        ApplyFilter();
    }

    private async void OnResumeAllClick(object sender, RoutedEventArgs e)
    {
        await AppServices.Downloads.ResumeAllAsync();
        foreach (var task in _all.Where(t => t.State == DownloadState.Paused))
        {
            task.State = DownloadState.Downloading;
        }

        UpdateSummary();
        ApplyFilter();
    }

    private async void OnClearCompletedClick(object sender, RoutedEventArgs e)
    {
        await AppServices.Downloads.ClearCompletedAsync();
        _all.RemoveAll(t => t.IsCompleted);
        UpdateSummary();
        ApplyFilter();
    }

    private async void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var ok = await AppServices.Downloads.OpenDownloadFolderAsync();
        if (!ok)
        {
            Subtitle.Text = "待接入：打开下载文件夹 D:\\Pixiv";
        }
    }
}
