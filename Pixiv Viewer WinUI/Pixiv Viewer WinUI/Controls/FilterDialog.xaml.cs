using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pixiv_Viewer_WinUI.Models;
using Pixiv_Viewer_WinUI.Services;
using Windows.System;

namespace Pixiv_Viewer_WinUI.Controls;

/// <summary>筛选弹窗的结果。</summary>
public sealed class FilterOptions
{
    public List<string> Tags { get; set; } = new();

    public bool ShowAi { get; set; } = true;

    public bool ShowR18 { get; set; }

    public bool ShowR18G { get; set; }

    public string BlockedTags { get; set; } = string.Empty;

    /// <summary>作品类别（仅推荐页使用）。</summary>
    public SearchTarget Target { get; set; } = SearchTarget.All;
}

/// <summary>
/// 通用筛选弹窗：标签（AND）、AI 作品、R-18 / R-18G、黑名单。
/// R-18 / R-18G 的「总开关」在设置页，总开关关闭时这里对应开关禁用。
/// </summary>
public sealed partial class FilterDialog : ContentDialog
{
    private readonly ObservableCollection<string> _tags;

    public FilterDialog(FilterOptions options, bool showCategory = false)
    {
        InitializeComponent();

        CategoryPanel.Visibility = showCategory ? Visibility.Visible : Visibility.Collapsed;
        if (showCategory)
        {
            foreach (var item in CategoryBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag as string == options.Target.ToString())
                {
                    CategoryBox.SelectedItem = item;
                    break;
                }
            }
        }

        _tags = new ObservableCollection<string>(options.Tags);
        TagChips.ItemsSource = _tags;
        BlockedBox.Text = options.BlockedTags;

        var adultAllowed = AppServices.Settings.ShowR18;
        var adultGAllowed = AppServices.Settings.ShowR18G;

        AiSwitch.IsOn = options.ShowAi;
        R18Switch.IsOn = adultAllowed && options.ShowR18;
        R18Switch.IsEnabled = adultAllowed;
        R18GSwitch.IsOn = adultGAllowed && options.ShowR18G;
        R18GSwitch.IsEnabled = adultGAllowed;

        if (!adultAllowed && !adultGAllowed)
        {
            AdultHint.Text = "R-18 / R-18G 总开关在「设置」里且当前为关闭，这里无法开启。";
        }
    }

    /// <summary>点击「应用」后的结果；取消时为 null。</summary>
    public FilterOptions? Result { get; private set; }

    private void OnApplyClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Result = new FilterOptions
        {
            Tags = _tags.ToList(),
            ShowAi = AiSwitch.IsOn,
            ShowR18 = R18Switch.IsOn,
            ShowR18G = R18GSwitch.IsOn,
            BlockedTags = BlockedBox.Text.Trim(),
            Target = ((CategoryBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
            {
                "Manga" => SearchTarget.Manga,
                "Ugoira" => SearchTarget.Ugoira,
                "Illustrations" => SearchTarget.Illustrations,
                _ => SearchTarget.All,
            },
        };
    }

    private CancellationTokenSource? _suggestCts;

    /// <summary>回车 / 点候选：把标签加入筛选。</summary>
    private void OnTagQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var text = (args.ChosenSuggestion as TagSuggestion)?.Tag ?? args.QueryText ?? string.Empty;
        TagInput.Text = text.Trim();
        AddCurrentTag();
    }

    /// <summary>输入时给出中英日候选（和搜索页同一套接口）。</summary>
    private async void OnTagTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
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
            await Task.Delay(300, cts.Token);
            var items = await AppServices.Pixiv.SuggestTagsAsync(text, cts.Token);

            if (!cts.IsCancellationRequested)
            {
                sender.ItemsSource = items;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // 候选失败不影响手动输入
        }
    }

    private void OnAddTagClick(object sender, RoutedEventArgs e) => AddCurrentTag();

    private CancellationTokenSource? _blockedSuggestCts;

    /// <summary>黑名单：选中候选 / 回车后追加到黑名单输入框。</summary>
    private void OnBlockedTagQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var tag = ((args.ChosenSuggestion as TagSuggestion)?.Tag ?? args.QueryText ?? string.Empty).Trim();

        sender.Text = string.Empty;
        sender.ItemsSource = null;

        if (tag.Length == 0)
        {
            return;
        }

        var separators = new[] { ',', '，', ' ', '\t', ';', '；' };
        var parts = BlockedBox.Text
            .Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (parts.Any(p => string.Equals(p, tag, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        parts.Add(tag);
        BlockedBox.Text = string.Join(", ", parts);
    }

    /// <summary>黑名单输入框的标签候选（和上面、搜索页同一套接口）。</summary>
    private async void OnBlockedTagTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        var text = sender.Text.Trim();

        _blockedSuggestCts?.Cancel();
        _blockedSuggestCts?.Dispose();
        _blockedSuggestCts = null;

        if (text.Length == 0)
        {
            sender.ItemsSource = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _blockedSuggestCts = cts;

        try
        {
            await Task.Delay(300, cts.Token);
            var items = await AppServices.Pixiv.SuggestTagsAsync(text, cts.Token);

            if (!cts.IsCancellationRequested)
            {
                sender.ItemsSource = items;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private void AddCurrentTag()
    {
        var tag = TagInput.Text.Trim();
        if (tag.Length == 0)
        {
            return;
        }

        if (!_tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
        {
            _tags.Add(tag);
        }

        TagInput.Text = string.Empty;
    }

    private void OnRemoveTagClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag)
        {
            _tags.Remove(tag);
        }
    }
}
