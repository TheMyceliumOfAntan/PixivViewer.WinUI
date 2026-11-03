using System;
using System.Collections.Generic;
using System.Linq;

namespace Pixiv_Viewer_WinUI.Services.Manga;

/// <summary>
/// 气泡分组：把同一气泡里的多列/多行文字归成一个「文字块」，整块一起翻译。
///
/// 做法：对整页做「浅色像素连通域」（气泡基本都是白底），
/// 再采样每个行框外侧一圈像素，落到哪个连通域就属于哪个气泡。
/// 这样「私が / リーダーに / なる」这种一句话拆成多列的排版能正确合成一句。
/// </summary>
internal static class MangaBubble
{
    private const int Size = MangaDetector.InputSize;
    private const byte LightThreshold = 185;

    /// <summary>返回每个行框所属的气泡编号（0 = 不在任何气泡里），以及每个气泡的包围盒（1024 空间）。</summary>
    public static (int[] Ids, Dictionary<int, (int X, int Y, int W, int H)> Bounds) Assign(
        MangaBitmap bitmap,
        IReadOnlyList<MangaLineBox> boxes)
    {
        if (boxes.Count == 0)
        {
            return (Array.Empty<int>(), new Dictionary<int, (int, int, int, int)>());
        }

        var labels = BuildLabels(bitmap, out var bounds);
        var scaleX = (double)Size / bitmap.Width;
        var scaleY = (double)Size / bitmap.Height;
        var result = new int[boxes.Count];

        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            var x0 = (int)(box.X * scaleX);
            var y0 = (int)(box.Y * scaleY);
            var x1 = (int)((box.X + box.W) * scaleX);
            var y1 = (int)((box.Y + box.H) * scaleY);

            // 在框外 6px 的一圈上采样（气泡底色，避开文字本身）
            var margin = 6;
            var best = 0;
            var counts = new Dictionary<int, int>();

            void Sample(int x, int y)
            {
                if (x < 0 || y < 0 || x >= Size || y >= Size)
                {
                    return;
                }

                var id = labels[y * Size + x];
                if (id == 0)
                {
                    return;
                }

                counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
                if (counts[id] > best)
                {
                    best = id;
                }
            }

            for (var x = x0 - margin; x <= x1 + margin; x += 4)
            {
                Sample(x, y0 - margin);
                Sample(x, y1 + margin);
                Sample(x0 - margin, (y0 + y1) / 2);
                Sample(x1 + margin, (y0 + y1) / 2);
            }

            // 外侧没采到（比如文字贴边），退回框内四角
            if (best == 0)
            {
                Sample(x0 + 2, y0 + 2);
                Sample(x1 - 2, y0 + 2);
                Sample(x0 + 2, y1 - 2);
                Sample(x1 - 2, y1 - 2);
            }

            result[i] = best;
        }

