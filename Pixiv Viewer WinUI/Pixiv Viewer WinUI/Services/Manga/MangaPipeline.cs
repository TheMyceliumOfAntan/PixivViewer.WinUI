using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pixiv_Viewer_WinUI.Services.Pixiv;

namespace Pixiv_Viewer_WinUI.Services.Manga;

/// <summary>
/// 漫画翻译流水线：文本检测 → 分方向 OCR → 批量翻译 → 去字修复 → 出图。
/// 纯本地识别/去字，只有识别出的文字会发给现有翻译服务（Index-Translate / MyMemory）。
/// </summary>
internal static class MangaPipeline
{
    private const float MinScore = 0.18f;
    private const int MinBoxSize = 10;

    /// <summary>上一次运行的分段耗时（诊断用）。</summary>
    public static string LastTimings { get; private set; } = string.Empty;

    /// <summary>上一次检测到的原始行框（诊断用）。</summary>
    public static IReadOnlyList<MangaLineBox> LastBoxes { get; private set; } = Array.Empty<MangaLineBox>();

    /// <summary>上一次每行的 OCR 结果（诊断用）。</summary>
    public static IReadOnlyList<string> LastLines { get; private set; } = Array.Empty<string>();

    public static async Task<MangaPageResult> TranslateAsync(
        MangaBitmap bitmap,
        string illustId,
        int pageIndex,
        MangaInpaintMode inpaintMode,
        IProgress<MangaProgress>? progress,
        CancellationToken cancellationToken)
    {
        var backend = MangaBackend.Instance;
        var result = new MangaPageResult
        {
            Version = MangaCache.SchemaVersion,
            IllustId = illustId,
            PageIndex = pageIndex,
            Width = bitmap.Width,
            Height = bitmap.Height,
        };

        // 1) 文本检测
        progress?.Report(new MangaProgress("检测文字区域…", 0.05));
        var stageWatch = System.Diagnostics.Stopwatch.StartNew();
        var boxes = await Task.Run(() => MangaDetector.Detect(backend.Detector, bitmap), cancellationToken)
            .ConfigureAwait(false);
        var detectMs = stageWatch.ElapsedMilliseconds;

        var pageArea = bitmap.Width * (double)bitmap.Height;
        LastBoxes = boxes;
        var candidates = SuppressOverlaps(boxes)
            .Where(b => b.Score >= MinScore && b.W >= MinBoxSize && b.H >= MinBoxSize)
            .Where(b => b.W * (double)b.H <= pageArea * 0.4)
            .Take(120)
            .ToList();

        // 2) 先按气泡合并碎片（一列竖排被截成几段时必须先拼回），再逐行 OCR
        progress?.Report(new MangaProgress("识别日文…", 0.25));
        var ocr = new MangaOcr(backend.Ocr, backend.Dict);
        var bubbleIds = MangaBubble.Assign(bitmap, candidates);
        var merged = MangaBubble.MergeFragments(candidates, bubbleIds.Ids);

        var lines = new List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)>(merged.Count);
        var lineNotes = new List<string>();
        var total = Math.Max(1, merged.Count);

