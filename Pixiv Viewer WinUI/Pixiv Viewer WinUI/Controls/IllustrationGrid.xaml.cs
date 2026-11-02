using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pixiv_Viewer_WinUI.Models;
using Windows.UI;

namespace Pixiv_Viewer_WinUI.Controls;

/// <summary>卡片右键菜单动作。</summary>
public enum CardAction
{
    Bookmark,
    Like,
    Download,
}

public sealed class CardActionEventArgs : EventArgs
{
    public required Illustration Item { get; init; }

    public required CardAction Action { get; init; }
}

/// <summary>
/// 瀑布流作品网格：卡片点击通过 <see cref="ItemInvoked"/> 抛给页面，
/// 右键菜单动作通过 <see cref="CardActionRequested"/> 抛给页面。
/// </summary>
public sealed partial class IllustrationGrid : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(object),
        typeof(IllustrationGrid),
        new PropertyMetadata(null, OnItemsSourcePropertyChanged));

    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth),
        typeof(double),
        typeof(IllustrationGrid),
        new PropertyMetadata(200d, OnMinItemWidthChanged));

    private readonly DispatcherQueueTimer _toastTimer;
    private DispatcherQueueTimer? _scrollResetTimer;

    public IllustrationGrid()
    {
        InitializeComponent();
        Scroller.SizeChanged += OnScrollerSizeChanged;
        Loaded += (_, _) =>
        {
            ApplyLayoutOptions();
            // 页面从缓存恢复时重新测量，否则虚拟化布局可能一个卡片都不实例化
            Repeater.InvalidateMeasure();

            // 内容不够一屏时自动再拉一页（虚拟化下 ScrollableHeight 可能是 0）
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Scroller.ScrollableHeight - Scroller.VerticalOffset <= 600)
                {
                    LoadMoreRequested?.Invoke(this, EventArgs.Empty);
                }
            });
        };
        ApplyLayoutOptions();

        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.IsRepeating = false;
        _toastTimer.Interval = TimeSpan.FromSeconds(2.5);
        _toastTimer.Tick += (_, _) => Toast.Visibility = Visibility.Collapsed;
    }

    /// <summary>点击卡片时触发，参数为被点击的作品。</summary>
    public event EventHandler<Illustration>? ItemInvoked;

    /// <summary>右键菜单动作（收藏 / 喜欢 / 下载）。</summary>
    public event EventHandler<CardActionEventArgs>? CardActionRequested;

    /// <summary>滚动接近底部时触发，页面可追加下一页。</summary>
    public event EventHandler? LoadMoreRequested;

    /// <summary>滚动方向变化（true = 向下滚），用于自动隐藏页面头部。</summary>
    public event EventHandler<bool>? ScrollDirectionChanged;

    private double _lastScrollOffset;
    private DateTimeOffset _lastDirectionChangeAt = DateTimeOffset.MinValue;

    private DateTimeOffset _lastLoadMoreAt = DateTimeOffset.MinValue;

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public void ScrollToTop()
    {
        Scroller.ChangeView(null, 0, null, true);

        // 内容首次测量完成后再修正一次：瀑布流高度是测量后才算出来的，早于布局的
        // ChangeView 会被忽略；导航时焦点落到卡片上也可能把视口带偏，这里延时再归零。
        DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, 0, null, true));

        _scrollResetTimer ??= DispatcherQueue.CreateTimer();
        _scrollResetTimer.Stop();
        _scrollResetTimer.Interval = TimeSpan.FromMilliseconds(300);
        _scrollResetTimer.IsRepeating = false;
        _scrollResetTimer.Tick += OnScrollResetTick;
        _scrollResetTimer.Start();
    }

    /// <summary>强制重新布局（页面从详情页返回、或刷新数据但列表实例没变时用）。</summary>
    public void Refresh()
    {
        Repeater.InvalidateMeasure();
        Scroller.UpdateLayout();
        Repeater.InvalidateMeasure();
    }

    /// <summary>在网格底部显示一条短提示（2.5 秒后自动消失）。</summary>
    public void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnScrollResetTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        Scroller.ChangeView(null, 0, null, true);
    }

    private void OnScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)    {
        var offset = Scroller.VerticalOffset;
        var delta = offset - _lastScrollOffset;

        // 滞回 + 节流：避免小幅抖动来回触发头部收起/展开（会连带整页重新布局）
        if (Math.Abs(delta) > 12 &&
            DateTimeOffset.Now - _lastDirectionChangeAt > TimeSpan.FromMilliseconds(220))
        {
            _lastScrollOffset = offset;
            _lastDirectionChangeAt = DateTimeOffset.Now;
            ScrollDirectionChanged?.Invoke(this, delta > 0);
        }

        if (Scroller.ScrollableHeight - Scroller.VerticalOffset > 600)
        {
            return;
        }

        // 节流，避免一次滚动触发多次加载
        if (DateTimeOffset.Now - _lastLoadMoreAt < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastLoadMoreAt = DateTimeOffset.Now;
        LoadMoreRequested?.Invoke(this, EventArgs.Empty);
    }

    private static void OnMinItemWidthChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((IllustrationGrid)sender).ApplyLayoutOptions();

    private static void OnItemsSourcePropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((IllustrationGrid)sender).OnItemsSourceChanged();

    /// <summary>把控件级配置同步给瀑布流布局（x:Bind 对非可视树对象不可靠，这里直接赋值）。</summary>
    private void ApplyLayoutOptions()
    {
        if (Repeater.Layout is MasonryLayout layout)
        {
            layout.MinItemWidth = MinItemWidth;
            layout.ItemHeightProvider = EstimateItemHeight;
        }
    }

    /// <summary>按作品宽高比估算卡片高度（封面 + 文字区），供虚拟化布局定位未实例化的卡片。</summary>
    private double EstimateItemHeight(int index, double columnWidth)
    {
        if (ItemsSource is IReadOnlyList<Illustration> list && index >= 0 && index < list.Count)
        {
            var ratio = Math.Clamp(list[index].AspectRatio, 0.35, 3.0);
            return columnWidth / ratio + 68;
        }

        return columnWidth * 1.3 + 68;
    }

    /// <summary>数据换了就强制重排一次，并把「内容不足一屏」的情况交给页面续加载。</summary>
    private void OnItemsSourceChanged()
    {
        Repeater.InvalidateMeasure();

        DispatcherQueue.TryEnqueue(() =>
        {
            Repeater.InvalidateMeasure();
            Scroller.UpdateLayout();

            if (Scroller.ScrollableHeight - Scroller.VerticalOffset <= 600)
            {
                LoadMoreRequested?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    /// <summary>
    /// ScrollViewer 测量内容时宽度可能是无限大，导致瀑布流拿不到真实宽度。
    /// 这里把视口宽度显式传给 ItemsRepeater，保证列数随窗口自适应。
    /// </summary>
    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : e.NewSize.Width;
        width -= Scroller.Padding.Left + Scroller.Padding.Right;

        if (width > 1 && (double.IsNaN(Repeater.Width) || Math.Abs(Repeater.Width - width) > 0.5))
        {
            Repeater.Width = width;
            // 宽度变了，虚拟化布局的列宽/位置表都要重算
            Repeater.InvalidateMeasure();
        }
    }

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as Illustration
                   ?? (sender as FrameworkElement)?.DataContext as Illustration;

        if (item is not null)
        {
            ItemInvoked?.Invoke(this, item);
        }
    }

    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement element)
        {
            // 阴影只在悬停时挂上，避免 80+ 张卡片常驻 ThemeShadow 拖慢滚动
            element.Shadow = new ThemeShadow();
            element.Translation = new Vector3(0, 0, 20);
        }
    }

    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement element)
        {
            element.Translation = Vector3.Zero;
            element.Shadow = null;
        }
    }

    private void OnMenuBookmark(object sender, RoutedEventArgs e) => RaiseCardAction(sender, CardAction.Bookmark);

    /// <summary>
    /// 右键菜单每次打开时按作品的收藏状态刷新文案与图标：
    /// 已收藏 → 「取消收藏」+ 实心星标；私密收藏 → 锁图标高亮。
    /// </summary>
    private void OnMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout)
        {
            return;
        }

        var item = (flyout.Target as FrameworkElement)?.Tag as Illustration;
        var bookmarked = item?.IsBookmarked == true;
        var isPrivate = item?.BookmarkIsPrivate == true;

        foreach (var menuItem in flyout.Items.OfType<MenuFlyoutItem>())
        {
            switch (menuItem.Name)
            {
                case "BookmarkMenuItem":
                    menuItem.Text = bookmarked ? "取消收藏" : "收藏";
                    ApplyMenuIcon(
                        menuItem,
                        bookmarked ? "\uE735" : "\uE734",
                        bookmarked ? BookmarkActiveColor : null);
                    break;

                case "PrivateBookmarkMenuItem":
                    menuItem.Text = bookmarked && isPrivate ? "取消私密收藏" : "私密收藏";
                    ApplyMenuIcon(
                        menuItem,
                        bookmarked && isPrivate ? "\uE785" : "\uE72E",
                        bookmarked && isPrivate ? PrivateActiveColor : null);
                    break;
            }
        }
    }

    private static readonly Color BookmarkActiveColor = Color.FromArgb(0xFF, 0xFF, 0xC8, 0x3D);

    private static readonly Color PrivateActiveColor = Color.FromArgb(0xFF, 0xFF, 0x8A, 0x3D);

    private static void ApplyMenuIcon(MenuFlyoutItem item, string glyph, Color? color)
    {
        if (item.Icon is not FontIcon icon)
        {
            icon = new FontIcon();
            item.Icon = icon;
        }

        icon.Glyph = glyph;

        // 未激活时必须清掉前景色（设为 null 会让图标整个不可见），恢复主题继承
        if (color is { } c)
        {
            icon.Foreground = new SolidColorBrush(c);
        }
        else
        {
            icon.ClearValue(FontIcon.ForegroundProperty);
        }
    }

    private void OnMenuLike(object sender, RoutedEventArgs e) => RaiseCardAction(sender, CardAction.Like);

    private void OnMenuDownload(object sender, RoutedEventArgs e) => RaiseCardAction(sender, CardAction.Download);

    private void RaiseCardAction(object sender, CardAction action)
    {
        if ((sender as FrameworkElement)?.Tag is Illustration item)
        {
            CardActionRequested?.Invoke(this, new CardActionEventArgs { Item = item, Action = action });
        }
    }
}
