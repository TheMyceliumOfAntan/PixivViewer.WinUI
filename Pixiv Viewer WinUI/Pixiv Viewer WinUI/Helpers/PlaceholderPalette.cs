using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Pixiv_Viewer_WinUI.Helpers;

/// <summary>
/// 图片加载前的骨架占位：纯色灰底（跟随主题），不再使用彩色渐变。
/// 保持「同一个 Id 得到同一个画刷」的语义只是为了让调用方不需要改，颜色已经统一。
/// </summary>
public static class PlaceholderPalette
{
    private static readonly Color LightSkeleton = Color.FromArgb(0xFF, 0xE8, 0xE8, 0xE8);
    private static readonly Color DarkSkeleton = Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B);

    /// <summary>骨架灰。优先取主题资源，取不到时按当前主题给一个兜底灰。</summary>
    public static Brush GetBrush(string? seed = null)
    {
        var app = Application.Current;
        if (app is not null &&
            app.Resources.TryGetValue("SkeletonFillBrush", out var resource) &&
            resource is Brush themed)
        {
            return themed;
        }

        var dark = app?.RequestedTheme == ApplicationTheme.Dark;
        return new SolidColorBrush(dark ? DarkSkeleton : LightSkeleton);
    }
}
