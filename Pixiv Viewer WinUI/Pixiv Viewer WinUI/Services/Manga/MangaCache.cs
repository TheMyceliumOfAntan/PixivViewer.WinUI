using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pixiv_Viewer_WinUI.Services.Manga;

/// <summary>原始行框（归一化坐标），用于精确擦除原文。</summary>
internal sealed class MangaLine
{
    public double X { get; set; }

    public double Y { get; set; }

    public double W { get; set; }

    public double H { get; set; }
}

/// <summary>一个译文区域（坐标归一化到 0~1，便于任意缩放渲染）。</summary>
internal sealed class MangaRegion
{
    public double X { get; set; }

    public double Y { get; set; }

    public double W { get; set; }

    public double H { get; set; }

    public string Source { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public bool Vertical { get; set; }

    /// <summary>译文字底板颜色（取气泡底色），UI 叠层用它铺底避免残影。</summary>
    public string PlateColor { get; set; } = "#FFFFFF";

    public float Score { get; set; }

    /// <summary>该文字块包含的原始行框（同一气泡的多列/多行）。</summary>
    public List<MangaLine> Lines { get; set; } = new();

    /// <summary>是否落在识别出的气泡内（气泡内可整块擦除，更干净）。</summary>
    public bool InBubble { get; set; }

    /// <summary>排版区域（气泡包围盒，归一化）；为 0 时退回 X/Y/W/H。</summary>
    public double AreaX { get; set; }

    public double AreaY { get; set; }

    public double AreaW { get; set; }

    public double AreaH { get; set; }
}

/// <summary>一页的翻译结果（图片路径 + 区域文本）。</summary>
internal sealed class MangaPageResult
{
    /// <summary>缓存结构版本：升级后旧结果自动失效（避免沿用有问题的排版）。</summary>
    public int Version { get; set; }

    public string IllustId { get; set; } = string.Empty;

    public int PageIndex { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public string ImagePath { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? Png { get; set; }

    public List<MangaRegion> Regions { get; set; } = new();
}

internal sealed record MangaProgress(
    string Stage,
    double Fraction,
    long ReceivedBytes = 0,
    long TotalBytes = 0,
    string FileName = "");

/// <summary>译文缓存：%LOCALAPPDATA%\PixivViewer\manga\&lt;作品ID&gt;\p&lt;页码&gt;.png|.json</summary>
internal static class MangaCache
{
    /// <summary>当前缓存结构版本（改了分组/排版逻辑就 +1）。</summary>
    public const int SchemaVersion = 7;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixivViewer",
        "manga");

    public static string GetPngPath(string illustId, int pageIndex, MangaInpaintMode mode)
        => Path.Combine(Root, Sanitize(illustId), $"p{pageIndex}{Suffix(mode)}.png");

    public static string GetJsonPath(string illustId, int pageIndex, MangaInpaintMode mode)
        => Path.Combine(Root, Sanitize(illustId), $"p{pageIndex}{Suffix(mode)}.json");

    private static string Suffix(MangaInpaintMode mode)
        => mode == MangaInpaintMode.Lama ? "-lama" : string.Empty;

    public static MangaPageResult? TryLoad(string illustId, int pageIndex, MangaInpaintMode mode)
    {
        try
        {
            var json = GetJsonPath(illustId, pageIndex, mode);
            var png = GetPngPath(illustId, pageIndex, mode);
            if (!File.Exists(json) || !File.Exists(png))
            {
                return null;
            }

            var result = JsonSerializer.Deserialize<MangaPageResult>(File.ReadAllText(json), JsonOptions);
            if (result is null || result.Version < SchemaVersion)
            {
                return null;
            }

            result.ImagePath = png;
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async Task SaveAsync(
        MangaPageResult result,
        byte[] pngBytes,
        MangaInpaintMode mode,
        CancellationToken cancellationToken)
    {
        var json = GetJsonPath(result.IllustId, result.PageIndex, mode);
        var png = GetPngPath(result.IllustId, result.PageIndex, mode);
        Directory.CreateDirectory(Path.GetDirectoryName(json)!);

        await File.WriteAllBytesAsync(png, pngBytes, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(json, JsonSerializer.Serialize(result, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        result.ImagePath = png;
    }

    public static long GetSize()
    {
        try
        {
            return Directory.Exists(Root)
                ? new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static void Clear()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
        catch (Exception)
        {
            // 文件被占用时忽略
        }
    }

    /// <summary>删除某一页的缓存（两种去字模式一起删，「重新翻译」用）。</summary>
    public static void DeletePage(string illustId, int pageIndex)
    {
        foreach (var mode in new[] { MangaInpaintMode.Classic, MangaInpaintMode.Lama })
        {
            foreach (var path in new[] { GetPngPath(illustId, pageIndex, mode), GetJsonPath(illustId, pageIndex, mode) })
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

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return value;
    }
}
