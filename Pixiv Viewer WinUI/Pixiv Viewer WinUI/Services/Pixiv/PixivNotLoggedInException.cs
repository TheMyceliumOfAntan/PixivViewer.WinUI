using System;
using System.Net;

namespace Pixiv_Viewer_WinUI.Services.Pixiv;

/// <summary>未登录 / 登录过期：UI 可以据此提示去登录，而不是显示裸的 400。</summary>
public sealed class PixivNotLoggedInException : Exception
{
    public PixivNotLoggedInException(string message, HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}
