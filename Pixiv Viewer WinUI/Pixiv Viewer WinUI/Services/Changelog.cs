using System.Collections.Generic;
using Pixiv_Viewer_WinUI.Models;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>
/// 版本更新日志（「关于 → 更新日志」弹窗的数据源）。
/// 发新版本时在这里加一条，并同步 csproj 的 &lt;Version&gt; 与 installer 的默认版本号。
/// </summary>
public static class Changelog
{
    public static IReadOnlyList<ChangelogEntry> Entries { get; } = new List<ChangelogEntry>
    {
        new()
        {
            Version = "1.1.0",
            Date = "2026-11-02",
            IsLatest = true,
            Items = new[]
            {
                "新增域前置直连：不依赖任何服务器，把 Pixiv 域名直接连到固定 IP（TLS 仍按原域名握手）",
                "反代失败自动兜底：连不上 / 超时 / 5xx 时自动切到域前置重试，本次运行内保持",
                "设置 → 服务地址：可选「自建 API 反代」或「域前置直连」，IP 映射可编辑并持久化，支持一键恢复默认",
                "域前置下图片直连 i.pximg.net（自动补 Referer），缩略图与原图下载同样走直连",
                "域前置下 www / accounts 改走 Cloudflare IP，评论、收藏集、标签候选恢复正常",
                "网页接口（www / accounts）改用浏览器 UA：Cloudflare 会拒绝 App API 的 UA（403）",
                "网页登录支持域前置：本地 CONNECT 隧道只分片 ClientHello，不终止 TLS（检测到系统代理时自动改走系统代理）",
                "关于页新增 Pixeval 致谢；默认 IP 与实现思路参考自开源项目 Pixeval（GPL-3.0）",
            },
        },
        new()
        {
            Version = "1.0.3",
            Date = "2026-11-02",
            Items = new[]
            {
                "筛选弹窗：标签输入框支持中英日候选，和搜索页同一套补全",
                "修复：添加标签筛选后列表图片不刷新（ItemsRepeater 复用了旧卡片）",
                "排行榜：新增男性向 / 女性向 / AI 日榜",
                "详情页：修复未收藏时收藏按钮不显示图标",
                "关于页：改为图标 + 名称 + 版本 + 更新日志 + 仓库 / API 项目链接",
                "安装包：改为固定「为所有用户安装」，升级时自动识别旧安装目录，直接覆盖安装",
                "安装包：升级不再需要卸载重装，登录状态 / 设置 / 下载记录保存在用户目录，不受安装影响",
                "服务地址：不再内置公共接口，首次启动引导填写自建的 Pixiv API 地址（设置 → 服务地址），地址本地保存",
            },
        },
        new()
        {
            Version = "1.0.2",
            Date = "2026-11-02",
            Items = new[]
            {
                "标签：中日双语显示（原文为主 + 浅色译文），标签详情页同步双语标题",
                "搜索：输入中文/英文自动补全日文原文标签，候选里同时显示中文与英文译名",
                "评论：表情占位符（如 (heart)(excited)）渲染成 pixiv 官方贴纸图片，与网页显示一致",
            },
        },
        new()
        {
            Version = "1.0.1",
            Date = "2026-11-01",
            Items = new[]
            {
                "评论：接入 Pixiv street 新接口（/ajax/street/comments/roots），作品评论恢复显示，支持分页（每页 20 条，最多 60 条）",
                "评论：印章（スタンプ）评论直接渲染为贴纸图片，不再是空白",
                "评论：修复「评论数永远是 0」的问题，评论输入框恢复正常",
                "翻译：作品说明与单条评论都支持「翻译 / 显示原文」",
                "翻译：主引擎换成 B 站 Index-Translate 免费公开 API，失败时自动回退 MyMemory",
                "翻译：译文与原文共用同一个富文本控件，翻译后作品说明里的链接依然可以点击",
                "翻译：修复点「显示原文」时误弹「翻译失败」提示的问题",
                "占位图：加载中的彩色渐变改成跟随主题的纯灰骨架，更符合 WinUI 观感",
                "收藏：右键菜单按状态显示「收藏 / 取消收藏」「私密收藏 / 取消私密收藏」",
                "收藏：已收藏时图标变为实心金色星标（右键菜单、详情页按钮、统计卡片同步）",
                "右键菜单：修复设置图标前景色导致图标不显示的问题",
                "打包：新增 Inno Setup 安装包（免 MSIX、免签名、免装运行时）",
            },
        },
        new()
        {
            Version = "1.0.0",
            Date = "2026-10-28",
            ShowDivider = false,
            Items = new[]
            {
                "首个版本：首页推荐、排行榜、搜索、账号、下载、设置",
                "作品详情：多 P 浏览、原图渐进加载、滚轮缩放、全屏查看",
                "作者页 / 标签页 / 收藏集页",
                "收藏 / 私密收藏 / 关注 / 下载（任务持久化）",
                "登录凭证与网页 Cookie 使用 Windows DPAPI 加密保存",
                "瀑布流列表虚拟化（只实例化视口附近的卡片）",
            },
        },
    };
}
