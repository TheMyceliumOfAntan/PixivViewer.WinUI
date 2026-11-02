using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pixiv_Viewer_WinUI.Models;

namespace Pixiv_Viewer_WinUI.Services.Pixiv;

/// <summary>
/// OAuth（refresh_token → access_token）与本地 token 存储。
/// token 只落在本机 %LOCALAPPDATA%\PixivViewer\refresh_token.txt，不写进源码。
/// </summary>
public sealed class PixivSession
{
    internal const string ClientId = "MOBrBDS8blbauoSck0ZfDbtuzpyT";
    internal const string ClientSecret = "lsACyCD94FhDUtGTXi3QzcFE2uU1hqtDaKeqrdwj";
    internal const string UserAgent = "PixivAndroidApp/5.0.234 (Android 11; Pixel 5)";

    /// <summary>
    /// 网页版（www / accounts）用的浏览器 UA。
    /// Cloudflare 会直接 403 掉 App API 的 UA，所以网页接口必须用浏览器标识。
    /// </summary>
    internal const string WebUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private HttpClient _http;
    private readonly string _tokenPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _refreshToken;
    private string? _accessToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private string _sessionCookie = string.Empty;

    public PixivSession(HttpClient http, PixivEndpoints endpoints)
    {
        _http = http;
        Endpoints = endpoints;

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PixivViewer");

        _tokenPath = Path.Combine(folder, "auth.bin");
        var cookiePath = Path.Combine(folder, "cookie.bin");

        // 旧版明文文件迁移到 DPAPI 加密文件
        SecretStore.MigratePlainFile(Path.Combine(folder, "refresh_token.txt"), _tokenPath);
        SecretStore.MigratePlainFile(Path.Combine(folder, "session_cookie.txt"), cookiePath);

        _refreshToken = Environment.GetEnvironmentVariable("PIXIV_REFRESH_TOKEN");
        if (string.IsNullOrWhiteSpace(_refreshToken))
        {
            _refreshToken = SecretStore.Load(_tokenPath);
        }

        _sessionCookie = SecretStore.Load(cookiePath) ?? string.Empty;
    }

    /// <summary>网页版登录 cookie（PHPSESSID），用于收藏集等 www.pixiv.net ajax。</summary>
    public string SessionCookie => _sessionCookie;

    public bool HasSessionCookie => !string.IsNullOrWhiteSpace(_sessionCookie);

    /// <summary>保存网页版 cookie（由网页登录窗口写入，DPAPI 加密落盘）。</summary>
    public void SaveSessionCookie(string cookie)
    {
        _sessionCookie = cookie?.Trim() ?? string.Empty;

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PixivViewer",
            "cookie.bin");

        SecretStore.Save(path, _sessionCookie);
    }

    public string BaseUrl => Endpoints.ApiBase;

    public PixivEndpoints Endpoints { get; private set; }

    /// <summary>反代失效切到域前置时换掉 HttpClient / 端点，已拿到的 access_token 继续复用。</summary>
    internal void SwitchEndpoints(HttpClient http, PixivEndpoints endpoints)
    {
        _http = http;
        Endpoints = endpoints;
    }

    public PixivUser? User { get; private set; }

    public bool HasRefreshToken => !string.IsNullOrWhiteSpace(_refreshToken);

    public bool IsAuthenticated => User is not null;

    /// <summary>上一次刷新 token 是否因为网络原因失败（反代挂了），用于自动切换域前置。</summary>
    public bool LastRefreshFailedWithNetworkError { get; private set; }

    public async Task<bool> SignInAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_refreshToken))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>保存 refresh_token 到本机并立即登录。</summary>
    public async Task<bool> SignInWithTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var token = refreshToken.Trim();
        if (token.Length == 0)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _refreshToken = token;
            _accessToken = null;
            User = null;

            var ok = await RefreshCoreAsync(cancellationToken);
            if (ok)
            {
                SaveToken(token);
            }

            return ok;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SignOut()
    {
        _accessToken = null;
        _expiresAt = DateTimeOffset.MinValue;
        User = null;
        _refreshToken = null;
        _sessionCookie = string.Empty;

        TryDelete(_tokenPath);
        TryDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PixivViewer",
            "cookie.bin"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 忽略文件占用等异常。
        }
    }

    /// <summary>取可用的 access_token；过期前 60 秒自动刷新。</summary>
    internal async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_accessToken) && _expiresAt > DateTimeOffset.Now.AddSeconds(60))
        {
            return _accessToken;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(_accessToken) && _expiresAt > DateTimeOffset.Now.AddSeconds(60))
            {
                return _accessToken;
            }

            return await RefreshCoreAsync(cancellationToken) ? _accessToken : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void InvalidateAccessToken()
    {
        _accessToken = null;
        _expiresAt = DateTimeOffset.MinValue;
    }

    private async Task<bool> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_refreshToken))
        {
            return false;
        }

        try
        {
            var result = await RefreshCoreUnsafeAsync(cancellationToken);
            LastRefreshFailedWithNetworkError = false;
            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LastRefreshFailedWithNetworkError =
                ex is HttpRequestException or TaskCanceledException or System.IO.IOException;

            PixivDiagnostics.Write($"auth exception: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> RefreshCoreUnsafeAsync(CancellationToken cancellationToken)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "refresh_token",
            ["include_policy"] = "true",
            ["refresh_token"] = _refreshToken!,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoints.AuthTokenUrl) { Content = form };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            PixivDiagnostics.Write($"auth failed: {(int)response.StatusCode} {body}");
            return false;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var auth = JsonSerializer.Deserialize<PixivAuthResponse>(json, JsonOptions);
        if (string.IsNullOrEmpty(auth?.AccessToken))
        {
            return false;
        }

        _accessToken = auth.AccessToken;
        _expiresAt = DateTimeOffset.Now.AddSeconds(Math.Max(60, auth.ExpiresIn));

        if (!string.IsNullOrWhiteSpace(auth.RefreshToken) && auth.RefreshToken != _refreshToken)
        {
            // Pixiv 偶尔会轮换 refresh_token，保存新的。
            _refreshToken = auth.RefreshToken;
            SaveToken(auth.RefreshToken!);
        }

        User = auth.User is null
            ? User
            : new PixivUser
            {
                Id = auth.User.Id.ToString(),
                Name = auth.User.Name ?? string.Empty,
                Account = auth.User.Account ?? string.Empty,
                AvatarUrl = auth.User.ProfileImageUrls?.Medium,
                Biography = auth.User.Comment ?? string.Empty,
            };

        return true;
    }

    /// <summary>保存 refresh_token（网页登录拿到后写入）。</summary>
    public void SaveRefreshToken(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        _refreshToken = refreshToken.Trim();
        SaveToken(_refreshToken);
    }

    private void SaveToken(string token)
    {
        SecretStore.Save(_tokenPath, token);
    }
}
