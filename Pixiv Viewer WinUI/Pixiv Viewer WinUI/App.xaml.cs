using Microsoft.UI.Xaml;
using Pixiv_Viewer_WinUI.Services;
using System;
using System.Linq;

namespace Pixiv_Viewer_WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// 当前主窗口（设置页切主题时需要）。
        /// </summary>
        public static Window? MainWindow { get; private set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            UnhandledException += (_, e) =>
            {
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(
                            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                            "PixivViewer",
                            "log.txt"),
                        $"[{System.DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED: {e.Exception}{System.Environment.NewLine}");
                }
                catch
                {
                    // 忽略日志失败
                }

                // 兜底：async void 里漏网的异常会以 stowed exception 形式直接崩进程，
                // 这里标记已处理，至少让用户还能继续用其它页面。
                e.Handled = true;
            };
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 开发自检：--manga-selftest <图片> <输出目录>，不打开窗口，跑完写日志退出
            var commandLine = System.Environment.GetCommandLineArgs();

            var fetchIndex = System.Array.IndexOf(commandLine, "--manga-fetch");
            if (fetchIndex >= 0 && commandLine.Length > fetchIndex + 2)
            {
                _ = RunMangaFetchAsync(commandLine[fetchIndex + 1], commandLine[fetchIndex + 2]);
                return;
            }

            var downloadIndex = System.Array.IndexOf(commandLine, "--manga-download-test");
            if (downloadIndex >= 0 && commandLine.Length > downloadIndex + 1)
            {
                var lamaOnly = commandLine.Length > downloadIndex + 2 &&
                               commandLine[downloadIndex + 2] is "lama" or "--lama";
                _ = RunMangaDownloadTestAsync(commandLine[downloadIndex + 1], lamaOnly);
                return;
            }

            var selfTestIndex = System.Array.IndexOf(commandLine, "--manga-selftest");
            if (selfTestIndex >= 0 && commandLine.Length > selfTestIndex + 2)
            {
                _ = RunMangaSelfTestAsync(commandLine[selfTestIndex + 1], commandLine[selfTestIndex + 2]);
                return;
            }

            _window = new MainWindow();
            MainWindow = _window;
            _window.Activate();

            AppServices.Settings.ApplyTheme();

            // 后台恢复登录态，首页/收藏等页面首次加载时就能直接拿到数据。
            _ = SignInAsync();

            // 启动 5 秒后检查更新（每天最多一次，失败静默）
            _ = CheckUpdatesAsync();
        }


        /// <summary>开发自检：把指定作品的每页原图抓到本地，用于离线复现。</summary>
        private static async System.Threading.Tasks.Task RunMangaFetchAsync(string illustId, string outputDirectory)
        {
            var notes = new System.Text.StringBuilder();
            void Log(string message)
            {
                lock (notes)
                {
                    notes.AppendLine($"[{System.DateTime.Now:HH:mm:ss}] {message}");
                    try
                    {
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(outputDirectory, "fetch.log"),
                            message + System.Environment.NewLine);
                    }
                    catch (System.Exception)
                    {
                        // 忽略
                    }
                }
            }

            try
            {
                System.IO.Directory.CreateDirectory(outputDirectory);
                var summary = await Services.Manga.MangaFetcher.FetchAsync(illustId, outputDirectory, Log);
                Log("result: " + summary);
            }
            catch (System.Exception ex)
            {
                Log("FAILED: " + ex);
            }
            finally
            {
                System.Environment.Exit(0);
            }
        }

        /// <summary>开发自检：只跑模型下载（验证续传与进度回调），日志写到指定目录。</summary>
        private static async System.Threading.Tasks.Task RunMangaDownloadTestAsync(string outputDirectory, bool lamaOnly = false)
        {
            var notes = new System.Text.StringBuilder();
            var gate = new object();

            void Log(string message)
            {
                lock (gate)
                {
                    var line = $"[{System.DateTime.Now:HH:mm:ss}] {message}";
                    notes.AppendLine(line);

                    // 立即落盘，进程被卡住时也能看到最后一步
                    try
                    {
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(outputDirectory, "selftest.log"),
                            line + System.Environment.NewLine);
                    }
                    catch (System.Exception)
                    {
                        // 忽略
                    }
                }
            }

            try
            {
                System.IO.Directory.CreateDirectory(outputDirectory);
                Log($"dir={Services.Manga.MangaModels.Directory} partial={Services.Manga.MangaTranslationService.PartialBytes}");

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var reporter = new SyncProgress<Services.Manga.MangaProgress>(p =>
                    Log($"  {p.Fraction:P0} {p.Stage} {p.ReceivedBytes / 1024d / 1024d:F1}/{p.TotalBytes / 1024d / 1024d:F1} MB"));

                var ok = lamaOnly
                    ? await Services.Manga.MangaTranslationService.EnsureLamaAsync(reporter, System.Threading.CancellationToken.None)
                    : await Services.Manga.MangaTranslationService.EnsureModelsAsync(reporter, System.Threading.CancellationToken.None);

                Log($"done ok={ok} in {watch.ElapsedMilliseconds} ms installed={Services.Manga.MangaTranslationService.IsReady} lama={Services.Manga.MangaTranslationService.IsLamaReady}");
            }
            catch (System.Exception ex)
            {
                Log("FAILED: " + ex);
            }
            finally
            {
                await System.IO.File.WriteAllTextAsync(
                    System.IO.Path.Combine(outputDirectory, "download.log"),
                    notes.ToString());
                System.Environment.Exit(0);
            }
        }

        /// <summary>开发自检：跑一遍完整流水线并写日志，用于在没有 UI 的情况下验证模型链路。</summary>
        private static async System.Threading.Tasks.Task RunMangaSelfTestAsync(string imagePath, string outputDirectory)
        {
            var notes = new System.Text.StringBuilder();
            var gate = new object();

            void Log(string message)
            {
                lock (gate)
                {
                    notes.AppendLine(message);
                }
            }

            try
            {
                System.IO.Directory.CreateDirectory(outputDirectory);
                Log($"models installed={Services.Manga.MangaTranslationService.IsReady} size={Services.Manga.MangaTranslationService.ModelSize}");

                var bytes = await System.IO.File.ReadAllBytesAsync(imagePath);
                var bitmap = await Services.Manga.MangaBitmap.DecodeAsync(bytes);
                Log($"decoded {bitmap.Width}x{bitmap.Height}");

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var reporter = new SyncProgress<Services.Manga.MangaProgress>(p => Log($"  {p.Fraction:P0} {p.Stage}"));
                var inpaintMode = string.Equals(
                    System.Environment.GetEnvironmentVariable("PIXIV_MANGA_INPAINT"),
                    "lama",
                    System.StringComparison.OrdinalIgnoreCase)
                    ? Services.Manga.MangaInpaintMode.Lama
                    : Services.Manga.MangaInpaintMode.Classic;
                Log($"inpaintMode={inpaintMode} lamaReady={Services.Manga.MangaTranslationService.IsLamaReady}");
                var result = await Services.Manga.MangaPipeline.TranslateAsync(
                    bitmap,
                    "selftest",
                    0,
                    inpaintMode,
                    reporter,
                    System.Threading.CancellationToken.None);

                Log($"pipeline {watch.ElapsedMilliseconds} ms, backend={Services.Manga.MangaBackend.Instance.BackendName}, regions={result.Regions.Count}");
                Log(Services.Manga.MangaPipeline.LastTimings);
                Log("batch=" + Services.TranslationService.LastBatchInfo);

                foreach (var line in Services.Manga.MangaPipeline.LastLines)
                {
                    Log($"  line {line}");
                }

                foreach (var region in result.Regions)
                {
                    Log($"  [{region.Score:F2}{(region.Vertical ? " V" : " H")}] {region.Source} => {region.Target}");
                }

                await System.IO.File.WriteAllTextAsync(
                    System.IO.Path.Combine(outputDirectory, "regions.json"),
                    System.Text.Json.JsonSerializer.Serialize(
                        result.Regions,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        }));

                if (result.Png is { } png)
                {
                    var outPath = System.IO.Path.Combine(outputDirectory, "out.png");
                    await System.IO.File.WriteAllBytesAsync(outPath, png);

                    // 去字效果量化：区域内的灰度标准差应显著下降
                    var after = await Services.Manga.MangaBitmap.DecodeAsync(png);
                    double beforeSum = 0, afterSum = 0;
                    foreach (var region in result.Regions)
                    {
                        var x = (int)(region.X * bitmap.Width);
                        var y = (int)(region.Y * bitmap.Height);
                        var w = Math.Max(2, (int)(region.W * bitmap.Width));
                        var h = Math.Max(2, (int)(region.H * bitmap.Height));
                        beforeSum += StdDev(bitmap, x, y, w, h);
                        afterSum += StdDev(after, x, y, w, h);
                    }

                    var count = Math.Max(1, result.Regions.Count);
                    Log($"inpaint stddev before={beforeSum / count:F1} after={afterSum / count:F1}");
                }
            }
            catch (System.Exception ex)
            {
                Log("FAILED: " + ex);
            }
            finally
            {
                await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(outputDirectory, "selftest.log"), notes.ToString());
                System.Environment.Exit(0);
            }
        }

        private static double StdDev(Services.Manga.MangaBitmap bitmap, int x, int y, int w, int h)
        {
            double sum = 0, squareSum = 0;
            var count = 0;
            var stepX = Math.Max(1, w / 32);
            var stepY = Math.Max(1, h / 32);

            for (var row = Math.Max(0, y); row < Math.Min(bitmap.Height, y + h); row += stepY)
            {
                for (var col = Math.Max(0, x); col < Math.Min(bitmap.Width, x + w); col += stepX)
                {
                    var i = (row * bitmap.Width + col) * 4;
                    var gray = 0.114 * bitmap.Pixels[i] + 0.587 * bitmap.Pixels[i + 1] + 0.299 * bitmap.Pixels[i + 2];
                    sum += gray;
                    squareSum += gray * gray;
                    count++;
                }
            }

            if (count == 0)
            {
                return 0;
            }

            var mean = sum / count;
            return Math.Sqrt(Math.Max(0, squareSum / count - mean * mean));
        }

        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;

            public SyncProgress(Action<T> handler) => _handler = handler;

            public void Report(T value) => _handler(value);
        }

        /// <summary>启动后延迟检查更新（每天最多一次，失败静默）。</summary>
        private static async System.Threading.Tasks.Task CheckUpdatesAsync()
        {
            await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(5));

            try
            {
                await UpdateCoordinator.CheckOnStartupAsync();
            }
            catch (System.Exception)
            {
                // 更新检查失败不影响使用
            }
        }

        private static async System.Threading.Tasks.Task SignInAsync()
        {
            try
            {
                await AppServices.Pixiv.SignInAsync();
            }
            catch (System.Exception)
            {
                // 离线/代理不可用时静默失败，页面会显示空状态。
            }

            // 登录完成后再刷新顶部「未登录」提示条（启动时登录是异步的）
            (MainWindow as MainWindow)?.UpdateLoginBar();
        }
    }
}
