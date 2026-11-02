using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Models;
using Pixiv_Viewer_WinUI.Services;

namespace Pixiv_Viewer_WinUI.Views;

public sealed partial class RankingPage : Page
{
    private const int PageSize = 40;

    private readonly ObservableCollection<Illustration> _items = new();

    private RankingMode _mode = RankingMode.Daily;
    private int _nextOffset;
    private bool _hasMore = true;
    private bool _loadingMore;
    private bool _loaded;

    public RankingPage()
    {
        InitializeComponent();
        Illustrations.ItemsSource = _items;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            UpdateAdultTabs();

            if (_loaded)
            {
                Illustrations.Refresh();
                return;
            }

            _loaded = true;
            await LoadAsync(reset: true);
        }
        catch (Exception)
        {
            LoadingRing.IsActive = false;
            EmptyState.Visibility = Visibility.Visible;
        }
    }

    /// <summary>R-18 / R-18G 榜单只在对应开关打开时出现。</summary>
    private void UpdateAdultTabs()
    {
        R18Item.Visibility = AppServices.Settings.ShowR18 ? Visibility.Visible : Visibility.Collapsed;
        R18GItem.Visibility = AppServices.Settings.ShowR18G ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadAsync(bool reset)
    {
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
            var result = await AppServices.Pixiv.GetRankingAsync(_mode, _nextOffset, PageSize);

            foreach (var item in result.Items)
            {
                _items.Add(item);
            }

            _nextOffset = result.NextOffset;
            _hasMore = result.HasMore;

            if (reset && _items.Count == 0 && AppServices.Settings.UseSampleData)
            {
                foreach (var item in SampleData.Ranking)
                {
                    _items.Add(item);
                }

                _hasMore = false;
            }

            Subtitle.Text = $"{_mode.ToLabel()}榜单 · 已加载 {_items.Count} 件{(_hasMore ? " · 继续滚动加载更多" : string.Empty)}";
            EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"加载失败：{ex.Message}";
            EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _loadingMore = false;
            LoadingRing.IsActive = false;
        }
    }

    private async void OnLoadMoreRequested(object? sender, EventArgs e) => await LoadAsync(reset: false);

    private async void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<RankingMode>(tag, out var mode))
        {
            _mode = mode;
            await LoadAsync(reset: true);
        }
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
