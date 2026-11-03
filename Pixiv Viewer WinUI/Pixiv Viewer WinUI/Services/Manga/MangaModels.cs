using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;

namespace Pixiv_Viewer_WinUI.Services.Manga;

/// <summary>去字模式。</summary>
public enum MangaInpaintMode
{
    /// <summary>经典算法（纯色填充 + 扩散），无需额外模型。</summary>
    Classic = 0,

    /// <summary>LaMa 神经修复，彩色/网点背景更自然，需另下 208 MB 模型。</summary>
    Lama = 1,
}

/// <summary>一个需要下载的模型文件。</summary>
internal sealed record MangaModelFile(
    string FileName,
    string DisplayName,
    long Size,
    string Sha256,
    string Url,
    string? FallbackUrl);

/// <summary>
/// 漫画翻译模型清单与本地存放位置。
///
/// 模型不随安装包分发，首次使用「漫画翻译」时按需下载：
/// - comictextdetector：文本检测，GPL-3.0（manga-image-translator beta-0.3）
/// - PP-OCRv6 medium rec：日文 OCR 识别，Apache-2.0（PaddleOCR）
/// 字典文件从 inference.yml 提取后随程序内置（150 KB）。
/// </summary>
internal static class MangaModels
{
    /// <summary>可用 PIXIV_MANGA_MODEL_DIR 环境变量覆盖（开发调试用）。</summary>
    public static string Directory => _directory ??= ResolveDirectory();

    private static string? _directory;

    private static string ResolveDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable("PIXIV_MANGA_MODEL_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PixivViewer",
            "manga-models");
    }

    public static string DetectorPath => Path.Combine(Directory, "comictextdetector.onnx");

    public static string OcrPath => Path.Combine(Directory, "ppocr_v6_medium_rec.onnx");

    /// <summary>旧版官方 LaMa（仅 CPU 可用；已下载的用户保留兼容）。</summary>
    public static string LamaPath => Path.Combine(Directory, "lama_fp32.onnx");

    /// <summary>
    /// LaMa 的 DirectML 兼容版本（FFT 的 rank-5 MatMul 已改写成 rank-4）。
    /// 数值与原版一致，CPU / GPU 都能跑，所以作为默认下载项。
    /// </summary>
    public static string LamaGpuPath => Path.Combine(Directory, "lama_dml.onnx");

    /// <summary>OCR 字符表（随程序内置，不下载）。</summary>
    public static string DictPath => Path.Combine(AppContext.BaseDirectory, "Assets", "manga", "ppocr_v6_dict.json");

    public static IReadOnlyList<MangaModelFile> Files { get; } = new[]
    {
        new MangaModelFile(
            "comictextdetector.onnx",
            "文本检测模型",
            94_669_756,
            "1a86ace74961413cbd650002e7bb4dcec4980ffa21b2f19b86933372071d718f",
            "https://github.com/zyddnys/manga-image-translator/releases/download/beta-0.3/comictextdetector.pt.onnx",
            "https://gh-proxy.com/https://github.com/zyddnys/manga-image-translator/releases/download/beta-0.3/comictextdetector.pt.onnx"),
        new MangaModelFile(
            "ppocr_v6_medium_rec.onnx",
            "日文 OCR 模型",
            76_554_979,
            "9c09abf0957f7968c7586464b7397b84ad2387a0497a351af40e9acc71b673ba",
            "https://hf-mirror.com/PaddlePaddle/PP-OCRv6_medium_rec_onnx/resolve/main/inference.onnx",
            "https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec_onnx/resolve/main/inference.onnx"),
    };

    public static long TotalSize => Files.Sum(f => f.Size);

    /// <summary>高精度去字模型（默认下载项：DirectML 兼容版，CPU/GPU 通用）。</summary>
    public static MangaModelFile LamaFile { get; } = new(
        "lama_dml.onnx",
        "高精度去字模型",
        208_115_894,
        "10be5108153a7dccfff7cab8a266d4716099570c0a62c16c5552e9bbd78b9a95",
        "https://github.com/TheMyceliumOfAntan/PixivViewer.WinUI/releases/download/models-v1/lama_dml.onnx",
        "https://gh-proxy.com/https://github.com/TheMyceliumOfAntan/PixivViewer.WinUI/releases/download/models-v1/lama_dml.onnx");

    /// <summary>旧版官方 LaMa，只用于兼容已经下载过的用户（DirectML 跑不了，只能 CPU）。</summary>
    public static MangaModelFile LamaLegacyFile { get; } = new(
        "lama_fp32.onnx",
        "高精度去字模型（旧版）",
        208_044_816,
        "1faef5301d78db7dda502fe59966957ec4b79dd64e16f03ed96913c7a4eb68d6",
        string.Empty,
        null);

    /// <summary>已安装（文件齐全且大小正确；哈希校验只在下载后进行，避免每次启动都算 170 MB）。</summary>
    public static bool IsInstalled => Files.All(f => new FileInfo(Path.Combine(Directory, f.FileName)) is { Exists: true } info
                                                    && info.Length == f.Size);

    /// <summary>高精度去字模型是否可用（新版 GPU 版或旧版官方都能用）。</summary>
    public static bool IsLamaInstalled
        => new FileInfo(LamaGpuPath) is { Exists: true } gpu && gpu.Length == LamaFile.Size
           || new FileInfo(LamaPath) is { Exists: true } legacy && legacy.Length == LamaLegacyFile.Size;

    /// <summary>DirectML 版（GPU + CPU 通用）是否已下载。</summary>
    public static bool IsLamaGpuInstalled
        => new FileInfo(LamaGpuPath) is { Exists: true } info && info.Length == LamaFile.Size;
}

