using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Models;
using Pixiv_Viewer_WinUI.Services;
using Windows.System;

namespace Pixiv_Viewer_WinUI.Views;

/// <summary>
/// 收藏集页（https://www.pixiv.net/collections/{id}）：名称 + 作者 + 简介 + 作品瀑布流。
/// 数据走网页版 ajax，需要网页登录 cookie。
/// </summary>
public sealed partial class CollectionPage : Page
{
    private string _collectionId = string.Empty;

    public CollectionPage()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, new KeyEventHandler(OnPageKeyDown), true);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _collectionId = (e.Parameter as string ?? string.Empty).Trim();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_collectionId.Length == 0)
        {
            ShowError("收藏集 ID 为空", "请从收藏集链接打开。");
            return;
        }

        LoadingRing.IsActive = true;
        EmptyState.Visibility = Visibility.Collapsed;

        try
        {
            var collection = await AppServices.PixivApi.GetCollectionAsync(_collectionId);

            if (collection is null)
            {
                ShowError("无法加载收藏集", "收藏集不存在、已删除，或当前账号无权访问。");
                return;
            }

            Heading.Text = string.IsNullOrWhiteSpace(collection.Name) ? "收藏集" : collection.Name;
            Description.Text = collection.Description;
            Description.Visibility = string.IsNullOrWhiteSpace(collection.Description)
                ? Visibility.Collapsed
                : Visibility.Visible;

            AuthorName.Text = collection.Author.Name;
            AuthorAvatar.DisplayName = string.IsNullOrWhiteSpace(collection.Author.Name) ? "?" : collection.Author.Name;
            AuthorAvatar.ProfilePicture = TryCreateImage(collection.Author.AvatarUrl);
            WorksText.Text = collection.WorksText;

            Illustrations.ItemsSource = collection.Illustrations;
            Illustrations.ScrollToTop();

            EmptyState.Visibility = collection.Illustrations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowError("加载失败", ex.Message);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void ShowError(string title, string message)
    {
        EmptyTitle.Text = title;
        EmptyMessage.Text = message;
        EmptyState.Visibility = Visibility.Visible;
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private void OnItemInvoked(object? sender, Illustration item)
        => Frame.Navigate(typeof(IllustrationDetailPage), item);

    private async void OnCardAction(object? sender, CardActionEventArgs e)
    {
        if (sender is IllustrationGrid grid)
        {
            await IllustrationActions.HandleAsync(grid, e);
        }
    }

    private async void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri($"https://www.pixiv.net/collections/{_collectionId}"));
        }
        catch (Exception)
        {
            // 忽略
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
}
