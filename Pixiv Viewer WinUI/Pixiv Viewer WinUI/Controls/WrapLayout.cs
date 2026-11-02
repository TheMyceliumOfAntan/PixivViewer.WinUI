using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Pixiv_Viewer_WinUI.Controls;

/// <summary>
/// 简单换行布局：从左到右排列，超出宽度自动换行（用于标签、筛选芯片）。
/// </summary>
public sealed class WrapLayout : NonVirtualizingLayout
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(WrapLayout),
        new PropertyMetadata(6d, OnLayoutPropertyChanged));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var maxWidth = double.IsInfinity(availableSize.Width) ? 320d : availableSize.Width;
        var spacing = Math.Max(0, Spacing);
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;

        for (var i = 0; i < context.Children.Count; i++)
        {
            var child = context.Children[i];
            child.Measure(new Size(Math.Max(0, maxWidth - x), double.PositiveInfinity));

            if (x > 0 && x + child.DesiredSize.Width > maxWidth)
            {
                x = 0;
                y += rowHeight + spacing;
                rowHeight = 0;
                child.Measure(new Size(maxWidth, double.PositiveInfinity));
            }

            x += child.DesiredSize.Width + spacing;
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }

        return new Size(maxWidth, y + rowHeight);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var maxWidth = finalSize.Width;
        var spacing = Math.Max(0, Spacing);
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;

        for (var i = 0; i < context.Children.Count; i++)
        {
            var child = context.Children[i];

            if (x > 0 && x + child.DesiredSize.Width > maxWidth)
            {
                x = 0;
                y += rowHeight + spacing;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, child.DesiredSize.Width, child.DesiredSize.Height));
            x += child.DesiredSize.Width + spacing;
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }

        return new Size(finalSize.Width, y + rowHeight);
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((WrapLayout)sender).InvalidateMeasure();
}