/// <summary>模型下载：断点续传 + 多源重试 + SHA-256 校验 + 字节级进度。</summary>
internal static class MangaModelStore
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>同一时刻只允许一个下载任务（设置页和详情页都会触发）。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>当前是否有下载在进行。</summary>
    public static bool IsDownloading { get; private set; }

    /// <summary>模型保存目录（UI 展示 / 打开文件夹用）。</summary>
    public static string Directory => MangaModels.Directory;

    /// <summary>下载基础模型（检测 + OCR）。已有 .part 会断点续传。</summary>
    public static Task<bool> EnsureAsync(IProgress<MangaProgress>? progress, CancellationToken cancellationToken)
        => RunAsync(MangaModels.Files, progress, cancellationToken);

    /// <summary>下载高精度去字模型（默认下 DirectML 兼容版，CPU/GPU 通用）。</summary>
    public static Task<bool> EnsureLamaAsync(IProgress<MangaProgress>? progress, CancellationToken cancellationToken)
        => RunAsync(new[] { MangaModels.LamaFile }, progress, cancellationToken);

    private static async Task<bool> RunAsync(
        IReadOnlyList<MangaModelFile> files,
        IProgress<MangaProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // 已有任务在跑，直接返回，让调用方提示「正在下载」
            return false;
        }

        IsDownloading = true;
        try
        {
            System.IO.Directory.CreateDirectory(MangaModels.Directory);

            var missing = files
                .Where(f => !IsValid(Path.Combine(MangaModels.Directory, f.FileName), f))
                .ToList();

            if (missing.Count == 0)
            {
                progress?.Report(new MangaProgress("模型已就绪", 1));
                return true;
            }

            for (var i = 0; i < missing.Count; i++)
            {
                var ok = await DownloadAsync(missing[i], i, missing.Count, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (!ok)
                {
                    return false;
                }
            }

            progress?.Report(new MangaProgress("模型已就绪", 1));
            return true;
        }
        finally
        {
            IsDownloading = false;
            Gate.Release();
        }
    }

    private static bool IsValid(string path, MangaModelFile file)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == file.Size;
    }

    /// <summary>
    /// 下载单个模型。优先从已有 .part 续传（服务端支持 Range 时只传剩下的部分）；
    /// 取消 / 失败都保留 .part，下次继续。
    /// </summary>
    private static async Task<bool> DownloadAsync(
        MangaModelFile file,
        int index,
        int count,
        IProgress<MangaProgress>? progress,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine(MangaModels.Directory, file.FileName);
        var part = target + ".part";

        foreach (var url in new[] { file.Url, file.FallbackUrl }.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            try
            {
                var resumeFrom = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (resumeFrom >= file.Size)
                {
                    // 残留文件比目标还大，视为损坏，重下
                    TryDelete(part);
                    resumeFrom = 0;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (resumeFrom > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
                }

                using var response = await Http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var resuming = response.StatusCode == System.Net.HttpStatusCode.PartialContent && resumeFrom > 0;
                long totalBytes = file.Size;
                if (response.Content.Headers.ContentRange?.Length is { } rangeLength)
                {
                    totalBytes = rangeLength;
                }
                else if (response.Content.Headers.ContentLength is { } contentLength)
                {
                    totalBytes = resuming ? resumeFrom + contentLength : contentLength;
                }

                if (!resuming)
                {
                    resumeFrom = 0;
                }

                var received = resumeFrom;
                var lastReport = -1;
                var lastTick = Environment.TickCount64;

                progress?.Report(new MangaProgress(
                    $"下载{file.DisplayName}",
                    (index + (totalBytes > 0 ? (double)received / totalBytes : 0)) / count,
                    received,
                    totalBytes,
                    file.DisplayName));

                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var destination = new FileStream(
                                 part,
                                 resuming ? FileMode.Append : FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 81920,
                                 useAsync: true))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        received += read;

                        // 每 1% 或 200ms 汇报一次，兼顾流畅与消息量
                        var percent = (int)(received * 100 / Math.Max(1, totalBytes));
                        var now = Environment.TickCount64;
                        if (percent != lastReport || now - lastTick >= 200)
                        {
                            lastReport = percent;
                            lastTick = now;
                            progress?.Report(new MangaProgress(
                                $"下载{file.DisplayName}",
                                (index + (double)received / Math.Max(1, totalBytes)) / count,
                                received,
                                totalBytes,
                                file.DisplayName));
                        }
                    }
                }

                if (new FileInfo(part).Length != file.Size)
                {
                    // 续传拼出来的长度不对，删掉重来
                    TryDelete(part);
                    continue;
                }

                var hash = await ComputeSha256Async(part, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(part);
                    continue;
                }

                File.Move(part, target, overwrite: true);
                return true;
            }
            catch (OperationCanceledException)
            {
                // 取消：保留 .part，下次断点续传
                throw;
            }
            catch (Exception)
            {
                // 换下一个源；.part 保留
            }
        }

        return false;
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
        catch (Exception)
        {
            // 删除失败不影响后续
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>删除已下载的模型（设置页「删除」用，含可选的 LaMa）。</summary>
    public static void Delete()
    {
        foreach (var path in MangaModels.Files
                     .Select(f => Path.Combine(MangaModels.Directory, f.FileName))
                     .Append(MangaModels.LamaPath)
                     .Append(MangaModels.LamaGpuPath))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 被占用时忽略
            }
        }
    }
}

