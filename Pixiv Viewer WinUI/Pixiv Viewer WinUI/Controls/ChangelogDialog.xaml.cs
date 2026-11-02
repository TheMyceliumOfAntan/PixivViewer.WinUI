using Microsoft.UI.Xaml.Controls;
using Pixiv_Viewer_WinUI.Services;

namespace Pixiv_Viewer_WinUI.Controls;

/// <summary>「关于 → 更新日志」弹窗。</summary>
public sealed partial class ChangelogDialog : ContentDialog
{
    public ChangelogDialog()
    {
        InitializeComponent();
        EntriesHost.ItemsSource = Changelog.Entries;
    }
}
