using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pixiv_Viewer_WinUI.Services;

namespace Pixiv_Viewer_WinUI.Controls;

/// <summary>
/// 「发现新版本」弹窗：展示 Release 说明 → 下载（带进度/镜像名）→ 静默安装并重启。
/// </summary>
public sealed partial class UpdateDialog : ContentDialog
{
    private readonly UpdateInfo _info;
    private CancellationTokenSource? _cancellation;
    private string? _installerPath;
    private bool _downloading;

    public UpdateDialog(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        var current = UpdateService.CurrentVersion;
        VersionText.Text = $"Pixiv Viewer {info.Version}";
        MetaText.Text = info.PublishedAt is { } published
            ? $"当前版本 {current} · 发布于 {published.LocalDateTime:yyyy-MM-dd}"
            : $"当前版本 {current}";

        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes)
            ? "本次发布未提供更新说明。"
            : UpdateService.ToPlainText(info.Notes);
    }

    /// <summary>用户点了「跳过此版本」。</summary>
    public bool Skipped { get; private set; }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_installerPath is null)
        {
            args.Cancel = true;
            await DownloadAsync();
            return;
        }

        args.Cancel = true;
        Install();
    }

    private async Task DownloadAsync()
    {
        _downloading = true;
        _cancellation = new CancellationTokenSource();

        PrimaryButtonText = "下载中…";
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        CloseButtonText = "取消";
        ProgressPanel.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressBar.Value = 0;
        ProgressText.Text = "正在选择最快的下载源…";

        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            ProgressBar.Value = value.Percent;
            ProgressText.Text = $"正在从 {value.Source} 下载 {value.Percent:0}%";
        });

        try
        {
            _installerPath = await UpdateService.DownloadAsync(_info, progress, _cancellation.Token);

            ProgressBar.Value = 100;
            ProgressText.Text = "下载完成";
            PrimaryButtonText = "立即安装并重启";
            IsPrimaryButtonEnabled = true;
            CloseButtonText = "稍后";
            SecondaryButtonText = string.Empty;
            IsSecondaryButtonEnabled = false;
        }
        catch (OperationCanceledException)
        {
            Hide();
        }
        catch (Exception ex)
        {
            ShowError($"下载失败：{ex.Message}");
            PrimaryButtonText = "重试下载";
            IsPrimaryButtonEnabled = true;
            CloseButtonText = "稍后";
            IsSecondaryButtonEnabled = true;
        }
        finally
        {
            _downloading = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void Install()
    {
        if (_installerPath is null)
        {
            return;
        }

        if (!UpdateService.TryLaunchInstaller(_installerPath, out var error))
        {
            ShowError(string.IsNullOrWhiteSpace(error) ? "无法启动安装程序" : $"无法启动安装程序：{error}");
            return;
        }

        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        CloseButtonText = string.Empty;
        PrimaryButtonText = "正在安装…";
        ProgressText.Text = "安装程序已启动，程序会自动关闭并重启，请稍候";

        Hide();
        _ = ExitSoonAsync();
    }

    /// <summary>给安装器一点时间拿到文件，然后退出，避免 exe 被占用。</summary>
    private static async Task ExitSoonAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        Application.Current.Exit();
    }

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_downloading)
        {
            args.Cancel = true;
            return;
        }

        Skipped = true;
    }

    private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_downloading)
        {
            _cancellation?.Cancel();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
