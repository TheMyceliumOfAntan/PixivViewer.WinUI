using System;
using System.Collections.Generic;
using System.Linq;
using Pixiv_Viewer_WinUI.Models;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>
/// 设计预览用示例数据。仅当「设置 → 使用示例数据」开启且真实服务返回空时使用，
/// 接入真实 API 后关掉开关即可完全不参与。
/// </summary>
public static class SampleData
{
    private static readonly string[] Titles =
    {
        "雨上がりの放課後",
        "夜明けの街と君",
        "深海の図書館",
        "夏の残響",
        "銀河鉄道の少女",
        "静かな午後",
        "花と硝子",
        "ネオン・パレード",
        "冬の窓辺",
        "星を編む",
        "路地裏の猫",
        "青の記憶",
        "午前四時の喫茶店",
        "風の通る部屋",
        "ひかりの粒",
        "黄昏アーケード",
        "白い鳥の行方",
        "水底のワルツ",
        "遠雷",
        "セピアの街角",
        "月と灯台",
        "透明な境界線",
        "朝焼けのスケッチ",
        "眠らない森",
        "コントラスト",
        "砂時計の街",
        "雪解けの音",
        "夜想曲",
    };

    private static readonly string[] AuthorNames =
    {
        "月見 ゆき", "蒼井 レイ", "kuro_m", "白瀬 かなめ", "灯 あかり",
        "Nagi", "小春 みのり", "雨宮 そら", "yuu_", "遠野 ひかり",
    };

    private static readonly string[] TagPool =
    {
        "オリジナル", "女の子", "風景", "イラスト", "初音ミク",
        "メカ", "背景", "水彩", "ファンタジー", "制服", "猫", "夜空",
        "新作", "1000users入り", "光", "雨",
    };

    private static readonly double[] Ratios = { 0.66, 0.75, 0.8, 1.0, 0.56, 1.33, 0.71, 0.62, 0.9, 1.5 };

    private static readonly string[] Bios =
    {
        "イラストを描いています。依頼はDMまで。",
        "風景と光の表現が好きです。無断転載はご遠慮ください。",
        "オリジナル中心。たまに版権ものも描きます。",
        "毎週金曜更新。感想はコメントへどうぞ。",
        "背景多め。作業環境: CLIP STUDIO PAINT。",
        "色と構図の練習帳。",
    };

    public static PixivUser CurrentUser { get; } = new()
    {
        Id = "10001",
        Name = "月見 ゆき",
        Account = "tsukimi_yuki",
        IllustrationCount = 128,
        FollowerCount = 15420,
        FollowingCount = 236,
        BookmarkCount = 488,
        IsFollowed = false,
        Biography = "月と夜のイラストを描いています。",
    };

    public static IReadOnlyList<Illustration> Recommended { get; } = CreateIllustrations(28, 7);

    public static IReadOnlyList<Illustration> Ranking { get; } = CreateRanking();

    public static IReadOnlyList<Illustration> PublicBookmarks { get; } = CreateIllustrations(18, 31);

    public static IReadOnlyList<Illustration> PrivateBookmarks { get; } = CreateIllustrations(9, 57);

    public static IReadOnlyList<Illustration> MyWorks { get; } = CreateIllustrations(12, 83);

    public static IReadOnlyList<Illustration> SearchResults { get; } = CreateIllustrations(22, 101);

    public static IReadOnlyList<PixivUser> FollowingUsers { get; } = CreateUsers(8);

    public static IReadOnlyList<string> SearchSuggestions { get; } = new[]
    {
        "オリジナル", "女の子", "風景", "初音ミク", "メカ", "水彩", "夜空", "制服",
    };

    public static IReadOnlyList<Comment> Comments { get; } = CreateComments();

    public static List<DownloadTask> DownloadTasks { get; } = CreateDownloadTasks();

    /// <summary>作者页用的示例作品（把作者统一替换成当前作者）。</summary>
    public static IReadOnlyList<Illustration> GetUserIllustrations(string userId, PixivUser author, int count = 16)
    {
        var items = CreateIllustrations(count, StableHash(userId) % 400 + 11);
        foreach (var item in items)
        {
            item.Author = author;
        }

        return items;
    }

    private static int StableHash(string text)
    {
        var hash = 17;
        foreach (var c in text)
        {
            hash = unchecked(hash * 31 + c);
        }

        return hash & 0x7FFFFFFF;
    }

