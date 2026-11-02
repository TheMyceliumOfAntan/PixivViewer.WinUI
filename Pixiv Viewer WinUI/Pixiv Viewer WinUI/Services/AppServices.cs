using System;
using Microsoft.UI.Xaml;
using Pixiv_Viewer_WinUI.Helpers;
using Pixiv_Viewer_WinUI.Services.Pixiv;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>
/// 应用级服务定位器 + 轻量设置。接入依赖注入后可替换为容器。
/// </summary>
public static class AppServices
{
    public static AppSettings Settings { get; } = new AppSettings();

    public static PixivApiService PixivApi { get; private set; } = null!;

    public static IPixivService Pixiv => PixivApi;

    public static IDownloadService Downloads { get; } = new DownloadService();

    static AppServices()
    {
        PixivApi = new PixivApiService(Settings.ApiBaseUrl);
        ApplyApiSettings();
    }

    /// <summary>设置里改了 API 地址后重建服务（会重新读取本机保存的登录态）。</summary>
    public static void ReloadApi()
    {
        PixivApi = new PixivApiService(Settings.ApiBaseUrl);
        ApplyApiSettings();
    }

    private static void ApplyApiSettings()
    {
        PixivApi.ShowR18 = Settings.ShowR18;
        PixivApi.ShowR18G = Settings.ShowR18G;
        PixivApi.ShowAi = Settings.ShowAi;
        PixivApi.BlockedTags = Settings.BlockedTags;
    }
}

public sealed class AppSettings : ObservableObject
{
    private bool _useSampleData;
    private bool _showR18;
    private bool _showR18G;
    private bool _showAi = true;
    private string _downloadFolder = string.Empty;
    private int _downloadConcurrency = 3;
    private string _blockedTags = string.Empty;
    private ElementTheme _theme = ElementTheme.Default;
    private string _apiBaseUrl = SettingsStore.LoadApiBaseUrl();

    /// <summary>
    /// Pixiv API 服务地址（用户自建）。软件不内置任何公共接口，
    /// 未填写时所有请求都会提示去「设置 → 服务地址」配置。
    /// </summary>
    public string ApiBaseUrl
    {
        get => _apiBaseUrl;
        set
        {
            var normalized = NormalizeBaseUrl(value);

            if (SetProperty(ref _apiBaseUrl, normalized))
            {
                SettingsStore.SaveApiBaseUrl(normalized);
            }
        }
    }

    /// <summary>规范化地址：去空格、去尾部斜杠，缺协议时补 https://。</summary>
    public static string NormalizeBaseUrl(string? value)
    {
        var url = (value ?? string.Empty).Trim().TrimEnd('/');

        if (url.Length == 0)
        {
            return string.Empty;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        return url;
    }

    /// <summary>接口失败时是否用示例数据兜底（仅用于离线预览/设计稿核对）。</summary>
    public bool UseSampleData
    {
        get => _useSampleData;
        set
        {
            if (SetProperty(ref _useSampleData, value))
            {
                SampleDataChanged?.Invoke(this, System.EventArgs.Empty);
            }
        }
    }

    /// <summary>列表里是否显示 R-18 作品。</summary>
    public bool ShowR18
    {
        get => _showR18;
        set
        {
            if (SetProperty(ref _showR18, value))
            {
                AppServices.PixivApi.ShowR18 = value;
                PixivApiSettingsChanged?.Invoke(this, System.EventArgs.Empty);
            }
        }
    }

    /// <summary>列表里是否显示 R-18G 作品。</summary>
    public bool ShowR18G
    {
        get => _showR18G;
        set
        {
            if (SetProperty(ref _showR18G, value))
            {
                AppServices.PixivApi.ShowR18G = value;
                PixivApiSettingsChanged?.Invoke(this, System.EventArgs.Empty);
            }
        }
    }

    /// <summary>是否显示 AI 生成作品。</summary>
    public bool ShowAi
    {
        get => _showAi;
        set
        {
            if (SetProperty(ref _showAi, value))
            {
                AppServices.PixivApi.ShowAi = value;
                PixivApiSettingsChanged?.Invoke(this, System.EventArgs.Empty);
            }
        }
    }

    /// <summary>黑名单标签，逗号或空格分隔，命中的作品在列表里隐藏。</summary>
    public string BlockedTags
    {
        get => _blockedTags;
        set
        {
            if (SetProperty(ref _blockedTags, value))
            {
                AppServices.PixivApi.BlockedTags = value;
                PixivApiSettingsChanged?.Invoke(this, System.EventArgs.Empty);
            }
        }
    }

    public ElementTheme Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                ApplyTheme();
            }
        }
    }

    /// <summary>作品下载保存目录；空则用「图片\Pixiv」。</summary>
    public string DownloadFolder
    {
        get => _downloadFolder;
        set => SetProperty(ref _downloadFolder, value);
    }

    /// <summary>同时下载的文件数（1~8）。</summary>
    public int DownloadConcurrency
    {
        get => _downloadConcurrency;
        set => SetProperty(ref _downloadConcurrency, System.Math.Clamp(value, 1, 8));
    }

    public event System.EventHandler? SampleDataChanged;

    /// <summary>R-18 / 黑名单等过滤条件变化时触发，页面可据此刷新列表。</summary>
    public event System.EventHandler? PixivApiSettingsChanged;

    public void ApplyTheme()
    {
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = Theme;
        }
    }
}
