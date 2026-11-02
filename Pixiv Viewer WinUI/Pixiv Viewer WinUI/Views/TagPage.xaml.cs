using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Helpers;
using Pixiv_Viewer_WinUI.Models;
using Pixiv_Viewer_WinUI.Services;
using Windows.System;

namespace Pixiv_Viewer_WinUI.Views;

/// <summary>
/// 标签详情页：放大显示 #标签 + 关联作品，支持排序 / 类型切换 / 筛选 / 滚动加载更多。
/// </summary>
public sealed partial class TagPage : Page
{
    private const int PageSize = 40;

    private readonly ObservableCollection<Illustration> _items = new();
    private readonly TranslateTransform _headerShift = new();
    private string _tag = string.Empty;
    private int _nextOffset;
    private bool _hasMore;
    private bool _loadingMore;

    public TagPage()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, new KeyEventHandler(OnPageKeyDown), true);
        Illustrations.ItemsSource = _items;

        // 向下滚收起大标题（滑出），向上滚滑回
        HeaderPanel.RenderTransform = _headerShift;
        Illustrations.ScrollDirectionChanged += (_, down) =>
        {
            if (down && HeaderPanel.Visibility == Visibility.Visible)
            {
                SlideAnimator.SlideOut(HeaderPanel, _headerShift);
            }
            else if (!down && HeaderPanel.Visibility != Visibility.Visible)
            {
                SlideAnimator.SlideIn(HeaderPanel, _headerShift);
            }
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 详情页会传 IllustTag（含官方译名），其它入口仍然只传字符串
        var info = e.Parameter as IllustTag;
        _tag = (info?.Name ?? e.Parameter as string ?? string.Empty).Trim();
        TagTitle.Text = "#" + _tag;
        Heading.Text = "#" + _tag;

        HeadingTranslation.Text = info is { HasTranslation: true } ? info.Translated : string.Empty;
        HeadingTranslation.Visibility = info is { HasTranslation: true }
            ? Visibility.Visible
            : Visibility.Collapsed;

        await LoadAsync(reset: true);
    }

    private SearchFilter BuildFilter() => new()
    {
        Tags = new[] { _tag },
        Sort = ((SortBox.SelectedItem as ComboBoxItem)?.Tag as string) == "Latest"
            ? SearchSort.Latest
            : SearchSort.Popular,
        Target = ((TargetBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "Manga" => SearchTarget.Manga,
            "Ugoira" => SearchTarget.Ugoira,
            "Illustrations" => SearchTarget.Illustrations,
            _ => SearchTarget.All,
        },
    };

    private async Task LoadAsync(bool reset)
    {
        if (_tag.Length == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            Subtitle.Text = "标签为空";
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
            var result = await AppServices.Pixiv.SearchAsync(BuildFilter(), _nextOffset, PageSize);

            foreach (var item in result.Items)
            {
                _items.Add(item);
            }

            _nextOffset = result.NextOffset;
            _hasMore = result.HasMore;

            Subtitle.Text = $"{_items.Count} 件作品 · 同时包含该标签{(_hasMore ? " · 继续滚动加载更多" : string.Empty)}";
            EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"加载失败：{ex.Message}";
            EmptyState.Visibility = Visibility.Visible;
        }
        finally
        {
            _loadingMore = false;
            LoadingRing.IsActive = false;
        }
    }

    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            await LoadAsync(reset: true);
        }
    }

    private async void OnFilterClick(object sender, RoutedEventArgs e)
    {
        var dialog = new FilterDialog(new FilterOptions
        {
            Tags = new System.Collections.Generic.List<string> { _tag },
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

    private async void OnLoadMoreRequested(object? sender, EventArgs e) => await LoadAsync(reset: false);

    private void OnItemInvoked(object? sender, Illustration item)
        => Frame.Navigate(typeof(IllustrationDetailPage), item);

    private async void OnCardAction(object? sender, CardActionEventArgs e)
    {
        if (sender is IllustrationGrid grid)
        {
            await IllustrationActions.HandleAsync(grid, e);
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
}
