using System;

namespace Pixiv_Viewer_WinUI.Services.Pixiv;

/// <summary>尚未配置接口方式（既没填反代地址，也没开启域前置兜底）。</summary>
public sealed class PixivNotConfiguredException : Exception
{
    public PixivNotConfiguredException()
        : base("还没有可用的接口方式，请到「设置 → 服务地址」选择域前置直连，或填写自建 API 地址。")
    {
    }
}
