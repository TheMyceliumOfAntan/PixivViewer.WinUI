using System;
using System.Collections.Generic;
using System.Linq;

namespace Pixiv_Viewer_WinUI.Services.Pixiv;

/// <summary>接口连接方式：自建反代优先，域前置直连兜底。</summary>
public enum PixivConnectionMode
{
    /// <summary>走用户自建的 pixiv-proxy-api（/api、/api/web、/image 前缀）。</summary>
    ReverseProxy,

    /// <summary>直连 Pixiv 官方域名，把域名映射到固定 IP（TLS SNI 保持不变）。</summary>
    DomainFronting,
}

/// <summary>
/// 两套端点映射。所有网络层只认这里的 base，不再各自拼 <c>BaseUrl</c>，
/// 这样切换反代 / 域前置只需要换一个实例。
/// </summary>
public sealed class PixivEndpoints
{
    public const string AppApiHost = "app-api.pixiv.net";
    public const string WebHost = "www.pixiv.net";
    public const string AccountHost = "accounts.pixiv.net";
    public const string OAuthHost = "oauth.secure.pixiv.net";
    public const string ImageHost = "i.pximg.net";
    public const string StaticHost = "s.pximg.net";

    /// <summary>域前置默认 IP（参考 Pixeval / Mako 的公开默认值，可在设置里改）。</summary>
    public static string DefaultHostMap { get; } = string.Join(
        Environment.NewLine,
        $"{AppApiHost} = 104.18.42.239, 172.64.145.17",
        $"{OAuthHost} = 104.18.42.239, 172.64.145.17",
        $"{WebHost} = 210.140.139.155, 210.140.139.156, 210.140.139.157",
        $"{AccountHost} = 210.140.139.155, 210.140.139.156, 210.140.139.157",
        $"{ImageHost} = 210.140.139.134, 210.140.139.135, 210.140.139.136, 210.140.139.137",
        $"{StaticHost} = 210.140.139.135, 210.140.139.136, 210.140.139.137");

    private static readonly IReadOnlyDictionary<string, string[]> EmptyMap =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    private PixivEndpoints(
        PixivConnectionMode mode,
        string apiBase,
        string webBase,
        string authTokenUrl,
        string imageBase,
        string staticBase,
        IReadOnlyDictionary<string, string[]> hostMap)
    {
        Mode = mode;
        ApiBase = apiBase;
        WebBase = webBase;
        AuthTokenUrl = authTokenUrl;
        ImageBase = imageBase;
        StaticBase = staticBase;
        HostMap = hostMap;
    }

    public PixivConnectionMode Mode { get; }

    /// <summary>App API 前缀，例如 https://host/api 或 https://app-api.pixiv.net。</summary>
    public string ApiBase { get; }

    /// <summary>网页版前缀，例如 https://host/api/web 或 https://www.pixiv.net。</summary>
    public string WebBase { get; }

    /// <summary>OAuth 换 token 的完整地址。</summary>
    public string AuthTokenUrl { get; }

    /// <summary>图片前缀，例如 https://host/image 或 https://i.pximg.net。</summary>
    public string ImageBase { get; }

    /// <summary>s.pximg.net 静态资源前缀（评论印章等）。</summary>
    public string StaticBase { get; }

    public IReadOnlyDictionary<string, string[]> HostMap { get; }

    public bool UsesReverseProxy => Mode == PixivConnectionMode.ReverseProxy;

    /// <summary>反代用 X-Pixiv-Cookie（由反代转成 Cookie），直连必须用标准 Cookie。</summary>
    public string CookieHeaderName => UsesReverseProxy ? "X-Pixiv-Cookie" : "Cookie";

    /// <summary>反代模式必须填地址；域前置模式开箱即用。</summary>
    public bool IsUsable => !UsesReverseProxy || ApiBase.Length > 0;

    public static PixivEndpoints ReverseProxy(string baseUrl)
    {
        var url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        return new PixivEndpoints(
            PixivConnectionMode.ReverseProxy,
            url.Length == 0 ? string.Empty : url + "/api",
            url.Length == 0 ? string.Empty : url + "/api/web",
            url.Length == 0 ? string.Empty : url + "/api/auth/token",
            url.Length == 0 ? string.Empty : url + "/image",
            url.Length == 0 ? string.Empty : url + "/static",
            EmptyMap);
    }

    public static PixivEndpoints DomainFronting(string? hostMap = null)
    {
        return new PixivEndpoints(
            PixivConnectionMode.DomainFronting,
            $"https://{AppApiHost}",
            $"https://{WebHost}",
            $"https://{OAuthHost}/auth/token",
            $"https://{ImageHost}",
            $"https://{StaticHost}",
            ParseHostMap(string.IsNullOrWhiteSpace(hostMap) ? DefaultHostMap : hostMap));
    }

    /// <summary>
    /// 解析「域名 = IP, IP」格式的多行文本；<c>#</c> 开头或没有等号的行会被忽略，
    /// 非法 IP 直接跳过（不影响其它条目）。
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> ParseHostMap(string? text)
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return map;
        }

        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var host = line[..separator].Trim().ToLowerInvariant();
            var ips = line[(separator + 1)..]
                .Split(new[] { ',', '，', ' ', ';', '、', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ip => System.Net.IPAddress.TryParse(ip.Trim(), out var parsed) ? parsed.ToString() : null)
                .Where(ip => ip is not null)
                .Select(ip => ip!)
                .Distinct()
                .ToArray();

            if (host.Length > 0 && ips.Length > 0)
            {
                map[host] = ips;
            }
        }

        return map;
    }
}
