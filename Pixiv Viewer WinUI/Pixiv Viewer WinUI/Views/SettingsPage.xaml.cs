using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Helpers;
using Pixiv_Viewer_WinUI.Services;
using Windows.System;

namespace Pixiv_Viewer_WinUI.Views;

public sealed partial class SettingsPage : Page
{
    // 改成你自己的仓库地址即可（默认先指向源项目）
    private const string RepoUrl = "https://github.com/vmoranv/pixiv-proxy";

    private const string UpstreamRepoUrl = "https://github.com/vmoranv/pixiv-proxy";

    private bool _initializing = true;
    private bool _loginInProgress;

    public SettingsPage()
    {
        InitializeComponent();

        ThemeBox.SelectedIndex = (int)AppServices.Settings.Theme;
        SampleSwitch.IsOn = AppServices.Settings.UseSampleData;
        R18Switch.IsOn = AppServices.Settings.ShowR18;
        R18GSwitch.IsOn = AppServices.Settings.ShowR18G;
        AiSwitch.IsOn = AppServices.Settings.ShowAi;
        BlockedTagsBox.Text = AppServices.Settings.BlockedTags;
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
    }
}
