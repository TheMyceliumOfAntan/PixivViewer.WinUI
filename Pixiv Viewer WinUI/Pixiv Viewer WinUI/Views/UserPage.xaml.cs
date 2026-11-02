using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Helpers;
using Pixiv_Viewer_WinUI.Models;
using Pixiv_Viewer_WinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Pixiv_Viewer_WinUI.Views;

/// <summary>
/// 作者主页：资料 + 作品瀑布流。从作品详情、评论头像、关注列表进入。
/// </summary>
public sealed partial class UserPage : Page
{
    private PixivUser? _user;
    private readonly TranslateTransform _headerShift = new();

    private const int PageSize = 40;
    private readonly System.Collections.ObjectModel.ObservableCollection<Illustration> _items = new();
    private int _nextOffset;
    private bool _hasMore = true;
    private bool _loadingMore;

    public UserPage()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, new KeyEventHandler(OnPageKeyDown), true);

        // 向下滚收起资料卡（滑出），向上滚滑回
        ProfileCard.RenderTransform = _headerShift;
        Illustrations.ItemsSource = _items;
        Illustrations.ScrollDirectionChanged += (_, down) =>
        {
            if (down && ProfileCard.Visibility == Visibility.Visible)
            {
                SlideAnimator.SlideOut(ProfileCard, _headerShift);
            }
            else if (!down && ProfileCard.Visibility != Visibility.Visible)
            {
                SlideAnimator.SlideIn(ProfileCard, _headerShift);
            }
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            _user = e.Parameter as PixivUser;
            if (_user is null)
            {
                ShowEmpty("没有作者信息", "请从作品详情或关注列表打开作者主页。");
                return;
            }

            ApplyUser(_user);
            await LoadAsync(reset: true);
        }
        catch (Exception ex)
        {
            ShowEmpty("加载失败", ex.Message);
        }
    }

    private void ApplyUser(PixivUser user)
    {
        UserName.Text = user.Name;
        UserAccount.Text = user.AccountText;
        UserBio.Text = string.IsNullOrWhiteSpace(user.Biography)
            ? "这位作者还没有填写个人简介。"
            : user.Biography;

        Avatar.DisplayName = user.Name;
        Avatar.ProfilePicture = TryCreateImage(user.AvatarUrl);

        WorkCount.Text = user.IllustrationCountText;
        FollowingCount.Text = user.FollowingText;
        BookmarkCountText.Text = user.BookmarkText;

        FollowLabel.Text = user.IsFollowed ? "已关注" : "关注";
    }

    private async Task LoadAsync(bool reset)
    {
        if (_user is null)
        {
            return;
        }

        if (reset)
        {
            _items.Clear();
            _nextOffset = 0;
            _hasMore = true;
        }

        if (!_hasMore || _loadingMore)
        {
            return;
        }

        _loadingMore = true;
        LoadingRing.IsActive = reset;
        EmptyState.Visibility = Visibility.Collapsed;

        try
        {
            if (reset)
            {
                var fresh = await AppServices.Pixiv.GetUserAsync(_user.Id);
                if (fresh is not null)
                {
                    _user = fresh;
                    ApplyUser(fresh);
                }
            }

            var result = await AppServices.Pixiv.GetUserIllustrationsAsync(_user.Id, _nextOffset, PageSize);

            foreach (var item in result.Items)
            {
                _items.Add(item);
            }

            _nextOffset = result.NextOffset;
            _hasMore = result.HasMore;

            if (reset && _items.Count == 0 && AppServices.Settings.UseSampleData)
            {
                foreach (var item in SampleData.GetUserIllustrations(_user.Id, _user))
                {
                    _items.Add(item);
                }

                _hasMore = false;
            }

            CountText.Text = $"{_items.Count} 件{(_hasMore ? " · 继续滚动加载更多" : string.Empty)}";
            ShowEmpty(
                _items.Count == 0 ? "还没有作品" : string.Empty,
                "这位作者还没有公开作品。");
        }
        catch (Exception ex)
        {
            ShowEmpty("加载失败", ex.Message);
        }
        finally
        {
            _loadingMore = false;
            LoadingRing.IsActive = false;
        }
    }

    private void ShowEmpty(string title, string message)
    {
        EmptyState.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrEmpty(title))
        {
            EmptyTitle.Text = title;
            EmptyMessage.Text = message;
        }
    }

    private async void OnCopyIdClick(object sender, RoutedEventArgs e) => await CopyAsync(_user?.Id, "用户 ID");

    private async void OnCopyIdClick(SplitButton sender, SplitButtonClickEventArgs args) => await CopyAsync(_user?.Id, "用户 ID");

    private async void OnCopyLinkClick(object sender, RoutedEventArgs e)
        => await CopyAsync(_user is null ? null : $"https://www.pixiv.net/users/{_user.Id}", "主页链接");

    private async void OnCopyNameClick(object sender, RoutedEventArgs e) => await CopyAsync(_user?.Name, "用户名");

    private async void OnCopyAvatarUrlClick(object sender, RoutedEventArgs e) => await CopyAsync(_user?.AvatarUrl, "头像链接");

    private async System.Threading.Tasks.Task CopyAsync(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
        HintText.Text = $"已复制{label}";
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private async void OnSaveAvatarClick(object sender, RoutedEventArgs e)
    {
        if (_user is null || string.IsNullOrWhiteSpace(_user.AvatarUrl))
        {
            HintText.Text = "没有可保存的头像";
            return;
        }

        try
        {
            var ok = await AppServices.Downloads.SaveImageAsync(_user.AvatarUrl, $"{_user.Id}_avatar");
            HintText.Text = ok ? "头像已保存" : "已取消";
        }
        catch (Exception ex)
        {
            HintText.Text = $"保存失败：{ex.Message}";
        }
    }

    private async void OnLoadMoreRequested(object? sender, EventArgs e) => await LoadAsync(reset: false);

    private async void OnFilterClick(object sender, RoutedEventArgs e)
    {
        var dialog = new FilterDialog(new FilterOptions
        {
            ShowAi = AppServices.Settings.ShowAi,
            ShowR18 = AppServices.Settings.ShowR18,
            ShowR18G = AppServices.Settings.ShowR18G,
            BlockedTags = AppServices.Settings.BlockedTags,
        })
        {
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || dialog.Result is null)
        {
            return;
        }

        AppServices.Settings.ShowAi = dialog.Result.ShowAi;
        AppServices.Settings.ShowR18 = dialog.Result.ShowR18;
        AppServices.Settings.ShowR18G = dialog.Result.ShowR18G;
        AppServices.Settings.BlockedTags = dialog.Result.BlockedTags;

        await LoadAsync(reset: true);
    }

    private async void OnFollowClick(object sender, RoutedEventArgs e)
    {
        if (_user is null)
        {
            return;
        }

        var value = !_user.IsFollowed;
        await AppServices.Pixiv.SetFollowAsync(_user.Id, value);
        _user.IsFollowed = value;
        FollowLabel.Text = value ? "已关注" : "关注";
    }

    private void OnItemInvoked(object? sender, Illustration item)
        => Frame.Navigate(typeof(IllustrationDetailPage), item);

    private async void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        if (_user is null)
        {
            return;
        }

        try
        {
            await Launcher.LaunchUriAsync(new Uri($"https://www.pixiv.net/users/{_user.Id}"));
        }
        catch (Exception)
        {
            // 忽略无法打开浏览器的情况。
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            GoBack();
            e.Handled = true;
        }
    }

    private void GoBack()
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private static ImageSource? TryCreateImage(string? url)
        => !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? new BitmapImage(uri)
            : null;

    private async void OnCardAction(object? sender, CardActionEventArgs e)
    {
        if (sender is IllustrationGrid grid)
        {
            await IllustrationActions.HandleAsync(grid, e);
        }
    }
}
