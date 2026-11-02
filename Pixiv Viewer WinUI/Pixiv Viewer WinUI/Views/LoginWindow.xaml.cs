using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Pixiv_Viewer_WinUI.Services;
using Pixiv_Viewer_WinUI.Services.Pixiv;
using Windows.Graphics;

namespace Pixiv_Viewer_WinUI.Views;

/// <summary>网页登录结果：refresh_token + 网页版 cookie。</summary>
public sealed class LoginResult
{
    public string? RefreshToken { get; init; }

    public string? Cookie { get; init; }
}

/// <summary>
/// 内嵌 WebView2 的 Pixiv 登录窗口：走 OAuth PKCE（pixiv-android client），
/// 登录成功后同时拿到 refresh_token 和网页版 PHPSESSID（收藏集需要）。
/// </summary>
public sealed partial class LoginWindow : Window
{
    private const string ClientId = "MOBrBDS8blbauoSck0ZfDbtuzpyT";
    private const string ClientSecret = "lsACyCD94FhDUtGTXi3QzcFE2uU1hqtDaKeqrdwj";
    private const string RedirectUri = "https://app-api.pixiv.net/web/v1/users/auth/pixiv/callback";

    private readonly string _codeVerifier;
    private readonly TaskCompletionSource<LoginResult?> _completion = new();

    /// <summary>
    /// WebView2 一个用户数据目录只能对应一个浏览器进程，环境参数创建后不能改，
    /// 所以隧道与环境在整个进程内复用。
    /// </summary>
    private static FrontingProxyServer? _environmentProxy;
    private static CoreWebView2Environment? _environment;
    private static string? _environmentKey;

    private bool _closing;
    private bool _webClosed;

    public LoginWindow()
    {
        InitializeComponent();

        _codeVerifier = CreateVerifier();

        try
        {
            AppWindow.Resize(new SizeInt32(560, 820));
        }
        catch (Exception)
        {
            // 忽略不支持调整窗口的环境
        }

        Web.NavigationStarting += OnNavigationStarting;
        Web.NavigationCompleted += OnNavigationCompleted;
        Web.CoreWebView2Initialized += OnCoreWebView2Initialized;

        // 等窗口真正激活再导航，避免 WebView2 初始化与窗口关闭竞争
        Activated += OnFirstActivated;

        // 窗口销毁前先关掉 WebView2（在 Closed 之后关已经太晚，会抛 winrt::hresult_error）
        try
        {
            AppWindow.Closing += OnAppWindowClosing;
        }
        catch (Exception)
        {
            // 个别环境不支持
        }

        Closed += OnClosed;
    }

    private string LoginUrl =>
        $"https://app-api.pixiv.net/web/v1/login?code_challenge={CreateChallenge(_codeVerifier)}&code_challenge_method=S256&client=pixiv-android";

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        if (_closing)
        {
            return;
        }

