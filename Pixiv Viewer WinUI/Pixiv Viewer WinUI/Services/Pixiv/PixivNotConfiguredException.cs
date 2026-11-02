using System;

namespace Pixiv_Viewer_WinUI.Services.Pixiv;

/// <summary>尚未配置 Pixiv API 服务地址（本软件不内置公共接口）。</summary>
public sealed class PixivNotConfiguredException : Exception
{
    public PixivNotConfiguredException()
        : base("还没有配置 Pixiv API 地址，请到「设置 → 服务地址」填写。")
    {
    }
}
