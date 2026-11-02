using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Controls;
using Pixiv_Viewer_WinUI.Models;
using Pixiv_Viewer_WinUI.Services;

namespace Pixiv_Viewer_WinUI.Views;

public sealed partial class SearchPage : Page
{
    private const int PageSize = 40;

    private readonly ObservableCollection<string> _tags = new();
    private readonly ObservableCollection<Illustration> _items = new();

    private bool _loaded;
    private int _nextOffset;
    private bool _hasMore;
    private bool _loadingMore;

    public SearchPage()
    {
        InitializeComponent();
        SuggestionList.ItemsSource = SampleData.SearchSuggestions;
        TagChips.ItemsSource = _tags;
        Illustrations.ItemsSource = _items;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (_loaded)
        {
            Illustrations.Refresh();
            return;
        }

        _loaded = true;
        SearchBox.Focus(FocusState.Programmatic);
    }

    private async void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // 选中候选时优先用候选里的原始标签（中文输入 → 日文标签）
        var text = (args.ChosenSuggestion as TagSuggestion)?.Tag ?? (args.QueryText ?? string.Empty);
        text = text.Trim();
        sender.Text = string.Empty;
        sender.ItemsSource = null;

        if (text.Length == 0)
        {
            return;
        }

        // 收藏集链接 → 收藏集页
        var collectionMatch = CollectionUrlRegex.Match(text);
        if (collectionMatch.Success)
        {
            Frame.Navigate(typeof(CollectionPage), collectionMatch.Groups[1].Value);
            return;
        }

        // 作者主页链接 → 作者页
        var userMatch = UserUrlRegex.Match(text);
        if (userMatch.Success)
        {
            try
            {
                var user = await AppServices.Pixiv.GetUserAsync(userMatch.Groups[1].Value);
                if (user is not null)
                {
                    Frame.Navigate(typeof(UserPage), user);
                    return;
                }
            }
            catch (Exception)
            {
                // 下面统一提示
            }

            Subtitle.Text = "作者不存在，或当前账号无权访问";
            return;
        }

        // 直接粘贴作品链接 / 纯 pid：跳转作品详情
        if (TryGetArtworkId(text, out var artworkId))
        {
            try
            {
                var item = await AppServices.Pixiv.GetIllustrationAsync(artworkId);
                if (item is not null)
                {
                    Frame.Navigate(typeof(IllustrationDetailPage), item);
                    return;
                }
            }
            catch (Exception)
            {
                // 下面统一提示
            }

            if (ArtworkUrlRegex.IsMatch(text))
            {
                Subtitle.Text = "作品不存在、已删除，或当前账号无权访问";
                return;
            }
        }

        await AddTagAsync(text);
    }

    private static readonly Regex ArtworkUrlRegex = new(@"artworks/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CollectionUrlRegex = new(@"collections/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UserUrlRegex = new(@"users/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PidRegex = new(@"^\d{4,}$", RegexOptions.Compiled);

    private static bool TryGetArtworkId(string text, out string id)
    {
        var match = ArtworkUrlRegex.Match(text);
        if (match.Success)
        {
            id = match.Groups[1].Value;
            return true;
        }

        if (PidRegex.IsMatch(text))
        {
            id = text;
            return true;
        }

        id = string.Empty;
        return false;
    }

    private async Task AddTagAsync(string tag)
    {
        if (!_tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
        {
            _tags.Add(tag);
        }

        TagPanel.Visibility = _tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        await SearchAsync(reset: true);
    }

    private CancellationTokenSource? _suggestCts;

    private async void OnQueryTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        var text = sender.Text.Trim();

        _suggestCts?.Cancel();
        _suggestCts?.Dispose();
        _suggestCts = null;

        if (text.Length == 0)
        {
            sender.ItemsSource = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _suggestCts = cts;

        try
        {
            // 防抖：停下来再查，避免每个按键都打接口
            await Task.Delay(300, cts.Token);

            var items = await AppServices.Pixiv.SuggestTagsAsync(text, cts.Token);

            if (!cts.IsCancellationRequested)
            {
                sender.ItemsSource = items;
            }
        }
        catch (OperationCanceledException)
        {
            // 输入已经变了，忽略
        }
        catch (Exception)
        {
            // 候选失败不影响直接回车搜索
        }
    }

    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_tags.Count > 0 && IsLoaded)
        {
            await SearchAsync(reset: true);
        }
    }

    private async void OnSuggestionClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string tag)
        {
            await AddTagAsync(tag);
        }
    }

    private async void OnRemoveTagClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag)
        {
            _tags.Remove(tag);
        }

        if (_tags.Count == 0)
        {
            ResetToEmpty();
            return;
        }

        await SearchAsync(reset: true);
    }

    private void OnClearTagsClick(object sender, RoutedEventArgs e) => ResetToEmpty();

    private async void OnFilterClick(object sender, RoutedEventArgs e)
    {
        var dialog = new FilterDialog(new FilterOptions
        {
            Tags = _tags.ToList(),
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

        _tags.Clear();
        foreach (var tag in dialog.Result.Tags)
        {
            _tags.Add(tag);
        }

        AppServices.Settings.ShowAi = dialog.Result.ShowAi;
        AppServices.Settings.ShowR18 = dialog.Result.ShowR18;
        AppServices.Settings.ShowR18G = dialog.Result.ShowR18G;
        AppServices.Settings.BlockedTags = dialog.Result.BlockedTags;

        TagPanel.Visibility = _tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_tags.Count == 0)
        {
            ResetToEmpty();
            return;
        }

        await SearchAsync(reset: true);
    }

    private async void OnLoadMoreRequested(object? sender, EventArgs e) => await SearchAsync(reset: false);

    private void ResetToEmpty()
    {
        _tags.Clear();
        _items.Clear();
        _hasMore = false;
        _nextOffset = 0;
        TagPanel.Visibility = Visibility.Collapsed;
        Subtitle.Text = "输入标签后回车加入筛选（可多选，同时包含）";
        EmptyTitle.Text = "开始搜索";
        EmptyMessage.Text = "输入标签后回车，可以叠加多个标签做 AND 搜索；黑名单标签在「筛选」里配置。";
        EmptyState.Visibility = Visibility.Visible;
        SuggestionPanel.Visibility = Visibility.Visible;
    }

    private SearchFilter BuildFilter() => new()
    {
        Tags = _tags.ToList(),
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

    private async Task SearchAsync(bool reset)
    {
        if (_tags.Count == 0)
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
            var result = await AppServices.Pixiv.SearchAsync(BuildFilter(), _nextOffset, PageSize);

            foreach (var item in result.Items)
            {
                _items.Add(item);
            }

            _nextOffset = result.NextOffset;
            _hasMore = result.HasMore;

            if (_items.Count == 0 && AppServices.Settings.UseSampleData)
            {
                foreach (var item in SampleData.SearchResults)
                {
                    _items.Add(item);
                }

                _hasMore = false;
            }

            Subtitle.Text = $"「{string.Join(" + ", _tags)}」· 已加载 {_items.Count} 件{(_hasMore ? " · 继续滚动加载更多" : string.Empty)}";

            EmptyTitle.Text = "没有找到作品";
            EmptyMessage.Text = "换个标签组合、类别或排序再试一次（类别是客户端过滤）。";
            EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SuggestionPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            EmptyTitle.Text = "搜索失败";
            EmptyMessage.Text = ex.Message;
            EmptyState.Visibility = Visibility.Visible;
        }
        finally
        {
            _loadingMore = false;
            LoadingRing.IsActive = false;
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