/// <summary>
/// 单例持有 ORT 会话。
///
/// - 检测 / OCR：开启 GPU 时用 DirectML，不可用或用户关闭时回落 CPU。
/// - LaMa：FFC 的 4D MatMul 在 DirectML 上会运行期报错（80070057），
///   所以先尝试 DML，真跑崩了自动切回 CPU 并记住，后续不再重试。
/// </summary>
internal sealed class MangaBackend : IDisposable
{
    private static readonly Lazy<MangaBackend> Shared = new(() => new MangaBackend());
    private static readonly object SessionLock = new();

    private InferenceSession? _detector;
    private InferenceSession? _ocr;
    private InferenceSession? _lama;
    private string[]? _dict;

    private bool _probed;
    private bool _deviceAvailable;
    private int _deviceId = -1;

    private bool _gpuRequested = true;
    private bool _lamaDmlDisabled;

    public static MangaBackend Instance => Shared.Value;

    /// <summary>当前推理后端：DirectML / CPU。</summary>
    public string BackendName { get; private set; } = "CPU";

    /// <summary>LaMa 是否跑在 GPU 上（DirectML 失败后会自动变 false）。</summary>
    public bool LamaUsesGpu => _deviceAvailable && !_lamaDmlDisabled && _gpuRequested;

    /// <summary>切换 GPU 开关时调用：销毁旧会话，下次按新设置重建。</summary>
    public void ApplyGpuSetting(bool gpuEnabled)
    {
        lock (SessionLock)
        {
            if (_gpuRequested == gpuEnabled && _detector is not null)
            {
                return;
            }

            _gpuRequested = gpuEnabled;
            DisposeSessions();
        }
    }

