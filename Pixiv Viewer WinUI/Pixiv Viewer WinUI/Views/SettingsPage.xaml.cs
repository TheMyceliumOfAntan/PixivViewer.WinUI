using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Helpers;
using Pixiv_Viewer_WinUI.Services;
using Pixiv_Viewer_WinUI.Services.Manga;
using Pixiv_Viewer_WinUI.Services.Pixiv;
using Windows.System;

namespace Pixiv_Viewer_WinUI.Views;

public sealed partial class SettingsPage : Page
{
    private const string RepoUrl = "https://github.com/TheMyceliumOfAntan/PixivViewer.WinUI";

    private const string UpstreamRepoUrl = "https://github.com/TheMyceliumOfAntan/pixiv-proxy-api";

    private const string PixevalUrl = "https://github.com/Pixeval/Pixeval";

    private bool _initializing = true;
    private bool _loginInProgress;
private bool _mangaUiReady;

    /// <summary>悬浮提示的自动关闭计时器。</summary>
    private readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public SettingsPage()
    {
        InitializeComponent();

        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            Hint.IsOpen = false;
        };

        Hint.Closed += (_, _) => _hintTimer.Stop();

        ThemeBox.SelectedIndex = (int)AppServices.Settings.Theme;
        SampleSwitch.IsOn = AppServices.Settings.UseSampleData;
        R18Switch.IsOn = AppServices.Settings.ShowR18;
        R18GSwitch.IsOn = AppServices.Settings.ShowR18G;
        AiSwitch.IsOn = AppServices.Settings.ShowAi;
        BlockedTagsBox.Text = AppServices.Settings.BlockedTags;
        ApiBaseUrlBox.Text = AppServices.Settings.ApiBaseUrl;
        ModeRadio.SelectedIndex = AppServices.Settings.ApiMode == PixivConnectionMode.DomainFronting ? 1 : 0;
        HostMapBox.Text = AppServices.Settings.FrontingHostMap;
        FallbackSwitch.IsOn = AppServices.Settings.AutoFallback;
        UpdateSwitch.IsOn = AppServices.Settings.AutoCheckUpdate;
        UpdateModePanels();
        DownloadFolderBox.Text = AppServices.Settings.DownloadFolder;
        ConcurrencyBox.Value = AppServices.Settings.DownloadConcurrency;
        VersionText.Text = $"v{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
        _initializing = false;

