using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Pixiv_Viewer_WinUI.Services.Manga;

/// <summary>
/// LaMa 神经去字（可选档，512×512 分块推理）。
///
/// 输出比经典算法更自然，适合彩色页 / 网点背景；模型约 208 MB，单独下载。
/// 只对含文字遮罩的分块做推理，并在一次 Run 里批量送 4 块，兼顾速度与显存。
/// </summary>
internal static class MangaLamaInpainter
{
    private const int Tile = 512;
    private const int BatchSize = 4;

    /// <summary>最近一次运行的诊断信息（自检/排查用）。</summary>
    public static string LastInfo { get; private set; } = string.Empty;

    /// <summary>抹掉给定区域，返回处理后的像素；失败时返回 null（调用方回退经典算法）。</summary>
    public static byte[]? Inpaint(
        MangaBitmap bitmap,
        IReadOnlyList<(int X, int Y, int W, int H)> regions,
        MangaBackend backend)
    {
        // 先试 GPU（DirectML），跑崩了就永久退回 CPU 再试一次
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var session = backend.Lama;
            if (session is null)
            {
                return null;
            }

            try
            {
                var result = Inpaint(bitmap, regions, session);
                LastInfo = $"tiles={regions.Count} backend={backend.LamaBackendName}";
                return result;
            }
            catch (Exception ex)
            {
                LastInfo = $"attempt{attempt + 1} FAILED: {ex.GetType().Name}: {ex.Message}";
                backend.DisableLamaGpu();
            }
        }

        return null;
    }

    /// <summary>抹掉给定区域，返回处理后的像素；失败时返回 null（调用方回退经典算法）。</summary>
    public static byte[]? Inpaint(
        MangaBitmap bitmap,
        IReadOnlyList<(int X, int Y, int W, int H)> regions,
        InferenceSession session)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var source = bitmap.Pixels;
        var result = (byte[])source.Clone();

        if (regions.Count == 0)
        {
            return result;
        }

        var mask = new bool[width * height];
        foreach (var (x, y, w, h) in regions)
        {
            var x0 = Math.Max(0, x - 3);
            var y0 = Math.Max(0, y - 3);
            var x1 = Math.Min(width, x + w + 3);
            var y1 = Math.Min(height, y + h + 3);

            for (var row = y0; row < y1; row++)
            {
                var offset = row * width;
                for (var col = x0; col < x1; col++)
                {
                    mask[offset + col] = true;
                }
            }
        }

        var tiles = new List<(int X, int Y)>();
        for (var ty = 0; ty < height; ty += Tile)
        {
            for (var tx = 0; tx < width; tx += Tile)
            {
                if (HasMask(mask, width, height, tx, ty))
                {
                    tiles.Add((tx, ty));
                }
            }
        }

        if (tiles.Count == 0)
        {
            return result;
        }

        try
        {
            var inputName = session.InputMetadata.Keys.First();
            var maskName = session.InputMetadata.Keys.Skip(1).FirstOrDefault() ?? "mask";
            var runs = 0;

            for (var offset = 0; offset < tiles.Count; offset += BatchSize)
            {
                var batch = tiles.Skip(offset).Take(BatchSize).ToList();
                runs++;
                var count = batch.Count;
                var images = new DenseTensor<float>(new[] { count, 3, Tile, Tile });
                var masks = new DenseTensor<float>(new[] { count, 1, Tile, Tile });

                for (var i = 0; i < count; i++)
                {
                    FillTile(source, width, height, batch[i].X, batch[i].Y, mask, images, masks, i);
                }

                using var outputs = session.Run(new[]
                {
                    NamedOnnxValue.CreateFromTensor(inputName, images),
                    NamedOnnxValue.CreateFromTensor(maskName, masks),
                });

                var output = outputs.First().AsTensor<float>();
                for (var i = 0; i < count; i++)
                {
                    ApplyTile(result, width, height, batch[i].X, batch[i].Y, mask, output, i);
                }
            }

            LastInfo = $"tiles={tiles.Count} runs={runs}";
            return result;
        }
        catch (Exception ex)
        {
            LastInfo = "FAILED: " + ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private static bool HasMask(bool[] mask, int width, int height, int x0, int y0)
    {
        var x1 = Math.Min(width, x0 + Tile);
        var y1 = Math.Min(height, y0 + Tile);

        for (var y = y0; y < y1; y += 2)
        {
            var offset = y * width;
            for (var x = x0; x < x1; x += 2)
            {
                if (mask[offset + x])
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void FillTile(
        byte[] source,
        int width,
        int height,
        int x0,
        int y0,
        bool[] mask,
        DenseTensor<float> images,
        DenseTensor<float> masks,
        int index)
    {
        for (var y = 0; y < Tile; y++)
        {
            var sourceY = Math.Min(height - 1, y0 + y);
            for (var x = 0; x < Tile; x++)
            {
                var sourceX = Math.Min(width - 1, x0 + x);
                var i = (sourceY * width + sourceX) * 4;

                // LaMa 官方推理用 cv2.imread（BGR），这里保持 BGR 顺序
                images[index, 0, y, x] = source[i] / 255f;
                images[index, 1, y, x] = source[i + 1] / 255f;
                images[index, 2, y, x] = source[i + 2] / 255f;
                masks[index, 0, y, x] = mask[sourceY * width + sourceX] ? 1f : 0f;
            }
        }
    }

    private static void ApplyTile(
        byte[] target,
        int width,
        int height,
        int x0,
        int y0,
        bool[] mask,
        Tensor<float> output,
        int index)
    {
        for (var y = 0; y < Tile; y++)
        {
            var targetY = y0 + y;
            if (targetY >= height)
            {
                break;
            }

            for (var x = 0; x < Tile; x++)
            {
                var targetX = x0 + x;
                if (targetX >= width)
                {
                    break;
                }

                var index2 = targetY * width + targetX;
                if (!mask[index2])
                {
                    continue;
                }

                var i = index2 * 4;
                target[i] = ToByte(output[index, 0, y, x]);
                target[i + 1] = ToByte(output[index, 1, y, x]);
                target[i + 2] = ToByte(output[index, 2, y, x]);
            }
        }
    }

    private static byte ToByte(float value)
        => (byte)Math.Clamp(value * 255f, 0f, 255f);
}