        for (var i = 0; i < merged.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (box, bubble) = merged[i];

            var text = await Task.Run(() =>
            {
                try
                {
                    return Recognize(ocr, bitmap, box);
                }
                catch (Exception ex)
                {
                    // 单个框识别异常不能拖垮整页
                    PixivDiagnostics.Write($"manga ocr: box ({box.X},{box.Y}) {box.W}x{box.H} EX {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }, cancellationToken).ConfigureAwait(false);
            if (text is { } value && IsJapanese(value.Text))
            {
                lines.Add((box, value.Text, value.Score, value.Vertical, bubble));
                lineNotes.Add($"b{bubble} ({box.X},{box.Y}) {box.W}x{box.H} [{value.Score:F2}{(value.Vertical ? " V" : " H")}] {value.Text}");
            }
            else
            {
                lineNotes.Add($"b{bubble} ({box.X},{box.Y}) {box.W}x{box.H} DROP {(text is null ? "no-text" : $"[{text.Value.Score:F2}] {text.Value.Text}")}");
            }

            if (i % 4 == 0 || i == merged.Count - 1)
            {
                progress?.Report(new MangaProgress("识别日文…", 0.25 + 0.3 * (i + 1) / total));
            }
        }

        // 3) 按气泡归块：同一气泡的多列文字合起来当一句话翻译
        LastLines = lineNotes;
        var ocrMs = stageWatch.ElapsedMilliseconds - detectMs;
        progress?.Report(new MangaProgress("翻译…", 0.6));
        stageWatch.Restart();

        var regions = new List<MangaRegion>(lines.Count);
        foreach (var group in GroupLines(lines, bubbleIds.Bounds))
        {
            var vertical = group.Count(m => m.Vertical) >= group.Count / 2.0;
            group.Sort(vertical
                ? (a, b) => Math.Abs(a.Box.X - b.Box.X) > Math.Min(a.Box.W, b.Box.W)
                    ? b.Box.X.CompareTo(a.Box.X)
                    : a.Box.Y.CompareTo(b.Box.Y)
                : (a, b) => Math.Abs(a.Box.Y - b.Box.Y) > Math.Min(a.Box.H, b.Box.H)
                    ? a.Box.Y.CompareTo(b.Box.Y)
                    : b.Box.X.CompareTo(a.Box.X));

            var left = group.Min(m => m.Box.X);
            var top = group.Min(m => m.Box.Y);
            var right = group.Max(m => m.Box.X + m.Box.W);
            var bottom = group.Max(m => m.Box.Y + m.Box.H);
            var pad = Math.Max(3, (int)(Math.Min(right - left, bottom - top) * 0.04));

            var plate = MangaInpainter.SampleBorder(bitmap, (left, top, right - left, bottom - top));

            // 排版区域 = 原文字框 + 有限余量（宽 1.35×、高 1.15×），居中放置。
            // 不借用气泡包围盒：整页白底 / 大面板会被误当成气泡，导致底板糊住整页。
            var textWidth = Math.Max(1, right - left);
            var textHeight = Math.Max(1, bottom - top);
            var areaWidth = Math.Max(8, (int)(textWidth * 1.35));
            var areaHeight = Math.Max(8, (int)(textHeight * 1.15));
            var centerX = (left + right) / 2;
            var centerY = (top + bottom) / 2;
            var areaLeft = Math.Max(0, centerX - areaWidth / 2);
            var areaTop = Math.Max(0, centerY - areaHeight / 2);
            var areaRight = Math.Min(bitmap.Width, areaLeft + areaWidth);
            var areaBottom = Math.Min(bitmap.Height, areaTop + areaHeight);
            var bubbleId = PrimaryBubble(group);

            regions.Add(new MangaRegion
            {
                X = Math.Max(0, left - pad) / (double)bitmap.Width,
                Y = Math.Max(0, top - pad) / (double)bitmap.Height,
                W = (Math.Min(bitmap.Width, right + pad) - Math.Max(0, left - pad)) / (double)bitmap.Width,
                H = (Math.Min(bitmap.Height, bottom + pad) - Math.Max(0, top - pad)) / (double)bitmap.Height,
                AreaX = Math.Max(0, areaLeft) / (double)bitmap.Width,
                AreaY = Math.Max(0, areaTop) / (double)bitmap.Height,
                AreaW = (Math.Min(bitmap.Width, areaRight) - Math.Max(0, areaLeft)) / (double)bitmap.Width,
                AreaH = (Math.Min(bitmap.Height, areaBottom) - Math.Max(0, areaTop)) / (double)bitmap.Height,
                Source = string.Join(string.Empty, group.Select(m => m.Text)),
                Vertical = vertical,
                InBubble = bubbleId != 0,
                PlateColor = $"#{plate.R:X2}{plate.G:X2}{plate.B:X2}",
                Score = (float)group.Average(m => m.Score),
                Lines = group.Select(m => new MangaLine
                {
                    X = m.Box.X / (double)bitmap.Width,
                    Y = m.Box.Y / (double)bitmap.Height,
                    W = m.Box.W / (double)bitmap.Width,
                    H = m.Box.H / (double)bitmap.Height,
                }).ToList(),
            });
        }

        var sources = regions.Select(r => r.Source).ToList();
        if (sources.Count > 0)
        {
            var translated = await TranslationService
                .TranslateLinesAsync(sources, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < regions.Count && i < translated.Count; i++)
            {
                regions[i].Target = translated[i] ?? string.Empty;
            }
        }

        regions.RemoveAll(r => string.IsNullOrWhiteSpace(r.Target));
        result.Regions = regions;

        // 4) 去字修复：按行框精确擦除（块框会把整块气泡都抹掉）
        progress?.Report(new MangaProgress("擦除原文…", 0.72));
        var rects = new List<(int X, int Y, int W, int H)>();
        foreach (var region in regions)
        {
            if (region.InBubble || region.Lines.Count == 0)
            {
                rects.Add((
                    (int)Math.Round(region.X * bitmap.Width),
                    (int)Math.Round(region.Y * bitmap.Height),
                    (int)Math.Round(region.W * bitmap.Width),
                    (int)Math.Round(region.H * bitmap.Height)));
                continue;
            }

            foreach (var line in region.Lines)
            {
                rects.Add((
                    (int)Math.Round(line.X * bitmap.Width),
                    (int)Math.Round(line.Y * bitmap.Height),
                    (int)Math.Round(line.W * bitmap.Width),
                    (int)Math.Round(line.H * bitmap.Height)));
            }
        }

        var cleaned = await Task.Run(() =>
            {
                if (inpaintMode == MangaInpaintMode.Lama && backend.Lama is not null)
                {
                    // 纯色气泡用经典算法（更快且更干净），只有复杂背景才交给 LaMa，
                    // 这样高精度模式在常见漫画上只处理少量区域，GPU/CPU 都快得多
                    var flat = new List<(int X, int Y, int W, int H)>();
                    var hard = new List<(int X, int Y, int W, int H)>();
                    foreach (var rect in rects)
                    {
                        if (MangaInpainter.IsFlatBackground(bitmap, rect))
                        {
                            flat.Add(rect);
                        }
                        else
                        {
                            hard.Add(rect);
                        }
                    }

                    var basePixels = MangaInpainter.Inpaint(bitmap, flat);
                    if (hard.Count == 0)
                    {
                        return basePixels;
                    }

                    var baseBitmap = new MangaBitmap(bitmap.Width, bitmap.Height, basePixels);
                    var lamaResult = MangaLamaInpainter.Inpaint(baseBitmap, hard, backend);
                    return lamaResult ?? MangaInpainter.Inpaint(baseBitmap, hard);
                }

                return MangaInpainter.Inpaint(bitmap, rects);
            }, cancellationToken)
            .ConfigureAwait(false);
        var inpaintMs = stageWatch.ElapsedMilliseconds;
        stageWatch.Restart();

        progress?.Report(new MangaProgress("生成译图…", 0.9));
        var png = await MangaBitmap.ToPngBytesAsync(bitmap.Width, bitmap.Height, cleaned).ConfigureAwait(false);
        result.Png = png;

        LastTimings =
            $"detect={detectMs}ms ocr={ocrMs}ms translate={inpaintMs - ocrMs}ms " +
            $"inpaint={stageWatch.ElapsedMilliseconds}ms boxes={candidates.Count} kept={regions.Count} " +
            $"lama=[{MangaLamaInpainter.LastInfo}]";

        progress?.Report(new MangaProgress("完成", 1));
        return result;
    }

    /// <summary>
    /// 同一气泡连通域里可能夹着远处的文字（白色面板会把气泡和标题连成一片），
    /// 这里按「线段粗细」估一个距离阈值，把相距过远的分开。
    /// </summary>
    private static List<List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)>> SplitByProximity(
        List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)> group)
    {
        if (group.Count <= 1)
        {
            return new List<List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)>> { group };
        }

        var thickness = group
            .Select(m => Math.Min(m.Box.W, m.Box.H))
            .OrderBy(v => v)
            .ElementAt(group.Count / 2);

        // 竖排（多列并排）允许较大的横向间距；横排大多是并列的旁白框，只在极近时才合并
        var verticalLines = group.Count(m => m.Box.H > m.Box.W * 1.2) >= group.Count / 2.0;
        var limit = verticalLines
            ? Math.Max(60, thickness * 2.5)
            : Math.Max(24, thickness * 0.8);
        var visited = new bool[group.Count];
        var result = new List<List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)>>();

