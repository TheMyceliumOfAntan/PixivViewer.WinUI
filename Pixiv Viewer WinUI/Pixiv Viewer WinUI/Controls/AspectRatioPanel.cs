using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Pixiv_Viewer_WinUI.Controls;

/// <summary>
/// 让子元素按固定宽高比占位（宽 = 可用宽度，高 = 宽 / AspectRatio）。
/// 瀑布流卡片用它决定封面高度，避免依赖图片自然尺寸。
/// </summary>
public sealed class AspectRatioPanel : Panel
{
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio),
        typeof(double),
        typeof(AspectRatioPanel),
        new PropertyMetadata(0.75, OnAspectRatioChanged));

    /// <summary>宽 / 高。&gt; 1 为横图，&lt; 1 为竖图。</summary>
    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 240d : availableSize.Width;
        var ratio = Math.Clamp(AspectRatio, 0.35, 3.0);
        var height = width / ratio;
        var size = new Size(width, height);

        foreach (var child in Children)
        {
            child.Measure(size);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            child.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        }

        return finalSize;
    }

    private static void OnAspectRatioChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((AspectRatioPanel)sender).InvalidateMeasure();
    }
}