        UpdateAccountUi();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UpdateAccountUi();
        UpdateCacheSize();
        UpdateMangaSection();
    }

    private void UpdateAccountUi()
    {
        var user = AppServices.Pixiv.CurrentUser;

        if (AppServices.Pixiv.IsLoggedIn && user is not null)
        {
            AccountTitle.Text = $"已登录 · {user.Name}";
            AccountDescription.Text = $"{user.AccountText} · token 已保存在本机";
            SignOutButton.Visibility = Visibility.Visible;
            TokenRow.Visibility = Visibility.Collapsed;
        }
        else
        {
            AccountTitle.Text = "未登录";
            AccountDescription.Text = "粘贴 Pixiv refresh_token 后登录；token 只保存在本机，不会写进源码";
            SignOutButton.Visibility = Visibility.Collapsed;
            TokenRow.Visibility = Visibility.Visible;
        }
    }

    private async void OnSignInClick(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if (token.Length == 0)
        {
            ShowHint("请先粘贴 refresh_token");
            return;
        }

        SignInButton.IsEnabled = false;
        ShowHint("正在登录…", "登录中");

        try
        {
            var ok = await AppServices.Pixiv.SignInWithTokenAsync(token);
            if (ok)
            {
                var user = AppServices.Pixiv.CurrentUser;
                ShowHint($"登录成功：{user?.Name}（{user?.AccountText}）", "已登录");
                TokenBox.Password = string.Empty;
            }
            else
            {
                ShowHint("登录失败：refresh_token 无效或已过期，请在 Pixiv 设置里重新授权后获取", "登录失败", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowHint($"登录失败：{ex.Message}", "登录失败", InfoBarSeverity.Error);
        }
        finally
        {
            SignInButton.IsEnabled = true;
            UpdateAccountUi();
        }
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        AppServices.Pixiv.SignOut();
        UpdateAccountUi();
        ShowHint("已退出登录，本机保存的 token 已删除", "已退出");
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
        {
            return;
        }

        AppServices.Settings.Theme = (ElementTheme)int.Parse(tag);
        ShowHint("已切换主题");
    }

    private void OnSampleDataToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        AppServices.Settings.UseSampleData = SampleSwitch.IsOn;
        ShowHint(SampleSwitch.IsOn ? "已开启示例数据兜底" : "已关闭示例数据，接口失败时显示空状态");
    }

    private void OnAiToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        AppServices.Settings.ShowAi = AiSwitch.IsOn;
        ShowHint(AiSwitch.IsOn ? "已显示 AI 生成作品" : "已隐藏 AI 生成作品");
    }

    private void OnR18Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        AppServices.Settings.ShowR18 = R18Switch.IsOn;
        ShowHint(R18Switch.IsOn ? "已显示 R-18 作品" : "已隐藏 R-18 作品");
    }

    private void OnR18GToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        AppServices.Settings.ShowR18G = R18GSwitch.IsOn;
        ShowHint(R18GSwitch.IsOn ? "已显示 R-18G 作品" : "已隐藏 R-18G 作品");
    }

    private void OnSaveBlockedTagsClick(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.BlockedTags = BlockedTagsBox.Text.Trim();
        ShowHint(string.IsNullOrWhiteSpace(BlockedTagsBox.Text) ? "已清空黑名单" : "黑名单已保存，列表会按标签过滤");
    }

    private void OnDownloadFolderChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        AppServices.Settings.DownloadFolder = DownloadFolderBox.Text.Trim();
    }

    private async void OnPickFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await AppServices.Downloads.PickDownloadFolderAsync();
            if (!string.IsNullOrWhiteSpace(path))
            {
                DownloadFolderBox.Text = path;
                AppServices.Settings.DownloadFolder = path;
                ShowHint($"下载目录已改为 {path}");
            }
        }
        catch (Exception ex)
        {
            ShowHint($"选择目录失败：{ex.Message}", "失败", InfoBarSeverity.Error);
        }
    }

    private void OnConcurrencyChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializing || double.IsNaN(args.NewValue))
        {
            return;
        }

        AppServices.Settings.DownloadConcurrency = (int)args.NewValue;
        ShowHint($"并发下载数：{AppServices.Settings.DownloadConcurrency}");
    }

    private async void OnOpenDownloadFolderClick(object sender, RoutedEventArgs e)
    {
        await AppServices.Downloads.OpenDownloadFolderAsync();
    }

    private async void OnOpenProxyRepoClick(object sender, RoutedEventArgs e)
        => await OpenUrlAsync(UpstreamRepoUrl);

    private async void OnOpenRepoClick(object sender, RoutedEventArgs e)
        => await OpenUrlAsync(RepoUrl);

    private async void OnOpenPixevalClick(object sender, RoutedEventArgs e)
        => await OpenUrlAsync(PixevalUrl);

    private void OnApiModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModePanels();

        if (_initializing)
        {
            return;
        }

        var mode = ModeRadio.SelectedIndex == 1
            ? PixivConnectionMode.DomainFronting
            : PixivConnectionMode.ReverseProxy;

        // 初始化后 RadioButtons 还会补发一次 SelectionChanged，和当前设置一致时不要误提示
        if (mode == AppServices.Settings.ApiMode)
        {
            return;
        }

        ShowHint(mode == PixivConnectionMode.DomainFronting
            ? "已选择域前置直连，点「保存接口设置」后生效"
            : "已选择自建 API 反代，点「保存接口设置」后生效");
    }

    private void UpdateModePanels()
    {
        var fronting = ModeRadio.SelectedIndex == 1;

        ProxyPanel.Visibility = fronting ? Visibility.Collapsed : Visibility.Visible;
        FrontingPanel.Visibility = fronting ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFallbackToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        ShowHint(FallbackSwitch.IsOn
            ? "反代失败时会自动切到域前置（点保存后生效）"
            : "已关闭自动兜底（点保存后生效）");
    }

    private void OnResetHostMapClick(object sender, RoutedEventArgs e)
    {
        HostMapBox.Text = PixivEndpoints.DefaultHostMap;
        ShowHint("已恢复默认 IP，记得点「保存接口设置」");
    }

    /// <summary>保存接口方式 / 地址 / IP 映射并立即生效。</summary>
    private void OnSaveApiSettingsClick(object sender, RoutedEventArgs e)
    {
        var mode = ModeRadio.SelectedIndex == 1
            ? PixivConnectionMode.DomainFronting
            : PixivConnectionMode.ReverseProxy;

        var url = AppSettings.NormalizeBaseUrl(ApiBaseUrlBox.Text);
        ApiBaseUrlBox.Text = url;

        AppServices.Settings.UpdateApiSettings(mode, url, HostMapBox.Text.Trim(), FallbackSwitch.IsOn);

        AppServices.ReloadApi();
        UpdateAccountUi();

        if (mode == PixivConnectionMode.DomainFronting)
        {
            ShowHint("已切换到域前置直连，图片与新请求会立刻使用该方式");
        }
        else if (url.Length == 0 && !FallbackSwitch.IsOn)
        {
            ShowHint("反代地址为空且未开启自动兜底，将无法加载内容", "配置不完整", InfoBarSeverity.Warning);
        }
        else
        {
            ShowHint("已保存，正在使用自建 API 反代");
        }
    }

    /// <summary>「关于 → 更新日志」：弹出对话框显示各版本更新内容。</summary>
    private async void OnChangelogClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ChangelogDialog { XamlRoot = XamlRoot };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // 快速连点导致重复 ShowAsync 时忽略
            ShowHint($"更新日志打开失败：{ex.Message}");
        }
    }

    private void OnAutoCheckUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        AppServices.Settings.AutoCheckUpdate = UpdateSwitch.IsOn;
        ShowHint(UpdateSwitch.IsOn ? "已开启启动时检查更新" : "已关闭启动时检查更新（仍可手动检查）");
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查…";

        try
        {
            var (hasUpdate, error) = await UpdateCoordinator.CheckManuallyAsync(XamlRoot);

            if (hasUpdate)
            {
                UpdateStatusText.Text = "发现新版本";
                ShowHint("发现新版本，已在弹窗中显示更新说明", "检查更新");
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                UpdateStatusText.Text = "检查失败";
                ShowHint($"检查更新失败：{error}", "检查更新", InfoBarSeverity.Warning);
            }
            else
            {
                var current = UpdateService.CurrentVersion;
                UpdateStatusText.Text = $"已是最新版本（v{current}）";
                ShowHint($"当前已是最新版本 v{current}", "检查更新");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "检查失败";
            ShowHint($"检查更新失败：{ex.Message}", "检查更新", InfoBarSeverity.Error);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async System.Threading.Tasks.Task OpenUrlAsync(string url)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception)
        {
            ShowHint("无法打开浏览器");
        }
    }

    private async void OnWebLoginClick(object sender, RoutedEventArgs e)
    {
        if (_loginInProgress)
        {
            return;
        }

        _loginInProgress = true;
        WebLoginButton.IsEnabled = false;

        try
        {
            var window = new LoginWindow();
            window.Activate();

            var result = await window.Result;
            if (result is null)
            {
                ShowHint("网页登录已取消或失败");
                return;
            }

            if (!string.IsNullOrEmpty(result.Cookie))
            {
                AppServices.PixivApi.SaveSessionCookie(result.Cookie);
            }

            if (!string.IsNullOrEmpty(result.RefreshToken))
            {
                AppServices.PixivApi.SaveRefreshToken(result.RefreshToken);
                await AppServices.Pixiv.SignInAsync();
            }

            UpdateAccountUi();
            (App.MainWindow as MainWindow)?.UpdateLoginBar();
            ShowHint(string.IsNullOrEmpty(result.RefreshToken)
                ? "已获取网页 cookie（未拿到 refresh_token）"
                : "登录成功，已保存 refresh_token 与 cookie");
        }
        catch (Exception ex)
        {
            ShowHint($"网页登录失败：{ex.Message}", "失败", InfoBarSeverity.Error);
        }
        finally
        {
            _loginInProgress = false;
            WebLoginButton.IsEnabled = true;
        }
    }

    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        ImageCache.Clear();
        UpdateCacheSize();
        ShowHint("缓存已清空");
    }

    // ---------------- 漫画翻译 ----------------

    private void UpdateMangaSection()
    {
        var ready = MangaTranslationService.IsReady;
        var megabytes = MangaTranslationService.ModelSize / 1024d / 1024d;
        var partial = MangaTranslationService.PartialBytes;

        _mangaUiReady = false;
        MangaGpuSwitch.IsOn = AppServices.Settings.MangaGpuEnabled;
        _mangaUiReady = true;

        var backend = MangaTranslationService.BackendName;
        MangaGpuHint.Text = !AppServices.Settings.MangaGpuEnabled
            ? "已关闭：全部用 CPU 推理（较慢）"
            : string.IsNullOrEmpty(backend)
                ? "已开启：检测与识别优先用显卡；首次翻译后这里会显示实际后端"
                : $"已开启 · 检测/识别：{backend} · 去字：{MangaTranslationService.LamaBackendName ?? "未使用"}";

        if (ready)
        {
            MangaModelStatusText.Text = string.IsNullOrEmpty(backend)
                ? "已就绪（首次使用时会加载模型）"
                : $"已就绪 · 推理后端 {backend}";
        }
        else if (partial > 0)
        {
            MangaModelStatusText.Text =
                $"未下载（已缓存 {Format.Bytes(partial)}，点击「下载」继续，共需约 {megabytes:F0} MB）";
        }
        else
        {
            MangaModelStatusText.Text = $"未下载，需约 {megabytes:F0} MB";
        }

        MangaModelPathText.Text = $"保存位置：{MangaTranslationService.ModelDirectory}";

        var downloading = MangaTranslationService.IsDownloading;
        MangaDownloadButton.Content = ready ? "重新下载" : (partial > 0 ? "继续下载" : "下载");
        MangaDownloadButton.IsEnabled = !downloading;
        MangaDeleteButton.IsEnabled = !downloading && (ready || partial > 0);

        var lamaReady = MangaTranslationService.IsLamaReady;
        _mangaUiReady = false;
        MangaInpaintBox.SelectedIndex = AppServices.Settings.MangaInpaintMode == MangaInpaintMode.Lama ? 1 : 0;
        _mangaUiReady = true;

        MangaLamaDownloadButton.Visibility = lamaReady ? Visibility.Collapsed : Visibility.Visible;
        MangaLamaDownloadButton.IsEnabled = !downloading;
        MangaInpaintHint.Text = AppServices.Settings.MangaInpaintMode == MangaInpaintMode.Lama
            ? (lamaReady
                ? "高精度：LaMa 神经修复，彩色页 / 网点背景更自然（较慢）"
                : "高精度模型还没下载，当前会自动回退到快速模式")
            : "快速：纯色填充 + 扩散，不额外耗时";

        var cacheBytes = MangaCache.GetSize();
        MangaCacheText.Text = cacheBytes <= 0
            ? "译文图片与文字区域，当前为空"
            : $"译文图片与文字区域，当前占用 {Format.Bytes(cacheBytes)}";
    }

    private async void OnMangaOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = MangaTranslationService.ModelDirectory;
            Directory.CreateDirectory(directory);
            await Launcher.LaunchFolderPathAsync(directory);
        }
        catch (Exception ex)
        {
            ShowHint($"打开文件夹失败：{ex.Message}", "失败", InfoBarSeverity.Error);
        }
    }

    private void OnMangaGpuToggled(object sender, RoutedEventArgs e)
    {
        if (!_mangaUiReady)
        {
            return;
        }

        AppServices.Settings.MangaGpuEnabled = MangaGpuSwitch.IsOn;

        // 不在这里重建会话：正在进行中的翻译会用旧会话跑完，
        // 下次开始翻译时会按新开关重新准备（见 MangaTranslationService.TranslatePageAsync）。
        UpdateMangaSection();
        ShowHint(MangaGpuSwitch.IsOn ? "已启用 GPU 加速（下次翻译生效）" : "已关闭 GPU 加速，下次翻译使用 CPU");
    }

    private void OnMangaInpaintModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_mangaUiReady)
        {
            return;
        }

        AppServices.Settings.MangaInpaintMode = MangaInpaintBox.SelectedIndex == 1
            ? MangaInpaintMode.Lama
            : MangaInpaintMode.Classic;

        UpdateMangaSection();

        if (AppServices.Settings.MangaInpaintMode == MangaInpaintMode.Lama &&
            !MangaTranslationService.IsLamaReady)
        {
            ShowHint("还需要下载高精度去字模型（约 208 MB）", "提示", InfoBarSeverity.Warning);
        }
    }

    private async void OnMangaLamaDownloadClick(object sender, RoutedEventArgs e)
        => await StartMangaDownloadAsync(lama: true);

    private CancellationTokenSource? _mangaDownloadCts;
    private long _lastProgressBytes;
    private long _lastProgressTick;
    private double _lastSpeedMb;

    private async void OnMangaDownloadClick(object sender, RoutedEventArgs e)
        => await StartMangaDownloadAsync(lama: false);

    private void OnMangaDownloadCancelClick(object sender, RoutedEventArgs e)
    {
        _mangaDownloadCts?.Cancel();
        ShowHint("已取消下载，已下载的部分会保留，下次可以继续");
    }

    /// <summary>统一下载入口：基础模型 / LaMa，同一时刻只允许一个（由服务层保证）。</summary>
    private async Task StartMangaDownloadAsync(bool lama)
    {
        if (MangaTranslationService.IsDownloading)
        {
            ShowHint("模型正在下载中，请等待完成", "提示", InfoBarSeverity.Informational);
            return;
        }

        if (lama && MangaTranslationService.IsLamaReady)
        {
            return;
        }

        var partial = MangaTranslationService.PartialBytes;
        var sizeMb = (lama ? MangaTranslationService.LamaSize : MangaTranslationService.ModelSize) / 1024d / 1024d;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = lama ? "下载高精度去字模型" : "下载漫画翻译模型",
            Content = lama
                ? $"LaMa 神经修复模型约 {sizeMb:F0} MB，下载后彩色页与网点背景的去字效果会明显更好。\n\n" +
                  $"保存位置：{MangaTranslationService.ModelDirectory}"
                : $"识别与去字在本机完成，需要下载模型（约 {sizeMb:F0} MB）。\n" +
                  (partial > 0 ? $"上次已下载 {Format.Bytes(partial)}，本次会继续下载。\n" : string.Empty) +
                  $"\n保存位置：{MangaTranslationService.ModelDirectory}",
            PrimaryButtonText = partial > 0 ? "继续下载" : "下载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _mangaDownloadCts = new CancellationTokenSource();
        _lastProgressBytes = 0;
        _lastProgressTick = Environment.TickCount64;
        _lastSpeedMb = 0;

        // 立刻禁用，避免等待期间还能再点
        MangaDownloadButton.IsEnabled = false;
        MangaDeleteButton.IsEnabled = false;
        MangaLamaDownloadButton.IsEnabled = false;
        MangaLamaDownloadButton.Visibility = Visibility.Collapsed;

        MangaDownloadPanel.Visibility = Visibility.Visible;
        MangaDownloadBar.Value = 0;
        MangaDownloadBar.IsIndeterminate = true;
        MangaDownloadDetailText.Text = "正在连接…";
        UpdateMangaSection();

        var progress = new Progress<MangaProgress>(ApplyDownloadProgress);

        try
        {
            var ok = lama
                ? await MangaTranslationService.EnsureLamaAsync(progress, _mangaDownloadCts.Token)
                : await MangaTranslationService.EnsureModelsAsync(progress, _mangaDownloadCts.Token);

            ShowHint(
                ok ? "模型已就绪，可以去作品页翻译了" : "下载失败，请检查网络后重试（已下载部分会保留）",
                ok ? "完成" : "失败",
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            ShowHint("已取消下载，已下载的部分会保留", "已取消", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowHint($"下载失败：{ex.Message}", "失败", InfoBarSeverity.Error);
        }
        finally
        {
            _mangaDownloadCts?.Dispose();
            _mangaDownloadCts = null;
            MangaDownloadPanel.Visibility = Visibility.Collapsed;
            MangaDownloadBar.IsIndeterminate = false;
            UpdateMangaSection();
        }
    }

    private void ApplyDownloadProgress(MangaProgress progress)
    {
        if (progress.TotalBytes <= 0)
        {
            if (progress.Fraction >= 1)
            {
                MangaDownloadBar.IsIndeterminate = false;
                MangaDownloadBar.Value = 1;
                MangaDownloadDetailText.Text = "完成";
                return;
            }

            MangaDownloadBar.IsIndeterminate = true;
            MangaDownloadDetailText.Text = progress.Stage;
            return;
        }

        MangaDownloadBar.IsIndeterminate = false;
        MangaDownloadBar.Value = Math.Clamp(progress.Fraction, 0, 1);

        // 速度取 0.5 秒以上的采样，避免数字乱跳
        var now = Environment.TickCount64;
        var elapsed = now - _lastProgressTick;
        if (elapsed >= 500 && progress.ReceivedBytes >= _lastProgressBytes)
        {
            var bytesPerSecond = (progress.ReceivedBytes - _lastProgressBytes) * 1000d / elapsed;
            _lastSpeedMb = bytesPerSecond / 1024d / 1024d;
            _lastProgressBytes = progress.ReceivedBytes;
            _lastProgressTick = now;
        }

        var received = progress.ReceivedBytes / 1024d / 1024d;
        var total = progress.TotalBytes / 1024d / 1024d;
        MangaDownloadDetailText.Text =
            $"{progress.Stage} · {received:F1} / {total:F1} MB · {progress.Fraction * 100:F0}%" +
            (_lastSpeedMb > 0.01 ? $" · {_lastSpeedMb:F1} MB/s" : string.Empty);
    }

    private void OnMangaDeleteClick(object sender, RoutedEventArgs e)
    {
        MangaTranslationService.DeleteModels();
        UpdateMangaSection();
        ShowHint("已删除本地模型");
    }

    private void OnClearMangaCacheClick(object sender, RoutedEventArgs e)
    {
        MangaCache.Clear();
        UpdateMangaSection();
        ShowHint("漫画译文缓存已清空");
    }

    private void UpdateCacheSize()
    {
        var bytes = ImageCache.GetCacheSize();
        CacheSizeText.Text = bytes <= 0
            ? "缩略图与作品信息缓存，当前为空"
            : $"缩略图与作品信息缓存，当前占用 {Format.Bytes(bytes)}";
    }

    private void ShowHint(string message, string title = "", InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Hint.Title = title;
        Hint.Message = message;
        Hint.Severity = severity;
        Hint.IsOpen = true;

        // 错误多留一会儿，其余 4 秒自动消失
        _hintTimer.Stop();
        _hintTimer.Interval = severity == InfoBarSeverity.Error
            ? TimeSpan.FromSeconds(6)
            : TimeSpan.FromSeconds(4);
        _hintTimer.Start();
    }
}