        for (var i = 0; i < group.Count; i++)
        {
            if (visited[i])
            {
                continue;
            }

            var cluster = new List<(MangaLineBox, string, float, bool, int)>();
            var queue = new Queue<int>();
            queue.Enqueue(i);
            visited[i] = true;

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                cluster.Add(group[current]);

                for (var j = 0; j < group.Count; j++)
                {
                    if (visited[j] || Gap(group[current].Box, group[j].Box) > limit)
                    {
                        continue;
                    }

                    visited[j] = true;
                    queue.Enqueue(j);
                }
            }

            result.Add(cluster);
        }

        return result;
    }

    private static double Gap(MangaLineBox a, MangaLineBox b)
    {
        var dx = Math.Max(0, Math.Max(a.X, b.X) - Math.Min(a.X + a.W, b.X + b.W));
        var dy = Math.Max(0, Math.Max(a.Y, b.Y) - Math.Min(a.Y + a.H, b.Y + b.H));
        return Math.Max(dx, dy);
    }

    /// <summary>
    /// 先按气泡分组，再把「气泡包围盒高度重叠」的组并起来。
    ///
    /// 为什么要合并：一个气泡里的竖排文字会把白色区域切成好几条，
    /// 同一句话的两列可能落到不同连通域，分开排版会生成两块互相重叠的底板。
    /// 为什么不能只按距离聚类：上下相邻的两个气泡可能只隔 100 多像素，会被误并成一句。
    /// </summary>
    private static List<List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)>> GroupLines(
        List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)> lines,
        Dictionary<int, (int X, int Y, int W, int H)> bounds)
    {
        var groups = new List<List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)>>();

        foreach (var line in lines)
        {
            List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)>? target = null;
            if (line.Bubble != 0)
            {
                target = groups.FirstOrDefault(g => g[0].Bubble == line.Bubble);
            }

            if (target is null)
            {
                target = new List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)>();
                groups.Add(target);
            }

            target.Add(line);
        }

        // 同一气泡连通域里也可能夹着远处的文字（白色面板会把气泡和标题连成一片），先按距离拆开
        var split = new List<List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)>>();
        foreach (var group in groups)
        {
            split.AddRange(SplitByProximity(group));
        }

        groups = split;

        // 合并包围盒高度重叠的组
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < groups.Count && !changed; i++)
            {
                for (var j = i + 1; j < groups.Count; j++)
                {
                    // 背景上的独立文字（气泡 id = 0）不按距离合并：那往往是并列的旁白框，
                    // 合并会把整页文字揉成一段
                    var bubbleI = PrimaryBubble(groups[i]);
                    var bubbleJ = PrimaryBubble(groups[j]);
                    if (bubbleI == 0 || bubbleJ == 0)
                    {
                        continue;
                    }

                    if (OverlapRatio(AreaOf(groups[i], bounds), AreaOf(groups[j], bounds)) <= 0.35)
                    {
                        continue;
                    }

                    // 包围盒重叠还不够：同一气泡可能被文字切成几块，但远处的标题不该被并进来
                    var thickness = Math.Max(
                        MedianThickness(groups[i]),
                        MedianThickness(groups[j]));

                    if (TextGap(groups[i], groups[j]) > Math.Max(40, thickness * 1.2))
                    {
                        continue;
                    }

                    groups[i].AddRange(groups[j]);
                    groups.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        return groups;
    }

    private static (int X, int Y, int W, int H) AreaOf(
        List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)> group,
        Dictionary<int, (int X, int Y, int W, int H)> bounds)
    {
        var bubble = PrimaryBubble(group);

        if (bubble != 0 && bounds.TryGetValue(bubble, out var rect))
        {
            return rect;
        }

        var left = group.Min(m => m.Box.X);
        var top = group.Min(m => m.Box.Y);
        var right = group.Max(m => m.Box.X + m.Box.W);
        var bottom = group.Max(m => m.Box.Y + m.Box.H);
        return (left, top, right - left, bottom - top);
    }

    /// <summary>组内出现最多的气泡编号（0 = 不在气泡里）。</summary>
    private static int PrimaryBubble(List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)> group)
        => group.Where(m => m.Bubble != 0)
            .GroupBy(m => m.Bubble)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .DefaultIfEmpty(0)
            .First();

    private static double OverlapRatio((int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.W, b.X + b.W);
        var y2 = Math.Min(a.Y + a.H, b.Y + b.H);
        var inter = Math.Max(0, x2 - x1) * (double)Math.Max(0, y2 - y1);
        var minArea = Math.Min(a.W * (double)a.H, b.W * (double)b.H);
        return minArea <= 0 ? 0 : inter / minArea;
    }

    private static double MedianThickness(List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)> group)
    {
        var values = group.Select(m => Math.Min(m.Box.W, m.Box.H)).OrderBy(v => v).ToList();
        return values[values.Count / 2];
    }

    private static double TextGap(
        List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)> a,
        List<(MangaLineBox Box, string Text, float Score, bool Vertical, int Bubble)> b)
    {
        var best = double.MaxValue;
        foreach (var x in a)
        {
            foreach (var y in b)
            {
                var dx = Math.Max(0, Math.Max(x.Box.X, y.Box.X) - Math.Min(x.Box.X + x.Box.W, y.Box.X + y.Box.W));
                var dy = Math.Max(0, Math.Max(x.Box.Y, y.Box.Y) - Math.Min(x.Box.Y + x.Box.H, y.Box.Y + y.Box.H));
                var gap = Math.Max(dx, dy);
                if (gap < best)
                {
                    best = gap;
                }
            }
        }

        return best == double.MaxValue ? 0 : best;
    }

    private static (string Text, float Score, bool Vertical)? Recognize(
        MangaOcr ocr,
        MangaBitmap bitmap,
        MangaLineBox box)
    {
        var padX = Math.Max(2, (int)(box.W * 0.06));
        var padY = Math.Max(2, (int)(box.H * 0.06));
        var x0 = Math.Clamp(box.X - padX, 0, bitmap.Width - 1);
        var y0 = Math.Clamp(box.Y - padY, 0, bitmap.Height - 1);
        var x1 = Math.Clamp(box.X + box.W + padX, x0 + 1, bitmap.Width);
        var y1 = Math.Clamp(box.Y + box.H + padY, y0 + 1, bitmap.Height);
        var width = x1 - x0;
        var height = y1 - y0;

        var crop = bitmap.Crop(x0, y0, width, height);

        // 方向预判：明显竖排/横排只跑一次，接近正方形才两个方向都试。
        // 注意：跳过的方向也要给「空字符串 + 0 分」，不能用 default（Text 为 null 会让后面 Trim 崩）。
        var verticalOnly = height > width * 1.15;
        var horizontalOnly = width > height * 1.15;

        (string Text, float Score) horizontal = (string.Empty, 0f);
        (string Text, float Score) vertical = (string.Empty, 0f);

        if (!verticalOnly)
        {
            horizontal = ocr.Recognize(crop, width, height, vertical: false);
        }

        if (!horizontalOnly)
        {
            vertical = ocr.Recognize(crop, width, height, vertical: true);
        }

        var preferVertical = height > width * 1.2;
        var best = preferVertical
            ? (vertical.Score >= horizontal.Score ? (vertical, true) : (horizontal, false))
            : (horizontal.Score >= vertical.Score ? (horizontal, false) : (vertical, true));

        var text = (best.Item1.Text ?? string.Empty).Trim();
        if (best.Item1.Score < 0.2f || !HasMeaningfulText(text))
        {
            // 主方向不可信时，另一方向若明显更好也接受
            var other = preferVertical ? horizontal : vertical;
            var otherText = (other.Text ?? string.Empty).Trim();
            if (other.Score >= Math.Max(0.35f, best.Item1.Score + 0.1f) && HasMeaningfulText(otherText))
            {
                return (otherText, other.Score, !preferVertical);
            }

            return null;
        }

        return (text, best.Item1.Score, best.Item2);
    }

    /// <summary>日语文本校验：至少一半字符是假名/汉字（滤掉把脸/装饰识别成 "JA" 这类垃圾）。</summary>
    private static bool IsJapanese(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var meaningful = 0;
        var japanese = 0;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c))
            {
                continue;
            }

            meaningful++;
            if (c >= '\u3040' && c <= '\u30ff')      // 平假名 + 片假名
            {
                japanese++;
            }
            else if (c >= '\u4e00' && c <= '\u9fff') // 汉字
            {
                japanese++;
            }
        }

        return meaningful > 0 && japanese >= meaningful * 0.5;
    }

    private static bool HasMeaningfulText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var meaningful = 0;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c))
            {
                meaningful++;
            }
        }

        return meaningful >= 2 || (meaningful == 1 && text.Trim().Length <= 2);
    }

    /// <summary>去掉高度重叠的框（同一行被拆成多个连通域时会出现）。</summary>
    private static IEnumerable<MangaLineBox> SuppressOverlaps(List<MangaLineBox> boxes)
    {
        var ordered = boxes.OrderByDescending(b => b.Score).ToList();
        var kept = new List<MangaLineBox>();

        foreach (var box in ordered)
        {
            var overlap = false;
            foreach (var existing in kept)
            {
                if (IntersectionOverUnion(box, existing) > 0.35)
                {
                    overlap = true;
                    break;
                }
            }

            if (!overlap)
            {
                kept.Add(box);
            }
        }

        return kept;
    }

    private static double IntersectionOverUnion(MangaLineBox a, MangaLineBox b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.W, b.X + b.W);
        var y2 = Math.Min(a.Y + a.H, b.Y + b.H);
        var intersection = Math.Max(0, x2 - x1) * (double)Math.Max(0, y2 - y1);
        var union = a.W * (double)a.H + b.W * (double)b.H - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}
