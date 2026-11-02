using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Models;
using Pixiv_Viewer_WinUI.Services;

namespace Pixiv_Viewer_WinUI.Views;

public sealed partial class HomePage : Page
{
    private readonly ObservableCollection<string> _filterTags = new();
    private SearchTarget _target = SearchTarget.All;
    private bool _loaded;

    public HomePage()
    {
        InitializeComponent();
        TagChips.ItemsSource = _filterTags;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            SampleBar.IsOpen = AppServices.Settings.UseSampleData;

            // 从详情页返回时复用已有列表（数据 + 滚动位置都不丢）
            if (_loaded)
            {
                Illustrations.Refresh();
                return;
            }

            _loaded = true;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            Subtitle.Text = $"初始化失败：{ex.Message}";
        }
    }

    private async Task LoadAsync()
    {
        LoadingRing.IsActive = true;
        EmptyState.Visibility = Visibility.Collapsed;

        try
        {
            IReadOnlyList<Illustration> items;

            if (_filterTags.Count > 0)
            {
                var result = await AppServices.Pixiv.SearchAsync(new SearchFilter
                {
                    Tags = _filterTags.ToList(),
                    Sort = SearchSort.Popular,
                    Target = _target,
                }, 0, 40);

                items = result.Items;
                Subtitle.Text = $"标签筛选：{string.Join(" + ", _filterTags)} · {items.Count} 件";
            }
            else
            {
                var result = await AppServices.Pixiv.GetRecommendedAsync(0, 40);
                items = result.Items;

                if (items.Count == 0 && AppServices.Settings.UseSampleData)
                {
                    items = SampleData.Recommended;
                }

                // 推荐接口没有类别参数，按 type 客户端过滤
                if (_target != SearchTarget.All)
                {
                    items = items.Where(i => _target.Matches(i.Type)).ToList();
                }

                Subtitle.Text = items.Count == 0
                    ? "暂无内容"
                    : $"为你挑选的 {items.Count} 件作品 · 点击卡片查看详情";
            }

            Illustrations.ItemsSource = items;
            Illustrations.ScrollToTop();
            EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            EmptyMessage.Text = $"加载失败：{ex.Message}";
            EmptyState.Visibility = Visibility.Visible;
            Subtitle.Text = "加载失败";
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        // 刷新要拿新数据：清掉接口缓存 + 强制重排
        AppServices.PixivApi.ClearCache();
        await LoadAsync();
        Illustrations.Refresh();
    }

    private async void OnFilterClick(object sender, RoutedEventArgs e)
    {
        var dialog = new FilterDialog(new FilterOptions
        {
            Tags = _filterTags.ToList(),
            ShowAi = AppServices.Settings.ShowAi,
            ShowR18 = AppServices.Settings.ShowR18,
            ShowR18G = AppServices.Settings.ShowR18G,
            BlockedTags = AppServices.Settings.BlockedTags,
            Target = _target,
        }, showCategory: true)
        {
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || dialog.Result is null)
        {
            return;
        }

        _filterTags.Clear();
        foreach (var tag in dialog.Result.Tags)
        {
            _filterTags.Add(tag);
        }

        AppServices.Settings.ShowAi = dialog.Result.ShowAi;
        AppServices.Settings.ShowR18 = dialog.Result.ShowR18;
        AppServices.Settings.ShowR18G = dialog.Result.ShowR18G;
        AppServices.Settings.BlockedTags = dialog.Result.BlockedTags;
        _target = dialog.Result.Target;

        UpdateFilterBar();
        await LoadAsync();
    }

    private void UpdateFilterBar()
        => FilterBar.Visibility = _filterTags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void OnRemoveTagClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag)
        {
            _filterTags.Remove(tag);
        }

        UpdateFilterBar();
        await LoadAsync();
    }

    private async void OnClearFilterClick(object sender, RoutedEventArgs e)
    {
        _filterTags.Clear();
        UpdateFilterBar();
        await LoadAsync();
    }

    private void OnItemInvoked(object? sender, Illustration item)
        => Frame.Navigate(typeof(IllustrationDetailPage), item);

    private async void OnCardAction(object? sender, CardActionEventArgs e)
    {
        if (sender is IllustrationGrid grid)
        {
            await IllustrationActions.HandleAsync(grid, e);
        }
    }
}
