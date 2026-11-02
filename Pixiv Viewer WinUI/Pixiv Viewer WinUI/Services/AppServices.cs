using System;
using System.Linq;
using System.Net.Http;
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

    public static DownloadService Downloads { get; private set; } = new();

    /// <summary>图片下载专用 HttpClient（域前置模式下走固定 IP + Referer）。</summary>
    public static HttpClient Images { get; private set; } = null!;

    public static PixivEndpoints Endpoints => PixivApi.Endpoints;

    static AppServices()
    {
        ReloadApi();
    }

    /// <summary>设置里改了接口方式 / 地址 / IP 后重建服务（会重新读取本机保存的登录态）。</summary>
    public static void ReloadApi()
    {
        var primary = BuildPrimaryEndpoints();
        var fallback = primary.Mode == PixivConnectionMode.ReverseProxy && Settings.AutoFallback
            ? PixivEndpoints.DomainFronting(Settings.FrontingHostMap)
            : null;

        PixivApi = new PixivApiService(primary, fallback);
        PixivApi.EndpointsChanged += (_, endpoints) => ApplyImageClient(endpoints);

        ApplyApiSettings();
        ApplyImageClient(primary);
    }

    private static PixivEndpoints BuildPrimaryEndpoints()
    {
        if (Settings.ApiMode == PixivConnectionMode.DomainFronting)
        {
            return PixivEndpoints.DomainFronting(Settings.FrontingHostMap);
        }

        // 没填反代地址又允许兜底：直接走域前置，避免一启动就什么都加载不出来
        if (string.IsNullOrEmpty(Settings.ApiBaseUrl) && Settings.AutoFallback)
        {
            return PixivEndpoints.DomainFronting(Settings.FrontingHostMap);
        }

        return PixivEndpoints.ReverseProxy(Settings.ApiBaseUrl);
    }

    private static void ApplyImageClient(PixivEndpoints endpoints)
    {
        var client = PixivHttp.Create(endpoints, TimeSpan.FromSeconds(45));

        Images = client;
        ImageCache.Client = client;
        Downloads.SetHttpClient(client);
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

    private string _apiBaseUrl;
    private PixivConnectionMode _apiMode;
    private string _frontingHostMap;
    private bool _autoFallback;

    public AppSettings()
    {
        var stored = SettingsStore.Load();

        _apiBaseUrl = NormalizeBaseUrl(stored.ApiBaseUrl);
        _apiMode = string.Equals(stored.ApiMode, "fronting", StringComparison.OrdinalIgnoreCase)
            ? PixivConnectionMode.DomainFronting
            : PixivConnectionMode.ReverseProxy;
        _frontingHostMap = ResolveFrontingHostMap(stored.FrontingHostMap);
        _autoFallback = stored.AutoFallback;

        // 旧默认值被自动升级时顺手落盘，设置页里也能看到新的 IP
        if (!string.IsNullOrWhiteSpace(stored.FrontingHostMap) &&
            !string.Equals(_frontingHostMap, stored.FrontingHostMap, StringComparison.Ordinal))
        {
            Persist();
        }
    }

    /// <summary>
    /// 空值用新默认；如果是 1.1.0 早期那份会把 www 指到源站、导致 403 的默认值，
    /// 也自动换成新默认（用户自己改过的内容原样保留）。
    /// </summary>
    private static string ResolveFrontingHostMap(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return PixivEndpoints.DefaultHostMap;
        }

        return NormalizeHostMap(stored) == NormalizeHostMap(PixivEndpoints.LegacyDefaultHostMap)
            ? PixivEndpoints.DefaultHostMap
            : stored;
    }

    private static string NormalizeHostMap(string text)
        => string.Join(
            ";",
            text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));

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
                Persist();
            }
        }
    }

    private bool _suppressApiNotifications;

    /// <summary>设置页保存时批量更新，避免中途触发页面刷新（此时新端点还没生效）。</summary>
    public void UpdateApiSettings(PixivConnectionMode mode, string url, string hostMap, bool autoFallback)
    {
        _suppressApiNotifications = true;

        try
        {
            ApiMode = mode;
            ApiBaseUrl = url;
            FrontingHostMap = hostMap;
            AutoFallback = autoFallback;
        }
        finally
        {
            _suppressApiNotifications = false;
        }
    }

    /// <summary>接口方式：自建反代 / 域前置直连。</summary>
    public PixivConnectionMode ApiMode
    {
        get => _apiMode;
        set
        {
            if (SetProperty(ref _apiMode, value))
            {
                Persist();

                if (!_suppressApiNotifications)
                {
                    PixivApiSettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    /// <summary>域前置的「域名 = IP」映射（多行文本）。</summary>
    public string FrontingHostMap
    {
        get => _frontingHostMap;
        set
        {
            if (SetProperty(ref _frontingHostMap, value ?? string.Empty))
            {
                Persist();
            }
        }
    }

    /// <summary>反代请求失败时自动切换到域前置直连。</summary>
    public bool AutoFallback
    {
        get => _autoFallback;
        set
        {
            if (SetProperty(ref _autoFallback, value))
            {
                Persist();
            }
        }
    }

    /// <summary>当前配置是否可用（域前置 / 已开启自动兜底时都视为可用）。</summary>
    public bool IsApiConfigured =>
        _apiMode == PixivConnectionMode.DomainFronting || _apiBaseUrl.Length > 0 || _autoFallback;

    private void Persist()
    {
        SettingsStore.Save(new StoredSettings
        {
            ApiBaseUrl = _apiBaseUrl,
            ApiMode = _apiMode == PixivConnectionMode.DomainFronting ? "fronting" : "proxy",
            FrontingHostMap = _frontingHostMap,
            AutoFallback = _autoFallback,
        });
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
                SampleDataChanged?.Invoke(this, EventArgs.Empty);
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
                PixivApiSettingsChanged?.Invoke(this, EventArgs.Empty);
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
                PixivApiSettingsChanged?.Invoke(this, EventArgs.Empty);
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
                PixivApiSettingsChanged?.Invoke(this, EventArgs.Empty);
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
                PixivApiSettingsChanged?.Invoke(this, EventArgs.Empty);
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

    public event EventHandler? SampleDataChanged;

    /// <summary>R-18 / 黑名单等过滤条件变化时触发，页面可据此刷新列表。</summary>
    public event EventHandler? PixivApiSettingsChanged;

    public void ApplyTheme()
    {
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = Theme;
        }
    }
}