    public InferenceSession Detector
    {
        get
        {
            lock (SessionLock)
            {
                EnsureProbed();
                return _detector ??= new InferenceSession(MangaModels.DetectorPath, CreateOptions(useGpu: true));
            }
        }
    }

    public InferenceSession Ocr
    {
        get
        {
            lock (SessionLock)
            {
                EnsureProbed();
                return _ocr ??= new InferenceSession(MangaModels.OcrPath, CreateOptions(useGpu: true));
            }
        }
    }

    /// <summary>可选的 LaMa 会话（未下载时返回 null）。</summary>
    public InferenceSession? Lama => GetLama(forceCpu: false);

    /// <summary>LaMa 运行失败后调用：下次改用 CPU 会话。</summary>
    public void DisableLamaGpu()
    {
        lock (SessionLock)
        {
            if (_lamaDmlDisabled)
            {
                return;
            }

            _lamaDmlDisabled = true;
            _lama?.Dispose();
            _lama = null;
        }
    }

    private InferenceSession? GetLama(bool forceCpu)
    {
        lock (SessionLock)
        {
            EnsureProbed();

            var wantGpu = !forceCpu && _deviceAvailable && !_lamaDmlDisabled && _gpuRequested;

            // 新版（DML 兼容）CPU/GPU 都能跑，优先用它；旧版官方模型只能 CPU
            var path = MangaModels.IsLamaGpuInstalled
                ? MangaModels.LamaGpuPath
                : MangaModels.IsLamaInstalled
                    ? MangaModels.LamaPath
                    : null;

            if (path is null)
            {
                return null;
            }

            // 只有 GPU 版模型才能在 DirectML 上跑；官方版一律走 CPU
            var useGpu = wantGpu && path == MangaModels.LamaGpuPath;

            if (_lama is not null && _lamaOnGpu == useGpu && _lamaPath == path)
            {
                return _lama;
            }

            _lama?.Dispose();
            _lamaOnGpu = useGpu;
            _lamaPath = path;
            _lama = new InferenceSession(path, CreateOptions(useGpu, interOpThreads: 1));
            return _lama;
        }
    }

    private bool _lamaOnGpu;
    private string? _lamaPath;

    /// <summary>去字模型当前跑在哪：DirectML (GPU) / CPU。</summary>
    public string LamaBackendName => _lama is null ? string.Empty : _lamaOnGpu ? "DirectML (GPU)" : "CPU";

    public string[] Dict
    {
        get
        {
            lock (SessionLock)
            {
                return _dict ??= System.Text.Json.JsonSerializer.Deserialize<string[]>(
                                    File.ReadAllText(MangaModels.DictPath))
                                ?? Array.Empty<string>();
            }
        }
    }

    private void EnsureProbed()
    {
        if (_probed)
        {
            return;
        }

        _probed = true;

        if (!_gpuRequested || Environment.GetEnvironmentVariable("PIXIV_MANGA_FORCE_CPU") == "1")
        {
            _deviceAvailable = false;
            BackendName = _gpuRequested ? "CPU" : "CPU（GPU 加速已关闭）";
            return;
        }

        try
        {
            using var probe = new SessionOptions();
            probe.AppendExecutionProvider_DML(0);
            using var session = new InferenceSession(MangaModels.DetectorPath, probe);
            _deviceAvailable = true;
            _deviceId = 0;
            BackendName = "DirectML (GPU)";
        }
        catch (Exception)
        {
            _deviceAvailable = false;
            _deviceId = -1;
            BackendName = "CPU（未检测到可用 GPU）";
        }
    }

    private SessionOptions CreateOptions(bool useGpu, int? interOpThreads = null)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            EnableCpuMemArena = true,
            InterOpNumThreads = interOpThreads ?? 1,
        };

        if (useGpu && _deviceAvailable && _deviceId >= 0)
        {
            options.AppendExecutionProvider_DML(_deviceId);
        }

        return options;
    }

    /// <summary>模型是否都已就绪（未下载时不要触碰会话属性）。</summary>
    public bool IsReady => MangaModels.IsInstalled;

    private void DisposeSessions()
    {
        _detector?.Dispose();
        _ocr?.Dispose();
        _lama?.Dispose();
        _detector = null;
        _ocr = null;
        _lama = null;
        _probed = false;
        _deviceAvailable = false;
    }

    public void Dispose()
    {
        lock (SessionLock)
        {
            DisposeSessions();
        }
    }
}
