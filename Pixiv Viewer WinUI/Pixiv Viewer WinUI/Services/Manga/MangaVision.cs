using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Pixiv_Viewer_WinUI.Services.Manga;

/// <summary>一行文字的检测框（图像像素坐标）。</summary>
internal sealed record MangaLineBox(int X, int Y, int W, int H, float Score, float Density);

/// <summary>
/// 文本检测：comictextdetector（YOLOv5 主干 + DB 文本行分支）。
/// 用 <c>det[0]</c>（文本行概率图）取行框，等价上游 SegDetectorRepresenter 的二值化 + 轮廓。
/// </summary>
internal static class MangaDetector
{
    public const int InputSize = 1024;

    /// <summary>概率图阈值，对应上游 <c>SegDetectorRepresenter(thresh=0.3)</c>。</summary>
    private const float MaskThreshold = 0.3f;

    public static List<MangaLineBox> Detect(InferenceSession session, MangaBitmap bitmap)
    {
        var input = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        var scale = Math.Min((double)InputSize / bitmap.Width, (double)InputSize / bitmap.Height);
        var newW = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var newH = Math.Max(1, (int)Math.Round(bitmap.Height * scale));

        var pixels = bitmap.Pixels;
        for (var y = 0; y < newH; y++)
        {
            var sourceY = Math.Min(bitmap.Height - 1, (int)(y / scale));
            for (var x = 0; x < newW; x++)
            {
                var sourceX = Math.Min(bitmap.Width - 1, (int)(x / scale));
                var i = (sourceY * bitmap.Width + sourceX) * 4;
                input[0, 0, y, x] = pixels[i + 2] / 255f;
                input[0, 1, y, x] = pixels[i + 1] / 255f;
                input[0, 2, y, x] = pixels[i] / 255f;
            }
        }

        var inputName = session.InputMetadata.Keys.First();
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });

        var det = results.First(r => r.Name == "det").AsTensor<float>().ToArray();
        var mask = new bool[InputSize * InputSize];
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = det[i] > MaskThreshold;
        }

        // 轻膨胀把断开的笔画连成一行
        var grown = Morphology.Dilate(mask, InputSize, InputSize, 1);
        var components = Morphology.Label(grown, InputSize, InputSize, minArea: 90);

        var boxes = new List<MangaLineBox>(components.Count);
        foreach (var component in components)
        {
            if (component.W < 6 || component.H < 6)
            {
                continue;
            }

            // 对应上游 SegDetectorRepresenter 的 unclip_ratio=1.5：
            // DB 输出的是收缩后的文字区域，不外扩会把字形边缘切掉
            var area = component.W * (double)component.H;
            var perimeter = 2.0 * (component.W + component.H);
            var expand = (int)Math.Round(area * 0.5 / perimeter);

            var left = Math.Max(0, component.X - expand);
            var top = Math.Max(0, component.Y - expand);
            var right = Math.Min(InputSize - 1, component.X + component.W + expand);
            var bottom = Math.Min(InputSize - 1, component.Y + component.H + expand);
            var boxW = Math.Max(1, right - left);
            var boxH = Math.Max(1, bottom - top);

            var score = Density(det, component);
            boxes.Add(new MangaLineBox(
                (int)(left / scale),
                (int)(top / scale),
                Math.Max(1, (int)(boxW / scale)),
                Math.Max(1, (int)(boxH / scale)),
                score,
                (float)component.Area / (component.W * component.H)));
        }

        // 阅读顺序：从上到下、从右到左（日式漫画）
        boxes.Sort((a, b) =>
        {
            var rowA = a.Y / Math.Max(1, a.H);
            var rowB = b.Y / Math.Max(1, b.H);
            return rowA != rowB ? rowA.CompareTo(rowB) : b.X.CompareTo(a.X);
        });

        return boxes;
    }

    private static float Density(float[] det, Morphology.Component component)
    {
        // 只在框内按网格抽样，避免大框遍历全部像素
        var sum = 0f;
        var count = 0;
        var stepX = Math.Max(1, component.W / 24);
        var stepY = Math.Max(1, component.H / 24);
        for (var y = component.Y; y < component.Y + component.H; y += stepY)
        {
            for (var x = component.X; x < component.X + component.W; x += stepX)
            {
                sum += det[y * InputSize + x];
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }
}

/// <summary>PPT-OCR 风格的单行识别（PP-OCRv6 medium rec，CTC 解码）。</summary>
internal sealed class MangaOcr
{
    private const int TargetHeight = 48;

    private readonly InferenceSession _session;
    private readonly string[] _dict;

    public MangaOcr(InferenceSession session, string[] dict)
    {
        _session = session;
        _dict = dict;
    }

    /// <summary>识别一张裁切图；<paramref name="vertical"/> 为 true 时先逆时针旋转 90°（竖排文字）。</summary>
    public (string Text, float Score) Recognize(byte[] crop, int width, int height, bool vertical)
    {
        var pixels = crop;
        var w = width;
        var h = height;

        if (vertical)
        {
            (pixels, w, h) = RotateCounterClockwise(crop, width, height);
        }

        var scale = (double)TargetHeight / h;
        var targetW = Math.Clamp((int)Math.Round(w * scale), 8, 3200);
        targetW = (targetW + 7) / 8 * 8;

        var input = new DenseTensor<float>(new[] { 1, 3, TargetHeight, targetW });
        for (var y = 0; y < TargetHeight; y++)
        {
            var sy = Math.Min(h - 1.0, (y + 0.5) * h / TargetHeight - 0.5);
            var y0 = Math.Max(0, (int)Math.Floor(sy));
            var y1 = Math.Min(h - 1, y0 + 1);
            var fy = Math.Max(0, sy - y0);

            for (var x = 0; x < targetW; x++)
            {
                var sx = Math.Min(w - 1.0, (x + 0.5) * w / targetW - 0.5);
                var x0 = Math.Max(0, (int)Math.Floor(sx));
                var x1 = Math.Min(w - 1, x0 + 1);
                var fx = Math.Max(0, sx - x0);

                var i00 = (y0 * w + x0) * 4;
                var i01 = (y0 * w + x1) * 4;
                var i10 = (y1 * w + x0) * 4;
                var i11 = (y1 * w + x1) * 4;

                for (var c = 0; c < 3; c++)
                {
                    var value =
                        pixels[i00 + c] * (1 - fx) * (1 - fy) +
                        pixels[i01 + c] * fx * (1 - fy) +
                        pixels[i10 + c] * (1 - fx) * fy +
                        pixels[i11 + c] * fx * fy;

                    // 模型按 BGR 训练：(v/255 - 0.5) / 0.5
                    input[0, c, y, x] = (float)(value / 127.5 - 1.0);
                }
            }
        }

        var inputName = _session.InputMetadata.Keys.First();
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
        var logits = results.First().AsTensor<float>();
        var steps = logits.Dimensions[1];
        var classes = logits.Dimensions[2];

        var builder = new System.Text.StringBuilder();
        var last = -1;
        var scoreSum = 0f;
        var scoreCount = 0;

        for (var t = 0; t < steps; t++)
        {
            var best = 0;
            var bestValue = float.MinValue;
            for (var c = 0; c < classes; c++)
            {
                var value = logits[0, t, c];
                if (value > bestValue)
                {
                    bestValue = value;
                    best = c;
                }
            }

            if (best == 0 || best == last)
            {
                last = best;
                continue;
            }

            last = best;
            scoreSum += bestValue;
            scoreCount++;
            builder.Append(best <= _dict.Length ? _dict[best - 1] : ' ');
        }

        return (builder.ToString(), scoreCount == 0 ? 0 : scoreSum / scoreCount);
    }

    private static (byte[] Pixels, int Width, int Height) RotateCounterClockwise(byte[] source, int width, int height)
    {
        var dst = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var nx = y;
                var ny = width - 1 - x;
                var si = (y * width + x) * 4;
                var di = (ny * height + nx) * 4;
                dst[di] = source[si];
                dst[di + 1] = source[si + 1];
                dst[di + 2] = source[si + 2];
                dst[di + 3] = source[si + 3];
            }
        }

        return (dst, height, width);
    }
}

