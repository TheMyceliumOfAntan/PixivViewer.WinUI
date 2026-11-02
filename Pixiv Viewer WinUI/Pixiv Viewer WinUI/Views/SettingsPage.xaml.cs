using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Helpers;
using Pixiv_Viewer_WinUI.Services;
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
