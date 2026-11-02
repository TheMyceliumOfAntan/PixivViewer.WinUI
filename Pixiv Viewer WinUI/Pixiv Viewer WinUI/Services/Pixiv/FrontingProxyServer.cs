using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pixiv_Viewer_WinUI.Services.Pixiv;

/// <summary>
/// 本地 HTTP CONNECT 隧道，专供 WebView2 在域前置模式下使用。
///
/// 隧道本身不终止 TLS：Chromium 发来的 ClientHello 由 <see cref="TlsFragmentedStream"/>
/// 分片后原样转发，证书校验与 SNI 都保持不变，所以不需要自签证书，也不用关证书错误。
/// 目标主机会按 <see cref="PixivEndpoints.HostMap"/> 映射到固定 IP。
/// </summary>
public sealed class FrontingProxyServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;

    private readonly TcpListener _listener;
    private readonly IReadOnlyDictionary<string, string[]> _hostMap;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = new();
    private readonly object _clientGate = new();

    private bool _disposed;

    private FrontingProxyServer(TcpListener listener, IReadOnlyDictionary<string, string[]> hostMap)
    {
        _listener = listener;
        _hostMap = hostMap;
    }

    /// <summary>本机监听端口（127.0.0.1）。</summary>
    public int Port { get; private init; }

    /// <summary>窗口关闭后置 false，隧道会立即拒绝新连接（端口保留，便于复用同一 WebView2 环境）。</summary>
    public bool IsEnabled { get; set; } = true;

    public static FrontingProxyServer Start(PixivEndpoints endpoints)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var server = new FrontingProxyServer(listener, endpoints.HostMap)
        {
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        };

        _ = Task.Run(server.AcceptLoopAsync);
        return server;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            lock (_clientGate)
            {
                _clients.Add(client);
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            client.NoDelay = true;

            using (client)
            using (var clientStream = client.GetStream())
            {
                if (!IsEnabled || _cts.IsCancellationRequested)
                {
                    return;
                }

                var (header, leftover) = await ReadHeaderAsync(clientStream, _cts.Token);
                if (header is null)
                {
                    return;
                }

                var target = ParseConnectTarget(header);
                if (target is null)
                {
                    await WriteAsciiAsync(clientStream, "HTTP/1.1 400 Bad Request\r\n\r\n", _cts.Token);
                    return;
                }

                var (host, port) = target.Value;

                Stream serverStream;
                try
                {
                    serverStream = await PixivHttp.OpenTcpStreamAsync(host, port, _hostMap, _cts.Token);
                }
                catch (Exception ex)
                {
                    PixivDiagnostics.Write($"web login proxy: 连接 {host}:{port} 失败 {ex.Message}");
                    await WriteAsciiAsync(clientStream, "HTTP/1.1 502 Bad Gateway\r\n\r\n", _cts.Token);
                    return;
                }

                using (serverStream)
                {
                    await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", _cts.Token);

                    var toServer = new TlsFragmentedStream(serverStream, host);

                    if (leftover.Length > 0)
                    {
                        await toServer.WriteAsync(leftover, _cts.Token);
                    }

                    var upload = PumpAsync(clientStream, toServer, _cts.Token);
                    var download = PumpAsync(serverStream, clientStream, _cts.Token);

                    await Task.WhenAny(upload, download);
                }
            }
        }
        catch (Exception)
        {
            // 隧道中断（用户关窗、目标断开）属正常情况
        }
        finally
        {
            lock (_clientGate)
            {
                _clients.Remove(client);
            }
        }
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        try
        {
            await from.CopyToAsync(to, 64 * 1024, cancellationToken);
            await to.FlushAsync(cancellationToken);
        }
        catch (Exception)
        {
            // 任意一端关闭都会走到这里
        }
    }

    /// <summary>读 HTTP 头，返回请求头文本和紧随其后的剩余字节（可能是 ClientHello）。</summary>
    private static async Task<(string? Header, byte[] Leftover)> ReadHeaderAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            var searchStart = Math.Max(0, length - 3);
            length += read;

            var end = FindHeaderEnd(buffer, searchStart, length);
            if (end < 0)
            {
                continue;
            }

            var header = Encoding.ASCII.GetString(buffer, 0, end);
            var leftover = new byte[length - end];
            Buffer.BlockCopy(buffer, end, leftover, 0, leftover.Length);
            return (header, leftover);
        }

        return (null, Array.Empty<byte>());
    }

    private static int FindHeaderEnd(byte[] buffer, int start, int length)
    {
        for (var i = start; i + 3 < length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i + 4;
            }
        }

        return -1;
    }

    private static (string Host, int Port)? ParseConnectTarget(string header)
    {
        var lineEnd = header.IndexOf('\r');
        var requestLine = lineEnd < 0 ? header : header[..lineEnd];

        // CONNECT host:port HTTP/1.1
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var authority = parts[1];
        var colon = authority.LastIndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        var host = authority[..colon].Trim('[', ']');
        if (host.Length == 0 ||
            !int.TryParse(authority[(colon + 1)..], out var port) ||
            port <= 0 ||
            port > 65535)
        {
            return null;
        }

        return (host, port);
    }

    private static async Task WriteAsciiAsync(Stream stream, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes.AsMemory(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _cts.Cancel();
        }
        catch (Exception)
        {
            // 忽略
        }

        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // 忽略
        }

        lock (_clientGate)
        {
            foreach (var client in _clients)
            {
                try
                {
                    client.Dispose();
                }
                catch (Exception)
                {
                    // 忽略
                }
            }

            _clients.Clear();
        }

        _cts.Dispose();
    }
}
