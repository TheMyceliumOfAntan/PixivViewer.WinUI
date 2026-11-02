# Pixiv Viewer WinUI — UI 结构说明

> 本文档说明界面层（UI）的结构、交互约定，以及接入真实 Pixiv API 时应该改哪里。
> 当前状态：UI 完整可运行，数据层为空实现（返回空结果），开启「示例数据」后可用假数据预览排版。

## 1. 目录结构

```
Pixiv Viewer WinUI/
├─ App.xaml / App.xaml.cs          应用入口；合并样式、注册值转换器、持有 MainWindow
├─ MainWindow.xaml(.cs)            应用外壳：Mica + 自定义标题栏 + NavigationView + 内容 Frame
├─ Styles/AppStyles.xaml           全部视觉规范（标题、卡片、胶囊标签、工具栏按钮…）
├─ Converters/CommonConverters.cs  Bool/字符串→Visibility、Id→渐变画刷、URL→ImageSource
├─ Helpers/
│  ├─ ObservableObject.cs          手写 INotifyPropertyChanged（未引入第三方 MVVM 包）
│  ├─ Format.cs                    数字（1.2万）、字节、相对时间格式化
│  └─ PlaceholderPalette.cs        按 Id 生成稳定的渐变占位画刷
├─ Models/                         Illustration / PixivUser / Comment / PagedResult / DownloadTask…
├─ Services/
│  ├─ IPixivService.cs             数据访问契约（页面只依赖它）
│  ├─ PixivService.cs              空实现，每个方法都标了对应 Pixiv API 端点
│  ├─ IDownloadService.cs / DownloadService.cs   下载任务管理契约与空实现
│  ├─ SampleData.cs                设计预览用假数据（可在设置里关闭）
│  └─ AppServices.cs               服务定位器 + AppSettings（示例数据开关、主题）
├─ Controls/
│  ├─ IllustrationGrid.xaml(.cs)   瀑布流网格（卡片模板、悬停抬升、点击事件）
│  ├─ MasonryLayout.cs             瀑布流布局（按最矮列填充，列数随宽度自适应）
│  ├─ AspectRatioPanel.cs          按宽高比占位的容器（决定卡片封面高度）
│  └─ WrapLayout.cs                自动换行布局（标签）
└─ Views/
   ├─ HomePage          推荐
   ├─ RankingPage       排行榜（SelectorBar：今日/本周/本月/新人/原创）
   ├─ SearchPage        搜索（AutoSuggestBox + 排序/类型筛选）
   ├─ MyPage            我的（公开收藏/私密收藏/关注/我的作品）
   ├─ DownloadsPage     下载任务管理
   ├─ SettingsPage      外观/数据/账号/关于
   ├─ UserPage          作者主页（资料 + 作品瀑布流）
   └─ IllustrationDetailPage(.xaml.cs) + IllustrationDetailViewModel.cs   作品详情（重点页面）
```

## 2. 页面与交互

| 页面 | 说明 |
| --- | --- |
| 推荐 / 排行榜 / 搜索 / 我的 | 顶部页头 + 内容区，内容区统一使用 `IllustrationGrid`（瀑布流）。加载中显示 `ProgressRing`，无数据显示空状态（图标 + 文案 + 重试）。 |
| 我的 | 账号卡片（头像/昵称/@账号/作品·关注·粉丝） + `SelectorBar` 切换四个列表；「关注」是用户列表（点击行进入作者主页），其余是作品瀑布流。 |
| 作者主页 | 资料卡（头像/昵称/简介/作品·关注·粉丝 + 关注按钮）+ 该作者的作品瀑布流。入口：作品详情的作者行、评论头像、关注列表。 |
| 下载 | 总进度卡片 + 筛选（全部/进行中/已完成/失败）+ 任务行（封面/标题/进度条/状态/大小/速度/页码/操作按钮）。 |
| 设置 | 主题（跟随系统/浅色/深色）、示例数据开关、清空缓存、账号状态、关于。 |
| 详情 | 见下。 |

### 详情页

