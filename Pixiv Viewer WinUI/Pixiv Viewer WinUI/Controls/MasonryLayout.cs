using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pixiv_Viewer_WinUI.Services.Pixiv;
using Windows.Foundation;

namespace Pixiv_Viewer_WinUI.Controls;

/// <summary>
/// 虚拟化瀑布流。
///
/// 关键点（按诊断建议）：
/// 1. 不自己管理元素生命周期 —— 用 <c>GetOrCreateElementAt(i)</c> 默认选项，
///    本轮没有请求的元素由 ItemsRepeater 在测量结束时自动回收。
/// 2. <c>_tops</c> 与 <c>RealizationRect</c> 都是内容坐标系（从 0 开始），可直接比较。
/// 3. 位置表缓存带完整 key（数量 / 列数 / 列宽 / 表长度），避免 0→0 误判。
/// 4. 每轮都重新 Measure 可见元素，宽度变化时高度会跟着更新。
/// 5. 真实高度变化时请求下一轮布局，但有标志位防止死循环。
/// </summary>
public sealed class MasonryLayout : VirtualizingLayout
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth),
        typeof(double),
        typeof(MasonryLayout),
        new PropertyMetadata(240d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(MasonryLayout),
        new PropertyMetadata(12d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns),
        typeof(int),
        typeof(MasonryLayout),
        new PropertyMetadata(6, OnLayoutPropertyChanged));

    private const double TextAreaHeight = 68;
    private const double Overscan = 600;

    private readonly List<int> _columns = new();
    private readonly List<double> _tops = new();
    private readonly Dictionary<int, double> _measured = new();
    private readonly List<(int Index, UIElement Element)> _visible = new();

    private int _builtCount = -1;
    private int _builtColumns = -1;
    private double _builtColumnWidth;
    private double _totalHeight;
    private bool _relayoutRequested;

    /// <summary>估算高度：给定索引与列宽返回卡片高度（用于未实例化的卡片）。</summary>
    public Func<int, double, double>? ItemHeightProvider { get; set; }

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    protected override void OnItemsChangedCore(
        VirtualizingLayoutContext context,
        object source,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        // 数据变了（追加/替换/清空），索引对应的内容可能完全不同，所有缓存作废
        ResetMetrics();
        _visible.Clear();
        _relayoutRequested = false;

        base.OnItemsChangedCore(context, source, args);
        InvalidateMeasure();
    }

    /// <summary>数据被整体换掉（换了筛选条件）时调用：丢掉缓存，强制按新数据重建。</summary>
    public void ResetItems()
    {
        _visible.Clear();
        ResetMetrics();
        _relayoutRequested = false;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var wasRelayout = _relayoutRequested;
        _relayoutRequested = false;

        var width = double.IsInfinity(availableSize.Width) ? MinItemWidth * 3 : availableSize.Width;
        var count = context.ItemCount;

        if (width <= 1 || count == 0)
        {
            ResetMetrics();
            _visible.Clear();
            return new Size(Math.Max(0, width), 0);
        }

        var columns = CalculateColumns(width, out var columnWidth);
        RebuildPositions(count, columns, columnWidth);

        if (_tops.Count != count)
        {
            ResetMetrics();
            _visible.Clear();
            return new Size(width, 0);
        }

        // RealizationRect 与 _tops 同为内容坐标系
        var rect = context.RealizationRect;
        var minY = rect.Top - Overscan;
        var maxY = rect.Bottom + Overscan;
        var heightsChanged = false;

        _visible.Clear();

        for (var i = 0; i < count; i++)
        {
            var height = EstimateHeight(i, columnWidth);
            var top = _tops[i];

            if (top + height < minY || top > maxY)
            {
                continue;
            }

            // 默认选项：框架负责回收本轮没请求的元素
            var element = context.GetOrCreateElementAt(i);
            element.Measure(new Size(columnWidth, double.PositiveInfinity));
            _visible.Add((i, element));

            var actual = element.DesiredSize.Height;
            if (actual > 0 && (!_measured.TryGetValue(i, out var old) || Math.Abs(old - actual) > 1.0))
            {
                _measured[i] = actual;
                heightsChanged = true;
            }
        }

        // 兜底诊断：有数据却一个都没实例化，写日志便于定位
        if (count > 0 && _visible.Count == 0)
        {
            PixivDiagnostics.Write($"masonry empty: count={count} rect={rect} origin={context.LayoutOrigin} width={width:0.#}");
        }

        if (heightsChanged && !wasRelayout)
        {
            _relayoutRequested = true;
            InvalidateMeasure();
        }

        return new Size(width, _totalHeight);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        var columns = CalculateColumns(finalSize.Width, out var columnWidth);
        var count = context.ItemCount;

        RebuildPositions(count, columns, columnWidth);

        if (_tops.Count != count)
        {
            return new Size(finalSize.Width, 0);
        }

        foreach (var (index, element) in _visible)
        {
            if (index >= _tops.Count)
            {
                continue;
            }

            element.Arrange(new Rect(
                (float)(_columns[index] * (columnWidth + Spacing)),
                (float)_tops[index],
                (float)columnWidth,
                (float)element.DesiredSize.Height));
        }

        return new Size(finalSize.Width, _totalHeight);
    }

    private double EstimateHeight(int index, double columnWidth)
    {
        if (_measured.TryGetValue(index, out var measured))
        {
            return measured;
        }

        var estimated = ItemHeightProvider?.Invoke(index, columnWidth) ?? columnWidth * 1.3 + TextAreaHeight;
        return estimated > 0 ? estimated : columnWidth * 1.3 + TextAreaHeight;
    }

    private void RebuildPositions(int count, int columns, double columnWidth)
    {
        // 缓存 key 必须包含表长度，否则 count 0→0 时会误判为「已构建」
        if (_tops.Count == count &&
            _builtCount == count &&
            _builtColumns == columns &&
            Math.Abs(columnWidth - _builtColumnWidth) < 0.5)
        {
            return;
        }

        if (Math.Abs(columnWidth - _builtColumnWidth) > 0.5)
        {
            _measured.Clear();
        }

        _columns.Clear();
        _tops.Clear();

        var heights = new double[columns];

        for (var i = 0; i < count; i++)
        {
            var height = EstimateHeight(i, columnWidth);
            var column = ShortestColumn(heights);

            _columns.Add(column);
            _tops.Add(heights[column]);
            heights[column] += height + Spacing;
        }

        _totalHeight = heights.Length == 0 ? 0 : Math.Max(0, heights.Max() - Spacing);
        _builtCount = count;
        _builtColumns = columns;
        _builtColumnWidth = columnWidth;
    }

    private void ResetMetrics()
    {
        _columns.Clear();
        _tops.Clear();
        _measured.Clear();
        _builtCount = -1;
        _builtColumns = -1;
        _builtColumnWidth = 0;
        _totalHeight = 0;
    }

    private int CalculateColumns(double width, out double columnWidth)
    {
        var spacing = Math.Max(0, Spacing);
        var minWidth = Math.Max(80, MinItemWidth);
        var columns = (int)Math.Floor((width + spacing) / (minWidth + spacing));
        columns = Math.Clamp(columns, 1, Math.Max(1, MaxColumns));
        columnWidth = Math.Max(0, (width - spacing * (columns - 1)) / columns);
        return columns;
    }

    private static int ShortestColumn(double[] heights)
    {
        var index = 0;
        for (var i = 1; i < heights.Length; i++)
        {
            if (heights[i] < heights[index])
            {
                index = i;
            }
        }

        return index;
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var layout = (MasonryLayout)sender;
        layout.ResetMetrics();
        layout.InvalidateMeasure();
    }
}
