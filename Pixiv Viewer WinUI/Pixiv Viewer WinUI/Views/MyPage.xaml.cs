using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Helpers;
using Pixiv_Viewer_WinUI.Models;
using Pixiv_Viewer_WinUI.Services;

namespace Pixiv_Viewer_WinUI.Views;

public sealed partial class MyPage : Page
{
    private const int PageSize = 40;

    private readonly ObservableCollection<Illustration> _items = new();
    private readonly TranslateTransform _headerShift = new();

    private string _section = "public";
    private PixivUser? _user;
    private bool _loaded;

    private string? _cursor;
    private int _nextOffset;
    private bool _hasMore = true;
    private bool _loadingMore;

    public MyPage()
    {
        InitializeComponent();
        Illustrations.ItemsSource = _items;

        // 向下滚时收起账号卡片（滑出），向上滚滑回
        ProfileCard.RenderTransform = _headerShift;
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
            if (_loaded)
            {
                Illustrations.Refresh();
                LoadProfile();
                return;
            }

            _loaded = true;
            await AppServices.Pixiv.SignInAsync();
            LoadProfile();
            await LoadSectionAsync(reset: true);
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"加载失败：{ex.Message}";
        }
    }

    private void LoadProfile()
    {
        _user = AppServices.Pixiv.IsLoggedIn
            ? AppServices.Pixiv.CurrentUser
            : AppServices.Settings.UseSampleData ? SampleData.CurrentUser : null;

        if (_user is null)
        {
            UserName.Text = "未登录";
            UserAccount.Text = "登录后可同步收藏、关注与自己的作品";
            Avatar.DisplayName = "未";
            Avatar.ProfilePicture = null;
            WorkCount.Text = "—";
            FollowingCount.Text = "—";
            BookmarkCountText.Text = "—";
            LoginButton.Content = "登录 Pixiv";
            Subtitle.Text = "登录后查看收藏与关注";
            return;
        }

        UserName.Text = _user.Name;
        UserAccount.Text = _user.AccountText;
        Avatar.DisplayName = _user.Name;
        Avatar.ProfilePicture = TryCreateImage(_user.AvatarUrl);
        WorkCount.Text = _user.IllustrationCountText;
        FollowingCount.Text = _user.FollowingText;
        BookmarkCountText.Text = _user.BookmarkText;
        LoginButton.Content = "退出登录";
        Subtitle.Text = $"已登录 · {_user.AccountText}";

        _ = RefreshProfileStatsAsync(_user);
    }

    /// <summary>OAuth 返回的 user 不带统计数字，登录后再拉一次 /v1/user/detail 补齐。</summary>
    private async Task RefreshProfileStatsAsync(PixivUser user)
    {
        if (user.IllustrationCount > 0 || user.FollowingCount > 0 || user.BookmarkCount > 0)
        {
            return;
        }

        try
        {
            var detail = await AppServices.Pixiv.GetUserAsync(user.Id);
            if (detail is null)
            {
                return;
            }

            user.IllustrationCount = detail.IllustrationCount;
            user.FollowingCount = detail.FollowingCount;
            user.BookmarkCount = detail.BookmarkCount;

            WorkCount.Text = user.IllustrationCountText;
            FollowingCount.Text = user.FollowingText;
            BookmarkCountText.Text = user.BookmarkText;
        }
        catch (Exception)
        {
            // 拉不到就保持 0，不打断页面
        }
    }

    private async Task LoadSectionAsync(bool reset)
    {
        if (reset)
        {
            _items.Clear();
            _cursor = null;
            _nextOffset = 0;
            _hasMore = true;
        }

        if (_section == "following")
        {
            await LoadFollowingAsync();
            return;
        }

        if (!_hasMore || _loadingMore)
        {
            return;
        }

        _loadingMore = true;
        LoadingRing.IsActive = reset;
        EmptyState.Visibility = Visibility.Collapsed;
        FollowingList.Visibility = Visibility.Collapsed;
        Illustrations.Visibility = Visibility.Visible;

        try
        {
            var result = _section switch
            {
                "private" => await AppServices.Pixiv.GetBookmarksAsync(true, _cursor, PageSize),
                "works" => await AppServices.Pixiv.GetMyWorksAsync(_nextOffset, PageSize),
                _ => await AppServices.Pixiv.GetBookmarksAsync(false, _cursor, PageSize),
            };

            foreach (var item in result.Items)
            {
                _items.Add(item);
            }

            _cursor = result.NextCursor;
            _nextOffset = result.NextOffset;
            _hasMore = result.HasMore;

            if (reset && _items.Count == 0 && AppServices.Settings.UseSampleData)
            {
                var fallback = _section switch
                {
                    "private" => SampleData.PrivateBookmarks,
                    "works" => SampleData.MyWorks,
                    _ => SampleData.PublicBookmarks,
                };

                foreach (var item in fallback)
                {
                    _items.Add(item);
                }

                _hasMore = false;
            }

            var (title, message) = _section switch
            {
                "private" => ("私密收藏为空", "只有你自己能看到的收藏会显示在这里。"),
                "works" => ("还没有作品", "你投稿的作品会显示在这里。"),
                _ => ("公开收藏为空", "在作品详情页点击「收藏」，作品就会出现在这里。"),
            };

            ShowEmpty(_items.Count == 0, title, message, "\uE734");
            Subtitle.Text = _items.Count == 0
                ? Subtitle.Text
                : $"已加载 {_items.Count} 件{(_hasMore ? " · 继续滚动加载更多" : string.Empty)}";
        }
        catch (Exception ex)
        {
            ShowEmpty(true, "加载失败", ex.Message, "\uE783");
        }
        finally
        {
            _loadingMore = false;
            LoadingRing.IsActive = false;
        }
    }

    private async Task LoadFollowingAsync()
    {
        LoadingRing.IsActive = true;
        EmptyState.Visibility = Visibility.Collapsed;
        Illustrations.Visibility = Visibility.Collapsed;

        try
        {
            var users = await AppServices.Pixiv.GetFollowingUsersAsync(0, 50);
            IReadOnlyList<PixivUser> list = users;

            if (list.Count == 0 && AppServices.Settings.UseSampleData)
            {
                list = SampleData.FollowingUsers;
            }

            FollowingList.ItemsSource = list;
            FollowingList.Visibility = Visibility.Visible;
            ShowEmpty(list.Count == 0, "还没有关注任何人", "在作品详情页点击作者旁的「关注」，就会出现在这里。", "\uE716");
        }
        catch (Exception ex)
        {
            ShowEmpty(true, "加载失败", ex.Message, "\uE783");
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private async void OnLoadMoreRequested(object? sender, EventArgs e) => await LoadSectionAsync(reset: false);

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

        await LoadSectionAsync(reset: true);
    }

    private void ShowEmpty(bool isEmpty, string title, string message, string glyph)
    {
        EmptyTitle.Text = title;
        EmptyMessage.Text = message;
        EmptyGlyph.Glyph = glyph;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string section)
        {
            _section = section;
            await LoadSectionAsync(reset: true);
        }
    }

    private void OnLoginClick(object sender, RoutedEventArgs e)
    {
        if (_user is null)
        {
            Frame.Navigate(typeof(SettingsPage));
            return;
        }

        AppServices.Pixiv.SignOut();
        _user = null;
        LoadProfile();
        _ = LoadSectionAsync(reset: true);
    }

    private void OnUnfollowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PixivUser user)
        {
            user.IsFollowed = false;
            Subtitle.Text = $"已取消关注 {user.Name}（示例操作）";
        }
    }

    private void OnItemInvoked(object? sender, Illustration item)
        => Frame.Navigate(typeof(IllustrationDetailPage), item);

    private void OnUserClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PixivUser user)
        {
            Frame.Navigate(typeof(UserPage), user);
        }
    }

    private async void OnCardAction(object? sender, CardActionEventArgs e)
    {
        if (sender is IllustrationGrid grid)
        {
            await IllustrationActions.HandleAsync(grid, e);
        }
    }

    private static ImageSource? TryCreateImage(string? url)
        => !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? new BitmapImage(uri)
            : null;
}