- 布局：宽窗口（≥1000 DIP）左图右信息；窄窗口信息面板流到图片下方（高度上限 360，内部独立滚动）。由 `VisualStateManager` + `AdaptiveTrigger` 实现，见 `IllustrationDetailPage.xaml` 顶部。
- 图片区：`ScrollViewer ZoomMode=Enabled`（Ctrl+滚轮/双指缩放）、左右圆形翻页按钮、底部页码胶囊、缩略图条（点击跳页、自动高亮并滚动到可视区）。
- 键盘：`←` / `→` / `PgUp` / `PgDn` 翻页，`Esc` 返回；焦点在输入框时不拦截。
- 信息面板：标题、作者（关注按钮）、赞/收藏/浏览统计卡、标签（自动换行）、作品说明（4 行截断 + 展开）、投稿时间、评论列表、底部固定评论输入框。
- 工具栏：收藏（ToggleButton）、下载、在浏览器打开、更多（复制链接/打开/举报）。操作反馈用顶部 `InfoBar`，3 秒自动关闭。

### 瀑布流实现要点

- `MasonryLayout : NonVirtualizingLayout`，一页数据全部实例化（20~50 条足够）；按当前最矮列依次放置，列数 = `(可用宽度 + 间距) / (MinItemWidth + 间距)`。
- 卡片封面高度由 `AspectRatioPanel` 按作品宽高比计算，因此形成错落效果。
- `ScrollViewer` 测量内容时宽度可能是无限大，`IllustrationGrid` 会把视口宽度显式赋给 `ItemsRepeater.Width`（注意 `Width` 初始为 `NaN`，比较时必须用 `double.IsNaN` 判断）。
- 列宽通过 `MinItemWidth`（默认 200）调整；`MaxColumns` 默认 6。
- 卡片结构：**圆角底板（始终可见）包住「图片 + 文字」**；图片只有上圆角，下边缘直角，用来和文字区分。底板颜色用自定义的 `GalleryCardFillBrush` / `GalleryCardStrokeBrush`（`Styles/AppStyles.xaml` 里的 ThemeDictionaries）——WinUI 自带的 `CardBackgroundFillColorDefaultBrush` 在深色模式下只有 5% 白，卡片轮廓几乎看不见。
- 卡片悬停**只加 1px 描边 + 上浮**，不改变底色：`IllustrationCardButtonStyle` 是自绘模板（默认 Button 模板在 PointerOver 时给整块铺 `ButtonBackgroundPointerOver`，半透明卡片底色会被"点亮"，导致图片和文字区看起来割裂）。

## 3. 接入真实 API 的步骤

已接入，实现类为 `Services/Pixiv/PixivApiService.cs`（走自建反代 `https://pixiv.junsi233.top`）：

| 能力 | 端点 |
| --- | --- |
| 推荐 | `GET /v1/illust/recommended` |
| 排行榜 | `GET /v1/illust/ranking?mode=day\|week\|month\|day_rookie\|day_original` |
| 搜索 | `GET /v1/search/illust`（`sort=date_desc\|popular_desc`） |
| 公开/私密收藏 | `GET /v1/user/bookmarks/illust?restrict=public\|private` |
| 我的作品 | `GET /v1/user/illusts` |
| 关注列表 | `GET /v1/user/following` |
| 作品详情 | `GET /v1/illust/detail`（`meta_pages` 取每页大图） |
| 作者资料 | `GET /v1/user/detail`（作品数 / 关注数 / 公开收藏数） |
| 收藏 / 关注 / 评论 | `POST /v2/illust/bookmark/add`、`/v1/illust/bookmark/delete`、`/v1/user/follow/add\|delete`、`/v1/illust/comment/add` |

- 图片地址把 `https://i.pximg.net` 换成 `https://pixiv.junsi233.top/image`（反代带 Referer，直连会 403）。
- 登录：`POST /api/auth/token`（refresh_token 换 access_token，1 小时有效，过期自动刷新）。
  token 来源优先级：环境变量 `PIXIV_REFRESH_TOKEN` → `%LOCALAPPDATA%\PixivViewer\refresh_token.txt` → 设置页手动粘贴。
- 列表默认过滤 R-18 / R-18G（`x_restrict` + 标签双重判断），设置页可关闭过滤。
- 评论端点 `/v1/illust/comments` 当前 Pixiv 侧返回 404，已降级为空列表（页面显示「还没有评论」）。
- 失败信息写入 `%LOCALAPPDATA%\PixivViewer\log.txt`，便于排查反代 / token 问题。

**下载仍是空实现**：`IDownloadService` 需要接队列 + 断点续传 + 进度回调；UI（暂停/继续/取消/重试/清空/打开目录）已就绪，只差服务实现。

