using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Helpers;
using Pixiv_Viewer_WinUI.Models;
using Microsoft.UI.Text;
using Pixiv_Viewer_WinUI.Services;
using Pixiv_Viewer_WinUI.Services.Manga;
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

    // ---- 漫画翻译 ----
    private MangaPageResult? _mangaResult;
    private CancellationTokenSource? _mangaCts;
    private bool _showMangaTranslated = true;
    private bool _mangaBusy;

    /// <summary>译文应用的版本号：翻页/新结果会 +1，避免解码慢导致把上一页的译图画到当前页。</summary>
    private int _mangaApplyVersion;

    /// <summary>当前去字模式（设置页可切换）。</summary>
    private static MangaInpaintMode CurrentInpaintMode => AppServices.Settings.MangaInpaintMode;

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
            UpdateFullScreenMangaStage();
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
        UpdateMangaStage();
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

            MarkTranslatedPages();

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
                ResetMangaForCurrentPage();
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
        UpdateMangaStage();
    }

    private void OnThumbSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 只响应用户点选：程序同步 SelectionIndex 时也会触发，重复跳转会打乱翻页顺序
        if (!_syncingThumb &&
            ThumbList.SelectedIndex >= 0 &&
            ThumbList.SelectedIndex != View.CurrentIndex)
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
            UpdateFullScreenMangaStage();

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
        if (_pendingStampId is { } stampId)
        {
            await SendStampAsync(stampId);
            return;
        }

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

    private async Task SendStampAsync(string stampId)
    {
        SendButton.IsEnabled = false;
        EmojiButton.IsEnabled = false;
        SendButton.Content = "发送中…";

        try
        {
            var ok = await View.AddStampCommentAsync(stampId);
            if (ok)
            {
                ClearStamp();
                ShowHint("贴图已发送");
            }
            else
            {
                ShowHint("贴图发送失败：请检查登录状态、网页 Cookie 或网络");
            }
        }
        catch (Exception ex)
        {
            ShowHint($"贴图发送失败：{ex.Message}");
        }
        finally
        {
            SendButton.Content = "发送";
            EmojiButton.IsEnabled = true;
            UpdateCommentState();
        }
    }
    /// <summary>pixiv 评论长度上限。</summary>
    private const int MaxCommentLength = 140;

    /// <summary>RichEditBox 里图片在文本中的占位符。</summary>
    private const char ImagePlaceholder = '\uFFFC';

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> EmojiBytes = new();

    /// <summary>官方贴图 id：101–110 / 201–210 / 301–310 / 401–410。</summary>
    private static readonly int[] StampIds = BuildStampIds();

    private static int[] BuildStampIds()
    {
        var ids = new List<int>(40);

        foreach (var group in new[] { 1, 2, 3, 4 })
        {
            for (var index = 1; index <= 10; index++)
            {
                ids.Add((group * 100) + index);
            }
        }

        return ids.ToArray();
    }

    /// <summary>当前选中的待发送贴图；null 表示文字模式。</summary>
    private string? _pendingStampId;

    private static string StampUrl(int id)
        => $"https://s.pximg.net/common/images/stamp/generated-stamps/{id}_s.jpg";

    private void OnCommentTextChanged(object sender, RoutedEventArgs e) => UpdateCommentState();

    private void UpdateCommentState()
    {
        if (_pendingStampId is not null)
        {
            SendButton.IsEnabled = true;
            return;
        }

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

    private void OnEmojiTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var stamp = sender.SelectedItem?.Tag as string == "stamp";

        EmojiHost.Visibility = stamp ? Visibility.Collapsed : Visibility.Visible;
        StampHost.Visibility = stamp ? Visibility.Visible : Visibility.Collapsed;

        if (stamp && StampHost.Children.Count == 0)
        {
            BuildStampPanel();
        }
    }

    private void BuildStampPanel()
    {
        for (var i = 0; i < StampIds.Length; i += 5)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
            };

            foreach (var id in StampIds.Skip(i).Take(5))
            {
                row.Children.Add(CreateStampButton(id));
            }

            StampHost.Children.Add(row);
        }
    }

    private Button CreateStampButton(int id)
    {
        var image = new Image
        {
            Width = 56,
            Height = 56,
            Stretch = Stretch.Uniform,
        };

        _ = LoadEmojiThumbAsync(StampUrl(id), image);

        var button = new Button
        {
            Width = 60,
            Height = 60,
            Padding = new Thickness(0),
            Content = image,
            Tag = id,
            AllowFocusOnInteraction = false,
            IsTabStop = false,
        };

        ToolTipService.SetToolTip(button, $"贴图 {id}");

        button.Click += (s, _) =>
        {
            if (s is Button { Tag: int stampId })
            {
                SelectStamp(stampId);
            }
        };

        return button;
    }

    /// <summary>选中一张贴图：切到贴图模式，文字框禁用（贴图和文字不能同时发）。</summary>
    private async void SelectStamp(int id)
    {
        _pendingStampId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        StampPreview.Visibility = Visibility.Visible;
        EmojiPanel.Visibility = Visibility.Collapsed;
        CommentCounter.Visibility = Visibility.Collapsed;
        CommentBox.Document.SetText(TextSetOptions.None, string.Empty);
        CommentBox.IsEnabled = false;

        try
        {
            StampPreviewImage.Source = await ImageCache.GetAsync(AppServices.MapStaticUrl(StampUrl(id)), 160);
        }
        catch (Exception)
        {
            // 预览失败不影响发送
        }

        UpdateCommentState();
    }

    private void OnCancelStampClick(object sender, RoutedEventArgs e) => ClearStamp();

    private void ClearStamp()
    {
        if (_pendingStampId is null)
        {
            return;
        }

        _pendingStampId = null;
        StampPreview.Visibility = Visibility.Collapsed;
        StampPreviewImage.Source = null;
        CommentCounter.Visibility = Visibility.Visible;
        CommentBox.IsEnabled = true;
        CommentBox.Document.SetText(TextSetOptions.None, string.Empty);
        UpdateCommentState();
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

    // ================= 漫画翻译 =================

    private async void OnMangaTranslateButtonClick(SplitButton sender, SplitButtonClickEventArgs args)
        => await TranslateMangaPageAsync(force: false);

    private async void OnMangaTranslatePageClick(object sender, RoutedEventArgs e)
        => await TranslateMangaPageAsync(force: false);

    private async void OnMangaRetranslateClick(object sender, RoutedEventArgs e)
        => await TranslateMangaPageAsync(force: true);

    private async void OnMangaTranslateAllClick(object sender, RoutedEventArgs e)
        => await TranslateMangaAllAsync();

    private void OnMangaToggleOriginalClick(object sender, RoutedEventArgs e)
    {
        if (_mangaResult is null)
        {
            ShowHint("本页还没有译文");
            return;
        }

        _showMangaTranslated = !_showMangaTranslated;
        UpdateMangaLayerVisibility();
        ShowHint(_showMangaTranslated ? "已切换到译文" : "已切换到原图");
    }

    private void OnMangaCancelClick(object sender, RoutedEventArgs e)
    {
        _mangaCts?.Cancel();
        ShowHint("已取消");
    }

    /// <summary>取某一页的原图地址：原图 → large 预览 → 缩略图逐级兜底。</summary>
    private string? MangaPageUrl(int index)
    {
        var item = View.Item;
        if (item is null)
        {
            return null;
        }

        foreach (var list in new[] { item.OriginalUrls, item.PageUrls, item.PreviewUrls })
        {
            if (index < list.Count && !string.IsNullOrWhiteSpace(list[index]))
            {
                return list[index];
            }
        }

        return null;
    }

    /// <summary>确保模型已下载；没有则弹窗询问并下载。</summary>
    private async Task<bool> EnsureMangaModelsAsync()
    {
        if (MangaTranslationService.IsReady)
        {
            return true;
        }

        var megabytes = MangaTranslationService.ModelSize / 1024d / 1024d;
        var partial = MangaTranslationService.PartialBytes;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "需要下载漫画翻译模型",
            Content = $"漫画翻译的识别与去字在本机进行，需要先下载模型（约 {megabytes:F0} MB）。\n" +
                      (partial > 0 ? $"上次已下载 {Format.Bytes(partial)}，本次会继续。\n" : string.Empty) +
                      $"\n保存位置：{MangaTranslationService.ModelDirectory}\n" +
                      "只有识别出的文字会发给翻译服务，图片不会上传。",
            PrimaryButtonText = partial > 0 ? "继续下载" : "下载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        ShowMangaBusy("下载模型…", 0, cancellable: true);
        _mangaCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<MangaProgress>(p => UpdateDownloadBusy(p));
            var ok = await MangaTranslationService.EnsureModelsAsync(progress, _mangaCts.Token);
            if (!ok)
            {
                ShowHint("模型下载失败，请检查网络后重试");
            }

            return ok;
        }
        catch (OperationCanceledException)
        {
            ShowHint("已取消下载");
            return false;
        }
        catch (Exception ex)
        {
            ShowHint("模型下载失败：" + ex.Message);
            return false;
        }
        finally
        {
            HideMangaBusy();
            _mangaCts = null;
        }
    }

    private async Task TranslateMangaPageAsync(bool force)
    {
        if (_mangaBusy || View.Item is null)
        {
            return;
        }

        if (!await EnsureMangaModelsAsync())
        {
            return;
        }

        if (CurrentInpaintMode == MangaInpaintMode.Lama && !MangaTranslationService.IsLamaReady)
        {
            ShowHint("高精度去字模型未下载，本次使用快速模式（可在设置里下载）");
        }

        var illustId = View.Id;
        var pageIndex = View.CurrentIndex;
        var imageUrl = View.CurrentOriginalUrl;

        if (force)
        {
            MangaCache.DeletePage(illustId, pageIndex);
        }
        else if (MangaTranslationService.TryGetCached(illustId, pageIndex, CurrentInpaintMode) is { } cached)
        {
            await ApplyMangaResultAsync(cached);
            return;
        }

        ShowMangaBusy("准备中…", 0);
        _mangaCts = new CancellationTokenSource();
        var succeeded = false;

        try
        {
            var progress = new Progress<MangaProgress>(p => UpdateMangaBusy(p.Stage, p.Fraction));
            var result = await MangaTranslationService.TranslatePageAsync(
                illustId,
                pageIndex,
                imageUrl,
                CurrentInpaintMode,
                progress,
                _mangaCts.Token);

            if (result is null)
            {
                ShowHint("翻译失败，可稍后重试");
                return;
            }

            if (View.CurrentIndex == pageIndex && View.Id == illustId)
            {
                await ApplyMangaResultAsync(result);
            }

            MarkPageTranslated(illustId, pageIndex);
            succeeded = true;
            ShowHint($"已翻译 {result.Regions.Count} 处文字");
        }
        catch (OperationCanceledException)
        {
            ShowHint("已取消");
        }
        catch (Exception ex)
        {
            ShowHint("翻译失败：" + ex.Message);
        }
        finally
        {
            HideMangaBusy();
            _mangaCts = null;

            if (!succeeded)
            {
                // 保持原状态
            }
        }
    }

    private async Task TranslateMangaAllAsync()
    {
        if (_mangaBusy || View.Item is null || View.PageCount <= 1)
        {
            if (View.PageCount <= 1)
            {
                await TranslateMangaPageAsync(force: false);
            }

            return;
        }

        if (!await EnsureMangaModelsAsync())
        {
            return;
        }

        var illustId = View.Id;
        var total = View.PageCount;
        var done = 0;
        var failed = 0;

        ShowMangaBusy($"翻译中… 0/{total}", 0, cancellable: true);
        _mangaCts = new CancellationTokenSource();

        try
        {
            for (var i = 0; i < total; i++)
            {
                _mangaCts.Token.ThrowIfCancellationRequested();

                var url = MangaPageUrl(i);
                if (string.IsNullOrWhiteSpace(url))
                {
                    failed++;
                    done++;
                    PixivDiagnostics.Write($"manga batch: page {i + 1} no url");
                    continue;
                }

                var pageIndex = i;
                UpdateMangaBusy($"翻译中… {done + 1}/{total}", (double)done / total);

                var progress = new Progress<MangaProgress>(p =>
                    UpdateMangaBusy($"第 {pageIndex + 1} 页 · {p.Stage}", (done + p.Fraction) / total));

                var watch = System.Diagnostics.Stopwatch.StartNew();
                MangaPageResult? result = null;

                try
                {
                    result = await MangaTranslationService.TranslatePageAsync(
                        illustId,
                        i,
                        url,
                        CurrentInpaintMode,
                        progress,
                        _mangaCts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单页失败不能中断整篇
                    PixivDiagnostics.Write($"manga batch: page {i + 1} EX {ex.GetType().Name}: {ex.Message}");
                }

                PixivDiagnostics.Write(
                    $"manga batch: page {i + 1} {(result is null ? "FAILED" : $"ok regions={result.Regions.Count}")} " +
                    $"{watch.ElapsedMilliseconds}ms url={(url.Length > 60 ? url[..60] : url)}");

                done++;

                if (result is not null && View.CurrentIndex == i && View.Id == illustId)
                {
                    await ApplyMangaResultAsync(result);
                }

                if (result is not null)
                {
                    MarkPageTranslated(illustId, i);
                }
                else
                {
                    failed++;
                }

                UpdateMangaBusy($"翻译中… {done}/{total}", (double)done / total);
            }

            ShowHint(failed == 0
                ? $"整篇翻译完成（{total} 页）"
                : $"整篇完成，{total - failed}/{total} 页成功（{failed} 页图片获取失败，可稍后重试）");
        }
        catch (OperationCanceledException)
        {
            ShowHint($"已取消，完成 {done}/{total} 页");
        }
        catch (Exception ex)
        {
            ShowHint("批量翻译失败：" + ex.Message);
        }
        finally
        {
            HideMangaBusy();
            _mangaCts = null;
        }
    }

    private async Task ApplyMangaResultAsync(MangaPageResult result)
    {
        var version = ++_mangaApplyVersion;
        var illustId = result.IllustId;
        var pageIndex = result.PageIndex;

        var bytes = result.Png ?? MangaTranslationService.ReadCachedImage(result);
        if (bytes is null)
        {
            ShowHint("译图丢失，请重新翻译");
            return;
        }

        var bitmap = await DecodeImageAsync(bytes, 1200);
        if (bitmap is null)
        {
            ShowHint("译图解码失败");
            return;
        }

        // 解码期间用户可能已经翻页，或者有更新的结果；此时把这次的丢掉，避免画错页
        if (version != _mangaApplyVersion || View.Id != illustId || View.CurrentIndex != pageIndex)
        {
            return;
        }

        _mangaResult = result;
        TranslatedImage.Source = bitmap;
        UpdateMangaStage();
        UpdateMangaLayerVisibility();
    }

    private void ResetMangaForCurrentPage()
    {
        // 让还在解码中的旧结果失效
        _mangaApplyVersion++;

        _mangaResult = null;
        TranslatedImage.Source = null;
        MangaOverlay.Children.Clear();
        _showMangaTranslated = true;
        UpdateMangaLayerVisibility();

        if (MangaTranslationService.IsReady && View.Item is not null)
        {
            _ = LoadCachedMangaAsync(View.Id, View.CurrentIndex);
        }
    }

    private async Task LoadCachedMangaAsync(string illustId, int pageIndex)
    {
        await Task.Yield();

        // 当前已经显示着同一页的译文，就不用再解一次图
        if (_mangaResult is not null &&
            _mangaResult.IllustId == illustId &&
            _mangaResult.PageIndex == pageIndex &&
            TranslatedImage.Source is not null)
        {
            return;
        }

        var cached = MangaTranslationService.TryGetCached(illustId, pageIndex, CurrentInpaintMode);
        if (cached is null || View.Id != illustId || View.CurrentIndex != pageIndex)
        {
            return;
        }

        await ApplyMangaResultAsync(cached);
    }

    private void UpdateMangaLayerVisibility()
    {
        var show = _mangaResult is not null && _showMangaTranslated;
        TranslatedImage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MangaOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FullScreenTranslated.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FullScreenMangaOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>全屏层与普通视图共用同一份译文，需要按全屏尺寸重算对齐。</summary>
    private void UpdateFullScreenMangaStage()
    {
        var hostWidth = FullScreenHost.ActualWidth;
        var hostHeight = FullScreenHost.ActualHeight;
        if (hostWidth <= 1 || hostHeight <= 1)
        {
            return;
        }

        var (imageWidth, imageHeight) = GetCurrentImageSize();
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(hostWidth / imageWidth, hostHeight / imageHeight);
        var stageWidth = Math.Max(1, imageWidth * scale);
        var stageHeight = Math.Max(1, imageHeight * scale);

        FullScreenStage.Width = stageWidth;
        FullScreenStage.Height = stageHeight;
        FullScreenMangaOverlay.Width = stageWidth;
        FullScreenMangaOverlay.Height = stageHeight;
        FullScreenTranslated.Source = TranslatedImage.Source;

        BuildMangaOverlayInto(FullScreenMangaOverlay, stageWidth, stageHeight);
    }

    /// <summary>刷新缩略图上的「译」角标（只查文件是否存在，不读 JSON）。</summary>
    private void MarkTranslatedPages()
    {
        if (!MangaTranslationService.IsReady)
        {
            return;
        }

        var illustId = View.Id;
        foreach (var page in View.Pages)
        {
            page.IsTranslated = MangaTranslationService.HasCached(illustId, page.Index, CurrentInpaintMode);
        }
    }

    /// <summary>标记单页已翻译（缩略图角标）。</summary>
    private void MarkPageTranslated(string illustId, int pageIndex)
    {
        if (View.Id != illustId || pageIndex < 0 || pageIndex >= View.Pages.Count)
        {
            return;
        }

        View.Pages[pageIndex].IsTranslated = true;
    }

    /// <summary>按图片比例计算图片实际显示区域，让叠层与图片严格对齐。</summary>
    private void UpdateMangaStage()
    {
        var hostWidth = ImageHost.ActualWidth;
        var hostHeight = ImageHost.ActualHeight;
        if (hostWidth <= 1 || hostHeight <= 1)
        {
            return;
        }

        var (imageWidth, imageHeight) = GetCurrentImageSize();
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(hostWidth / imageWidth, hostHeight / imageHeight);
        var stageWidth = Math.Max(1, imageWidth * scale);
        var stageHeight = Math.Max(1, imageHeight * scale);

        ImageStage.Width = stageWidth;
        ImageStage.Height = stageHeight;
        MangaOverlay.Width = stageWidth;
        MangaOverlay.Height = stageHeight;

        BuildMangaOverlay();
    }

    private (double Width, double Height) GetCurrentImageSize()
    {
        if (PageImage.Source is BitmapImage bitmap && bitmap.PixelWidth > 0)
        {
            return (bitmap.PixelWidth, bitmap.PixelHeight);
        }

        if (_mangaResult is not null && _mangaResult.Width > 0)
        {
            return (_mangaResult.Width, _mangaResult.Height);
        }

        return (0, 0);
    }

    /// <summary>把译文区域画到图片上：底色板 + 自动字号的中文。</summary>
    private void BuildMangaOverlay()
        => BuildMangaOverlayInto(MangaOverlay, ImageStage.Width, ImageStage.Height);

    private void BuildMangaOverlayInto(Canvas canvas, double stageWidth, double stageHeight)
    {
        canvas.Children.Clear();

        if (_mangaResult is null || stageWidth <= 1 || stageHeight <= 1)
        {
            return;
        }

        foreach (var region in _mangaResult.Regions)
        {
            if (string.IsNullOrWhiteSpace(region.Target))
            {
                continue;
            }

            // 排版用气泡区域（没有气泡信息时退回文字框）
            var useArea = region.AreaW > 0 && region.AreaH > 0;
            var left = (useArea ? region.AreaX : region.X) * stageWidth;
            var top = (useArea ? region.AreaY : region.Y) * stageHeight;
            var width = Math.Max(8, (useArea ? region.AreaW : region.W) * stageWidth);
            var height = Math.Max(8, (useArea ? region.AreaH : region.H) * stageHeight);

            var plateColor = ParseColor(region.PlateColor, Windows.UI.Color.FromArgb(255, 255, 255, 255));
            var plate = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(Math.Min(6, height * 0.12)),
                Background = new SolidColorBrush(plateColor) { Opacity = 0.94 },
            };

            Canvas.SetLeft(plate, left);
            Canvas.SetTop(plate, top);
            canvas.Children.Add(plate);

            var foreground = IsLight(plateColor)
                ? Windows.UI.Color.FromArgb(255, 20, 20, 20)
                : Windows.UI.Color.FromArgb(255, 245, 245, 245);

            var text = new TextBlock
            {
                Text = region.Target,
                Width = width * 0.92,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(foreground),
            };

            // 先挂到画布再测量，避免脱离可视化树时 DesiredSize 不可靠
            canvas.Children.Add(text);
            var fontSize = FitFontSize(text, width * 0.92, height * 0.92);

            // 字号封顶：不超过原文字号太多，避免短译文被放大到糊满整格。
            // 竖排行框的「高度」是整列长度，字号应该用粗细（min(w,h)）来估。
            if (region.Lines.Count > 0)
            {
                var thickness = region.Lines
                    .Select(l => Math.Min(l.W, l.H))
                    .OrderBy(v => v)
                    .ElementAt(region.Lines.Count / 2);
                var cap = thickness * stageHeight * 1.15;
                fontSize = Math.Min(fontSize, Math.Max(9, cap));
            }

            text.FontSize = fontSize;
            text.Measure(new Size(text.Width, double.PositiveInfinity));

            var textHeight = Math.Min(height, Math.Max(1, text.DesiredSize.Height));
            Canvas.SetLeft(text, left + width * 0.04);
            Canvas.SetTop(text, top + Math.Max(0, (height - textHeight) / 2));
        }
    }

    /// <summary>二分查找能放进框里的最大字号（元素需已挂到可视化树）。</summary>
    private static double FitFontSize(TextBlock probe, double width, double height)
    {
        if (width <= 2 || height <= 2)
        {
            return 6;
        }

        double low = 6;
        double high = Math.Clamp(height, 7, 64);
        var best = low;

        for (var i = 0; i < 8; i++)
        {
            var mid = (low + high) / 2;
            probe.FontSize = mid;
            probe.Measure(new Size(width, double.PositiveInfinity));
            if (probe.DesiredSize.Height <= height)
            {
                best = mid;
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return best;
    }

    private static Windows.UI.Color ParseColor(string value, Windows.UI.Color fallback)
    {
        try
        {
            if (value.Length == 7 && value[0] == '#')
            {
                var r = Convert.ToByte(value.Substring(1, 2), 16);
                var g = Convert.ToByte(value.Substring(3, 2), 16);
                var b = Convert.ToByte(value.Substring(5, 2), 16);
                return Windows.UI.Color.FromArgb(255, r, g, b);
            }
        }
        catch (Exception)
        {
            // 退回默认色
        }

        return fallback;
    }

    private static bool IsLight(Windows.UI.Color color)
        => (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255 > 0.6;

    private static async Task<BitmapImage?> DecodeImageAsync(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var image = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = decodeWidth,
            };

            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ShowMangaBusy(string message, double _ = 0, bool cancellable = false)
    {
        _mangaBusy = true;
        MangaBusyText.Text = message;
        MangaCancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        MangaBusy.Visibility = Visibility.Visible;
    }

    private void UpdateMangaBusy(string stage, double fraction)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            MangaBusyText.Text = fraction > 0 ? $"{stage} {fraction * 100:F0}%" : stage;
        });
    }

    /// <summary>下载模型时的进度文案（带 MB 与速度），供忙碌条复用。</summary>
    private void UpdateDownloadBusy(MangaProgress progress)
    {
        var text = progress.TotalBytes > 0
            ? $"{progress.Stage} · {progress.ReceivedBytes / 1024d / 1024d:F1}/{progress.TotalBytes / 1024d / 1024d:F1} MB" +
              $" · {progress.Fraction * 100:F0}%"
            : progress.Stage;
        DispatcherQueue.TryEnqueue(() => MangaBusyText.Text = text);
    }

    private void HideMangaBusy()
    {
        _mangaBusy = false;
        MangaBusy.Visibility = Visibility.Collapsed;
        MangaCancelButton.Visibility = Visibility.Collapsed;
    }

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