    private static List<Illustration> CreateIllustrations(int count, int seed)
    {
        var random = new Random(seed);
        var items = new List<Illustration>(count);
        for (var i = 0; i < count; i++)
        {
            var author = new PixivUser
            {
                Id = $"2000{random.Next(1, 99)}",
                Name = AuthorNames[random.Next(AuthorNames.Length)],
                Account = $"user_{random.Next(1000, 9999)}",
                IllustrationCount = random.Next(20, 900),
                FollowerCount = random.Next(200, 90000),
                FollowingCount = random.Next(30, 500),
                BookmarkCount = random.Next(50, 20000),
                Biography = Bios[random.Next(Bios.Length)],
            };

            var tagCount = random.Next(2, 5);
            var tags = Enumerable.Range(0, tagCount)
                .Select(_ => TagPool[random.Next(TagPool.Length)])
                .Distinct()
                .ToArray();

            items.Add(new Illustration
            {
                Id = (seed * 1000 + i).ToString(),
                Title = Titles[(seed + i) % Titles.Length],
                Description = "これはサンプルの作品説明です。UI のレイアウト確認用に生成されたテキストで、実際の作品紹介文はここに表示されます。タグやコメント、統計情報の配置を確認するためのダミー文章が続きます。",
                Author = author,
                PageCount = random.Next(1, 10) > 6 ? random.Next(2, 13) : 1,
                AspectRatio = Ratios[random.Next(Ratios.Length)],
                Tags = tags.Select(t => new IllustTag { Name = t }).ToArray(),
                LikeCount = random.Next(120, 48000),
                BookmarkCount = random.Next(80, 26000),
                ViewCount = random.Next(1200, 260000),
                CreatedAt = DateTimeOffset.Now.AddHours(-random.Next(1, 240)),
                IsBookmarked = random.Next(0, 4) == 0,
            });
        }

        return items;
    }

    private static List<Illustration> CreateRanking()
    {
        var items = CreateIllustrations(20, 211)
            .OrderByDescending(item => item.LikeCount)
            .ToList();

        for (var i = 0; i < items.Count; i++)
        {
            items[i].Rank = i + 1;
        }

        return items;
    }

    private static List<PixivUser> CreateUsers(int count)
    {
        var random = new Random(313);
        var users = new List<PixivUser>(count);
        for (var i = 0; i < count; i++)
        {
            users.Add(new PixivUser
            {
                Id = $"3000{i}",
                Name = AuthorNames[i % AuthorNames.Length],
                Account = $"followed_{random.Next(1000, 9999)}",
                IllustrationCount = random.Next(50, 1200),
                FollowerCount = random.Next(1000, 250000),
                FollowingCount = random.Next(20, 800),
                BookmarkCount = random.Next(100, 50000),
                IsFollowed = true,
                Biography = Bios[random.Next(Bios.Length)],
            });
        }

        return users;
    }

    private static List<Comment> CreateComments()
    {
        string[] texts =
        {
            "色使いがとても綺麗です！参考にさせていただきます。",
            "この構図すごく好きです。新作待ってます！",
            "光の表現が神がかってる…",
            "背景の描き込みがすごい。保存しました。",
            "雰囲気が最高です。",
            "毎回楽しみにしています！",
            "(heart)(excited)",
        };

        var random = new Random(517);
        var comments = new List<Comment>(texts.Length);
        for (var i = 0; i < texts.Length; i++)
        {
            comments.Add(new Comment
            {
                Id = $"c{i}",
                User = new PixivUser
                {
                    Id = $"4000{i}",
                    Name = AuthorNames[(i + 3) % AuthorNames.Length],
                    Account = $"viewer_{random.Next(100, 999)}",
                },
                Text = texts[i],
                CreatedAt = DateTimeOffset.Now.AddHours(-random.Next(1, 96)),
                LikeCount = random.Next(0, 320),
            });
        }

        return comments;
    }

    private static List<DownloadTask> CreateDownloadTasks()
    {
        var source = CreateIllustrations(6, 617);
        var tasks = new List<DownloadTask>
        {
            NewTask(source[0], DownloadState.Downloading, 42, 4_200_000, 1),
            NewTask(source[1], DownloadState.Downloading, 78, 9_600_000, 3),
            NewTask(source[2], DownloadState.Paused, 35, 2_400_000, 1),
            NewTask(source[3], DownloadState.Queued, 0, 0, 1),
            NewTask(source[4], DownloadState.Completed, 100, 6_800_000, 1),
            NewTask(source[5], DownloadState.Failed, 12, 640_000, 2),
        };

        tasks[0].BytesPerSecond = 1_800_000;
        tasks[1].BytesPerSecond = 2_600_000;
        tasks[5].ErrorMessage = "连接超时";
        tasks[4].TargetPath = @"D:\Pixiv\雨上がりの放課後";
        return tasks;
    }

    private static DownloadTask NewTask(Illustration item, DownloadState state, double progress, long total, int page)
    {
        return new DownloadTask
        {
            Id = "dl-" + item.Id,
            IllustrationId = item.Id,
            Title = item.Title,
            AuthorName = item.Author.Name,
            PlaceholderSeed = item.Id,
            PageCount = Math.Max(item.PageCount, 1),
            CurrentPage = page,
            State = state,
            Progress = progress,
            TotalBytes = total,
            BytesReceived = (long)(total * progress / 100d),
            TargetPath = $@"D:\Pixiv\{item.Title}",
        };
    }
}