## 4. 已知待办

- 详情页图片区尚未接入真实图片加载状态（骨架/失败重试）。
- 收藏、关注、评论目前只更新本地状态；接入 API 后应处理失败回滚。
- 设置项未持久化（可接 `ApplicationData.Current.LocalSettings` 或 JSON 配置）。
- 瀑布流未做虚拟化（数据量极大时建议改 `VirtualizingLayout` + 高度估算）。

## 5. 交互与缓存补充（本轮）

| 能力 | 说明 |
| --- | --- |
| 接口缓存 | `PixivApiService` 内存 TTL 缓存：列表 3 分钟、详情 10 分钟，同一个 URL 不重复打反代；写操作不做缓存失效（UI 已本地更新） |
| 图片缓存 | `ImageCache`（`%LOCALAPPDATA%\PixivViewer\images`）+ 内存 `BitmapImage`，XAML 用附加属性 `controls:CachedImage.Url` 绑定 |
| 返回不丢数据 | 推荐/排行榜/搜索/我的页 `NavigationCacheMode="Required"` + `_loaded` 守卫，从详情页返回时保留列表和滚动位置 |
| 右键菜单 | 瀑布流卡片右键：收藏 / 喜欢（私密收藏）/ 下载；动作经 `IllustrationGrid.CardActionRequested` → `IllustrationActions` |
| 喜欢 | Pixiv App API 没有独立点赞端点，这里用「私密收藏（restrict=private）」实现 |
| 详情页另存为 | 图片区右键「另存为…」→ `FileSavePicker`，只保存当前页原图（`OriginalUrls`） |
| 标签页 | 详情页标签可点击 → `TagPage`（`#tag` + 排序/类型 + 瀑布流） |
| 多标签搜索 | 搜索页 / 首页筛选都支持叠加多个标签（AND），`SearchFilter.Tags` |
| 黑名单 | 设置页维护标签黑名单，服务层按标签过滤，对所有列表生效 |
| R-18 | 设置页分 R-18 / R-18G 两个开关；卡片左上角显示红色等级标注 |
| 类型筛选 | 搜索/标签页的「类型」是客户端过滤（Pixiv 搜索接口不支持按 illust/manga/ugoira 过滤） |

## 6. 本轮补充（交互 / 分页 / 性能）

| 项 | 实现 |
| --- | --- |
| 收藏分割按钮 | 详情页 `SplitButton`：主按钮=收藏/取消，下拉=私密收藏 |
| 筛选弹窗 | `Controls/FilterDialog`：标签（AND）、显示 AI 作品、R-18 / R-18G、黑名单标签；推荐页、搜索页、标签页共用 |
| 总开关约束 | 设置页的 R-18 / R-18G 是总开关，关闭时弹窗里对应开关禁用并强制关闭 |
| 分页 | `PagedResult.NextOffset` + `IllustrationGrid.LoadMoreRequested`（滚动接近底部触发），搜索页 / 标签页滚动加载更多 |
| 排序修正 | `popular_desc` 对非会员会被 Pixiv 静默降级，服务层再按收藏数本地排序 |
| 类型修正 | `SearchTarget.All/Illustrations/Manga/Ugoira`，服务层循环多拉几页再按 type 过滤，避免"选漫画直接空" |
| 缩略图降采样 | 列表用 `medium`（540×540）+ `DecodePixelWidth=360`；详情页用 `large`，下载用 `original` |
| 图片缓存 | 磁盘 + 内存，`CachedImage.Url` 带 `DecodeWidth`，头像走 `CachedImage.ProfileUrl`（96px） |
| 缩放 / 全屏 | 详情页滚轮缩放（0.5x~6x）、放大后左键拖动平移、底部悬浮控制条（适应窗口 / 倍率 / 加减 / 滑块 / 全屏），右键「全屏查看」或 F11，Esc 退出 |
| 渐进加载 | 详情页先加载 `large`（600×1200，秒开）显示，后台再加载原图替换（`CachedImage.HighQualityUrl`）；列表用 `medium` + `DecodePixelWidth=360` |
| 图片信息 | 详情页右键「详情」→ 标题 / 作者 / PID / 分辨率 / 页数 / 标签 / 作品链接 / 原图源链接（i.pximg.net，非代理） |
