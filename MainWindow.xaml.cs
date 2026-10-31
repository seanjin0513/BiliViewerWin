using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace BiliViewer
{
    /// <summary>
    /// 主窗口：WebView2 承载 B 站网页，原生侧负责窗口全屏、菜单、标题、系统音量、窗口记忆。
    /// 滚轮调音量 / HUD / 单击暂停 / 双击全屏 / 鼠标侧键 / ESC 全部在注入脚本 Web/page.js 里实现。
    /// </summary>
    public partial class MainWindow : Window
    {
        const string HomeUrl = "https://www.bilibili.com/";
        const string DefaultTitle = "哔哩哔哩";

        // B 站会校验 UA，WebView2 初始 UA 里带 Edg/ 且版本号老旧，这里统一注入较新的 Chrome UA
        const string DesktopUA =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/131.0.0.0 Safari/537.36";

        static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        readonly Settings _settings = Settings.Load();
        readonly DispatcherTimer _saveTimer;

        bool _ready;
        bool _windowFullscreen;
        bool _pageFullscreen;
        /// <summary>窗口全屏是由网页全屏带出来的：网页一退出全屏，窗口也跟着退出</summary>
        bool _fullscreenFollowsPage;

        WindowState _restoreState;
        Rect _restoreBounds;

        public MainWindow()
        {
            InitializeComponent();

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); SaveWindowBounds(); };

            ApplyWindowIcon();
            ApplySavedBounds();
            ApplySensitivityMenu();

            Loaded += async (s, e) => await InitWebViewAsync();
            LocationChanged += (s, e) => ScheduleSave();
            SizeChanged += (s, e) => ScheduleSave();
            StateChanged += (s, e) => ScheduleSave();
            Closing += OnClosing;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        // ───────────────────────── 初始化 ─────────────────────────

        async Task InitWebViewAsync()
        {
            var browserArgs = "--autoplay-policy=no-user-gesture-required";

            // 调试用：设置 BILIVIEWER_BROWSER_ARGS=--remote-debugging-port=9222 可接入 DevTools/CDP
            var debugArgs = Environment.GetEnvironmentVariable("BILIVIEWER_BROWSER_ARGS");
            if (!string.IsNullOrWhiteSpace(debugArgs))
                browserArgs += " " + debugArgs.Trim();

            var options = new CoreWebView2EnvironmentOptions
            {
                // 允许自动播放，行为和桌面版 B 站一致
                AdditionalBrowserArguments = browserArgs
            };

            try
            {
                var env = await CoreWebView2Environment.CreateAsync(
                    null,
                    Path.Combine(Settings.AppDataDir, "WebView2"), // 登录状态持久化
                    options);

                await Web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "WebView2 初始化失败：\n\n" + ex.Message +
                    "\n\n请确认已安装 Microsoft Edge WebView2 Runtime（Win11 一般已自带）。",
                    DefaultTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            var core = Web.CoreWebView2;

            core.Settings.UserAgent = DesktopUA;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = true;
            core.Settings.IsGeneralAutofillEnabled = true;

            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 23, 24, 28);

            // 第一段：把灵敏度暴露给页面脚本（新文档每个都会先执行它，再执行 page.js）
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.__bvSensitivity = " + _settings.Sensitivity.ToString("0.###", Ci) + ";");

            // 第二段：页面交互脚本
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScript.Value);

            core.DocumentTitleChanged += OnDocumentTitleChanged;
            core.ContainsFullScreenElementChanged += OnContainsFullScreenElementChanged;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebMessageReceived += OnWebMessageReceived;

            _ready = true;
            core.Navigate(HomeUrl);
        }

        /// <summary>内嵌的页面交互脚本（编译期嵌入为 "page.js"）</summary>
        static class PageScript
        {
            internal static readonly string Value = Load();

            static string Load()
            {
                try
                {
                    using (var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("page.js"))
                    {
                        if (stream == null) return "/* page.js 未嵌入 */";
                        using (var reader = new StreamReader(stream))
                            return reader.ReadToEnd();
                    }
                }
                catch
                {
                    return "/* page.js 读取失败 */";
                }
            }
        }

        // ───────────────────────── 图标 ─────────────────────────

        void ApplyWindowIcon()
        {
            try
            {
                var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (!File.Exists(ico)) ico = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
                if (File.Exists(ico))
                    Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(ico, UriKind.Absolute));
            }
            catch
            {
                // 没图标也能跑
            }
        }

        // ───────────────────────── 窗口位置记忆 ─────────────────────────

        void ApplySavedBounds()
        {
            if (_settings.Left.HasValue && _settings.Top.HasValue &&
                _settings.Width.HasValue && _settings.Height.HasValue &&
                FitsOnAnyScreen(_settings.Left.Value, _settings.Top.Value, _settings.Width.Value, _settings.Height.Value))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.Left.Value;
                Top = _settings.Top.Value;
                Width = Math.Max(MinWidth, _settings.Width.Value);
                Height = Math.Max(MinHeight, _settings.Height.Value);
            }

            if (_settings.Maximized)
                WindowState = WindowState.Maximized;
        }

        static bool FitsOnAnyScreen(double left, double top, double width, double height)
        {
            double vl = SystemParameters.VirtualScreenLeft;
            double vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth;
            double vb = vt + SystemParameters.VirtualScreenHeight;
            // 至少要有可点的一角留在屏幕内，避免换了显示器后窗口跑到看不见的地方
            return left + width > vl + 80 && left < vr - 80 &&
                   top + 40 > vt && top < vb - 80;
        }

        void ScheduleSave()
        {
            if (!_ready) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        void SaveWindowBounds()
        {
            if (_windowFullscreen) return;

            _settings.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.Left = Left;
                _settings.Top = Top;
                _settings.Width = Width;
                _settings.Height = Height;
            }
            _settings.Save();
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            _saveTimer.Stop();
            SaveWindowBounds();
            try { Web.Dispose(); } catch { /* ignore */ }
        }

        // ───────────────────────── 原生快捷键 ─────────────────────────

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 裸 Alt 会让 WPF 进入「菜单模式」（菜单栏高亮、窗口输入被菜单的模态循环接管，
            // 实测会把 WebView2 卡住几十秒）。把单独的 Alt 吃掉，Alt+滚轮 / Alt+←→ 不受影响。
            if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
                (e.Key == Key.System && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt)))
            {
                e.Handled = true;
                return;
            }

            if (HandleShortcut(e.Key, Keyboard.Modifiers)) e.Handled = true;
        }

        // WebView2(WPF) 会把页面未处理的快捷键转发成普通 WPF 键盘输入，
        // 所以统一在 Window.PreviewKeyDown 里拦截即可（不再需要 CoreWebView2Controller 的 AcceleratorKeyPressed）。

        bool HandleShortcut(Key key, ModifierKeys mods)
        {
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;
            bool alt = (mods & ModifierKeys.Alt) != 0;

            switch (key)
            {
                case Key.F11:
                    ToggleWindowFullscreen();
                    return true;

                case Key.F12:
                    OpenDevTools();
                    return true;

                case Key.F5:
                    if (shift) ForceReload(); else Reload();
                    return true;

                case Key.R:
                    if (!ctrl) return false;
                    if (shift) ForceReload(); else Reload();
                    return true;

                case Key.D0:
                case Key.NumPad0:
                    if (!ctrl) return false;
                    SetActualSize();
                    return true;

                case Key.Home:
                    if (!alt) return false;
                    NavigateHome();
                    return true;

                case Key.Left:
                    if (!alt) return false;
                    GoBack();
                    return true;

                case Key.Right:
                    if (!alt) return false;
                    GoForward();
                    return true;
            }
            return false;
        }

        // ───────────────────────── 导航动作 ─────────────────────────

        void NavigateHome()
        {
            if (!_ready) return;
            Web.CoreWebView2.Navigate(HomeUrl);
        }

        void GoBack()
        {
            if (!_ready) return;
            if (Web.CoreWebView2.CanGoBack) Web.CoreWebView2.GoBack();
        }

        void GoForward()
        {
            if (!_ready) return;
            if (Web.CoreWebView2.CanGoForward) Web.CoreWebView2.GoForward();
        }

        void Reload()
        {
            if (!_ready) return;
            Web.CoreWebView2.Reload();
        }

        /// <summary>强制刷新：走 DevTools Protocol，忽略所有缓存</summary>
        async void ForceReload()
        {
            if (!_ready) return;
            try
            {
                await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
            }
            catch
            {
                Web.CoreWebView2.Reload();
            }
        }

        void SetActualSize()
        {
            if (!_ready) return;
            Web.ZoomFactor = 1.0;
        }

        void OpenDevTools()
        {
            if (!_ready) return;
            try { Web.CoreWebView2.OpenDevToolsWindow(); } catch { /* ignore */ }
        }

        // ───────────────────────── 窗口全屏 ─────────────────────────

        void ToggleWindowFullscreen()
        {
            if (_windowFullscreen) ExitWindowFullscreen();
            else EnterWindowFullscreen();
        }

        void EnterWindowFullscreen()
        {
            if (_windowFullscreen) return;

            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;

            _restoreState = WindowState;
            _restoreBounds = new Rect(Left, Top, Width, Height);
            _windowFullscreen = true;

            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;

            // RECT 是物理像素，WPF 的 Left/Top/Width/Height 是 DIP，需要按当前显示器 DPI 换算
            double dpiX = 1.0, dpiY = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            {
                Matrix m = source.CompositionTarget.TransformToDevice;
                dpiX = m.M11;
                dpiY = m.M22;
            }

            var r = info.rcMonitor;
            Left = r.Left / dpiX;
            Top = r.Top / dpiY;
            Width = (r.Right - r.Left) / dpiX;
            Height = (r.Bottom - r.Top) / dpiY;

            UpdateMenuBarVisibility();
        }

        void ExitWindowFullscreen()
        {
            if (!_windowFullscreen) return;
            _windowFullscreen = false;
            _fullscreenFollowsPage = false;

            Topmost = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;

            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            WindowState = _restoreState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;

            UpdateMenuBarVisibility();
        }

        /// <summary>网页 HTML5 全屏时也把菜单栏收起来，退出时恢复</summary>
        void OnContainsFullScreenElementChanged(object sender, object e)
        {
            bool wasPageFullscreen = _pageFullscreen;
            _pageFullscreen = _ready && Web.CoreWebView2.ContainsFullScreenElement;
            UpdateMenuBarVisibility();

            // B 站的全屏走 HTML5 Fullscreen API：ESC 被浏览器吃掉用于退出 HTML5 全屏，
            // 页面的 keydown 收不到，所以「网页退全屏 → 窗口跟着退全屏」必须在这里兜底。
            if (wasPageFullscreen && !_pageFullscreen && _windowFullscreen && _fullscreenFollowsPage)
            {
                ExitWindowFullscreen();
            }
        }

        void UpdateMenuBarVisibility()
        {
            bool hide = _windowFullscreen || _pageFullscreen;
            MenuBar.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        }

        // ───────────────────────── WebView2 事件 ─────────────────────────

        void OnDocumentTitleChanged(object sender, object e)
        {
            var title = _ready ? Web.CoreWebView2.DocumentTitle : null;
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title;
        }

        void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;

            var uri = e.Uri;
            if (string.IsNullOrEmpty(uri)) return;

            if (IsBilibiliHost(uri))
            {
                // 站内链接就在本窗口打开（和 macOS 版一致）
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_ready) Web.CoreWebView2.Navigate(uri);
                }));
                return;
            }

            // 站外链接交给系统浏览器
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch
            {
                // 忽略
            }
        }

        static bool IsBilibiliHost(string uri)
        {
            try
            {
                var host = new Uri(uri).Host;
                return host.EndsWith("bilibili.com", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("b23.tv", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("hdslb.com", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // ───────────────────────── JS → 原生消息 ─────────────────────────

        void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JsonElement root;
            try
            {
                root = JsonSerializer.Deserialize<JsonElement>(e.WebMessageAsJson);
            }
            catch
            {
                return;
            }

            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return;

            string name = nameEl.GetString();
            string body = root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
                ? bodyEl.GetString()
                : null;

            switch (name)
            {
                case "escape":
                    // 网页层自己会先退出 HTML5 全屏；这里负责退出窗口全屏
                    if (_windowFullscreen) ExitWindowFullscreen();
                    break;

                case "dblclick":
                    // 双击 = 网页全屏 + 窗口全屏；body 是双击后网页应处的状态
                    if (body == "full")
                    {
                        EnterWindowFullscreen();
                        _fullscreenFollowsPage = true;
                    }
                    else
                    {
                        ExitWindowFullscreen();
                    }
                    break;

                case "nav":
                    if (body == "back") GoBack();
                    else if (body == "forward") GoForward();
                    break;

                case "sysvol":
                    {
                        double delta = ParseDouble(body);
                        if (Math.Abs(delta) > 0.0001)
                        {
                            double v = SystemVolume.Adjust(delta);
                            if (v >= 0) PushHud(v);
                        }
                        break;
                    }

                case "hover":
                    // 目前只用于调试，无副作用
                    break;
            }
        }

        static double ParseDouble(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return double.TryParse(text, NumberStyles.Float, Ci, out double v) ? v : 0;
        }

        /// <summary>让页面里的 HUD 显示音量（0~1）</summary>
        void PushHud(double volume)
        {
            if (!_ready) return;
            try
            {
                var json = "{\"hud\":" + volume.ToString("0.###", Ci) + "}";
                Web.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch
            {
                // ignore
            }
        }

        // ───────────────────────── 菜单动作 ─────────────────────────

        void OnHome(object sender, RoutedEventArgs e) => NavigateHome();
        void OnBack(object sender, RoutedEventArgs e) => GoBack();
        void OnForward(object sender, RoutedEventArgs e) => GoForward();
        void OnReload(object sender, RoutedEventArgs e) => Reload();
        void OnForceReload(object sender, RoutedEventArgs e) => ForceReload();
        void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleWindowFullscreen();
        void OnActualSize(object sender, RoutedEventArgs e) => SetActualSize();
        void OnDevTools(object sender, RoutedEventArgs e) => OpenDevTools();
        void OnExit(object sender, RoutedEventArgs e) => Close();

        void ApplySensitivityMenu()
        {
            double s = _settings.Sensitivity;
            SensSlow.IsChecked = Math.Abs(s - 0.6) < 0.01;
            SensNormal.IsChecked = Math.Abs(s - 1.0) < 0.01;
            SensFast.IsChecked = Math.Abs(s - 1.5) < 0.01;
        }

        void OnSensitivity(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuItem;
            if (item == null || item.Tag == null) return;

            if (!double.TryParse(Convert.ToString(item.Tag, Ci), NumberStyles.Float, Ci, out double value))
                return;

            _settings.Sensitivity = value;
            _settings.Save();
            ApplySensitivityMenu();

            if (!_ready) return;
            // 立刻生效，不用等下次导航
            _ = Web.CoreWebView2.ExecuteScriptAsync(
                "window.__bvSetSensitivity && window.__bvSetSensitivity(" + value.ToString("0.###", Ci) + ")");
        }

        void OnAbout(object sender, RoutedEventArgs e)
        {
            var version = typeof(MainWindow).Assembly.GetName().Version;
            MessageBox.Show(this,
                "BiliViewer for Windows  " + version + "\n\n" +
                "B 站网页客户端，交互对齐 macOS 版：\n" +
                "· 播放器上滚轮 = 调视频音量（不滚动页面）\n" +
                "· Alt+滚轮 = 调系统音量\n" +
                "· 单击播放器 = 暂停/播放，双击 = 全屏\n" +
                "· 鼠标侧键 = 后退/前进\n" +
                "· F11 全屏，ESC 退出全屏，F12 开发者工具\n\n" +
                "设置目录：%LOCALAPPDATA%\\BiliViewer",
                DefaultTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    internal static class NativeMethods
    {
        internal const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    }
}
