using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Helpers;
using Pixiv_Viewer_WinUI.Models;
using Microsoft.UI.Text;
using Pixiv_Viewer_WinUI.Services;
using Pixiv_Viewer_WinUI.Services.Pixiv;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;

namespace Pixiv_Viewer_WinUI.Views;

public sealed partial class IllustrationDetailPage : Page
{
    private bool _syncingThumb;
    private int _lastPageIndex;
    private bool _syncingZoom;
    private bool _dragging;
    private Point _dragStart;
    private double _dragStartH;
    private double _dragStartV;
    private bool _isFullScreen;
    private string? _shownImageUrl;
    private float _targetZoom = 1f;
    private readonly TranslateTransform _fullScreenShift = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hintTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zoomBarTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _imageTimeout;

    public IllustrationDetailViewModel View { get; } = new();

    public IllustrationDetailPage()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, new KeyEventHandler(OnPageKeyDown), true);
        View.PropertyChanged += OnViewPropertyChanged;
        ImageScroller.SizeChanged += OnImageScrollerSizeChanged;

        // 滚轮缩放：挂在内容层（ImageHost）上，先于 ScrollViewer 拿到事件，
        // 否则 ScrollViewer 会先把画面滚一下，导致"到最大后继续滚会跳"。
        ImageHost.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnZoomWheel), true);
        FullScreenHost.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnZoomWheel), true);

        foreach (var scroller in new[] { ImageScroller, FullScreenScroller })
        {
            scroller.AddHandler(PointerPressedEvent, new PointerEventHandler(OnZoomPointerPressed), true);
            scroller.AddHandler(PointerMovedEvent, new PointerEventHandler(OnZoomPointerMoved), true);
            scroller.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnZoomPointerReleased), true);
            scroller.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnZoomPointerReleased), true);
            scroller.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnZoomPointerReleased), true);
        }

        ImageScroller.ViewChanged += (_, _) => SyncZoomUi(ImageScroller);
        ImageScroller.PointerExited += (_, _) => ScheduleZoomBarHide();
        CachedImage.SourceApplied += OnCachedImageSourceApplied;

        FullScreenOverlay.RenderTransform = _fullScreenShift;

        _zoomBarTimer = DispatcherQueue.CreateTimer();
        _zoomBarTimer.IsRepeating = false;
        _zoomBarTimer.Interval = TimeSpan.FromSeconds(2.5);
        _zoomBarTimer.Tick += (_, _) => ZoomBar.Visibility = Visibility.Collapsed;
        _zoomBarTimer.Start();

        _imageTimeout = DispatcherQueue.CreateTimer();
        _imageTimeout.IsRepeating = false;
        _imageTimeout.Interval = TimeSpan.FromSeconds(15);
        _imageTimeout.Tick += (_, _) =>
        {
            if (PageImage.Opacity < 0.5)
            {
                ImageSkeleton.Visibility = Visibility.Collapsed;
                ImageLoading.IsActive = false;
                ImageErrorText.Text = "图片加载超时，可右键「另存为」或重新打开";
                ImageErrorText.Visibility = Visibility.Visible;
            }
        };

        FullScreenScroller.SizeChanged += (_, e) =>
        {
            FullScreenHost.Width = Math.Max(0, e.NewSize.Width);
            FullScreenHost.Height = Math.Max(0, e.NewSize.Height);
        };
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        CachedImage.SourceApplied -= OnCachedImageSourceApplied;
    }

    /// <summary>图片挂上 Source 后关闭骨架和 loading（ImageOpened 不可靠）。</summary>
    private void OnCachedImageSourceApplied(object? sender, Image image)
    {
        if (image != PageImage)
        {
            return;
        }

        ImageSkeleton.Visibility = Visibility.Collapsed;
        ImageLoading.IsActive = false;
        ImageErrorText.Visibility = Visibility.Collapsed;
        _imageTimeout?.Stop();
    }

    private void ShowZoomBar()
    {
        ZoomBar.Visibility = Visibility.Visible;
        _zoomBarTimer?.Stop();
        _zoomBarTimer?.Start();
    }

    private void ScheduleZoomBarHide()
    {
        _zoomBarTimer?.Stop();
        _zoomBarTimer?.Start();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            await View.LoadAsync(e.Parameter as Illustration);

            ThumbList.ItemsSource = View.Pages;
            if (View.Pages.Count > 0)
            {
                ThumbList.SelectedIndex = 0;
            }

            FollowLabel.Text = View.IsFollowed ? "已关注" : "关注";
            LinkText.Fill(DescriptionText, View.DescriptionDisplayText, TryNavigatePixivLink);

            // 让页面拿到焦点，方向键/空格翻页、Esc 返回才能生效
            Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            ShowHint($"加载失败：{ex.Message}");
        }
    }

    private void OnViewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IllustrationDetailViewModel.CurrentIndex):
                _syncingThumb = true;
                ThumbList.SelectedIndex = View.CurrentIndex;
                if (View.CurrentIndex >= 0 && View.CurrentIndex < ThumbList.Items.Count)
                {
                    ThumbList.ScrollIntoView(ThumbList.Items[View.CurrentIndex], ScrollIntoViewAlignment.Default);
                }

                _syncingThumb = false;
                break;

            case nameof(IllustrationDetailViewModel.CurrentImageUrl):
                // 同一个 URL（比如 NotifyAll 又通知了一次）不要重置，否则会把已经显示的图盖成 loading
                if (string.Equals(_shownImageUrl, View.CurrentImageUrl, StringComparison.Ordinal))
                {
                    break;
                }

                _shownImageUrl = View.CurrentImageUrl;
                BeginPageChange();
                break;

            case nameof(IllustrationDetailViewModel.IsFollowed):
                FollowLabel.Text = View.IsFollowed ? "已关注" : "关注";
                break;

            case nameof(IllustrationDetailViewModel.Description):
            case nameof(IllustrationDetailViewModel.IsDescriptionTranslated):
                // 原文与译文都用同一个 RichTextBlock 渲染，链接在两种状态下都可点击
                LinkText.Fill(DescriptionText, View.DescriptionDisplayText, TryNavigatePixivLink);
                break;
        }
    }

    private void OnImageScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ImageHost.Width = Math.Max(0, e.NewSize.Width);
        ImageHost.Height = Math.Max(0, e.NewSize.Height);
    }

    private void OnThumbSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingThumb && ThumbList.SelectedIndex >= 0)
        {
            View.GoToPage(ThumbList.SelectedIndex);
        }
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => View.PreviousPage();

    private void OnNextClick(object sender, RoutedEventArgs e) => View.NextPage();

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isFullScreen)
        {
            if (e.Key is VirtualKey.Escape or VirtualKey.F11)
            {
                SetFullScreen(false);
                e.Handled = true;
            }

            return;
        }

        // 输入框内的方向键 / Esc 交给输入框自己处理
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Left:
            case VirtualKey.PageUp:
                View.PreviousPage();
                e.Handled = true;
                break;

            case VirtualKey.Right:
            case VirtualKey.PageDown:
                View.NextPage();
                e.Handled = true;
                break;

            case VirtualKey.F11:
                SetFullScreen(true);
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                GoBack();
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- 缩放 / 全屏

    private void OnZoomWheel(object sender, PointerRoutedEventArgs e)
    {
        var scroller = ReferenceEquals(sender, FullScreenHost) ? FullScreenScroller : ImageScroller;

        var delta = e.GetCurrentPoint(scroller).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        // 以鼠标位置为锚点缩放（否则会固定往左上角放大）
        var position = e.GetCurrentPoint(scroller).Position;
        ApplyZoom(scroller, (float)(scroller.ZoomFactor * (delta > 0 ? 1.15 : 1 / 1.15)), position);
        e.Handled = true;
    }

    private void OnZoomPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ScrollViewer scroller || scroller.ZoomFactor <= 1.01f)
        {
            return;
        }

        var point = e.GetCurrentPoint(scroller);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        _dragStart = point.Position;
        _dragStartH = scroller.HorizontalOffset;
        _dragStartV = scroller.VerticalOffset;
        scroller.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnZoomPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ShowZoomBar();

        if (!_dragging || sender is not ScrollViewer scroller)
        {
            return;
        }

        var point = e.GetCurrentPoint(scroller);
        scroller.ChangeView(
            _dragStartH - (point.Position.X - _dragStart.X),
            _dragStartV - (point.Position.Y - _dragStart.Y),
            null,
            true);
        e.Handled = true;
    }

    private void OnZoomPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (sender is ScrollViewer scroller)
        {
            scroller.ReleasePointerCapture(e.Pointer);
        }

        e.Handled = true;
    }

    private void ApplyZoom(ScrollViewer scroller, float zoom, Point? anchor = null)
    {
        var clamped = Math.Clamp(zoom, scroller.MinZoomFactor, scroller.MaxZoomFactor);

        // 用上一次的目标值做基准：ChangeView 是异步的，连续滚轮时 ZoomFactor 会滞后，导致来回跳
        var current = _targetZoom;

        if (Math.Abs(clamped - current) < 0.001f)
        {
            // 已经到上下限：不要再动偏移，否则会突然滚到顶部
            SyncZoomUi(scroller);
            return;
        }

        var point = anchor ?? new Point(scroller.ViewportWidth / 2, scroller.ViewportHeight / 2);

        // 缩放前光标下的内容坐标 → 缩放后保持在同一屏幕位置
        var contentX = (scroller.HorizontalOffset + point.X) / current;
        var contentY = (scroller.VerticalOffset + point.Y) / current;

        _targetZoom = clamped;

        scroller.ChangeView(
            contentX * clamped - point.X,
            contentY * clamped - point.Y,
            clamped,
            true);

        ShowZoomBar();
        SyncZoomUi(scroller);
    }

    private void SyncZoomUi(ScrollViewer scroller)
    {
        // XAML 解析期间 ComboBox/Slider 的初始值会触发事件，此时控件可能还没创建
        if (scroller != ImageScroller || ZoomSlider is null || ZoomCombo is null)
        {
            return;
        }

        var percent = (int)Math.Round(_targetZoom * 100);
        _syncingZoom = true;
        ZoomSlider.Value = Math.Clamp(percent, ZoomSlider.Minimum, ZoomSlider.Maximum);

        var preset = ZoomCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == percent.ToString());

        ZoomCombo.SelectedItem = preset;
        ZoomCombo.PlaceholderText = $"{percent}%";
        _syncingZoom = false;
    }

    private void OnZoomSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // InitializeComponent 阶段 Slider 设初值也会触发，此时 ImageScroller 还没创建
        if (_syncingZoom || !IsLoaded || ImageScroller is null)
        {
            return;
        }

        // 程序化同步（SyncZoomUi 写回滑块）不算用户操作
        if (Math.Abs(e.NewValue / 100.0 - _targetZoom) < 0.005)
        {
            return;
        }

        ApplyZoom(ImageScroller, (float)(e.NewValue / 100.0));
    }

    private void OnZoomPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingZoom || !IsLoaded || ImageScroller is null)
        {
            return;
        }

        if (ZoomCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            int.TryParse(tag, out var percent) &&
            Math.Abs(percent / 100.0 - _targetZoom) > 0.005)
        {
            ApplyZoom(ImageScroller, percent / 100f);
        }
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
        => ApplyZoom(ImageScroller, (float)(ImageScroller.ZoomFactor * 1.25));

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
        => ApplyZoom(ImageScroller, (float)(ImageScroller.ZoomFactor / 1.25));

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        _targetZoom = 1f;
        ImageScroller.ChangeView(0, 0, 1f, true);
        SyncZoomUi(ImageScroller);
    }

    private async void OnImageInfoClick(object sender, RoutedEventArgs e)
    {
        var item = View.Item;
        if (item is null)
        {
            return;
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 460 };

        void AddRow(string label, string value)
        {
            panel.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = label, FontSize = 12, Opacity = 0.7 },
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                },
            });
        }

        AddRow("标题", item.Title);
        AddRow("作者", $"{item.Author.Name}  {item.Author.AccountText}");
        AddRow("PID", item.Id);
        AddRow("分辨率", item.Width > 0 && item.Height > 0 ? $"{item.Width} × {item.Height}" : "未知");
        AddRow("页数", $"{View.CurrentIndex + 1} / {View.PageCount}");
        AddRow("标签", string.Join("  ", item.Tags));
        AddRow("作品链接", $"https://www.pixiv.net/artworks/{item.Id}");
        AddRow("原图链接", View.CurrentSourceUrl);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "图片信息",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "复制作品链接",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText($"https://www.pixiv.net/artworks/{item.Id}");
        Clipboard.SetContent(package);
        ShowHint("已复制作品链接");
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => SetFullScreen(true);

    private void OnExitFullScreenClick(object sender, RoutedEventArgs e) => SetFullScreen(false);

    private void OnFullScreenRightTapped(object sender, RightTappedRoutedEventArgs e) => SetFullScreen(false);

    private void SetFullScreen(bool on)
    {
        if (_isFullScreen == on)
        {
            return;
        }

        _isFullScreen = on;

        // 全屏时把导航栏 / 标题栏一起藏起来（只收起面板，不隐藏 Frame 内容）
        (App.MainWindow as MainWindow)?.SetChromeVisible(!on);

        if (on)
        {
            SlideAnimator.FadeIn(FullScreenOverlay, _fullScreenShift, 60);

            // 覆盖层刚显示时 SizeChanged 可能还没触发，先按窗口尺寸铺满，避免空白
            try
            {
                if (XamlRoot is not null)
                {
                    FullScreenHost.Width = XamlRoot.Size.Width;
                    FullScreenHost.Height = XamlRoot.Size.Height;
                }
            }
            catch (Exception)
            {
                // 忽略
            }

            FullScreenScroller.ChangeView(0, 0, 1f, true);

            // 把焦点交给页面，保证 Esc / F11 还能收到
            Focus(FocusState.Programmatic);
        }
        else
        {
            SlideAnimator.FadeOut(FullScreenOverlay, _fullScreenShift, 60);
        }
    }

    private void OnPrevAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
        {
            return;
        }

        View.PreviousPage();
        args.Handled = true;
    }

    private void OnNextAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
        {
            return;
        }

        View.NextPage();
        args.Handled = true;
    }

    private void OnFullScreenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetFullScreen(!_isFullScreen);
        args.Handled = true;
    }

    private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_isFullScreen)
        {
            SetFullScreen(false);
            args.Handled = true;
            return;
        }

        // 输入框里的 Esc 不返回
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
        {
            return;
        }

        GoBack();
        args.Handled = true;
    }

    /// <summary>作品说明里的 Pixiv 站内链接直接在应用内跳转，其它交给浏览器。</summary>
    private bool TryNavigatePixivLink(string url)
    {
        try
        {
            if (url.Contains("/artworks/", StringComparison.OrdinalIgnoreCase))
            {
                var id = url.Split("/artworks/")[1].Split('/', '?', '#')[0];
                if (id.Length > 0)
                {
                    Frame.Navigate(typeof(IllustrationDetailPage), new Illustration { Id = id });
                    return true;
                }
            }

            if (url.Contains("/users/", StringComparison.OrdinalIgnoreCase))
            {
                var id = url.Split("/users/")[1].Split('/', '?', '#')[0];
                if (id.Length > 0)
                {
                    Frame.Navigate(typeof(UserPage), new PixivUser { Id = id });
                    return true;
                }
            }

            if (url.Contains("/collections/", StringComparison.OrdinalIgnoreCase))
            {
                var id = url.Split("/collections/")[1].Split('/', '?', '#')[0];
                if (id.Length > 0)
                {
                    Frame.Navigate(typeof(CollectionPage), id);
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // 解析失败就走浏览器
        }

        return false;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void OnAuthorClick(object sender, RoutedEventArgs e)
    {
        if (View.Item?.Author is PixivUser author)
        {
            Frame.Navigate(typeof(UserPage), author);
        }
    }

    private void OnTagClick(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag;

        if (tag is IllustTag info && !string.IsNullOrWhiteSpace(info.Name))
        {
            // 带上官方译名，标签详情页双语显示
            Frame.Navigate(typeof(TagPage), info);
        }
        else if (tag is string name && !string.IsNullOrWhiteSpace(name))
        {
            Frame.Navigate(typeof(TagPage), name);
        }
    }

    private async void OnSaveImageClick(object sender, RoutedEventArgs e)
    {
        var url = View.CurrentOriginalUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowHint("没有可保存的图片");
            return;
        }

        try
        {
            var ok = await AppServices.Downloads.SaveImageAsync(url, $"{View.Id}_p{View.CurrentIndex + 1}");
            ShowHint(ok ? "已保存" : "已取消");
        }
        catch (Exception ex)
        {
            ShowHint($"保存失败：{ex.Message}");
        }
    }

    private void OnCommentAuthorClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PixivUser user)
        {
            Frame.Navigate(typeof(UserPage), user);
        }
    }

    private void GoBack()
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async void OnBookmarkClick(SplitButton sender, SplitButtonClickEventArgs args)
    {
        var ok = await View.ToggleBookmarkAsync();
        ShowHint(ok
            ? (View.IsBookmarked ? "已加入收藏" : "已取消收藏")
            : "收藏失败：请检查登录状态或网络");
    }

    private async void OnPrivateBookmarkClick(object sender, RoutedEventArgs e)
    {
        var ok = await View.ToggleBookmarkAsync(isPrivate: true);

        ShowHint(ok
            ? (View.IsBookmarked ? "已加入私密收藏" : "已取消收藏")
            : "操作失败：请检查登录状态或网络");
    }

    /// <summary>作品说明「翻译 / 显示原文」。</summary>
    private async void OnTranslateDescriptionClick(object sender, RoutedEventArgs e)
    {
        if (!await View.ToggleDescriptionTranslationAsync())
        {
            ShowHint("翻译失败：请稍后重试");
        }
    }

    /// <summary>单条评论「翻译 / 显示原文」。</summary>
    private async void OnTranslateCommentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Comment comment &&
            !await View.ToggleCommentTranslationAsync(comment))
        {
            ShowHint("翻译失败：请稍后重试");
        }
    }

    /// <summary>切换页：先铺骨架 + 滑入动画，图片加载完再淡入。</summary>
    private void BeginPageChange()
    {
        PageImage.Opacity = 0;
        ImageSkeleton.Visibility = Visibility.Visible;
        ImageLoading.IsActive = true;
        ImageErrorText.Visibility = Visibility.Collapsed;

        // 翻页后缩放重置（等布局完成再设置，避免 ScrollViewer 把 zoom 钳到最小值）
        _targetZoom = 1f;
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                ImageScroller.ChangeView(0, 0, 1f, true);
            }
            catch (Exception)
            {
                // 忽略
            }
        });

        // 兜底：15 秒还没出图就收起 loading，避免一直转圈
        _imageTimeout?.Stop();
        _imageTimeout?.Start();

        var forward = View.CurrentIndex >= _lastPageIndex;
        _lastPageIndex = View.CurrentIndex;

        var animation = new DoubleAnimation
        {
            From = forward ? 60 : -60,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(240)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(animation, ImageShift);
        Storyboard.SetTargetProperty(animation, "X");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void OnPageImageOpened(object sender, RoutedEventArgs e)
    {
        ImageSkeleton.Visibility = Visibility.Collapsed;
        ImageLoading.IsActive = false;
        ImageErrorText.Visibility = Visibility.Collapsed;
        PageImage.Opacity = 1;
    }

    private void OnPageImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ImageSkeleton.Visibility = Visibility.Collapsed;
        ImageLoading.IsActive = false;
        ImageErrorText.Visibility = Visibility.Visible;
    }

    private async void OnFollowClick(object sender, RoutedEventArgs e)
    {
        var ok = await View.ToggleFollowAsync();
        ShowHint(ok
            ? (View.IsFollowed ? $"已关注 {View.AuthorName}" : $"已取消关注 {View.AuthorName}")
            : "操作失败：请检查登录状态或网络");
    }

    private void OnToggleDescriptionClick(object sender, RoutedEventArgs e) => View.ToggleDescription();

    private async void OnSendCommentClick(object sender, RoutedEventArgs e)
    {
        var text = BuildCommentText().Trim();
        if (text.Length == 0)
        {
            ShowHint("评论内容不能为空");
            return;
        }

        if (text.Length > MaxCommentLength)
        {
            ShowHint($"评论最多 {MaxCommentLength} 个字符");
            return;
        }

        SendButton.IsEnabled = false;
        EmojiButton.IsEnabled = false;
        SendButton.Content = "发送中…";

        try
        {
            var ok = await View.AddCommentAsync(text);
            if (ok)
            {
                ResetCommentInput();
                ShowHint("评论已发布");
            }
            else
            {
                ShowHint("评论发送失败：请检查登录状态、网页 Cookie 或网络");
            }
        }
        catch (Exception ex)
        {
            ShowHint($"评论发送失败：{ex.Message}");
        }
        finally
        {
            SendButton.Content = "发送";
            EmojiButton.IsEnabled = true;
            UpdateCommentState();
        }
    }

    // ---------------------------------------------------------------- 评论编辑器

    /// <summary>pixiv 评论长度上限。</summary>
    private const int MaxCommentLength = 140;

    /// <summary>RichEditBox 里图片在文本中的占位符。</summary>
    private const char ImagePlaceholder = '\uFFFC';

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> EmojiBytes = new();

    private void OnCommentTextChanged(object sender, RoutedEventArgs e) => UpdateCommentState();

    private void UpdateCommentState()
    {
        var text = BuildCommentText();
        CommentCounter.Text = $"{text.Length} / {MaxCommentLength}";
        SendButton.IsEnabled = text.Trim().Length is > 0 and <= MaxCommentLength;
    }

    private void ResetCommentInput()
    {
        CommentBox.Document.SetText(TextSetOptions.None, string.Empty);
        UpdateCommentState();
    }

    /// <summary>
    /// 把 RichEditBox 的内容转成 pixiv 能识别的文本：图片 → <c>(slug)</c>。
    /// slug 存在图片的 alternateText 里（RTF 的 \picprop wzDescription），
    /// 图片本身只占一个字符，删除/撤销都是一次一个整体。
    /// </summary>
    private string BuildCommentText()
    {
        CommentBox.Document.GetText(TextGetOptions.None, out var plain);
        var slugs = ReadEmojiDescriptions();

        var builder = new System.Text.StringBuilder(plain.Length);
        var imageIndex = 0;

        for (var i = 0; i < plain.Length; i++)
        {
            var c = plain[i];

            if (c == ImagePlaceholder)
            {
                if (imageIndex < slugs.Count && slugs[imageIndex] is { } slug)
                {
                    builder.Append('(').Append(slug).Append(')');
                }

                imageIndex++;
                continue;
            }

            builder.Append(c == '\r' ? '\n' : c);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>按文档顺序读出每张图片的 alternateText（emoji slug）。</summary>
    private List<string?> ReadEmojiDescriptions()
    {
        var slugs = new List<string?>();

        try
        {
            CommentBox.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
            slugs.AddRange(ParsePictDescriptions(rtf));
        }
        catch (Exception ex)
        {
            PixivDiagnostics.Write($"read emoji descriptions failed: {ex.Message}");
        }

        return slugs;
    }

    /// <summary>从 RTF 的 \pict 组里按顺序取 alternateText；不是合法 slug 的返回 null。</summary>
    private static IEnumerable<string?> ParsePictDescriptions(string rtf)
    {
        foreach (var group in ExtractPictGroups(rtf))
        {
            var description = group.IndexOf("wzDescription", StringComparison.Ordinal);
            if (description < 0)
            {
                yield return null;
                continue;
            }

            var value = group.IndexOf("\\sv ", description, StringComparison.Ordinal);
            if (value < 0)
            {
                yield return null;
                continue;
            }

            var start = value + 4;
            var end = group.IndexOf('}', start);
            if (end <= start)
            {
                yield return null;
                continue;
            }

            var slug = group[start..end].Trim();
            yield return PixivEmoji.IdForSlug(slug) > 0 ? slug : null;
        }
    }

    /// <summary>按出现顺序取出 RTF 里的每个 \pict 组（花括号配对）。</summary>
    private static IEnumerable<string> ExtractPictGroups(string rtf)
    {
        var index = 0;

        while (index < rtf.Length)
        {
            var pict = rtf.IndexOf("\\pict", index, StringComparison.Ordinal);
            if (pict < 0)
            {
                yield break;
            }

            var start = rtf.LastIndexOf('{', pict);
            if (start < 0)
            {
                index = pict + 5;
                continue;
            }

            var depth = 0;
            var end = -1;

            for (var i = start; i < rtf.Length; i++)
            {
                if (rtf[i] == '{')
                {
                    depth++;
                }
                else if (rtf[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }

            if (end < 0)
            {
                yield break;
            }

            yield return rtf[start..(end + 1)];
            index = end + 1;
        }
    }

    private void OnEmojiClick(object sender, RoutedEventArgs e)
    {
        if (EmojiHost.Children.Count == 0)
        {
            BuildEmojiPanel();
        }

        EmojiPanel.Visibility = EmojiPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        // 面板内联显示，按钮不抢焦点；这里再兜底把焦点还给编辑器
        CommentBox.Focus(FocusState.Programmatic);
    }

    private void BuildEmojiPanel()
    {
        var emojis = PixivEmoji.All;

        for (var i = 0; i < emojis.Count; i += 6)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
            };

            foreach (var (slug, url) in emojis.Skip(i).Take(6))
            {
                row.Children.Add(CreateEmojiButton(slug, url));
            }

            EmojiHost.Children.Add(row);
        }
    }

    private Button CreateEmojiButton(string slug, string url)
    {
        var image = new Image
        {
            Width = 24,
            Height = 24,
            Stretch = Stretch.Uniform,
        };

        _ = LoadEmojiThumbAsync(url, image);

        var button = new Button
        {
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Content = image,
            Tag = slug,
            // 关键：点表情按钮不夺走 RichEditBox 的焦点，否则插入后无法继续输入
            AllowFocusOnInteraction = false,
            IsTabStop = false,
        };

        ToolTipService.SetToolTip(button, $":{slug}:");

        button.Click += (s, _) =>
        {
            if (s is Button { Tag: string clicked })
            {
                InsertEmoji(clicked);
            }
        };

        return button;
    }

    private static async Task LoadEmojiThumbAsync(string url, Image image)
    {
        try
        {
            var bitmap = await ImageCache.GetAsync(AppServices.MapStaticUrl(url), 48);
            if (bitmap is not null)
            {
                image.Source = bitmap;
            }
        }
        catch (Exception)
        {
            // 缩略图失败不影响插入
        }
    }

    private async void InsertEmoji(string slug)
    {
        var bytes = await GetEmojiBytesAsync(slug);
        if (bytes is null)
        {
            ShowHint("表情加载失败，请检查网络");
            return;
        }

        try
        {
            var selection = CommentBox.Document.Selection;
            var start = selection.StartPosition;
            var end = selection.EndPosition;

            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }

                stream.Seek(0);

                // 图片的 alternateText 存 slug：RTF 里会写成 \picprop wzDescription，
                // 发送时按图片顺序读回来，编辑器里图片只占一个字符（可整体删除）。
                var range = CommentBox.Document.GetRange(start, end);
                range.InsertImage(20, 20, 0, VerticalCharacterAlignment.Baseline, slug, stream);
            }

            var caret = Math.Min(start + 1, CommentBox.Document.GetRange(0, int.MaxValue).EndPosition);
            CommentBox.Document.Selection.SetRange(caret, caret);

            CommentBox.Focus(FocusState.Programmatic);
            UpdateCommentState();
        }
        catch (Exception ex)
        {
            PixivDiagnostics.Write($"insert emoji failed: {ex.Message}");
            ShowHint("插入表情失败");
        }
    }

    private static async Task<byte[]?> GetEmojiBytesAsync(string slug)
    {
        if (EmojiBytes.TryGetValue(slug, out var cached))
        {
            return cached;
        }

        var url = PixivEmoji.UrlForSlug(slug);
        if (url is null)
        {
            return null;
        }

        try
        {
            var bytes = await AppServices.Images.GetByteArrayAsync(AppServices.MapStaticUrl(url));
            EmojiBytes[slug] = bytes;
            return bytes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (View.Item is not Illustration item)
        {
            return;
        }

        ShowHint("开始下载…");

        try
        {
            var folder = await AppServices.Downloads.DownloadIllustrationAsync(item);
            ShowHint(folder is null ? "下载失败：没有可用的图片地址" : $"已下载到 {folder}");
        }
        catch (Exception ex)
        {
            ShowHint($"下载失败：{ex.Message}");
        }
    }

    private async void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        var uri = new Uri($"https://www.pixiv.net/artworks/{View.Id}");
        try
        {
            await Launcher.LaunchUriAsync(uri);
        }
        catch (Exception)
        {
            ShowHint("无法打开浏览器");
        }
    }

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        var link = $"https://www.pixiv.net/artworks/{View.Id}";
        var package = new DataPackage();
        package.SetText(link);
        Clipboard.SetContent(package);
        ShowHint("已复制链接");
    }

    private void OnReportClick(object sender, RoutedEventArgs e) => ShowHint("举报功能待接入");

    private void ShowHint(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;

        _hintTimer?.Stop();
        _hintTimer = DispatcherQueue.CreateTimer();
        _hintTimer.Interval = TimeSpan.FromSeconds(3);
        _hintTimer.IsRepeating = false;
        _hintTimer.Tick += (_, _) => Toast.Visibility = Visibility.Collapsed;
        _hintTimer.Start();
    }
}
