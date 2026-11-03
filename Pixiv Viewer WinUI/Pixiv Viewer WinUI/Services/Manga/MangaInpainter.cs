using System;
using System.Collections.Generic;
using System.Linq;

namespace Pixiv_Viewer_WinUI.Services.Manga;

/// <summary>
/// 去字修复（经典算法，无需模型）。
///
/// 漫画对白大多落在纯色气泡里，用「从边界向内扩散取色 + 局部平滑」就能抹掉原文字，
/// 效果接近神经去字；彩色/网点背景会有轻微涂抹感，后续可替换为 LaMa 模型实现。
/// </summary>
internal static class MangaInpainter
{
    /// <summary>矩形外侧一圈像素的采样结果。</summary>
    public readonly record struct BorderSample(byte B, byte G, byte R, double StdDev, double Uniformity);

    /// <summary>采样矩形外侧一圈像素：中位色 + 灰度方差 + 与中位色接近的像素占比。</summary>
    public static BorderSample SampleBorder(
        MangaBitmap bitmap,
        (int X, int Y, int W, int H) rect,
        int ring = 4)
    {
        var samples = new List<(byte B, byte G, byte R)>();
        var x0 = Math.Max(0, rect.X - ring);
        var y0 = Math.Max(0, rect.Y - ring);
        var x1 = Math.Min(bitmap.Width - 1, rect.X + rect.W + ring);
        var y1 = Math.Min(bitmap.Height - 1, rect.Y + rect.H + ring);

        for (var x = x0; x <= x1; x++)
        {
            AddSample(bitmap, x, Math.Max(0, rect.Y - 1), samples);
            AddSample(bitmap, x, Math.Min(bitmap.Height - 1, rect.Y + rect.H), samples);
        }

        for (var y = y0; y <= y1; y++)
        {
            AddSample(bitmap, Math.Max(0, rect.X - 1), y, samples);
            AddSample(bitmap, Math.Min(bitmap.Width - 1, rect.X + rect.W), y, samples);
        }

        if (samples.Count == 0)
        {
            return new BorderSample(255, 255, 255, 0, 1);
        }

        // 主色聚类：把样本按灰度量化到 16 级，取最多的那一簇再取平均，
        // 这样即使环上混进文字边缘/网点，也能拿到气泡真正的主色（通常是白）。
        var buckets = new Dictionary<int, List<(byte B, byte G, byte R)>>();
        foreach (var s in samples)
        {
            var gray = (int)(0.114 * s.B + 0.587 * s.G + 0.299 * s.R) / 16;
            if (!buckets.TryGetValue(gray, out var list))
            {
                list = new List<(byte B, byte G, byte R)>();
                buckets[gray] = list;
            }

            list.Add(s);
        }

        var dominant = buckets.OrderByDescending(kv => kv.Value.Count).First().Value;
        var b = (byte)dominant.Average(s => s.B);
        var g = (byte)dominant.Average(s => s.G);
        var r = (byte)dominant.Average(s => s.R);

        double sum = 0, squareSum = 0;
        var medianGray = 0.114 * b + 0.587 * g + 0.299 * r;
        var close = 0;
        foreach (var s in samples)
        {
            var gray = 0.114 * s.B + 0.587 * s.G + 0.299 * s.R;
            sum += gray;
            squareSum += gray * gray;
            if (Math.Abs(gray - medianGray) <= 18)
            {
                close++;
            }
        }

        var mean = sum / samples.Count;
        var stddev = Math.Sqrt(Math.Max(0, squareSum / samples.Count - mean * mean));
        return new BorderSample(b, g, r, stddev, (double)close / samples.Count);
    }

    private static void AddSample(MangaBitmap bitmap, int x, int y, List<(byte B, byte G, byte R)> samples)
    {
        if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
        {
            return;
        }

        var i = (y * bitmap.Width + x) * 4;
        samples.Add((bitmap.Pixels[i], bitmap.Pixels[i + 1], bitmap.Pixels[i + 2]));
    }