/// <summary>二值形态学与连通域（替代 OpenCV 的最小实现）。</summary>
internal static class Morphology
{
    internal readonly record struct Component(int X, int Y, int W, int H, int Area);

    public static bool[] Dilate(bool[] source, int width, int height, int radius)
    {
        if (radius <= 0)
        {
            return source;
        }

        // 横纵分离的方形核，比逐像素卷积快一个量级
        var horizontal = new bool[source.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (!source[row + x])
                {
                    continue;
                }

                var from = Math.Max(0, x - radius);
                var to = Math.Min(width - 1, x + radius);
                for (var i = from; i <= to; i++)
                {
                    horizontal[row + i] = true;
                }
            }
        }

        var result = new bool[source.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (!horizontal[row + x])
                {
                    continue;
                }

                var from = Math.Max(0, y - radius);
                var to = Math.Min(height - 1, y + radius);
                for (var i = from; i <= to; i++)
                {
                    result[i * width + x] = true;
                }
            }
        }

        return result;
    }

    public static List<Component> Label(bool[] bits, int width, int height, int minArea)
    {
        var labels = new int[bits.Length];
        var components = new List<Component>();
        var stack = new Stack<int>();
        var next = 0;

        for (var start = 0; start < bits.Length; start++)
        {
            if (!bits[start] || labels[start] != 0)
            {
                continue;
            }

            next++;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0, area = 0;
            stack.Push(start);
            labels[start] = next;

            while (stack.Count > 0)
            {
                var p = stack.Pop();
                var px = p % width;
                var py = p / width;
                area++;
                if (px < minX) minX = px;
                if (py < minY) minY = py;
                if (px > maxX) maxX = px;
                if (py > maxY) maxY = py;

                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        var nx = px + dx;
                        var ny = py + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            continue;
                        }

                        var np = ny * width + nx;
                        if (bits[np] && labels[np] == 0)
                        {
                            labels[np] = next;
                            stack.Push(np);
                        }
                    }
                }
            }

            if (area >= minArea)
            {
                components.Add(new Component(minX, minY, maxX - minX + 1, maxY - minY + 1, area));
            }
        }

        return components;
    }
}
