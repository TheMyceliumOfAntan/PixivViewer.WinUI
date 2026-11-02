# Pixiv Viewer WinUI

一个用 **WinUI 3**（Windows App SDK）写的第三方 Pixiv 桌面客户端，界面全中文，Fluent / Mica 风格，支持真实 Pixiv API。

> 非官方客户端，仅供学习与技术交流，请遵守 Pixiv 用户协议。

---

## 功能

**浏览**

- 首页推荐（可按作品类别筛选）
- 排行榜（日 / 周 / 月 / 新人 / R-18 / R-18G）
- 搜索：多标签同时包含（AND）、黑名单、AI / R-18 过滤、按人气或最新排序
- 瀑布流列表（虚拟化，只渲染视口附近卡片）

**作品详情**

- 多 P 浏览，先加载 large 再渐进替换为原图
- 滚轮以鼠标为锚点缩放、拖拽平移、F11 / Esc / 方向键快捷键
- 全屏查看（淡入 + 上滑动画）
- 作品说明里的 Pixiv 链接可直接点击跳转
- 右键另存为 / 查看详情 / 全屏

**账号与收藏**

- OAuth PKCE 登录，也支持网页登录自动抓取 refresh_token 与 Cookie
- 凭证用 **Windows DPAPI** 加密保存（只有同一 Windows 账户能解密）
- 收藏 / 私密收藏 / 关注，右键菜单按状态显示「收藏 ↔ 取消收藏」
- 下载：真实下载原图、任务持久化、可删除文件

**评论**

- 读取 Pixiv street 新接口（`/ajax/street/comments/roots`），支持分页
- 印章（スタンプ）与表情占位符（如 `(heart)`）渲染成官方贴纸图片
- 单条评论可翻译

**标签**

- 显示中日双语（原文 + 浅色译文）
- 输入框支持中文 / 英文补全日文原文标签，候选里同时显示中英译名

**翻译**

- 作品说明、单条评论支持「翻译 / 显示原文」
- 主引擎为 B 站 [Index-Translate](https://github.com/bilibili/Index-Translate) 免费公开 API，失败自动回退 MyMemory

---

## 系统要求

| 项 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809（17763）及以上 / Windows 11 |
| 架构 | x64 |
| 运行时 | **无需安装**（安装包自带 .NET 8 与 Windows App SDK） |

---

## 安装

下载 `PixivViewer-Setup-<版本>-x64.exe` 双击安装即可，装完直接运行。

- 安装到 `%ProgramFiles%\Pixiv Viewer`（需要管理员权限）
- 已安装旧版本时直接再装新版就是**覆盖升级**，不需要先卸载
- 登录状态、设置、下载记录保存在 `%LOCALAPPDATA%\PixivViewer`，升级和卸载都不会丢（卸载时会单独询问是否删除）

也提供免安装版：解压后双击 `Pixiv Viewer WinUI.exe`，不需要 MSIX、不需要签名。

---

## 从源码构建

```powershell
# 调试构建
dotnet build "Pixiv Viewer WinUI\Pixiv Viewer WinUI\Pixiv Viewer WinUI.csproj" -p:Platform=x64 -c Debug

# 发布免安装版（自包含，输出到 D:\CSharp\Release\PixivViewer-win-x64）
dotnet publish "Pixiv Viewer WinUI\Pixiv Viewer WinUI\Pixiv Viewer WinUI.csproj" -p:PublishProfile=Unpackaged

# 打安装包（需要先安装 Inno Setup 6：winget install JRSoftware.InnoSetup）
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 1.0.3
```

---

## 关于反代（必读）

Pixiv 的 App API / 图片 / OAuth 在国内无法直连，本项目通过自建的反向代理访问：

- 默认地址：`https://pixiv.junsi233.top`
- 修改位置：`Services/Pixiv/PixivApiService.cs` 里的 `DefaultBaseUrl`
- 反代基于 [vmoranv/pixiv-proxy](https://github.com/vmoranv/pixiv-proxy) 修改（新增 `/web/*` 路由、`X-Pixiv-Cookie` 转发、App 身份切换等）

自建反代需要：

| 路径 | 目标 |
| --- | --- |
| `/api/v1/*` | `app-api.pixiv.net` |
| `/api/auth/*` | `oauth.secure.pixiv.net` |
| `/api/image/*` | `i.pximg.net` |
| `/api/web/*` | `www.pixiv.net` |

---

## 项目结构

```
Pixiv Viewer WinUI/
├─ Pixiv Viewer WinUI/                 # 主工程（WinUI 3）
│  ├─ Controls/                        # 瀑布流、筛选弹窗、缓存图片、表情文本等
│  ├─ Models/                          # 作品、用户、评论、标签等模型
│  ├─ Services/
│  │  ├─ Pixiv/                        # API、Session（OAuth）、DTO、诊断日志
│  │  ├─ DownloadService.cs            # 下载与任务持久化
│  │  ├─ TranslationService.cs         # Index-Translate + MyMemory
│  │  └─ Changelog.cs                  # 更新日志数据
│  ├─ Views/                           # 首页/排行榜/搜索/详情/作者/标签/收藏集/设置…
│  ├─ Helpers/                         # 动画、格式化、DPAPI、表情映射
│  └─ Styles/                          # 主题与控件样式
├─ Pixiv Viewer WinUI (Package)/       # MSIX 打包工程（可选）
├─ installer/                          # Inno Setup 脚本与一键打包脚本
└─ docs/UI.md                          # UI 结构、交互与接口说明
```

---

## 常见问题

**Q：提示「未登录或登录已过期」？**
设置 → 账号 → 网页登录，重新登录一次即可。refresh_token 每小时自动续期。

**Q：评论显示「接口不可用」？**
Pixiv 会阶段性限制评论接口，程序会自动重试旧端点；网络恢复后重新进入作品页即可。

**Q：翻译失败？**
翻译依赖外部免费接口（Index-Translate / MyMemory），可能被限流，稍后重试。

**Q：安装时提示已存在更高版本？**
覆盖安装即可；如需回退，先卸载再装旧版（卸载时选择保留本地数据）。

---

## 致谢

- [vmoranv/pixiv-proxy](https://github.com/vmoranv/pixiv-proxy) —— 反代基础
- [bilibili/Index-Translate](https://github.com/bilibili/Index-Translate) —— 免费翻译 API
- [upbit/pixivpy](https://github.com/upbit/pixivpy) —— App API 端点参考

## 许可证

本项目采用 **GNU General Public License v3.0** 许可，详见 [LICENSE](LICENSE)。
