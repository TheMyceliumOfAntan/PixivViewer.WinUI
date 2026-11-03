using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pixiv_Viewer_WinUI.Services.Manga;

/// <summary>
/// 漫画翻译对外的唯一入口：负责模型就绪检查、取图、跑流水线、读写缓存。
/// UI 只和这个类打交道。
/// </summary>
internal static class MangaTranslationService
{
    /// <summary>模型是否已下载（没下载时 UI 先引导下载）。</summary>
    public static bool IsReady => MangaModels.IsInstalled;

    /// <summary>LaMa 高精度去字模型是否已下载。</summary>
    public static bool IsLamaReady => MangaModels.IsLamaInstalled;

    /// <summary>当前推理后端：DirectML / CPU（未初始化时为空）。</summary>
    public static string BackendName => MangaBackend.Instance.IsReady ? MangaBackend.Instance.BackendName : string.Empty;

    /// <summary>去字（LaMa）当前后端：DirectML (GPU) / CPU；未使用去字模型时为空。</summary>
    public static string LamaBackendName => MangaBackend.Instance.LamaBackendName;

    /// <summary>基础模型总大小（用于下载提示）。</summary>
    public static long ModelSize => MangaModels.TotalSize;

    /// <summary>LaMa 模型大小。</summary>
    public static long LamaSize => MangaModels.LamaFile.Size;

    public static Task<bool> EnsureModelsAsync(IProgress<MangaProgress>? progress, CancellationToken cancellationToken)
        => MangaModelStore.EnsureAsync(progress, cancellationToken);

    /// <summary>下载可选的 LaMa 去字模型。</summary>
    public static Task<bool> EnsureLamaAsync(IProgress<MangaProgress>? progress, CancellationToken cancellationToken)
        => MangaModelStore.EnsureLamaAsync(progress, cancellationToken);

    /// <summary>是否已有下载任务在跑（防重复点击）。</summary>
    public static bool IsDownloading => MangaModelStore.IsDownloading;

    /// <summary>模型保存目录。</summary>
    public static string ModelDirectory => MangaModelStore.Directory;

    /// <summary>未完成的 .part 文件大小（用于提示「可续传」）。</summary>
    public static long PartialBytes
    {
        get
        {
            try
            {
                if (!Directory.Exists(MangaModels.Directory))
                {
                    return 0;
                }

                return new DirectoryInfo(MangaModels.Directory)
                    .EnumerateFiles("*.part")
                    .Sum(f => f.Length);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    public static void DeleteModels() => MangaModelStore.Delete();

    public static MangaPageResult? TryGetCached(string illustId, int pageIndex, MangaInpaintMode mode)
        => MangaCache.TryLoad(illustId, pageIndex, mode);

    /// <summary>只看缓存文件在不在，并且结构版本匹配（缩略图角标用）。</summary>
    public static bool HasCached(string illustId, int pageIndex, MangaInpaintMode mode)
    {
        if (!File.Exists(MangaCache.GetPngPath(illustId, pageIndex, mode)) ||
            !File.Exists(MangaCache.GetJsonPath(illustId, pageIndex, mode)))
        {
            return false;
        }

        return MangaCache.TryLoad(illustId, pageIndex, mode) is not null;
    }

    /// <summary>
    /// 翻译一页。命中缓存直接返回；否则取原图 → 识别 → 翻译 → 去字 → 落盘。
    /// </summary>
    public static async Task<MangaPageResult?> TranslatePageAsync(
        string illustId,
        int pageIndex,
        string? imageUrl,
        MangaInpaintMode inpaintMode,
        IProgress<MangaProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(illustId) || string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        // GPU 开关可能被改过，按最新设置准备会话
        MangaBackend.Instance.ApplyGpuSetting(AppServices.Settings.MangaGpuEnabled);

        if (TryGetCached(illustId, pageIndex, inpaintMode) is { } cached)
        {
            return cached;
        }

        if (!IsReady)
        {
            return null;
        }

        progress?.Report(new MangaProgress("下载原图…", 0.01));
        var bytes = await ImageCache.GetBytesAsync(imageUrl, cancellationToken).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        var bitmap = await MangaBitmap.DecodeAsync(bytes).ConfigureAwait(false);
        var result = await MangaPipeline
            .TranslateAsync(bitmap, illustId, pageIndex, inpaintMode, progress, cancellationToken)
            .ConfigureAwait(false);

        if (result.Png is { } png)
        {
            await MangaCache.SaveAsync(result, png, inpaintMode, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>把缓存的译图读成字节（UI 侧再解码成 BitmapImage）。</summary>
    public static byte[]? ReadCachedImage(MangaPageResult result)
    {
        try
        {
            return File.Exists(result.ImagePath) ? File.ReadAllBytes(result.ImagePath) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
