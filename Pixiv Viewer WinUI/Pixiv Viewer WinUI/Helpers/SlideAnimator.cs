using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Pixiv_Viewer_WinUI.Helpers;

/// <summary>
/// 简单的滑入 / 滑出动画（配合 RenderTransform 的 TranslateTransform 使用）。
/// </summary>
public static class SlideAnimator
{
    private const int DurationMs = 190;

    /// <summary>滑出（向上移出并淡出），结束后执行 <paramref name="completed"/>。</summary>
    public static void SlideOut(FrameworkElement element, TranslateTransform transform, Action? completed = null)
    {
        var distance = element.ActualHeight > 0 ? element.ActualHeight : 80;

        var storyboard = new Storyboard();

        storyboard.Children.Add(Create(transform, "Y", 0, -distance, new CubicEase { EasingMode = EasingMode.EaseIn }));
        storyboard.Children.Add(Create(element, "Opacity", 1, 0, null));

        storyboard.Completed += (_, _) =>
        {
            transform.Y = 0;
            element.Opacity = 1;
            element.Visibility = Visibility.Collapsed;
            completed?.Invoke();
        };

        storyboard.Begin();
    }

    /// <summary>滑入（从上方滑回并淡入）。</summary>
    public static void SlideIn(FrameworkElement element, TranslateTransform transform)
    {
        var distance = element.ActualHeight > 0 ? element.ActualHeight : 80;

        element.Visibility = Visibility.Visible;

        var storyboard = new Storyboard();

        storyboard.Children.Add(Create(transform, "Y", -distance, 0, new CubicEase { EasingMode = EasingMode.EaseOut }));
        storyboard.Children.Add(Create(element, "Opacity", 0, 1, null));

        storyboard.Completed += (_, _) =>
        {
            transform.Y = 0;
            element.Opacity = 1;
        };

        storyboard.Begin();
    }

    /// <summary>淡入 + 轻微上移（全屏覆盖层用）。</summary>
    public static void FadeIn(FrameworkElement element, TranslateTransform? transform, double fromY = 40)
    {
        element.Visibility = Visibility.Visible;

        var storyboard = new Storyboard();

        if (transform is not null)
        {
            storyboard.Children.Add(Create(transform, "Y", fromY, 0, new CubicEase { EasingMode = EasingMode.EaseOut }));
        }

        storyboard.Children.Add(Create(element, "Opacity", 0, 1, null));
        storyboard.Begin();
    }

    public static void FadeOut(FrameworkElement element, TranslateTransform? transform, double toY = 40)
    {
        var storyboard = new Storyboard();

        if (transform is not null)
        {
            storyboard.Children.Add(Create(transform, "Y", 0, toY, new CubicEase { EasingMode = EasingMode.EaseIn }));
        }

        storyboard.Children.Add(Create(element, "Opacity", 1, 0, null));

        storyboard.Completed += (_, _) =>
        {
            element.Visibility = Visibility.Collapsed;
            element.Opacity = 1;

            if (transform is not null)
            {
                transform.Y = 0;
            }
        };

        storyboard.Begin();
    }

    private static DoubleAnimation Create(DependencyObject target, string property, double from, double to, EasingFunctionBase? easing)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(DurationMs)),
        };

        if (easing is not null)
        {
            animation.EasingFunction = easing;
        }

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);

        return animation;
    }
}
