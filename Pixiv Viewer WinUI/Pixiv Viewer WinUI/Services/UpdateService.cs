using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pixiv_Viewer_WinUI.Services.Pixiv;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>GitHub 上找到的新版本信息。</summary>
public sealed class UpdateInfo
{
    public Version Version { get; init; } = new(1, 0, 0);

    public string Tag { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>Release 正文（Markdown，原样用于弹窗展示）。</summary>
    public string Notes { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public long Size { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>
/// GitHub 下载镜像：多个 gh-proxy 前缀 + 直连，并发测速后优先用最快的。
/// 做法与 Qomicex.Tauri 的 update 端点一致（Range: bytes=0-0 竞速、30 分钟缓存）。
/// </summary>
public static class GithubMirror
{
    /// <summary>空字符串表示直连 github.com。</summary>
    private static readonly string[] Prefixes =
    {
        "",
        "https://edgeone.gh-proxy.org/",
        "https://cdn.gh-proxy.org/",
        "https://hk.gh-proxy.org/",
        "https://v6.gh-proxy.org/",
        "https://ghfast.top/",
    };

    private static readonly TimeSpan RaceTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly HttpClient ProbeHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly object CacheGate = new();
    private static string? _cachedPrefix;
    private static DateTimeOffset _cachedAt;

    /// <summary>把镜像前缀拼到 GitHub 原始下载地址前面。</summary>
    public static string Apply(string prefix, string url) => prefix + url;

    /// <summary>前缀的显示名（直连 / 主机名）。</summary>
    public static string Describe(string prefix)
        => string.IsNullOrEmpty(prefix)
            ? "GitHub 直连"
            : new Uri(prefix).Host;

    /// <summary>
    /// 返回按可用性/延迟排好序的前缀列表（最快在前，失败的排在最后），
    /// 供下载时逐个重试。结果缓存 30 分钟。
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetCandidatesAsync(string url, CancellationToken cancellationToken)
    {
        lock (CacheGate)
        {
            if (_cachedPrefix is not null && DateTimeOffset.Now - _cachedAt < CacheTtl)
            {
                return Order(_cachedPrefix);
            }
        }

        var results = await Task.WhenAll(Prefixes.Select(prefix => ProbeAsync(prefix, url, cancellationToken)));

        var best = results
            .Where(result => result.Ok)
            .OrderBy(result => result.Milliseconds)
            .Select(result => result.Prefix)
            .FirstOrDefault();

        if (best is not null)
        {
            lock (CacheGate)
            {
                _cachedPrefix = best;
                _cachedAt = DateTimeOffset.Now;
            }
        }

        return results
            .OrderBy(result => result.Ok ? 0 : 1)
            .ThenBy(result => result.Milliseconds)
            .Select(result => result.Prefix)
            .ToList();
    }

    private static IReadOnlyList<string> Order(string fastest)
        => Prefixes.OrderBy(prefix => prefix == fastest ? 0 : 1).ToList();

    private static async Task<(string Prefix, long Milliseconds, bool Ok)> ProbeAsync(
        string prefix,
        string url,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RaceTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, Apply(prefix, url));
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);

            using var response = await ProbeHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            stopwatch.Stop();

            var ok = response.IsSuccessStatusCode || (int)response.StatusCode == 206;
            return (prefix, ok ? stopwatch.ElapsedMilliseconds : long.MaxValue, ok);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (prefix, long.MaxValue, false);
        }
    }
}

/// <summary>下载进度（百分比 + 当前使用的镜像）。</summary>
public sealed record UpdateDownloadProgress(double Percent, string Source);

/// <summary>
/// 更新检查 / 下载 / 启动安装器。
/// 更新源就是项目自己的 GitHub Release，发版流程保持不变。
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/TheMyceliumOfAntan/PixivViewer.WinUI/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static UpdateService()
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PixivViewer-Updater");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
    }

    /// <summary>当前程序版本（取 3 段，忽略修订号）。</summary>
    public static Version CurrentVersion
    {
        get
        {
            var version = typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    /// <summary>
    /// 查询 latest release；没有新版本或网络失败都返回 null。
    /// 需要区分「失败」时看 <paramref name="error"/>。
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default, Action<string>? error = null)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                error?.Invoke($"GitHub 返回 {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() ?? string.Empty : string.Empty;
            if (!TryParseVersion(tag, out var version))
            {
                error?.Invoke("Release 版本号无法解析");
                return null;
            }

            if (version <= CurrentVersion)
            {
                return null;
            }

            string? downloadUrl = null;
            long size = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? string.Empty : string.Empty;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        !name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    downloadUrl = asset.TryGetProperty("browser_download_url", out var urlValue)
                        ? urlValue.GetString()
                        : null;
                    size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var parsedSize)
                        ? parsedSize
                        : 0;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                error?.Invoke("Release 里没有找到安装包");
                return null;
            }

            return new UpdateInfo
            {
                Version = version,
                Tag = tag,
                Title = root.TryGetProperty("name", out var titleValue) ? titleValue.GetString() ?? tag : tag,
                Notes = root.TryGetProperty("body", out var bodyValue) ? bodyValue.GetString() ?? string.Empty : string.Empty,
                DownloadUrl = downloadUrl,
                Size = size,
                PublishedAt = root.TryGetProperty("published_at", out var published) &&
                              published.TryGetDateTimeOffset(out var publishedAt)
                    ? publishedAt
                    : null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error?.Invoke(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 下载安装包到临时目录：先并发测速选出最快的 GitHub 镜像，失败时自动换下一个源。
    /// 已存在且大小一致的安装包会直接复用。
    /// </summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo info,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "PixivViewer", "update");
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, $"PixivViewer-Setup-{info.Version}-x64.exe");

        if (File.Exists(target) && (info.Size <= 0 || new FileInfo(target).Length == info.Size))
        {
            progress?.Report(new UpdateDownloadProgress(100, "本地缓存"));
            return target;
        }

        var candidates = await GithubMirror.GetCandidatesAsync(info.DownloadUrl, cancellationToken);
        Exception? lastError = null;

        foreach (var prefix in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DownloadFromAsync(prefix, info, target, progress, cancellationToken);
                return target;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                PixivDiagnostics.Write($"update: 从 {GithubMirror.Describe(prefix)} 下载失败：{ex.Message}");
            }
        }

        throw new IOException("所有 GitHub 镜像都下载失败", lastError);
    }

    private static async Task DownloadFromAsync(
        string prefix,
        UpdateInfo info,
        string target,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partial = target + ".part";
        var source = GithubMirror.Describe(prefix);
        progress?.Report(new UpdateDownloadProgress(0, source));

        using var response = await Http.GetAsync(
            GithubMirror.Apply(prefix, info.DownloadUrl),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? info.Size;

        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(partial))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;

            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;

                if (total > 0)
                {
                    progress?.Report(new UpdateDownloadProgress(Math.Min(100, received * 100d / total), source));
                }
            }
        }

        if (info.Size > 0 && new FileInfo(partial).Length != info.Size)
        {
            File.Delete(partial);
            throw new IOException("安装包大小不一致，可能下载不完整");
        }

        if (File.Exists(target))
        {
            File.Delete(target);
        }

        File.Move(partial, target);
        progress?.Report(new UpdateDownloadProgress(100, source));
    }

    /// <summary>以管理员身份静默运行安装包；取消 UAC 或启动失败返回 false。</summary>
    public static bool TryLaunchInstaller(string installerPath, out string? error)
    {
        error = null;

        try
        {
            var startInfo = new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Verb = "runas",
            };

            // /SILENT 显示进度但不需要点下一步；安装完由安装包的 [Run] 自动拉起新版
            startInfo.ArgumentList.Add("/SILENT");
            startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
            startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");
            startInfo.ArgumentList.Add("/NORESTART");
            startInfo.ArgumentList.Add("/NORESTARTAPPLICATIONS");

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var text = (tag ?? string.Empty).Trim().TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            text = text[..dash];
        }

        if (Version.TryParse(text, out var parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return true;
        }

        version = new Version(1, 0, 0);
        return false;
    }

    /// <summary>把 Release 正文里的 Markdown 标记粗略压成纯文本（弹窗用 TextBlock 显示）。</summary>
    public static string ToPlainText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var builder = new System.Text.StringBuilder();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            line = line.TrimStart('#', ' ');
            line = line.Replace("**", string.Empty).Replace("`", string.Empty);

            // [文字](链接) -> 文字（链接）
            var linkStart = line.IndexOf('[');
            var linkEnd = line.IndexOf("](", StringComparison.Ordinal);
            if (linkStart >= 0 && linkEnd > linkStart)
            {
                var text = line[(linkStart + 1)..linkEnd];
                var urlEnd = line.IndexOf(')', linkEnd);
                var url = urlEnd > linkEnd ? line[(linkEnd + 2)..urlEnd] : string.Empty;
                line = line[..linkStart] + text + (url.Length > 0 ? $"（{url}）" : string.Empty) +
                       (urlEnd > 0 && urlEnd + 1 < line.Length ? line[(urlEnd + 1)..] : string.Empty);
            }

            builder.AppendLine(line);
        }

        return builder.ToString().Trim();
    }

    public static string FormatSize(long bytes)
        => bytes <= 0
            ? string.Empty
            : bytes >= 1024L * 1024 * 1024
                ? (bytes / 1024d / 1024 / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
                : (bytes / 1024d / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " MB";
}