        return (result, bounds);
    }

    private static int[] BuildLabels(
        MangaBitmap bitmap,
        out Dictionary<int, (int X, int Y, int W, int H)> bounds)
    {
        bounds = new Dictionary<int, (int X, int Y, int W, int H)>();

        // 超过整页 1/4 的浅色连通域视为页面背景，不当气泡
        var backgroundLimit = (int)(Size * (long)Size * 0.25);

        var light = new bool[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            var sourceY = Math.Min(bitmap.Height - 1, (int)((long)y * bitmap.Height / Size));
            for (var x = 0; x < Size; x++)
            {
                var sourceX = Math.Min(bitmap.Width - 1, (int)((long)x * bitmap.Width / Size));
                var i = (sourceY * bitmap.Width + sourceX) * 4;
                var luminance = 0.114 * bitmap.Pixels[i] + 0.587 * bitmap.Pixels[i + 1] + 0.299 * bitmap.Pixels[i + 2];
                light[y * Size + x] = luminance >= LightThreshold;
            }
        }

        var labels = new int[Size * Size];
        var stack = new Stack<int>();
        var next = 0;
        var minArea = 600;

        for (var start = 0; start < light.Length; start++)
        {
            if (!light[start] || labels[start] != 0)
            {
                continue;
            }

            next++;
            var area = 0;
            var id = next;
            var pixels = new List<int>();
            int minX = Size, minY = Size, maxX = 0, maxY = 0;
            stack.Push(start);
            labels[start] = id;

            while (stack.Count > 0)
            {
                var p = stack.Pop();
                var px = p % Size;
                var py = p / Size;
                area++;
                pixels.Add(p);
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
                        if (nx < 0 || ny < 0 || nx >= Size || ny >= Size)
                        {
                            continue;
                        }

                        var np = ny * Size + nx;
                        if (light[np] && labels[np] == 0)
                        {
                            labels[np] = id;
                            stack.Push(np);
                        }
                    }
                }
            }

            if (area < minArea)
            {
                // 太小的白块（网点孔洞之类）不算气泡
                foreach (var p in pixels)
                {
                    labels[p] = 0;
                    light[p] = false;   // 标记为已处理，避免外层扫描再次把它当成新区域
                }
            }
            else if (area >= backgroundLimit)
            {
                // 整页白底也会连成一个巨大的连通域：它不是气泡，
                // 否则同一页所有文字都会被归到同一个「气泡」里并排成一块
                foreach (var p in pixels)
                {
                    labels[p] = 0;
                    light[p] = false;
                }
            }
            else
            {
                bounds[id] = (minX, minY, maxX - minX + 1, maxY - minY + 1);
            }
        }

        return labels;
    }

    /// <summary>
    /// 合并同一气泡内的碎片框：检测会把一列竖排文字截成几段（如「リ」+「ーダーに」），
    /// 直接逐段 OCR 会读成乱码，必须先按「同列/同行」拼回一条。
    /// </summary>
    public static List<(MangaLineBox Box, int Bubble)> MergeFragments(
        IReadOnlyList<MangaLineBox> boxes,
        int[] bubbleIds,
        Dictionary<int, (int X, int Y, int W, int H)> bounds)
    {
        var merged = new List<(MangaLineBox Box, int Bubble)>(boxes.Count);
        for (var i = 0; i < boxes.Count; i++)
        {
            merged.Add((boxes[i], bubbleIds[i]));
        }

        // 气泡「紧凑度」：真实气泡的白色区域与文字面积相称；
        // 整块白背景 / 大面板会远大于文字面积，这种区域里的多行文字不能合并
        var compact = new Dictionary<int, bool>();
        foreach (var id in bubbleIds.Where(id => id != 0).Distinct())
        {
            if (!bounds.TryGetValue(id, out var rect) || rect.W <= 0 || rect.H <= 0)
            {
                compact[id] = false;
                continue;
            }

            var textArea = 0.0;
            for (var i = 0; i < boxes.Count; i++)
            {
                if (bubbleIds[i] == id)
                {
                    textArea += boxes[i].W * (double)boxes[i].H;
                }
            }

            var bubbleArea = rect.W * (double)rect.H;
            compact[id] = textArea > 0 && bubbleArea <= textArea * 2.2;
        }

        var changed = true;
        while (changed)
        {
            changed = false;

            for (var i = 0; i < merged.Count && !changed; i++)
            {
                for (var j = i + 1; j < merged.Count; j++)
                {
                    // 只合并「同一气泡内」的碎片：
                    // 背景上的旁白框常常上下紧挨且左右对齐，按 sameColumn 会被并成一整段
                    if (merged[i].Bubble == 0 || merged[i].Bubble != merged[j].Bubble)
                    {
                        continue;
                    }

                    if (!compact.TryGetValue(merged[i].Bubble, out var isCompact) || !isCompact)
                    {
                        continue;
                    }

                    if (!SameLine(merged[i].Box, merged[j].Box))
                    {
                        continue;
                    }

                    merged[i] = (Union(merged[i].Box, merged[j].Box), merged[i].Bubble);
                    merged.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        return merged;
    }

    private static bool SameLine(MangaLineBox a, MangaLineBox b)
    {
        var aVertical = a.H > a.W * 1.2;
        var bVertical = b.H > b.W * 1.2;

        // 竖排/横排不混并（同一气泡里的两列是两行，交给气泡分组）
        if (aVertical != bVertical)
        {
            return false;
        }

        var overlapX = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
        var overlapY = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);

        var gapY = Math.Max(0, Math.Max(a.Y, b.Y) - Math.Min(a.Y + a.H, b.Y + b.H));
        var gapX = Math.Max(0, Math.Max(a.X, b.X) - Math.Min(a.X + a.W, b.X + b.W));

        return aVertical
            ? overlapX > Math.Min(a.W, b.W) * 0.5 && gapY <= Math.Min(a.H, b.H) * 0.45
            : overlapY > Math.Min(a.H, b.H) * 0.5 && gapX <= Math.Min(a.W, b.W) * 0.45;
    }

    private static MangaLineBox Union(MangaLineBox a, MangaLineBox b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.X + a.W, b.X + b.W);
        var bottom = Math.Max(a.Y + a.H, b.Y + b.H);
        return new MangaLineBox(left, top, right - left, bottom - top, Math.Max(a.Score, b.Score), Math.Max(a.Density, b.Density));
    }

    /// <summary>按气泡分组，组内按日式阅读顺序排序（竖排从右到左、横排从上到下）。</summary>
    public static List<List<MangaLineBox>> Group(
        IReadOnlyList<MangaLineBox> boxes,
        int[] bubbleIds)
    {
        var groups = new List<List<MangaLineBox>>();
        var byBubble = new Dictionary<int, List<MangaLineBox>>();

        for (var i = 0; i < boxes.Count; i++)
        {
            var id = bubbleIds[i];
            if (id == 0)
            {
                groups.Add(new List<MangaLineBox> { boxes[i] });
                continue;
            }

            if (!byBubble.TryGetValue(id, out var list))
            {
                list = new List<MangaLineBox>();
                byBubble[id] = list;
            }

            list.Add(boxes[i]);
        }

        foreach (var list in byBubble.Values)
        {
            var vertical = list.Average(b => b.H > b.W * 1.2 ? 1 : 0) >= 0.5;
            list.Sort(vertical
                ? (a, b) =>
                {
                    var dx = b.X.CompareTo(a.X);
                    return Math.Abs(a.X - b.X) > Math.Min(a.W, b.W) ? dx : a.Y.CompareTo(b.Y);
                }
                : (a, b) =>
                {
                    var dy = a.Y.CompareTo(b.Y);
                    return Math.Abs(a.Y - b.Y) > Math.Min(a.H, b.H) ? dy : b.X.CompareTo(a.X);
                });

            groups.Add(list);
        }

        return groups;
    }
}
