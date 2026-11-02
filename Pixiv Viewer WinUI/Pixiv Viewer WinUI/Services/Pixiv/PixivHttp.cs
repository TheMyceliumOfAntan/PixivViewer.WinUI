using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Pixiv_Viewer_WinUI.Services.Pixiv;

/// <summary>
/// 统一的 HttpClient 工厂。
///
/// 域前置模式下用 <see cref="SocketsHttpHandler.ConnectCallback"/> 把请求的主机名
/// 映射到固定 IP，并把 ClientHello 分片后再发（见 <see cref="TlsFragmentedStream"/>）：
/// TLS 仍然按原主机名做 SNI 和证书校验，只是绕开 DNS 污染与 SNI 阻断。
/// </summary>
public static class PixivHttp
{
    public static HttpClient Create(PixivEndpoints endpoints, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(12),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        if (endpoints.Mode == PixivConnectionMode.DomainFronting)
        {
            var map = endpoints.HostMap;

            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                var network = await OpenTcpStreamAsync(
                    context.DnsEndPoint.Host,
                    context.DnsEndPoint.Port,
                    map,
                    cancellationToken);

                return new TlsFragmentedStream(network, context.DnsEndPoint.Host);
            };
        }

        return new HttpClient(new RefererHandler { InnerHandler = handler }) { Timeout = timeout };
    }

    /// <summary>
    /// 按「域名 → 固定 IP」映射建立 TCP 连接（没有映射时退回系统 DNS）。
    /// 返回的流是裸 TCP，TLS / 分片由调用方决定。
    /// </summary>
    public static async Task<Stream> OpenTcpStreamAsync(
        string host,
        int port,
        IReadOnlyDictionary<string, string[]> hostMap,
        CancellationToken cancellationToken)
    {
        var addresses = hostMap.TryGetValue(host, out var mapped) && mapped.Length > 0
            ? mapped
            : Array.ConvertAll(
                await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false),
                address => address.ToString());

        Exception? lastError = null;

        foreach (var address in addresses)
        {
            if (!IPAddress.TryParse(address, out var ip))
            {
                continue;
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            try
            {
                await socket.ConnectAsync(ip, port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                lastError = ex;

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException($"无法连接到 {host}:{port}", lastError);
    }

    /// <summary>
    /// 直连 pximg 时必须带 Referer，否则 Pixiv 图片服务器返回 403。
    /// 反代模式由反代自己处理，这里的判断对两种模式都无害。
    /// </summary>
    private sealed class RefererHandler : DelegatingHandler
    {
        private static readonly Uri ImageReferer = new("https://www.pixiv.net/");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;

            if (uri is not null &&
                request.Headers.Referrer is null &&
                uri.Host.EndsWith("pximg.net", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Referrer = ImageReferer;
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