        _ = NavigateAsync();
    }

    /// <summary>
    /// 域前置模式下让 WebView2 走本机 CONNECT 隧道：隧道只对 ClientHello 做分片转发，
    /// 不终止 TLS，因此证书校验、SNI、HTTP/2 全部保持原样。
    /// </summary>
    private async Task NavigateAsync()
    {
        try
        {
            var environment = await GetEnvironmentAsync();
            if (environment is not null)
            {
                await Web.EnsureCoreWebView2Async(environment);
            }

            if (!_closing)
            {
                Web.Source = new Uri(LoginUrl);
            }
        }
        catch (Exception ex)
        {
            PixivDiagnostics.Write($"login navigate failed: {ex.Message}");

            // 环境创建失败时退回默认环境（反代模式仍可登录）
            try
            {
                if (!_closing)
                {
                    Web.Source = new Uri(LoginUrl);
                }
            }
            catch (Exception)
            {
                // 窗口已关闭
            }
        }
    }

    private static async Task<CoreWebView2Environment?> GetEnvironmentAsync()
    {
        var endpoints = AppServices.Endpoints;
        if (endpoints.Mode != PixivConnectionMode.DomainFronting || endpoints.HostMap.Count == 0)
        {
            return _environment;
        }

        // 系统已经配了代理（Clash/V2Ray 等）时优先用它，不然强制直连反而更差
        if (_environment is null && IsSystemProxyEnabled())
        {
            PixivDiagnostics.Write("login: 检测到系统代理，WebView2 直接使用系统代理");
            return null;
        }

        var key = $"fronting:{string.Join(';', endpoints.HostMap.Select(pair => $"{pair.Key}={string.Join(',', pair.Value)}"))}";

        if (_environment is not null)
        {
            if (_environmentProxy is not null)
            {
                _environmentProxy.IsEnabled = true;
            }

            if (_environmentKey != key)
            {
                PixivDiagnostics.Write("login: 域前置 IP 已改动，WebView2 环境仍沿用本次运行的旧配置");
            }

            return _environment;
        }

        try
        {
            _environmentProxy = FrontingProxyServer.Start(endpoints);

            var options = new CoreWebView2EnvironmentOptions
            {
                // --disable-quic：QUIC 走 UDP，不经过 HTTP 代理
                AdditionalBrowserArguments = $"--proxy-server=http://127.0.0.1:{_environmentProxy.Port} --disable-quic",
            };

            _environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, null, options);
            _environmentKey = key;

            PixivDiagnostics.Write($"login: 已启动本地分片代理 127.0.0.1:{_environmentProxy.Port}");
            return _environment;
        }
        catch (Exception ex)
        {
            PixivDiagnostics.Write($"login proxy/environment failed: {ex.Message}");
            return null;
        }
    }

    private static bool IsSystemProxyEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");

            return key?.GetValue("ProxyEnable") is int enabled && enabled != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        _closing = true;
        _completion.TrySetResult(null);
        CloseWebView();
    }

    /// <summary>登录结果；取消或关闭窗口时为 null。</summary>
    public Task<LoginResult?> Result => _completion.Task;

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closing = true;
        _completion.TrySetResult(null);
        CloseWebView();
    }

    /// <summary>
    /// 先关 WebView2 再让窗口销毁，否则 WebView2 宿主在窗口关闭时抛 winrt::hresult_error。
    /// </summary>
    private void CloseWebView()
    {
        if (_webClosed)
        {
            return;
        }

        _webClosed = true;

        try
        {
            if (_environmentProxy is not null)
            {
                _environmentProxy.IsEnabled = false;
            }
        }
        catch (Exception)
        {
            // 忽略
        }

        try
        {
            Web.CoreWebView2?.Stop();
        }
        catch (Exception)
        {
            // 忽略
        }

        try
        {
            Web.Close();
        }
        catch (Exception)
        {
            // 已经关闭时忽略
        }
    }

    private void SafeClose()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        CloseWebView();

        try
        {
            Close();
        }
        catch (Exception)
        {
            // 窗口已关闭
        }
    }

    private void OnCoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (!_closing)
        {
            Loading.IsActive = true;
        }
    }

    private void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!_closing)
        {
            Loading.IsActive = false;
        }
    }

    private async void OnNavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        var uri = args.Uri;
        PixivDiagnostics.Write($"login nav: {Truncate(uri, 180)}");

        if (_closing)
        {
            return;
        }

        // 两种回调都要接：https 回调页（带 code）和 pixiv:// 深链
        var isDeepLink = uri.StartsWith("pixiv://account/login", StringComparison.OrdinalIgnoreCase);
        var isHttpsCallback = uri.Contains("users/auth/pixiv/callback", StringComparison.OrdinalIgnoreCase);

        if (!isDeepLink && !isHttpsCallback)
        {
            return;
        }

        var code = ExtractQuery(uri, "code");
        if (string.IsNullOrEmpty(code))
        {
            // 没有 code（比如用户取消授权），交给页面自己处理
            return;
        }

        args.Cancel = true;

        if (!_closing)
        {
            StatusText.Text = "登录成功，正在换取令牌…";
            Loading.IsActive = true;
        }

        var result = await ExchangeAsync(code);
        PixivDiagnostics.Write($"login result: token={(string.IsNullOrEmpty(result?.RefreshToken) ? "null" : "ok")}, cookie={(string.IsNullOrEmpty(result?.Cookie) ? "null" : "ok")}");

        if (_closing)
        {
            // 窗口已经被用户关掉：只交结果，不再碰任何 UI
            _completion.TrySetResult(result);
            return;
        }

        _completion.TrySetResult(result);
        SafeClose();
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private async Task<LoginResult?> ExchangeAsync(string code)
    {
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["grant_type"] = "authorization_code",
                ["include_policy"] = "true",
                ["code"] = code,
                ["code_verifier"] = _codeVerifier,
                ["redirect_uri"] = RedirectUri,
            });

            var endpoints = AppServices.Endpoints;

            using var http = PixivHttp.Create(endpoints, TimeSpan.FromSeconds(40));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.AuthTokenUrl) { Content = form };
            request.Headers.TryAddWithoutValidation("User-Agent", "PixivAndroidApp/5.0.234 (Android 11; Pixel 5)");

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                if (!_closing)
                {
                    StatusText.Text = $"换取令牌失败：{(int)response.StatusCode}";
                }

                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);

            var refreshToken = document.RootElement.TryGetProperty("refresh_token", out var refresh)
                ? refresh.GetString()
                : null;

            var cookie = await GetSessionCookieAsync();

            return new LoginResult { RefreshToken = refreshToken, Cookie = cookie };
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                StatusText.Text = $"登录失败：{ex.Message}";
            }

            return null;
        }
    }

    /// <summary>从 WebView2 的 Cookie 容器里取 www.pixiv.net 的 PHPSESSID。</summary>
    private async Task<string?> GetSessionCookieAsync()
    {
        try
        {
            if (_closing || Web.CoreWebView2 is null)
            {
                return null;
            }

            var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.pixiv.net");
            // __cf_bm 是 Cloudflare 的 bot 校验 cookie，ajax 偶发 403 时能救回来；取不到就自然跳过
            var pairs = cookies
                .Where(c => c.Name is "PHPSESSID" or "device_token" or "__cf_bm")
                .Select(c => $"{c.Name}={c.Value}")
                .ToList();

            return pairs.Count > 0 ? string.Join("; ", pairs) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ExtractQuery(string uri, string key)
    {
        var index = uri.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return string.Empty;
        }

        var start = index + key.Length + 1;
        var end = uri.IndexOf('&', start);
        var value = end < 0 ? uri[start..] : uri[start..end];
        return Uri.UnescapeDataString(value);
    }

    private static string CreateVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    private static string CreateChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