    private static byte Median(List<byte> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>该区域背景是否接近纯色（纯色用经典算法又快又干净，不必上 LaMa）。</summary>
    public static bool IsFlatBackground(MangaBitmap bitmap, (int X, int Y, int W, int H) rect)
        => SampleBorder(bitmap, rect).Uniformity >= 0.5;

    /// <summary>抹掉给定的矩形区域，返回处理后的像素（原数组不被修改）。</summary>
    public static byte[] Inpaint(MangaBitmap bitmap, IReadOnlyList<(int X, int Y, int W, int H)> regions)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixels = (byte[])bitmap.Pixels.Clone();

        if (regions.Count == 0)
        {
            return pixels;
        }

        var mask = new bool[width * height];
        var flat = new bool[width * height];

        foreach (var rect in regions)
        {
            // 外扩：字形边缘/抗锯齿通常超出检测框几个像素，按尺寸自适应
            var margin = Math.Max(4, (int)(Math.Min(rect.W, rect.H) * 0.08));
            var x0 = Math.Max(0, rect.X - margin);
            var y0 = Math.Max(0, rect.Y - margin);
            var x1 = Math.Min(width, rect.X + rect.W + margin);
            var y1 = Math.Min(height, rect.Y + rect.H + margin);

            for (var row = y0; row < y1; row++)
            {
                var offset = row * width;
                for (var col = x0; col < x1; col++)
                {
                    mask[offset + col] = true;
                }
            }

            // 纯色气泡直接填主色，比扩散干净；背景复杂则交给扩散
            var sample = SampleBorder(bitmap, rect);
            if (sample.Uniformity >= 0.5)
            {
                var b = sample.B;
                var g = sample.G;
                var r = sample.R;
                for (var row = y0; row < y1; row++)
                {
                    var offset = row * width;
                    for (var col = x0; col < x1; col++)
                    {
                        flat[offset + col] = true;
                        var i = (offset + col) * 4;
                        pixels[i] = b;
                        pixels[i + 1] = g;
                        pixels[i + 2] = r;
                    }
                }
            }
        }

        DiffusionFill(pixels, mask, flat, width, height);
        Smooth(pixels, mask, width, height, passes: 1);
        return pixels;
    }

    /// <summary>从已知像素向内逐层扩散填色（类似 Telea 的简化版）；<paramref name="skip"/> 为已纯色填充的像素。</summary>
    private static void DiffusionFill(byte[] pixels, bool[] mask, bool[] skip, int width, int height)
    {
        var filled = new bool[mask.Length];
        var queue = new Queue<int>();

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (mask[index] && !skip[index] && HasKnownNeighbor(mask, width, height, x, y))
                {
                    queue.Enqueue(index);
                }
            }
        }

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            if (filled[index])
            {
                continue;
            }

            var x = index % width;
            var y = index / width;
            int bSum = 0, gSum = 0, rSum = 0, count = 0;

            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        continue;
                    }

                    var neighbor = ny * width + nx;
                    if (mask[neighbor] && !skip[neighbor] && !filled[neighbor])
                    {
                        continue;
                    }

                    var i = neighbor * 4;
                    bSum += pixels[i];
                    gSum += pixels[i + 1];
                    rSum += pixels[i + 2];
                    count++;
                }
            }

            if (count == 0)
            {
                // 整个区域都被遮住（极大色块）时给个中性填充
                var i2 = index * 4;
                pixels[i2] = 255;
                pixels[i2 + 1] = 255;
                pixels[i2 + 2] = 255;
            }
            else
            {
                var i2 = index * 4;
                pixels[i2] = (byte)(bSum / count);
                pixels[i2 + 1] = (byte)(gSum / count);
                pixels[i2 + 2] = (byte)(rSum / count);
            }

            filled[index] = true;

            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        continue;
                    }

                    var neighbor = ny * width + nx;
                    if (mask[neighbor] && !skip[neighbor] && !filled[neighbor])
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }
    }

    private static bool HasKnownNeighbor(bool[] mask, int width, int height, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                {
                    continue;
                }

                if (!mask[ny * width + nx])
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>只在遮罩区域内做 3x3 均值，消除扩散填充的块状痕迹。</summary>
    private static void Smooth(byte[] pixels, bool[] mask, int width, int height, int passes)
    {
        var buffer = new byte[pixels.Length];

        for (var pass = 0; pass < passes; pass++)
        {
            Buffer.BlockCopy(pixels, 0, buffer, 0, pixels.Length);

            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var index = y * width + x;
                    if (!mask[index])
                    {
                        continue;
                    }

                    int b = 0, g = 0, r = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var i = ((y + dy) * width + (x + dx)) * 4;
                            b += buffer[i];
                            g += buffer[i + 1];
                            r += buffer[i + 2];
                        }
                    }

                    var o = index * 4;
                    pixels[o] = (byte)(b / 9);
                    pixels[o + 1] = (byte)(g / 9);
                    pixels[o + 2] = (byte)(r / 9);
                }
            }
        }
    }
}
