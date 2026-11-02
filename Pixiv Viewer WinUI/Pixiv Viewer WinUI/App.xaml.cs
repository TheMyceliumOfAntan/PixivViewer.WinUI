using Microsoft.UI.Xaml;
using Pixiv_Viewer_WinUI.Services;

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
            _window = new MainWindow();
            MainWindow = _window;
            _window.Activate();

            AppServices.Settings.ApplyTheme();

            // 后台恢复登录态，首页/收藏等页面首次加载时就能直接拿到数据。
            _ = SignInAsync();

            // 启动 5 秒后检查更新（每天最多一次，失败静默）
            _ = CheckUpdatesAsync();
        }

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
