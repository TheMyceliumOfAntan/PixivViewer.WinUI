using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pixiv_Viewer_WinUI.Services;
using Pixiv_Viewer_WinUI.Views;
using Windows.Graphics;
using Windows.UI;

namespace Pixiv_Viewer_WinUI;

/// <summary>
/// 应用外壳：自定义标题栏 + NavigationView + 内容 Frame。
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Title = "Pixiv Viewer";
        AppWindow.Title = Title;

        // 任务栏 / 标题栏图标（exe 图标由 csproj 的 ApplicationIcon 提供）
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch (Exception)
        {
            // 图标设置失败不影响启动
        }

        Activated += OnFirstActivated;

        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
            ApplyTitleBarTheme();
        }

        ContentFrame.Navigated += OnFrameNavigated;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        // 等第一次布局完成再改窗口尺寸/选导航项，避免与 XAML 首次测量竞争
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var scale = GetDpiForWindow(handle) / 96.0;
                if (scale <= 0)
                {
                    scale = 1.0;
                }

                AppWindow.Resize(new SizeInt32((int)(1280 * scale), (int)(860 * scale)));
            }
            catch (Exception)
            {
                // 忽略不支持调整尺寸的环境。
            }

            try
            {
                Nav.SelectedItem = Nav.MenuItems[0];
            }
            catch (Exception)
            {
                // 忽略导航失败
            }
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "home" => typeof(HomePage),
            "ranking" => typeof(RankingPage),
            "search" => typeof(SearchPage),
            "my" => typeof(MyPage),
            "downloads" => typeof(DownloadsPage),
            _ => null,
        };

        if (pageType is not null && ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked && ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
        {
            ContentFrame.Navigate(typeof(SettingsPage));
        }
    }

    private void OnNavBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        Nav.IsBackEnabled = ContentFrame.CanGoBack;
        UpdateLoginBar();
    }

    /// <summary>未登录时显示顶部提示条（Pixiv API 需要登录）。</summary>
    public void UpdateLoginBar()
    {
        try
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(UpdateLoginBar);
                return;
            }

            LoginBar.Visibility = AppServices.Pixiv.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception)
        {
            // 窗口正在销毁时忽略
        }
    }

    private void OnGoLoginClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
        {
            ContentFrame.Navigate(typeof(SettingsPage));
        }
    }

    /// <summary>全屏看图时隐藏导航栏和标题栏，退出时恢复。</summary>
    public void SetChromeVisible(bool visible)
    {
        try
        {
            // 不能直接 Nav.Visibility=Collapsed（会把 Frame 内容一起隐藏，导致白屏）。
            // 用 LeftMinimal + 隐藏汉堡按钮，让内容区真正铺满。
            if (visible)
            {
                Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Auto;
                Nav.IsPaneToggleButtonVisible = true;
                Nav.IsPaneOpen = true;
            }
            else
            {
                Nav.IsPaneOpen = false;
                Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftMinimal;
                Nav.IsPaneToggleButtonVisible = false;
            }

            AppTitleBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private void ApplyTitleBarTheme()
    {
        var titleBar = AppWindow.TitleBar;
        var isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 26, 26, 26);
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 128, 128, 128);
        titleBar.ButtonHoverBackgroundColor = isDark
            ? Color.FromArgb(36, 255, 255, 255)
            : Color.FromArgb(24, 0, 0, 0);
        titleBar.ButtonHoverForegroundColor = isDark
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 0, 0, 0);
        titleBar.ButtonPressedBackgroundColor = isDark
            ? Color.FromArgb(56, 255, 255, 255)
            : Color.FromArgb(40, 0, 0, 0);
    }
}
