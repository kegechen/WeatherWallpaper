using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

namespace WeatherWallpaper
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            var first = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            // 安装包用：quit = 退出运行中的实例并等它结束；uninstall = 同时清掉开机自启
            if (first == "uninstall") App.SetAutoStart(false);
            using (var mutex = new Mutex(true, "WeatherWallpaper_SingleInstance", out created))
            {
                if (!created)
                {
                    // 已在运行：把命令行转发给运行中的实例，如 WeatherWallpaper.exe preview rain / capture D:\tmp / refresh / quit
                    // 本进程是用户刚启动的、持有前台权限，允许运行中的实例把天气面板切到前台
                    Native.AllowSetForegroundWindow(-1 /*ASFW_ANY*/);
                    if (first == "uninstall") ShellWatcher.Send("quit");
                    else if (args.Length > 0) ShellWatcher.Send(string.Join(" ", args));
                    else ShellWatcher.Send("show");   // 再次从开始菜单启动：弹出天气面板，表示程序已在运行
                    if (first == "quit" || first == "uninstall")
                    {
                        var me = System.Diagnostics.Process.GetCurrentProcess();
                        foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
                            if (p.Id != me.Id) try { p.WaitForExit(8000); } catch { }
                    }
                    return;
                }
                if (first == "quit" || first == "uninstall") return;
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Write("UI exception: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("fatal: " + e.ExceptionObject);
                Log.Write("start");
                Application.Run(new App());
            }
        }

        static Icon appIcon;
        /// <summary>内嵌的 app.ico（与 exe 图标同一个文件，含 16–256 各尺寸）。窗口直接用；托盘用 new Icon(AppIcon, 尺寸) 取合适的一帧。</summary>
        public static Icon AppIcon
        {
            get
            {
                if (appIcon != null) return appIcon;
                try
                {
                    using (var s = typeof(Program).Assembly.GetManifestResourceStream("WeatherWallpaper.app.ico"))
                        if (s != null) return appIcon = new Icon(s);
                }
                catch (Exception ex) { Log.Write("app icon: " + ex.Message); }
                return appIcon = SystemIcons.Application;
            }
        }
    }

    class App : ApplicationContext
    {
        readonly Settings settings = Settings.Load();
        readonly WeatherService ws = new WeatherService();
        readonly List<WallpaperWindow> walls = new List<WallpaperWindow>();
        readonly TaskbarOverlay overlay = new TaskbarOverlay();
        readonly EaveManager eaves = new EaveManager();
        readonly WinTimer weatherTimer = new WinTimer(), watchTimer = new WinTimer(), rebuildTimer = new WinTimer();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ShellWatcher shell;
        CoreWebView2Environment env;
        string preview = "live", sceneText = "";
        bool previewDay;
        DateTime previewUntil;
        readonly WinTimer previewTimer = new WinTimer { Interval = 1000 };
        // 天气预览的时长与极简模式每次播放的时长是同一个设置（PlaySeconds）；一天快进固定 48 秒走完一遍后恢复
        const int DayDemoSeconds = 49;
        bool locked;
        // 自定义效果（见 Effects）：当前的文件列表，跟着 layout 发给页面
        List<KeyValuePair<string, long>> effects = new List<KeyValuePair<string, long>>();
        string effectsJson = "[]";
        FileSystemWatcher effectsWatcher;
        readonly WinTimer effectsRescan = new WinTimer { Interval = 600 };
        // 自定义卡片（见 CustomCards）：cards 文件夹里的脚本。layout 里每张自定义卡片带上它脚本的修改时间，改过文件页面就重新加载那张卡片
        List<CustomCards.Script> cardScripts = new List<CustomCards.Script>();
        string cardScriptsSig = "";
        readonly WinTimer cardsRescan = new WinTimer { Interval = 600 };
        // 自定义卡片现在的错误（卡片 Id → 原因；页面报上来的，没有 = 正常）：卡片设置里显示；日志里同一个错误 10 分钟内只记一次
        readonly Dictionary<string, string> cardErrs = new Dictionary<string, string>();
        readonly Dictionary<string, DateTime> cardLogged = new Dictionary<string, DateTime>();
        string perfText = "";
        readonly PerfMonitor perf = new PerfMonitor();
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer();
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        public App()
        {
            shell = new ShellWatcher(() => ScheduleRebuild(2500)) { OnCommand = OnCommand };
            BuildTray();
            overlay.SetFps(settings.Fps);
            rebuildTimer.Tick += (s, e) => RebuildNow();
            weatherTimer.Interval = 30 * 1000;   // 每 30 秒看一眼哪个城市到了该刷新的时间（各城市 15 分钟一次，失败 2 分钟后重试）
            weatherTimer.Tick += (s, e) => RefreshDue();
            previewTimer.Tick += (s, e) => { if (DateTime.Now >= previewUntil) SetPreview("live", false); };
            watchTimer.Interval = 700;
            watchTimer.Tick += (s, e) => Watch();
            SystemEvents.DisplaySettingsChanged += (s, e) => ScheduleRebuild(1500);
            SystemEvents.SessionSwitch += (s, e) =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
                { locked = true; Log.Write("session: lock (" + e.Reason + ")"); }
                else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
                {
                    locked = false; Log.Write("session: unlock (" + e.Reason + ")");
                    foreach (var w in walls) w.Post("{\"t\":\"unlock\"}");   // 国庆节当天解锁时升旗（页面自己按天、按屏只升一次）
                }
                Watch();
            };
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General) ConfigureOverlay();
                // 换了系统壁纸，或工作区变了（任务栏自动隐藏、改大小）：卡片按工作区摆，要重排
                if (e.Category == UserPreferenceCategory.Desktop) { backdropTimer.Stop(); backdropTimer.Start(); }
            };
            backdropTimer.Tick += (s, e) =>
            {
                backdropTimer.Stop();
                // 换壁纸（幻灯片每次切换）也走这里：工作区没变就不用重排
                var wa = string.Join("|", Screen.AllScreens.Select(x => x.DeviceName + x.WorkingArea));
                if (wa != lastWorkAreas) { lastWorkAreas = wa; ConfigureOverlay(); }
                if (AnyBackdrop) UpdateBackdrops(true);
            };
            cardSaveTimer.Tick += (s, e) => FlushCardSave();
            SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode != PowerModes.Resume) return;
                foreach (var c in cities.Values) c.Due = DateTime.UtcNow.AddSeconds(15);   // 唤醒后网络通常要等一会儿
                // 30 秒一次的 tick 相位不定：另起一个一次性定时器，15 秒后准时捡起来（不改 weatherTimer 的间隔）
                var wake = new WinTimer { Interval = 15500 };
                wake.Tick += (s2, e2) => { wake.Stop(); wake.Dispose(); if (quitStage == 0) RefreshDue(); };
                wake.Start();
            };
            StartUpdateTimer();
            WatchSettings();
            WatchEffects();
            Init();
        }

        async void Init()
        {
            try
            {
                var opts = new CoreWebView2EnvironmentOptions(
                    "--disable-features=CalculateNativeWinOcclusion --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows --autoplay-policy=no-user-gesture-required");
                env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.LocalDir, "WebView2"), opts);
            }
            catch (Exception ex)
            {
                Log.Write("WebView2 env failed: " + ex);
                MessageBox.Show("无法初始化 WebView2 运行时：" + ex.Message, "天气壁纸", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ExitThread();
                return;
            }
            MigrateOldCache();
            SyncCities(false);   // 先读各城市的缓存：壁纸页面一就绪就有天气可显示
            CleanCache();
            BuildWalls();
            watchTimer.Start();
            weatherTimer.Start();
            RefreshDue();
            // 启动时 Load 之后文件可能变过（Load 里迁移写盘被外部修改拒掉、Init 期间被改了）、或者 Load 那会儿文件被占着读不出来（按默认值起来的）：
            // 主动对一次（文件就是上次读写的内容的话 ReloadSettings 什么都不做）
            settingsReload.Start();
            // 壁纸起来后再在后台预热托盘面板，不和壁纸抢启动时间
            var warm = new WinTimer { Interval = 4000 };
            warm.Tick += (s, e) => { warm.Stop(); warm.Dispose(); ApplyFlyoutMode(); };
            warm.Start();
        }

        // ---------------------------------------------------------------- 壁纸窗口
        DateTime rebuildFirst, rebuildDue;   // 这次排着的重建第一次被要求的时间、现在排定的到点时间

        /// <summary>
        /// 去抖：每来一次都从现在重新等 ms（接扩展坞时几秒内连着来好几次显示器变化，要等编号、硬件 ID 都稳定了再建，只建一次）；
        /// 只往后推、不往前挪（别的调用方要求的等待短，不能把显示器变化排好的较长等待提前）；
        /// 但从第一次要求算起最多等 5 秒，事件一直不停也不会被无限期推迟。
        /// </summary>
        void ScheduleRebuild(int ms)
        {
            var now = DateTime.UtcNow;
            if (!rebuildTimer.Enabled) rebuildFirst = rebuildDue = now;
            var at = now.AddMilliseconds(ms);
            if (at < rebuildDue) at = rebuildDue;
            var cap = rebuildFirst.AddSeconds(5);
            if (at > cap) at = cap;
            rebuildDue = at;
            rebuildTimer.Stop();
            rebuildTimer.Interval = Math.Max(1, (int)(at - now).TotalMilliseconds);
            rebuildTimer.Start();
        }

        /// <summary>立即按当前排定的方式重建（消费掉 rebuildRestore）。排着的重建到点、托盘「重建壁纸」共用。</summary>
        void RebuildNow()
        {
            rebuildTimer.Stop();
            bool r = rebuildRestore;
            rebuildRestore = false;
            BuildWalls(r);
        }

        /// <summary>重建所有壁纸窗口。restore = 先让系统重绘原壁纸（旧桌面结构下，窗口销毁后那块屏会停在最后一帧）。</summary>
        bool buildingWalls;
        void BuildWalls(bool restore = false)
        {
            // 防重入：建窗口期间会走到布局、同步窗口的代码（HealAllScreens、ConfigureOverlay），那里再触发整体重建的话内层直接跳过，外层接着建完
            if (env == null || buildingWalls) return;
            buildingWalls = true;
            try
            {
                foreach (var w in walls) w.Dispose();
                walls.Clear();
                if (restore) DesktopHost.RestoreSystemWallpaper();
                if (!DesktopHost.Prepare())
                {
                    Log.Write("desktop not ready, retry");
                    ScheduleRebuild(3000);
                    return;
                }
                HealAllScreens();
                var layered = settings.Layered;
                wallsLayered = layered == "on" || (layered == "auto" && DesktopHost.Raised);
                foreach (var s in WallScreens()) walls.Add(NewWall(s));
                Log.Write("walls: " + (walls.Count == 0 ? "none" : string.Join(", ", walls.Select(w => w.DeviceName))));
                DesktopHost.FixZOrder(walls.Select(w => w.Handle).ToList());
                ConfigureOverlay();
                UpdateBackdrops(false);
            }
            finally { buildingWalls = false; }
        }

        /// <summary>
        /// 卡片按显示器硬件 ID 找屏：\\.\DISPLAYn 在重插扩展坞、更新驱动后可能换号，硬件 ID 还在就把卡片的 Screen 改成新编号；
        /// 硬件 ID 找不到的（没记过、或换了接口 / 驱动后 ID 变了）按设备名找：
        ///  · 这个编号现在那块显示器有别的卡片按硬件 ID 认领、而且它们原来记的是别的编号 → 是另一块屏换号占了这个编号，
        ///    当作"屏没接上"：Screen 记成 CardSpec.AwayPrefix + 原编号，
        ///    不显示、不去挤那块屏上的卡片，原来那块接回来时按硬件 ID 找回；
        ///    没记过硬件 ID 的卡片不这样做（让出去就再也认不回来了）：和下一条一样，留在这个编号上；
        ///  · 没人认领 → 就是这块屏（多半是同一块显示器换了 ID）：改记它现在的硬件 ID，免得之后新卡片记了新 ID、把这些卡片误判成"没接上"。
        /// 已经让出去的卡片，原编号上的显示器后来没有别的卡片认领了（占着它的那块屏拔掉了、那张卡片删了或挪走了）：回到原编号、改记现在的 ID。
        /// all = 所有卡片（判断硬件 ID 归谁，按改之前的样子算）。返回 true = 改了。
        /// </summary>
        static bool HealScreens(IEnumerable<CardSpec> cards, IList<CardSpec> all)
        {
            var ids = Screen.AllScreens.Select(s => new { s, id = CardLayout.MonitorId(s) }).ToList();
            // 先记下"哪些卡片按硬件 ID 找得到自己的屏"（连同它原来记的编号）：本轮改记的新 ID 不算认领（同一块屏上的几张卡片都要改记，不能互相判成别人的屏）；
            // 原来记的就是这个编号的也不算（同一个编号上一直是这块屏，只是这张卡片记的 ID 旧了）
            var owners = all.Where(k => k.Monitor != null && ids.Any(x => x.id == k.Monitor)).Select(k => new { k.Id, k.Monitor, k.Screen }).ToList();
            // 还住着没让出去的卡片的编号：away 卡片不收回这样的编号——上面可能摆着按设备名住下的卡片（没记过硬件 ID），
            // 收回会把两张卡片叠到一块屏上，还把硬件 ID 改记成现在这块屏的（原来那块接回来就再也找不回它了）。
            // 和 owners 一样按改之前的样子算：两张一起让出去的卡片要还能一起回来
            var residents = new HashSet<string>(all.Where(k => !k.Screen.StartsWith(CardSpec.AwayPrefix)).Select(k => k.Screen));
            bool changed = false;
            foreach (var c in cards)
            {
                if (c.Screen == CardSpec.PrimaryScreen) continue;
                var hw = c.Monitor == null ? null : ids.FirstOrDefault(x => x.id == c.Monitor);
                if (hw != null)
                {
                    if (hw.s.DeviceName == c.Screen) continue;
                    Log.Write("card " + c.Id + ": screen " + c.Screen + " -> " + hw.s.DeviceName + " (same monitor)");
                    c.Screen = hw.s.DeviceName;
                    changed = true;
                    continue;
                }
                bool away = c.Screen.StartsWith(CardSpec.AwayPrefix);
                var num = away ? c.Screen.Substring(CardSpec.AwayPrefix.Length) : c.Screen;
                var cur = ids.FirstOrDefault(x => x.s.DeviceName == num);
                if (cur == null) continue;   // 这个编号现在没有屏：等它的屏回来。屏在但取不到硬件 ID（cur.id == null，部分虚拟显示器）：照常处理，按设备名留在它上面
                if (away)
                {
                    // 还被别的卡片占着就不能收回：按硬件 ID 认领它的；还有别的卡片住在这个编号上（含按设备名住下、没记过硬件 ID 的）也算被占
                    if (owners.Any(o => o.Id != c.Id && o.Monitor == cur.id) || residents.Contains(num)) continue;
                    Log.Write("card " + c.Id + ": " + num + " is no longer taken by another card's monitor -> back");
                    c.Screen = num;
                    if (cur.id != null) c.Monitor = cur.id;   // 取不到现在的 ID：留着原来记的，别抹掉（那块屏以后还能按它找回）
                }
                else if (c.Monitor != null && owners.Any(o => o.Id != c.Id && o.Monitor == cur.id && o.Screen != c.Screen))
                {
                    Log.Write("card " + c.Id + ": monitor gone, " + c.Screen + " is now another monitor -> away");
                    c.Screen = CardSpec.AwayPrefix + c.Screen;
                }
                else
                {
                    // 本来就是：别每轮都当成改了去写盘。
                    // 现在取不到这块屏的 ID（部分虚拟显示器、插拔扩展坞 / 更新驱动的那一下）：按设备名留在它上面，但原来记的 ID 不动——
                    // 一次偶发的读取失败不能永久抹掉它，之后换号时还要靠它找回原来那块屏
                    if (c.Monitor == cur.id || cur.id == null) continue;
                    if (c.Monitor != null) Log.Write("card " + c.Id + ": " + c.Screen + " monitor id changed, adopt new id");
                    c.Monitor = cur.id;
                }
                changed = true;
            }
            return changed;
        }

        void HealAllScreens()
        {
            // 编辑中的那张也要改，并告诉页面（只改屏，不重新初始化：正在输入、搜索的内容不丢）。
            // 页面在收到之前发出的编辑还带着旧编号：靠 cardGen 认出来，屏以宿主为准（见 ParseCard）
            if (draft != null && HealScreens(new[] { draft }, settings.Cards))
            {
                cardGen++;
                cardWin?.Post(Js.Serialize(new { t = "screen", id = draft.Id, screen = draft.Screen, gen = cardGen }));
            }
            if (!HealScreens(settings.Cards, settings.Cards)) return;
            Settings.NormalizeScenes(settings.Cards);
            settings.Save();   // 文件刚被外部改过的话 Save 不写、改为重新加载（重新加载后会再整理一遍）
        }

        bool wallsLayered, syncingWalls;   // wallsLayered：BuildWalls 时定下的分层方式，之后单独加的窗口沿用
        bool layoutQueued;                 // 已经排了一次"回到消息循环再重排"，还没执行
        bool rebuildRestore;               // 排着的那次重建要先让系统重绘原壁纸（旧桌面结构下去掉了某块屏的壁纸窗口）
        string lastWorkAreas;              // 上次系统设置变化时各屏的工作区：没变就不重排

        WallpaperWindow NewWall(Screen s)
        {
            var w = new WallpaperWindow(s, env, wallsLayered, settings.DevTools);
            // 先按卡片定好画不画天气（UpdateLayout 里 PickScene 再按排好的位置定一次）：不然新窗口先当成不画天气，
            // 重建时任务栏效果先关后开、weatherMoved 成立又多生成一遍底图
            w.HasWeather = EffectiveCards().Any(c => !c.IsCustom() && SameScreen(c.Screen, s));
            w.OnReady += OnWallReady;
            w.OnString += OnWallString;
            w.OnJson += OnWallJson;
            return w;
        }

        /// <summary>
        /// 放了卡片（含只显示天气画面的卡片、设置窗口里正在编辑的那张；隐藏（Off）的不算）的屏要壁纸窗口，其余屏只有系统壁纸。
        /// 天气只跟着天气卡片走：只放了自定义卡片的屏也有窗口（卡片画在里面），但不画天气，只垫系统壁纸（见 PickScene）。
        /// </summary>
        List<Screen> WallScreens()
        {
            var cards = EffectiveCards();
            return Screen.AllScreens.Where(s => cards.Any(c => SameScreen(c.Screen, s))).ToList();
        }

        /// <summary>
        /// 卡片换了屏、增删了卡片：只增减对应屏的壁纸窗口，其他屏的窗口不动（不重建、不闪）。
        /// 有增减返回 true（已经重新布局过）；屏幕变化等需要整体重建的情况交给 BuildWalls。
        /// </summary>
        bool SyncWalls()
        {
            // buildingWalls：BuildWalls 执行中（它里面的 ConfigureOverlay → UpdateLayout 会走到这里）——
            // 此时 walls 已被清空，这里把窗口建出来后外层又会建一遍，每块屏叠两个壁纸窗口；外层自己会建好，直接跳过
            if (env == null || syncingWalls || buildingWalls) return false;
            var want = WallScreens();
            var gone = walls.Where(w => !want.Any(s => s.DeviceName == w.DeviceName && s.Bounds == w.Bounds)).ToList();
            var add = want.Where(s => !walls.Any(w => w.DeviceName == s.DeviceName && w.Bounds == s.Bounds)).ToList();
            if (gone.Count == 0 && add.Count == 0) return false;
            // 旧桌面结构（壁纸窗口挂在 WorkerW 下）：单独销毁一块屏的窗口后 WorkerW 不会重画系统壁纸，那块屏会停在最后一帧。
            // 这种情况整体重建，中间让系统重绘一次原壁纸（24H2 的 raised 结构下系统壁纸是单独一层，窗口没了自然露出来）。
            // 稍等一下再重建：在卡片设置的屏幕下拉框里连着切换时只重建一次（等待期间不再增减窗口，那块屏暂时只显示天气画面）
            if (gone.Count > 0 && !DesktopHost.Raised) { rebuildRestore = true; ScheduleRebuild(1200); return false; }
            if (rebuildTimer.Enabled) return false;   // 已经排着一次重建：窗口的增减交给它（要撤掉的屏上面已把 restore 记上）
            // 桌面父窗口没了（资源管理器重启、WorkerW 被重建）：新窗口挂不上去，马上整体重建。
            // 放在上一行之后：排着的重建（如显示器变化的去抖，等编号稳定）不能被这里提前顶掉
            if (add.Count > 0 && (DesktopHost.Parent == IntPtr.Zero || !Native.IsWindow(DesktopHost.Parent))) { RebuildNow(); return true; }
            syncingWalls = true;
            try
            {
                foreach (var w in gone) { walls.Remove(w); w.Dispose(); }
                foreach (var s in add) walls.Add(NewWall(s));
                Log.Write("walls: " + (walls.Count == 0 ? "none" : string.Join(", ", walls.Select(w => w.DeviceName)))
                    + (add.Count > 0 ? " +" + string.Join(",", add.Select(s => s.DeviceName)) : "") + (gone.Count > 0 ? " -" + string.Join(",", gone.Select(w => w.DeviceName)) : ""));
                DesktopHost.FixZOrder(walls.Select(w => w.Handle).ToList());
                ConfigureOverlay();   // 里面会 UpdateLayout（syncingWalls 挡住重入）
                if (add.Count > 0) UpdateBackdrops(false);   // 新窗口要底图（极简模式 / 壁纸叠加）；已有屏的底图有缓存文件，不会重新生成
            }
            finally { syncingWalls = false; }
            return true;
        }

        /// <summary>上报场景（任务栏 / 屋檐雨雪、托盘提示）的那块：优先画着天气的屏，其中主屏优先，否则用第一块。</summary>
        WallpaperWindow MainWall => walls.FirstOrDefault(w => w.Primary && w.HasWeather) ?? walls.FirstOrDefault(w => w.HasWeather)
            ?? walls.FirstOrDefault(w => w.Primary) ?? walls.FirstOrDefault();

        static Rectangle? TaskbarRect(out IntPtr hwnd)
        {
            hwnd = Native.FindWindow("Shell_TrayWnd", null);
            var abd = new Native.APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.APPBARDATA)) };
            bool autoHide = (Native.SHAppBarMessage(4 /*ABM_GETSTATE*/, ref abd).ToInt64() & 1) != 0;
            if (Native.SHAppBarMessage(5 /*ABM_GETTASKBARPOS*/, ref abd) == IntPtr.Zero) return null;
            if (autoHide || abd.uEdge != 3 /*ABE_BOTTOM*/) return null;
            return Rectangle.FromLTRB(abd.rc.Left, abd.rc.Top, abd.rc.Right, abd.rc.Bottom);
        }

        void ConfigureOverlay()
        {
            IntPtr tbh;
            var tb = TaskbarRect(out tbh);
            ConfigureTaskbarFx(tb, tbh);
            UpdateLayout(tb, true);
        }

        /// <summary>任务栏效果只画在主屏任务栏上：主屏没在画天气（没有卡片，或只有自定义卡片）就不画。</summary>
        void ConfigureTaskbarFx(Rectangle? tb, IntPtr tbh)
        {
            float scale = tbh != IntPtr.Zero ? Native.GetDpiForWindow(tbh) / 96f : 1f;
            if (scale <= 0) scale = 1;
            overlay.Configure(tb, Screen.PrimaryScreen.Bounds, scale, settings.TaskbarFx && walls.Any(w => w.Primary && w.HasWeather));
        }

        // ---------------------------------------------------------------- 卡片布局与每块屏的画面城市
        List<CardPlace> places = new List<CardPlace>();

        /// <summary>这块屏在页面里的尺寸：页面报过视口就按它换算（最准），否则按显示器 DPI。</summary>
        ScreenArea AreaOf(Screen s)
        {
            var w = walls.FirstOrDefault(x => x.DeviceName == s.DeviceName && x.Bounds == s.Bounds);
            float k = w != null && w.CssWidth > 0 ? s.Bounds.Width / w.CssWidth : Native.ScaleOf(s);
            return ScreenArea.Of(s, k);
        }

        static bool SameScreen(string id, Screen s)
        {
            var r = CardLayout.Resolve(id);
            return r != null && s != null && r.DeviceName == s.DeviceName;
        }

        IEnumerable<CardPlace> PlacesOn(WallpaperWindow w) => places.Where(p => p.Host != null && p.Host.DeviceName == w.DeviceName && p.Rect.HasValue);

        /// <summary>
        /// 每块屏只能画一种全屏天气，画面跟着哪个城市：这块屏上勾了"画面跟随"的天气卡片 → 这块屏的第一张天气卡片。
        /// 只放了自定义卡片的屏不画天气（自定义卡片没有城市，也不该把天气带过来）：SceneKey = ""、HasWeather = false，页面只垫系统壁纸。
        /// 这块屏上一张卡片都没有（卡片刚被删光、壁纸窗口还在等着销毁——旧桌面结构下延迟重建）：保持原样，别闪一下。
        /// </summary>
        void PickScene(WallpaperWindow w)
        {
            var mine = places.Where(p => p.Host != null && p.Host.DeviceName == w.DeviceName).ToList();
            if (mine.Count == 0) return;
            var on = mine.Where(p => !p.Card.IsCustom()).ToList();
            var pick = on.FirstOrDefault(p => p.Card.Scene) ?? on.FirstOrDefault();
            w.HasWeather = pick != null;
            w.SceneKey = pick != null ? pick.Card.LocKey() : "";
        }

        /// <summary>重新排卡片、定每块屏的画面城市，发给各页面（卡片设置窗口开着时一并更新它的"放不放得下"）。</summary>
        /// <param name="tbKnown">true = 调用方刚查过任务栏，tb 就是结果（null = 没有底部任务栏），别再查一遍（两次之间任务栏弹出 / 收起会前后不一致）</param>
        void UpdateLayout(Rectangle? tb = null, bool tbKnown = false)
        {
            if (SyncWalls()) return;   // 卡片换了屏 / 增删：先增减壁纸窗口（里面会再调到这里）
            // 删掉的卡片量过的高度不用再记着（正在编辑、还没保存的那张不算删掉）
            foreach (var k in CustomCards.Fit.Keys.Where(k => k != draft?.Id && !settings.Cards.Any(c => c.Id == k)).ToList()) CustomCards.Fit.Remove(k);
            if (!tbKnown) { IntPtr h; tb = TaskbarRect(out h); }
            var shown = walls.Select(w => Screen.AllScreens.FirstOrDefault(s => s.DeviceName == w.DeviceName)).Where(s => s != null).ToList();
            places = CardLayout.Arrange(EffectiveCards(), shown, AreaOf);
            // 哪块屏都没在运行的卡片（挪到了放不下的屏、原来那块屏的窗口跟着撤掉了——没有页面再报 gone）：记着的错误清掉，卡片设置里别再显示
            foreach (var id in cardErrs.Keys.Where(id => !places.Any(p => p.Card.Id == id && p.Host != null && p.Rect.HasValue)).ToList()) cardErrs.Remove(id);
            var hadWeather = walls.Select(w => w.HasWeather).ToList();
            foreach (var w in walls) PickScene(w);   // 先全部定好：LayoutJson 里的 primary（MainWall）要看所有屏画不画天气
            // 有屏开始 / 不再画天气（天气卡片挪走了、只剩自定义卡片，或者反过来）：任务栏效果跟着主屏走，底图换成要 / 不要系统壁纸
            bool weatherMoved = !walls.Select(w => w.HasWeather).SequenceEqual(hadWeather);
            if (weatherMoved) ConfigureTaskbarFx(tb, Native.FindWindow("Shell_TrayWnd", null));
            foreach (var w in walls)
            {
                var j = LayoutJson(w, tb);
                if (j == w.LayoutSent) continue;   // 这块屏的布局没变（如在卡片设置里改的是别的屏的卡片）：页面不用重排一遍
                w.LayoutSent = j;
                w.Post(j);
            }
            if (weatherMoved) UpdateBackdrops(false);   // 在 layout 之后：页面先知道这块屏画不画天气，再收底图（撤底图要等天气画面淡入完）
            FxStep();
            PostFx();
            cardWin?.Post(CardFitJson());
        }

        string LayoutJson(WallpaperWindow w, Rectangle? tb)
        {
            double ground = 0;
            bool hasTb = tb.HasValue && w.Bounds.Contains(new Point(tb.Value.X + tb.Value.Width / 2, tb.Value.Y + tb.Value.Height / 2));
            if (hasTb) ground = (w.Bounds.Bottom - tb.Value.Top) / (double)w.Bounds.Height;
            bool main = w == MainWall;
            // 天气卡片带城市键和显示内容；自定义卡片带脚本名和脚本的修改时间（0 = 文件不在，页面照样去要、拿到"找不到脚本"的错误）
            var cards = string.Join(",", PlacesOn(w).Select(p => "{\"id\":" + WeatherService.JsonStr(p.Card.Id)
                + (p.Card.IsCustom() ? ",\"script\":" + WeatherService.JsonStr(p.Card.Script) + ",\"v\":" + ScriptVersion(p.Card.Script)
                    + (CustomCards.IsAuto(p.Card.Script) ? ",\"auto\":true" : "")   // 页面不用它；宿主晚一步才读到脚本写了 autoHeight（文件被占着）时，靠它让 layout 变一下、页面再报一次量出的高度
                    : ",\"key\":" + WeatherService.JsonStr(p.Card.LocKey()) + ",\"show\":" + WeatherService.JsonStr(p.Card.Show))
                + ",\"x\":" + p.Rect.Value.X.ToString("F1", Inv) + ",\"y\":" + p.Rect.Value.Y.ToString("F1", Inv)
                + ",\"h\":" + p.Rect.Value.Height.ToString("F1", Inv) + "}"));
            return "{\"t\":\"layout\",\"primary\":" + B(main) + ",\"ground\":" + ground.ToString("F4", Inv)
                + ",\"overlay\":" + B(hasTb && settings.TaskbarFx && w.Primary && w.HasWeather) + ",\"panel\":" + B(settings.ShowPanel)
                + ",\"noWeather\":" + B(!w.HasWeather)   // 只有自定义卡片的屏：不画天气，只垫系统壁纸（每次都带上：页面是 Object.assign 合并的）
                + ",\"fps\":" + w.Fps + ",\"lowmem\":" + B(settings.LowMemory) + ",\"minimal\":" + B(settings.Minimal)
                + ",\"overlayWall\":" + B(settings.OverlayWall) + ",\"skyOpacity\":" + settings.SkyOpacityValue().ToString("F3", Inv)
                + ",\"cloudAlpha\":" + settings.CloudAlphaValue().ToString("F3", Inv) + ",\"festive\":" + B(settings.Festive)
                + ",\"scene\":" + WeatherService.JsonStr(w.SceneKey) + ",\"screen\":" + WeatherService.JsonStr(w.DeviceName) + ",\"cards\":[" + cards + "]"
                + ",\"effects\":" + effectsJson
                + ",\"defKey\":" + WeatherService.JsonStr(DefaultKey())   // 自定义卡片的 api.weather 用默认卡片城市的天气
                + ",\"keys\":[" + string.Join(",", cities.Keys.Select(WeatherService.JsonStr)) + "]}";   // 还在用的城市：页面据此丢掉不再用的天气数据
        }
        static string B(bool b) => b ? "true" : "false";
        /// <summary>列表里的卡片脚本（文件名不分大小写），不在列表里（找不到、隐藏的、排在 50 个之后的）是 null。版本号、能不能运行、新增时的默认名称和高度都按它。</summary>
        CustomCards.Script FindScript(string name) => cardScripts.FirstOrDefault(s => string.Equals(s.File, name, StringComparison.OrdinalIgnoreCase));
        // 不在列表里是 0：页面来要脚本时回不运行的原因（csrc）；列出来了版本号一变，页面就重新加载
        long ScriptVersion(string name) => FindScript(name)?.V ?? 0;

        void OnWallReady(WallpaperWindow w)
        {
            IntPtr h;
            w.LayoutSent = LayoutJson(w, TaskbarRect(out h));   // 页面（重新）就绪：不管之前发没发过都发一次
            w.Post(w.LayoutSent);
            foreach (var c in cities.Values) if (c.Payload != null) w.Post(c.Payload);
            w.Post(PreviewJson());
            w.FxSent = FxKindOf(w);
            w.Post(FxJson(w.FxSent));
            if (w.BackdropJson != null) w.Post(w.BackdropJson);
        }

        void OnWallString(WallpaperWindow w, string s)
        {
            if (s.Length > 0 && s[0] == 'H') { if (w.Primary) overlay.AddHits(s.Substring(1)); }
            else if (s.StartsWith("log:")) Log.Write("js: " + s.Substring(4));
        }

        void OnWallJson(WallpaperWindow w, string json)
        {
            try
            {
                var d = Js.Deserialize<Dictionary<string, object>>(json);
                if (d == null || !d.ContainsKey("t")) return;
                Func<string, double> num = k => d.ContainsKey(k) && d[k] != null ? Convert.ToDouble(d[k], Inv) : 0;
                Func<string, string> str = k => d.ContainsKey(k) && d[k] != null ? Convert.ToString(d[k], Inv) : null;
                var t = d["t"] as string;
                // 自定义卡片：要脚本、读 cards 文件夹里的文件、联网、报告出错 / 恢复（见 web/cards.js）
                switch (t)
                {
                    case "csrc": { var f = str("f"); w.Post(CustomCards.ReadScript(f, FindScript(f) != null)); return; }
                    case "cfile": w.Post("{\"t\":\"cfile\",\"rid\":" + (long)num("rid") + "," + CustomCards.ReadFile(str("path"), str("kind")) + "}"); return;
                    case "cget": CardFetch(w, (long)num("rid"), str("id"), str("kind"), str("url"), str("method"), str("body"), d.ContainsKey("headers") ? d["headers"] as Dictionary<string, object> : null, d.ContainsKey("fresh") && true.Equals(d["fresh"])); return;
                    case "cstate": CardState(w, str("id"), str("script"), str("err"), d.ContainsKey("gone")); return;
                    // 高度随内容变的卡片画好了：页面量出来的高度（h，0 = 脚本不再是自动高度，不用记了）
                    case "cfit": CardFit(w, str("id"), num("h")); return;
                    // 点了「刷新」的那一轮画完了（ok = 画好了，err = 出错的原因）：告诉卡片设置窗口，按钮那里显示结果
                    case "crefreshed": cardWin?.Post(Js.Serialize(new { t = "refreshed", id = str("id"), err = str("err") ?? "" })); return;
                }
                if (t == "viewport")
                {
                    // 页面的实际视口：卡片位置按它换算，和页面的 CSS 像素严格一致
                    float cw = (float)num("w");
                    if (cw > 0 && Math.Abs(cw - w.CssWidth) > 0.5f)
                    {
                        w.CssWidth = cw;
                        QueueLayout();
                    }
                    return;
                }
                if (t != "scene" || w != MainWall) return;
                overlay.SetScene(num("rain"), num("snow"), num("wind"));
                eaves.SetScene(num("rain"), num("snow"), num("wind"));
                sceneText = d.ContainsKey("text") ? Convert.ToString(d["text"]) : "";
                var tip = "天气壁纸" + (sceneText != "" ? "\n" + sceneText : "");
                tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            }
            catch (Exception ex) { Log.Write("scene parse: " + ex.Message); }
        }

        /// <summary>
        /// 放回消息循环里再重排：重排可能增减、重建壁纸窗口，不能在发消息的这个窗口自己的 WebView2 事件回调里把它销毁。
        /// 几块屏的页面差不多同时报（视口、卡片高度，重建后）：只排一次。
        /// 同步上下文不在（理论上的防御分支）：宁可跳过这次，也不能在这里同步重排——
        /// 报来的值已经记下，之后任意一次触发重排的事件（设置变化、卡片编辑、重建）会把它用上
        /// </summary>
        void QueueLayout()
        {
            var ui = SynchronizationContext.Current;
            if (ui == null || layoutQueued) return;
            layoutQueued = true;
            ui.Post(_ => { layoutQueued = false; if (quitStage == 0) UpdateLayout(); }, null);
        }

        /// <summary>
        /// 页面报告自动高度（autoHeight）卡片的内容高度：记下来，按它重排。只认真在这块屏上运行的、脚本是自动高度的卡片；
        /// 一样的不重排（页面每次画完都会量，还会在版面和它量的对不上时再报一次）。h 小于 1 = 清掉。
        /// </summary>
        void CardFit(WallpaperWindow w, string id, double h)
        {
            if (string.IsNullOrEmpty(id)) return;
            var p = places.FirstOrDefault(x => x.Card.Id == id && x.Host != null && x.Host.DeviceName == w.DeviceName);
            if (h < 1)
            {
                if (CustomCards.Fit.Remove(id)) QueueLayout();
                return;
            }
            if (p == null || !CustomCards.IsAuto(p.Card.Script)) return;
            int v = CustomCards.ClampHeight(h), old;
            if (CustomCards.Fit.TryGetValue(id, out old) && old == v) return;
            CustomCards.Fit[id] = v;
            QueueLayout();
        }

        void Broadcast(string json) { foreach (var w in walls) w.Post(json); flyout?.Post(json); }

        /// <summary>自定义卡片的 api.fetchText / fetchJson / fetchImage：宿主替它发请求（限制见 CustomCards.Fetch），结果按 rid 发回那个页面。</summary>
        async void CardFetch(WallpaperWindow w, long rid, string id, string kind, string url, string method, string body, Dictionary<string, object> headers, bool fresh)
        {
            string r;
            try { r = await CustomCards.Fetch(id ?? "", kind == "image" ? "image" : "text", url, method, body, headers, fresh); }
            catch (Exception ex) { r = "\"ok\":false,\"err\":" + WeatherService.JsonStr(ex.Message); }
            if (quitStage == 0 && walls.Contains(w)) w.Post("{\"t\":\"cget\",\"rid\":" + rid + "," + r + "}");   // 这期间页面换掉了：旧请求的回复页面自己会丢掉
        }

        /// <summary>
        /// 页面报告自定义卡片出错 / 恢复（err 为空 = 正常）：记日志，告诉卡片设置窗口（「预览」那里显示原因）。
        /// gone = 这张卡片不在这个页面上运行了（删了、隐藏了、挪到别的屏）：只清掉记着的错误，不记日志。
        /// </summary>
        void CardState(WallpaperWindow w, string id, string script, string err, bool gone)
        {
            if (string.IsNullOrEmpty(id)) return;
            // 挪到了别的屏、在那边摆上了：那边的页面会报它自己的状态。那边放不下（Rect 是 null）就没有页面运行它，这里得把旧的错误清掉
            if (gone && places.Any(p => p.Card.Id == id && p.Host != null && p.Host.DeviceName != w.DeviceName && p.Rect.HasValue)) return;
            err = gone || string.IsNullOrEmpty(err) ? null : err.Length > 600 ? err.Substring(0, 600) + "…" : err;
            string old;
            cardErrs.TryGetValue(id, out old);
            if (old == err) return;
            if (err == null) cardErrs.Remove(id); else cardErrs[id] = err;
            if (gone) { cardWin?.Post(Js.Serialize(new { t = "cerr", id, err = "" })); return; }
            // 日志：同一张卡片的同一句话（某个错误 / 恢复正常）10 分钟内只记一次（refresh 很短、时好时坏的卡片别把日志刷满）
            var now = DateTime.UtcNow;
            var key = id + "\n" + (err ?? "ok");
            DateTime at;
            if (!(cardLogged.TryGetValue(key, out at) && (now - at).TotalMinutes < 10))
            {
                if (cardLogged.Count > 200) foreach (var k in cardLogged.Where(e => (now - e.Value).TotalMinutes >= 10).Select(e => e.Key).ToList()) cardLogged.Remove(k);
                cardLogged[key] = now;
                Log.Write("card " + script + " [" + id + "]: " + (err ?? "ok"));
            }
            cardWin?.Post(Js.Serialize(new { t = "cerr", id, err = err ?? "" }));
        }

        FlyoutWindow flyout;
        FlyoutWindow Flyout()
        {
            if (env == null) return null;
            if (flyout == null)
            {
                flyout = new FlyoutWindow(env) { OnCommand = FlyoutCommand };
                var dc = DefaultCity();
                if (dc?.Payload != null) flyout.Post(dc.Payload);
                flyout.Post(PreviewJson());
                flyout.Post(UpdateJson());
            }
            flyout.KeepAlive = !settings.LowMemory;
            return flyout;
        }

        void ToggleFlyout() => Flyout()?.Toggle();

        /// <summary>默认模式下面板常驻并在启动后预热（点托盘立刻弹出）；省内存模式下用时再建、收起 1 分钟后释放。</summary>
        void ApplyFlyoutMode()
        {
            if (settings.LowMemory) { if (flyout != null) flyout.KeepAlive = false; }
            else Flyout()?.Warm();
        }

        void FlyoutCommand(string c)
        {
            switch (c)
            {
                case "refresh": RefreshAll(true); break;
                case "card": case "city": flyout.HideFlyout(); OpenCardSettings(CardSpec.DefaultId); break;   // 托盘面板显示的就是默认卡片
                case "hide": flyout.HideFlyout(); break;
                case "update": flyout.HideFlyout(); if (newer != null) PromptUpdate(newer); break;
                case "menu":
                    flyout.HideFlyout();
                    // NotifyIcon 没有公开的弹菜单方法，用它内部的 ShowContextMenu（和右键效果一致）
                    typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(tray, null);
                    break;
            }
        }

        // ---------------------------------------------------------------- 遮挡 / 全屏检测
        void Watch()
        {
            if (walls.Count > 0)
            {
                // 体检：系统会在锁屏 / 息屏 / 解锁后悄悄重建桌面层（WorkerW），把系统壁纸压到我们上面，
                // 没有任何广播可听，只能自己盯。能修层级的当场修，结构坏了才整体重建。
                var st = DesktopHost.CheckWalls(walls.Select(w => w.Handle).ToArray());
                if (st == DesktopState.Invalid)
                {
                    // 只在没排上时才调度：本方法 0.7 秒一次，每次都重置会把重建无限期顶掉
                    if (!rebuildTimer.Enabled) ScheduleRebuild(1500);
                    return;
                }
            }
            var tops = new List<IntPtr>();
            uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsCloaked(h)) return true;
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid == myPid) return true;
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
                // 点击穿透的全屏叠加层（录屏/游戏覆盖层等）不算遮挡
                if ((ex & Native.WS_EX_TRANSPARENT) != 0 && (ex & Native.WS_EX_LAYERED) != 0) return true;
                var cls = Native.ClassOf(h);
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return true;
                tops.Add(h);
                return true;
            }, IntPtr.Zero);

            perf.Sample();
            var fg = Native.GetForegroundWindow();
            bool fullscreenOnPrimary = false;
            var rects = tops.Select(h => new { h, r = Native.VisibleRect(h) }).Where(x => x.r.Width > 0 && x.r.Height > 0).ToList();

            // 全局帧率上限：电池 / 系统繁忙
            int cap = settings.Fps;
            var reasons = new List<string>();
            if (perf.OnBattery) { cap = Math.Min(cap, 20); reasons.Add("电池供电"); }
            if (perf.Busy) { cap = Math.Min(cap, 12); reasons.Add($"系统繁忙 CPU {perf.Cpu:P0} 内存 {perf.MemLoad}%"); }

            int running = 0, minFps = int.MaxValue;
            foreach (var w in walls)
            {
                var scr = Screen.AllScreens.FirstOrDefault(s => s.Bounds == w.Bounds);
                if (scr == null) { w.SetPerf(true, w.Fps); w.Shown = false; continue; }
                // ↑ 这块屏已经不在了（拔掉 / 卡片删光后等销毁的旧窗口）：当作不可见，别拿主屏的遮挡情况喂它；
                //   只暂停，帧率保持原值（填 0 会被 layout 带出去，窗口留下来时页面一直按最低帧率画）
                foreach (var x in rects)
                    if (x.h == fg && x.r.Contains(scr.Bounds) && scr.Primary) fullscreenOnPrimary = true;

                // 网格采样估算桌面露出比例
                const int GX = 32, GY = 18;
                int visible = 0;
                var wa = scr.WorkingArea;
                for (int iy = 0; iy < GY; iy++)
                    for (int ix = 0; ix < GX; ix++)
                    {
                        var p = new Point(wa.X + (2 * ix + 1) * wa.Width / (2 * GX), wa.Y + (2 * iy + 1) * wa.Height / (2 * GY));
                        bool hit = false;
                        foreach (var x in rects) if (x.r.Contains(p)) { hit = true; break; }
                        if (!hit) visible++;
                    }
                double vis = visible / (double)(GX * GY);
                bool pause = locked || vis < 0.03;
                int fps = cap;
                if (!pause && vis < 0.35) fps = Math.Min(fps, 15);
                w.SetPerf(pause, fps);
                w.Shown = !pause;
                if (w.Primary) overlay.SetCovered(vis < 0.03);
                if (!pause && w.HasWeather) { running++; minFps = Math.Min(minFps, fps); }   // 不画天气的屏（只有自定义卡片）不算帧率
            }
            overlay.SetSuppressed(fullscreenOnPrimary || locked);
            overlay.SetFps(perf.Busy ? 20 : perf.OnBattery ? 30 : settings.Fps);

            // 屋檐：只挑"大小适中"的普通窗口——最大化 / 全屏 / 太小 / 几乎铺满屏幕的都跳过
            eaves.SetEnabled(settings.WindowFx);
            eaves.SetSuppressed(locked || perf.Busy);
            eaves.SetFps(perf.OnBattery ? 20 : 30);
            if (settings.WindowFx && !locked && !perf.Busy)
            {
                var shown = walls.Where(w => w.HasWeather).Select(w => w.Bounds).ToList();   // 只有自定义卡片的屏不画天气，那里的窗口也不溅雨雪
                var picked = new List<IntPtr>();
                for (int i = 0; i < rects.Count && picked.Count < 6; i++)
                {
                    var x = rects[i];
                    if (Native.IsZoomed(x.h)) continue;
                    long st = Native.GetWindowLongPtr(x.h, Native.GWL_STYLE).ToInt64();
                    long ex = Native.GetWindowLongPtr(x.h, Native.GWL_EXSTYLE).ToInt64();
                    if ((st & Native.WS_CHILD) != 0 || (st & (Native.WS_CAPTION | Native.WS_THICKFRAME)) == 0 || (ex & Native.WS_EX_TOPMOST) != 0) continue;
                    var scr = Screen.FromRectangle(x.r);
                    if (!shown.Contains(scr.Bounds)) continue;
                    var sb = scr.Bounds;
                    if (x.r.Contains(sb) || x.r.Contains(scr.WorkingArea)) continue;            // 全屏
                    double ratio = (double)x.r.Width * x.r.Height / ((double)sb.Width * sb.Height);
                    if (ratio < 0.04 || ratio > 0.8) continue;
                    if (x.r.Width < sb.Width * 0.12 || x.r.Top < sb.Top + 24) continue;            // 太窄 / 顶边贴着屏幕上沿，没地方溅
                    // 顶边至少有一部分没被更上层的窗口挡住
                    int open = 0;
                    for (int k = 0; k < 12; k++)
                    {
                        var p = new Point(x.r.Left + (2 * k + 1) * x.r.Width / 24, x.r.Top - 3);
                        bool hit = false;
                        for (int j = 0; j < i; j++) if (rects[j].r.Contains(p)) { hit = true; break; }
                        if (!hit) open++;
                    }
                    if (open >= 2) picked.Add(x.h);
                }
                eaves.Update(picked);
            }
            else eaves.Update(new IntPtr[0]);

            // walls 为空时要判一次"是一张卡片都不在已连接的屏上，还是壁纸没建好 / 在排队重建"：FxStep 和下面的 perfText 都要用，算一次传下去
            var emptyScreens = walls.Count == 0 ? WallScreens() : null;
            FxStep(emptyScreens);
            if (AnyBackdrop && ++backdropCheck >= 43) { backdropCheck = 0; CheckBackdrops(); }   // 约 30 秒查一次（幻灯片放映换图不一定有广播）

            if (locked) perfText = "已暂停（锁屏）";
            else if (walls.Count == 0) perfText = emptyScreens.Count == 0 ? "没有显示天气壁纸（已连接的屏上都没有卡片）" : "正在准备桌面…";
            else if (!walls.Any(w => w.HasWeather)) perfText = "没有天气画面（只放了自定义卡片）";
            else if (running == 0) perfText = "已暂停（桌面被遮挡）";
            else if (settings.Minimal && !AnyFx() && preview == "live" && !previewDay) perfText = "极简模式：静止，天气有变化时播放动画";
            else perfText = $"{minFps} FPS" + (reasons.Count > 0 ? "（" + string.Join("，", reasons) + "）" : minFps < settings.Fps ? "（桌面大部分被遮挡）" : "");
        }

        /// <summary>
        /// 开 / 关壁纸叠加：它和极简模式是普通模式的两个变体，同时只能开一个（极简模式平时不画天气，没有天气可叠）。
        /// 打开时自动关掉极简模式，反过来打开极简模式时由那边的菜单项负责关掉壁纸叠加——两边的开关互相照应，勾哪个直接切到哪个。
        /// </summary>
        void SetOverlayWall(bool on)
        {
            settings.OverlayWall = on;
            if (on) settings.Minimal = false;
            settings.Save();
            ConfigureOverlay();
            UpdateBackdrops(false);
            Watch();
        }

        // ---------------------------------------------------------------- 极简模式 / 壁纸叠加：系统壁纸做底
        readonly WinTimer backdropTimer = new WinTimer { Interval = 1000 };
        int backdropGen, backdropCheck, backdropFails;
        string backdropKeys = "", backdropProbe;
        readonly Dictionary<string, string> backdropFiles = new Dictionary<string, string>();   // 屏（设备名）→ 上次给它生成的底图文件（null = 纯色）
        DateTime backdropRetryAt;

        /// <summary>
        /// 这块屏要不要系统壁纸底图：极简模式（平时只有它）、壁纸叠加（它从半透明的天空里透出来）、
        /// 只放了自定义卡片的屏（不画天气，底下就是系统壁纸）。
        /// </summary>
        bool NeedsBackdrop(WallpaperWindow w) => settings.Minimal || settings.OverlayWall || !w.HasWeather;
        bool AnyBackdrop => walls.Any(NeedsBackdrop);
        const string NoBackdrop = "{\"t\":\"backdrop\",\"url\":null}";

        /// <summary>
        /// 把要底图的屏的系统壁纸缩放好交给页面垫底；onlyIfChanged = 只在壁纸设置变了时才重新生成（定时检查用）。
        /// 不要底图的屏通知页面撤掉。
        /// </summary>
        async void UpdateBackdrops(bool onlyIfChanged)
        {
            var all = walls.ToList();
            var list = all.Where(NeedsBackdrop).ToList();
            foreach (var w in all) if (!list.Contains(w) && w.BackdropJson != NoBackdrop) SendBackdrop(w, NoBackdrop);
            if (list.Count == 0)
            {
                ++backdropGen;   // 作废还在生成中的底图
                backdropKeys = "";
                return;   // 也别拿空列表去清缓存（桌面还没就绪时 walls 也是空的）
            }
            try
            {
                // 只查要底图的屏（IDesktopWallpaper 是跨进程的，在 UI 线程上，资源管理器卡住时会拖住）
                var infos = list.Select(w => Backdrop.Query(w.Bounds)).ToList();
                // 指纹带上哪几块屏要底图：要底图的屏变了（天气卡片挪了屏）也算变化
                var keys = string.Join("\n", list.Select((w, i) => w.DeviceName + "=" + infos[i].Key));
                if (onlyIfChanged && keys == backdropKeys) return;   // 没变：也不能作废正在生成的那一次
                int gen = ++backdropGen;
                backdropKeys = keys;
                var files = await Task.Run(() => infos.Select(Backdrop.Render).ToList());
                if (gen != backdropGen) return;
                for (int i = 0; i < list.Count; i++)
                {
                    SendBackdrop(list[i], "{\"t\":\"backdrop\",\"url\":" + (files[i] == null ? "null" : WeatherService.JsonStr("https://" + Backdrop.Host + "/" + files[i]))
                        + ",\"color\":" + WeatherService.JsonStr(infos[i].Css) + "}");
                    backdropFiles[list[i].DeviceName] = files[i];
                }
                // 现在不要底图的屏：上次给它生成的文件也留着（天气卡片挪来挪去时不用重新生成；那块屏的壁纸这期间换了的话，到时候指纹不同会重新生成）
                string kept;
                Backdrop.Clean(files.Concat(all.Where(w => !list.Contains(w)).Select(w => backdropFiles.TryGetValue(w.DeviceName, out kept) ? kept : null)));
                backdropFails = 0;
                Log.Write("backdrop: " + string.Join(", ", all.Select(w => { int i = list.IndexOf(w); return i < 0 ? "-" : (files[i] ?? "color " + infos[i].Css) + " pos " + infos[i].Pos; })));
                GC.Collect();   // 解码大图留下的大对象堆，生成完就还回去
            }
            catch (Exception ex)
            {
                // 读不出壁纸（刚换图文件还被占着等）：清掉指纹和探测结果，按 30 秒、1 分钟……最长 10 分钟的间隔重试
                backdropKeys = ""; backdropProbe = null;
                backdropRetryAt = DateTime.UtcNow.AddSeconds(Math.Min(600, 30 << Math.Min(backdropFails, 5)));
                backdropFails++;
                Log.Write("backdrop failed: " + ex.Message);
            }
        }

        /// <summary>页面 ready 之前不发（排队的消息会抢在 layout 前面），记下来由 OnWallReady 在 layout 之后补发。</summary>
        static void SendBackdrop(WallpaperWindow w, string json)
        {
            w.BackdropJson = json;
            if (w.Ready) w.Post(json);
        }

        /// <summary>Watch 里约 30 秒一次：先做廉价探测，壁纸设置看起来变了（或上次失败到了重试时间）才完整查询。</summary>
        void CheckBackdrops()
        {
            if (backdropProbe == null && DateTime.UtcNow < backdropRetryAt) return;
            var p = Backdrop.Probe();
            if (p == backdropProbe) return;
            backdropProbe = p;
            UpdateBackdrops(true);
        }

        // ---------------------------------------------------------------- 天气动画的"播一次"（极简模式）与雨后彩虹
        // 每个城市一份状态（WeatherFx）：城市 X 的天气变了，只在画面跟着 X 的屏上播；"桌面露出来"和倒计时也只看这些屏。
        // 只出现在卡片上、不是任何一块屏画面城市的城市不播全屏动画（等着播的留着，成了画面城市时再播）。
        const double FxIdleSeconds = 120;
        DateTime fxTick = DateTime.UtcNow;

        bool AnyFx() => cities.Values.Any(c => c.Fx.Kind != null);
        string FxKindOf(WallpaperWindow w) { City c; return cities.TryGetValue(w.SceneKey ?? "", out c) ? c.Fx.Kind : null; }
        static string FxJson(string kind) => "{\"t\":\"fx\",\"kind\":" + (kind == null ? "null" : WeatherService.JsonStr(kind)) + "}";

        /// <summary>每块屏按自己画面城市的播放状态，变了才通知页面。</summary>
        void PostFx()
        {
            foreach (var w in walls)
            {
                var k = FxKindOf(w);
                if (k == w.FxSent) continue;
                w.FxSent = k;
                w.Post(FxJson(k));
            }
        }

        /// <summary>Watch 每 0.7 秒调一次（布局变化、拿到天气时也调）：推进各城市的倒计时，等着播的在可以开播时开播。
        /// wallScreens：调用方（Watch）这轮已经算过的 WallScreens()，省得同一个 0.7 秒里算两遍；null = 自己算。</summary>
        void FxStep(List<Screen> wallScreens = null)
        {
            var now = DateTime.UtcNow;
            double dt = Math.Max(0, Math.Min(2, (now - fxTick).TotalSeconds));
            fxTick = now;
            bool canStart = !locked && Native.IdleSeconds() <= FxIdleSeconds;
            int secs = settings.PlaySecondsValue();
            bool changed = false;
            // walls 为空有两种情况：卡片确实都不在已连接的屏上（该丢的丢），或者有卡片但壁纸还没建好 / 正在重建（桌面没就绪，都留着）
            bool rebuilding = walls.Count == 0 && (wallScreens ?? WallScreens()).Count > 0;
            foreach (var c in cities.Values)
            {
                var mine = walls.Where(w => w.SceneKey == c.Key).ToList();
                if (mine.Count == 0) { if (c.Fx.Detach(!rebuilding)) changed = true; continue; }
                if (c.Fx.Step(dt, mine.Any(w => w.Shown), canStart, secs)) changed = true;
            }
            if (changed) PostFx();
        }

        /// <summary>
        /// 立即播放（fx 命令、托盘「当前天气（立即播放）」）：cardId 为空 = 每块屏各播各自画面城市；否则只播这张卡片的城市
        /// （只在画面跟着这个城市的屏上播，不是任何屏的画面城市就不播）。off = 全部停止。
        /// </summary>
        void PlayFx(string kind, string cardId = null)
        {
            int secs = settings.PlaySecondsValue();
            var keys = kind == "off" ? cities.Keys.ToList()
                : cardId != null ? WeatherCards().Where(c => c.Id == cardId).Select(c => c.LocKey()).ToList()
                : walls.Select(w => w.SceneKey).Distinct().ToList();
            foreach (var k in keys)
            {
                City c;
                if (!cities.TryGetValue(k, out c)) continue;
                if (kind != "off" && !walls.Any(w => w.SceneKey == k)) { Log.Write("fx[" + k + "]: not a scene city, skipped"); continue; }
                c.Fx.Play(kind, secs);
            }
            PostFx();
        }

        // ---------------------------------------------------------------- 天气：按城市分组，每个城市独立刷新、失败重试、缓存
        class City
        {
            public readonly string Key;
            public CardSpec Spec;              // 定位用：用到这个城市的第一张卡片
            public Loc Loc;
            public DateTime LocTime, Due;      // UTC
            public int LocGen;                 // 每次要求重新定位（立即刷新、卡片改名）加一：RefreshCity 据此认出定位途中又被要求过
            public string Payload;             // 最近一份天气（发给页面的 JSON）
            public string FxBad;               // 解析失败过的那份 Payload（坏缓存）：不再拿它去播动画
            public bool Busy;
            public DateTime RetiredAt;         // 不再用到的时间（UTC），见 retired
            public readonly WeatherFx Fx;
            public City(string key, CardSpec spec) { Key = key; Spec = spec; Fx = new WeatherFx(key); }
            public string CachePath => CachePathOf(Key);
        }
        readonly Dictionary<string, City> cities = new Dictionary<string, City>();
        // 刚不再用到的城市先留一阵（最多 30 分钟、16 个）：在卡片设置里换了城市又换回来、只预览的草稿关掉时，
        // 不用重新联网，也不会被当成新城市再播一次"启动"动画；雨后彩虹的计时、一小时内不重播的记录都还在
        readonly Dictionary<string, City> retired = new Dictionary<string, City>();

        static string cacheDir;
        static string CacheDir => cacheDir ?? (cacheDir = Path.Combine(Settings.LocalDir, "cache"));   // 目录在写缓存时才建（运行中被删了也能再建出来）

        string DefaultKey() => EffectiveCards()[0].LocKey();
        City DefaultCity() { City c; return cities.TryGetValue(DefaultKey(), out c) ? c : null; }
        string AutoName() { City c; return cities.TryGetValue("auto", out c) && c.Loc != null ? c.Loc.Name : null; }

        /// <summary>卡片变了之后：给新出现的城市建状态（先读缓存，refresh = 马上去取），不再用到的城市先收起来（见 retired）。
        /// edited = 刚在卡片设置里改过城市的卡片：这个地点的名字优先按它（画面、托盘面板的地名跟着最新的编辑走）。</summary>
        void SyncCities(bool refresh = true, CardSpec edited = null)
        {
            var now = DateTime.UtcNow;
            var eff = WeatherCards();   // 自定义卡片不取天气（它的 api.weather 用默认卡片的，默认卡片总是天气卡片）
            var want = new Dictionary<string, CardSpec>();
            foreach (var c in eff) if (!want.ContainsKey(c.LocKey())) want[c.LocKey()] = c;
            foreach (var k in cities.Keys.ToList())
            {
                if (want.ContainsKey(k)) continue;
                var c = cities[k];
                cities.Remove(k);
                c.Fx.Detach(true);
                c.RetiredAt = now;
                retired[k] = c;
            }
            var stale = retired.Values.OrderByDescending(x => x.RetiredAt).Where((x, i) => i >= 16 || (now - x.RetiredAt).TotalMinutes > 30).Select(x => x.Key).ToList();
            foreach (var k in stale) retired.Remove(k);
            foreach (var kv in want)
            {
                City c;
                bool known = cities.TryGetValue(kv.Key, out c);
                if (!known && retired.TryGetValue(kv.Key, out c))
                {
                    retired.Remove(kv.Key);
                    cities[kv.Key] = c;
                    known = true;
                    if (c.Payload != null) foreach (var w in walls) if (w.Ready) w.Post(c.Payload);   // 页面已经按 keys 丢掉了它的天气
                }
                if (known)
                {
                    // 定位、显示用哪张卡片：优先刚编辑过的，其次上次用的那张（还在这个地点上），都不在就用第一张。
                    // 按卡片挑而不是按名字挑：同地点多张卡片名字不同时，每来一次就换一个名字重取一遍（地名来回跳）
                    var pick = (edited != null && edited.LocKey() == kv.Key ? eff.FirstOrDefault(x => x.Id == edited.Id) : null)
                        ?? eff.FirstOrDefault(x => x.Id == c.Spec.Id && x.LocKey() == kv.Key)
                        ?? kv.Value;
                    if (!pick.IsAuto() && pick.City != c.Spec.City)
                    {
                        // 用着的这张换了名字（刚编辑过、或原来定位的那张改名了）：马上按新名字重取（正在取的那次取完后接着再取，见 RefreshCity）
                        c.Loc = null; c.LocTime = DateTime.MinValue; c.LocGen++; c.Due = DateTime.UtcNow;
                    }
                    c.Spec = pick;
                    continue;
                }
                c = new City(kv.Key, kv.Value) { Due = DateTime.MinValue, LocTime = DateTime.MinValue };
                c.Payload = ReadCache(c);
                cities[kv.Key] = c;
                if (c.Payload != null) foreach (var w in walls) if (w.Ready) w.Post(c.Payload);   // 还没就绪的窗口在 OnWallReady 里会收到所有城市的天气
            }
            // 托盘面板显示默认卡片的城市：换了城市就跟着换（新城市还没有天气时显示"正在获取"，不留着旧城市的）
            var dk = DefaultKey();
            if (dk != flyoutKey) { flyoutKey = dk; flyout?.Post(DefaultCity()?.Payload ?? "{\"t\":\"weather\",\"none\":true}"); }
            if (refresh) RefreshDue();
        }
        string flyoutKey;

        /// <summary>启动时清理缓存目录：不再用到、30 天没更新的城市缓存删掉（搜过又换掉的城市会留下缓存文件）。</summary>
        void CleanCache()
        {
            try
            {
                if (!Directory.Exists(CacheDir)) return;
                var keep = new HashSet<string>(cities.Values.Select(c => Path.GetFileName(c.CachePath)), StringComparer.OrdinalIgnoreCase);
                foreach (var p in Directory.GetFiles(CacheDir, "*.json"))
                    if (!keep.Contains(Path.GetFileName(p)) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(p)).TotalDays > 30) File.Delete(p);
            }
            catch (Exception ex) { Log.Write("cache clean: " + ex.Message); }
        }

        static string CachePathOf(string key) => Path.Combine(CacheDir, key.Replace(',', '_') + ".json");

        /// <summary>
        /// 启动时做一次：旧版本只有一份缓存（cache.json，就是默认卡片的城市），补上城市键挪成默认卡片城市的缓存；
        /// 那个城市已经有自己的缓存就不用它。旧文件总是删掉（留着的话，以后换到没缓存的城市会错拿到它）。
        /// </summary>
        void MigrateOldCache()
        {
            var old = Path.Combine(Settings.LocalDir, "cache.json");
            try
            {
                if (!File.Exists(old)) return;
                var key = settings.DefaultCard().LocKey();
                var path = CachePathOf(key);
                if (!File.Exists(path))
                {
                    var p = File.ReadAllText(old);
                    const string head = "{\"t\":\"weather\",";
                    if (p.StartsWith(head))
                    {
                        if (!p.StartsWith(head + "\"key\"")) p = head + "\"key\":" + WeatherService.JsonStr(key) + "," + p.Substring(head.Length);
                        Directory.CreateDirectory(CacheDir);
                        File.WriteAllText(path, p);
                    }
                    else Log.Write("cache migrate: unrecognized cache.json, dropped");   // 搬过去也带不上城市键，页面取不到，别占着位置
                }
                File.Delete(old);
            }
            catch (Exception ex) { Log.Write("cache migrate: " + ex.Message); }
        }

        string ReadCache(City c)
        {
            try
            {
                if (!File.Exists(c.CachePath)) return null;
                var p = File.ReadAllText(c.CachePath);
                // 写到一半崩了 / 断电会留下半截 JSON：PostWebMessageAsJson 会拒收，卡片一直「正在获取天气…」。解析不了就当没有缓存
                if (!(Js.DeserializeObject(p) is Dictionary<string, object>)) throw new FormatException("not an object");
                return p;
            }
            catch (Exception ex) { var m = ex.Message; Log.Write("cache read: " + c.Key + " " + (m.Length > 120 ? m.Substring(0, 120) + "…" : m)); }   // 解析异常的消息会带上整段文本
            return null;
        }

        /// <summary>到了刷新时间的城市去取天气。</summary>
        void RefreshDue()
        {
            var now = DateTime.UtcNow;
            foreach (var c in cities.Values.ToList()) if (!c.Busy && now >= c.Due) RefreshCity(c);
        }

        /// <summary>「立即刷新天气」：所有城市马上重取，relocate = 自动定位的也重新定位。正在取的城市取完后接着再取一次（RefreshCity 里处理）。</summary>
        void RefreshAll(bool relocate)
        {
            var now = DateTime.UtcNow;
            foreach (var c in cities.Values) { if (relocate) { c.LocTime = DateTime.MinValue; c.LocGen++; } c.Due = now; }
            RefreshDue();
        }

        async void RefreshCity(City c)
        {
            c.Busy = true;
            // 取的过程中「立即刷新」、唤醒改了 Due / LocTime：取完不拿默认的间隔盖掉，按新的要求接着再取
            DateTime due = c.Due;
            int gen = c.LocGen;   // 不能拿 LocTime 对照：第一次定位时它本来就是 MinValue，和「立即刷新」设的值分不出来
            try
            {
                var loc = c.Loc;   // 本地留一份：取的过程中卡片换了名字，c.Loc 会被清掉
                if (loc == null || (DateTime.UtcNow - c.LocTime).TotalHours > 2)
                {
                    var l = await ws.Locate(c.Spec);
                    if (l != null)
                    {
                        loc = l;   // 这次照常用它取
                        // 定位途中又被要求重新定位（或卡片改了名字）：结果不记下，Due 也被改过了，取完接着按新的要求再定位
                        if (c.LocGen == gen) { c.Loc = l; c.LocTime = DateTime.UtcNow; }
                    }
                }
                if (loc == null) throw new Exception("定位失败");
                var p = await ws.Fetch(loc, c.Key);
                City cur;
                if (!cities.TryGetValue(c.Key, out cur) || cur != c) return;   // 取的过程中这个城市已经不用了
                c.Payload = p;
                if (c.Due == due) c.Due = DateTime.UtcNow.AddMinutes(15);
                try { Directory.CreateDirectory(CacheDir); File.WriteAllText(c.CachePath, p); } catch { }
                foreach (var w in walls) if (w.Ready) w.Post(p);
                if (c.Key == DefaultKey()) flyout?.Post(p);
                if (c.Key == "auto") cardWin?.Post(Js.Serialize(new { t = "autoname", name = AutoName() ?? "" }));   // 卡片设置里的「自动定位（城市名）」跟着更新
                Log.Write("weather updated: " + c.Key + " " + loc.Name);
                c.Fx.OnWeather(p, true, settings.Minimal);
                FxStep();
            }
            catch (Exception ex)
            {
                Log.Write("refresh failed (" + c.Key + "): " + ex.Message);
                if (c.Due == due) c.Due = DateTime.UtcNow.AddMinutes(2);
                // 启动时联网失败：先按缓存的天气播启动动画（缓存坏了解析不了的，同一份不再反复解析）
                if (!c.Fx.HasWeather && c.Payload != null && !ReferenceEquals(c.Payload, c.FxBad))
                {
                    c.Fx.OnWeather(c.Payload, false, settings.Minimal);
                    if (!c.Fx.HasWeather) c.FxBad = c.Payload;
                    FxStep();
                }
            }
            finally
            {
                c.Busy = false;
                City cur;
                if (c.Due != due && DateTime.UtcNow >= c.Due && cities.TryGetValue(c.Key, out cur) && cur == c) RefreshCity(c);
            }
        }

        async void OnCommand(string cmd)
        {
            Log.Write("command: " + cmd);
            var parts = cmd.Trim().Split(new[] { ' ' }, 2);
            var arg = parts.Length > 1 ? parts[1].Trim().Trim('"') : "";
            switch (parts[0].ToLowerInvariant())
            {
                case "preview":
                    // preview <天气> [day] / preview day（用当前天气快进）
                    var pa = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    bool day = pa.Remove("day");
                    SetPreview(pa.Count > 0 ? pa[0] : (day ? preview : "live"), day);
                    break;
                case "refresh": RefreshAll(true); break;
                case "fx":   // fx [start|rain|heavy|thunder|snow|rainbow|off] [卡片 Id]：立即播放（测试用），带卡片 Id 只播那张卡片的城市
                    var fa = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    PlayFx(fa.Length > 0 ? fa[0].ToLowerInvariant() : "start", fa.Length > 1 ? fa[1] : null);
                    break;
                case "card": OpenCardSettings(arg == "" ? CardSpec.DefaultId : arg == "new" ? null : arg); break;   // card [Id|new]：打开卡片设置
                case "about": OpenAbout(); break;
                case "overlay":   // overlay [on|off|<天空不透明度>|<天空> <云>]：壁纸叠加，直接写设置并立即生效（测试 / 调参用）
                    var ov = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (ov.Length == 1 && (ov[0] == "on" || ov[0] == "off")) SetOverlayWall(ov[0] == "on");
                    else if (ov.Length > 0)
                    {
                        settings.SkyOpacity = ov[0];
                        if (ov.Length > 1) settings.CloudAlpha = ov[1];
                        settings.NormalizeOpacities();
                        settings.OverlayWall = true; settings.Minimal = false;
                        settings.Save(); ConfigureOverlay(); UpdateBackdrops(false); Watch();
                        Log.Write("overlay: sky " + settings.SkyOpacityValue().ToString("F2", Inv) + ", cloud " + settings.CloudAlphaValue().ToString("F2", Inv));
                    }
                    break;
                case "festive":   // festive [on|off]：节日装饰与彩蛋开关（不带参数 = 切换）
                    settings.Festive = arg == "" ? !settings.Festive : arg != "off";
                    settings.Save(); UpdateLayout();
                    break;
                case "rebuild": RebuildNow(); break;
                case "quit": Quit(); break;
                case "checkupdate": CheckUpdate(arg == "manual"); break;
                case "update-now":   // 不弹确认直接更新（脚本 / 测试用）
                    try { var ui = await Updater.Check(); if (ui.Version > Updater.Current || arg == "force") await InstallUpdate(ui); }
                    catch (Exception ex) { Log.Write("update-now: " + ex.Message); }
                    break;
                case "flyout": ToggleFlyout(); break;
                case "show": if (flyout == null || !flyout.Visible) ToggleFlyout(); break;
                case "capture":
                    var dir = arg == "" ? Settings.LocalDir : arg;
                    Directory.CreateDirectory(dir);
                    for (int i = 0; i < walls.Count; i++)
                        await walls[i].Capture(Path.Combine(dir, "wall" + i + (walls[i].Primary ? "_primary" : "") + ".png"));
                    break;
            }
        }

        string PreviewJson() => "{\"t\":\"preview\",\"mode\":" + WeatherService.JsonStr(preview) + ",\"day\":" + B(previewDay) + "}";

        void SetPreview(string mode, bool day)
        {
            bool demoRunning = previewDay && day;   // 演示中途换天气：时间轴不重来，结束时间也不变
            preview = string.IsNullOrEmpty(mode) ? "live" : mode;
            previewDay = day;
            if (!demoRunning)
            {
                previewTimer.Stop();
                if (preview != "live" || day)
                {
                    previewUntil = DateTime.Now.AddSeconds(day ? DayDemoSeconds : settings.PlaySecondsValue());
                    previewTimer.Start();
                }
            }
            Broadcast(PreviewJson());
        }

        // ---------------------------------------------------------------- 托盘
        void BuildTray()
        {
            tray.Icon = new Icon(Program.AppIcon, SystemInformation.SmallIconSize);
            tray.Text = "天气壁纸";
            tray.Visible = true;
            PromoteTrayIconSoon();
            var menu = new ContextMenuStrip();

            menu.Items.Add("立即刷新天气", null, (s, e) => RefreshAll(true));
            menu.Items.Add(new ToolStripSeparator());

            var pv = new ToolStripMenuItem("效果预览");
            var modes = new[]
            {
                new[] { "live", "实时天气" }, null,
                new[] { "clear", "晴天" }, new[] { "sunrise", "晴 · 日出" }, new[] { "sunset", "晴 · 日落" }, new[] { "night", "晴 · 夜晚" },
                new[] { "partly", "多云" }, new[] { "cloudy", "阴天" }, new[] { "fog", "雾" }, null,
                new[] { "drizzle", "毛毛雨" }, new[] { "rain", "中雨" }, new[] { "heavy", "暴雨" }, new[] { "thunder", "雷阵雨" }, new[] { "snow", "雪" },
                new[] { "rainbow", "雨后彩虹" },
            };
            ToolStripMenuItem liveIt = null;
            foreach (var m in modes)
            {
                if (m == null) { pv.DropDownItems.Add(new ToolStripSeparator()); continue; }
                var key = m[0];
                var it = new ToolStripMenuItem(m[1]) { Tag = key };
                it.Click += (s, e) =>
                {
                    // 极简模式平时不显示天气，没有可"切回"的实时画面：这一项就是"按当前天气播放一次"
                    if (key == "live" && settings.Minimal && !previewDay) { SetPreview("live", false); PlayFx("start"); }
                    else SetPreview(key, previewDay);
                };
                pv.DropDownItems.Add(it);
                if (key == "live") liveIt = it;
            }
            // 节日装饰单独收进一个子菜单（预览里不管「节日装饰与彩蛋」开没开都照常演示）
            var festPv = new ToolStripMenuItem("节日");
            foreach (var m in new[]
            {
                new[] { "cny", "春节（灯笼 · 烟花）" }, new[] { "lantern", "元宵节（花灯 · 烟花）" }, new[] { "dragon", "端午节（粽子 · 艾草）" },
                new[] { "midautumn", "中秋节（灯笼 · 月饼 · 玉兔）" }, new[] { "national", "国庆节（国旗）" }, new[] { "newyear", "跨年（烟花）" },
                new[] { "halloween", "万圣节（南瓜灯 · 蝙蝠）" }, new[] { "xmas", "圣诞节（圣诞树 · 雪人 · 雪橇）" }, new[] { "easter", "复活节（彩蛋 · 兔子）" },
            })
            {
                var key = m[0];
                var it = new ToolStripMenuItem(m[1]) { Tag = key };
                it.Click += (s, e) => SetPreview(key, previewDay);
                festPv.DropDownItems.Add(it);
            }
            pv.DropDownItems.Add(festPv);
            // 自定义效果：effects 文件夹里的每个脚本一项（每次打开「效果预览」时按当前文件列表重建），末尾是「打开效果文件夹」
            var userPv = new ToolStripMenuItem("自定义效果");
            Action fillUser = () =>
            {
                foreach (var o in userPv.DropDownItems.Cast<ToolStripItem>().ToList()) o.Dispose();   // Dispose 会把它从菜单里拿掉
                foreach (var ef in effects)
                {
                    var key = "user:" + ef.Key;
                    var it = new ToolStripMenuItem(ef.Key) { Tag = key, Checked = preview == key };
                    it.Click += (s, e) => SetPreview(key, previewDay);
                    userPv.DropDownItems.Add(it);
                }
                if (effects.Count == 0) userPv.DropDownItems.Add(new ToolStripMenuItem("（文件夹里还没有效果脚本）") { Enabled = false });
                userPv.DropDownItems.Add(new ToolStripSeparator());
                userPv.DropDownItems.Add("打开效果文件夹（照着里面的示例写自己的效果）…", null, (s, e) => OpenEffectsDir());
                userPv.Checked = preview.StartsWith("user:", StringComparison.Ordinal);
            };
            fillUser();
            pv.DropDownItems.Add(userPv);
            pv.DropDownItems.Add(new ToolStripSeparator());
            var dayIt = new ToolStripMenuItem("一天快进演示（按上面所选天气，48 秒一遍）");
            dayIt.Click += (s, e) => SetPreview(preview, !previewDay);
            pv.DropDownItems.Add(dayIt);
            var pvTip = new ToolStripMenuItem("") { Enabled = false };
            pv.DropDownItems.Add(pvTip);
            pv.DropDownOpening += (s, e) =>
            {
                foreach (var o in pv.DropDownItems.OfType<ToolStripMenuItem>().Concat(festPv.DropDownItems.OfType<ToolStripMenuItem>()))
                    if (o.Tag is string) o.Checked = (string)o.Tag == preview;
                festPv.Checked = festPv.DropDownItems.OfType<ToolStripMenuItem>().Any(o => o.Checked);
                fillUser();
                dayIt.Checked = previewDay;
                // 极简模式：「当前天气」只在真正播放时打勾；平时桌面上是系统壁纸，什么都不勾
                if (settings.Minimal && preview == "live" && !previewDay && !AnyFx()) liveIt.Checked = false;
                liveIt.Text = settings.Minimal ? "当前天气（立即播放 " + settings.PlaySecondsValue() + " 秒）" : "实时天气";
                var back = settings.Minimal ? "收起" : "恢复实时天气";
                var left = (int)Math.Ceiling((previewUntil - DateTime.Now).TotalSeconds);
                pvTip.Text = previewTimer.Enabled && left > 0 ? $"{left} 秒后{back}" : $"预览 {settings.PlaySecondsValue()} 秒后自动{back}";
            };
            // 菜单按常见托盘程序的习惯排：常用操作（刷新）→ 各种效果（效果设置 / 节日 / 预览 / 两个显示模式）→ 系统相关（自启 / 卡片 / 关于）→ 退出。
            // 检查更新、自动检查更新、设置文件、日志目录这些不常用的放进「关于」窗口
            var look = new ToolStripMenuItem("效果设置");
            var fx = new ToolStripMenuItem("任务栏雨雪效果") { CheckOnClick = true };
            fx.Click += (s, e) => { settings.TaskbarFx = fx.Checked; settings.Save(); ConfigureOverlay(); };
            var wfx = new ToolStripMenuItem("窗口顶边雨滴 / 积雪（屋檐）") { CheckOnClick = true };
            wfx.Click += (s, e) => { settings.WindowFx = wfx.Checked; settings.Save(); Watch(); };
            var festive = new ToolStripMenuItem("节日效果") { CheckOnClick = true };
            festive.Click += (s, e) => { settings.Festive = festive.Checked; settings.Save(); UpdateLayout(); };
            var fps60 = new ToolStripMenuItem("高帧率（60 FPS）") { CheckOnClick = true };
            fps60.Click += (s, e) => { settings.Fps = fps60.Checked ? 60 : 30; settings.Save(); overlay.SetFps(settings.Fps); ConfigureOverlay(); };
            // 省内存模式不再作为常规选项：想省资源用极简模式（静止时 CPU / GPU 为 0，内存也更低）；settings.json 里的 LowMemory 仍然有效。
            // 只在已经开着时显示这一项，让以前开过的人能直接在菜单里关掉
            var lowMem = new ToolStripMenuItem("省内存模式") { CheckOnClick = true };
            lowMem.Click += (s, e) => { settings.LowMemory = lowMem.Checked; settings.Save(); ConfigureOverlay(); ApplyFlyoutMode(); };
            var status = new ToolStripMenuItem("状态") { Enabled = false };   // 当前帧率 / 暂停原因，和这些开关放一起看
            look.DropDownItems.Add(fx); look.DropDownItems.Add(wfx); look.DropDownItems.Add(festive); look.DropDownItems.Add(fps60); look.DropDownItems.Add(lowMem);
            look.DropDownItems.Add(new ToolStripSeparator());
            look.DropDownItems.Add(status);
            look.DropDownOpening += (s, e) =>
            {
                lowMem.Checked = settings.LowMemory; lowMem.Visible = settings.LowMemory;
                fx.Checked = settings.TaskbarFx; wfx.Checked = settings.WindowFx; festive.Checked = settings.Festive;
                fps60.Checked = settings.Fps >= 60;
                status.Text = "当前：" + (perfText == "" ? "启动中" : perfText);
            };
            menu.Items.Add(look);
            menu.Items.Add(pv);

            var minimal = new ToolStripMenuItem("极简模式") { CheckOnClick = true };   // 文字在菜单弹出时按 PlaySeconds 填
            minimal.Click += (s, e) =>
            {
                settings.Minimal = minimal.Checked;
                if (settings.Minimal) settings.OverlayWall = false;   // 两者互斥：开极简模式就关掉壁纸叠加
                settings.Save(); ConfigureOverlay(); UpdateBackdrops(false); Watch();
            };
            menu.Items.Add(minimal);

            // 壁纸叠加：普通模式的变体——系统壁纸常驻，天空半透明，云/日月/雨雪浮在壁纸上
            var overlayWall = new ToolStripMenuItem("壁纸叠加") { CheckOnClick = true };
            overlayWall.Click += (s, e) => SetOverlayWall(overlayWall.Checked);
            menu.Items.Add(overlayWall);

            menu.Items.Add(new ToolStripSeparator());
            var startup = new ToolStripMenuItem("开机自启") { CheckOnClick = true };
            startup.Click += (s, e) => SetAutoStart(startup.Checked);
            menu.Items.Add(startup);
            menu.Items.Add("卡片设置…", null, (s, e) => OpenCardSettings(CardSpec.DefaultId));   // 新增、切换、隐藏、删除卡片都在设置窗口里

            // 有新版本时才出现（加粗）；平时检查更新在「关于」里
            updateItem = new ToolStripMenuItem("更新", null, (s, e) => { if (newer != null) PromptUpdate(newer); }) { Visible = false };
            updateItem.Font = new Font(updateItem.Font, FontStyle.Bold);
            menu.Items.Add(updateItem);
            menu.Items.Add("关于", null, (s, e) => OpenAbout());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => Quit());

            menu.Opening += (s, e) =>
            {
                minimal.Checked = settings.Minimal;
                minimal.Text = "极简模式（平时只显示卡片，天气变化时播放 " + settings.PlaySecondsValue() + " 秒）";
                // 普通模式 / 壁纸叠加 / 极简模式三种状态可以任意直接切换：勾选哪个就切到哪个（打开一个自动关掉另一个），取消勾选回到普通模式
                overlayWall.Checked = settings.OverlayWall;
                startup.Checked = IsAutoStart();
                updateItem.Visible = installing || NewerOffered;   // 正在更新时显示进度
            };
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };
        }

        // Win11 会把第一次出现的托盘图标（按 exe 路径区分）默认收进"^"溢出区，看起来就像图标没了。
        // 图标登记后，若用户还没对它做过选择（IsPromoted 不存在）就设为在任务栏显示；每个登记项只做一次，
        // 之后用户在"设置 → 个性化 → 任务栏"里关掉就不再干预。
        void PromoteTrayIconSoon()
        {
            int tries = 0;
            var t = new WinTimer { Interval = 2000 };
            t.Tick += (s, e) =>
            {
                if (PromoteTrayIcon() || ++tries >= 5) { t.Stop(); t.Dispose(); }
                else t.Interval = 6000;
            };
            t.Start();
        }

        static bool PromoteTrayIcon()
        {
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root == null) return true;   // 旧系统没有这套机制
                    foreach (var id in root.GetSubKeyNames())
                    {
                        using (var k = root.OpenSubKey(id))
                            if (k == null || !string.Equals(k.GetValue("ExecutablePath") as string, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) continue;
                        using (var mine = Registry.CurrentUser.CreateSubKey(@"Software\WeatherWallpaper"))
                        using (var k = root.OpenSubKey(id, true))
                        {
                            var done = ((mine.GetValue("TrayPromoted") as string) ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                            if (done.Contains(id) || k.GetValue("IsPromoted") != null) return true;
                            k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            done.Add(id);
                            mine.SetValue("TrayPromoted", string.Join(";", done.Skip(Math.Max(0, done.Count - 20))));
                            Log.Write("tray icon promoted: " + id);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("tray promote: " + ex.Message); return true; }
            return false;
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static bool IsAutoStart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue("WeatherWallpaper") != null;
        }
        internal static void SetAutoStart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("WeatherWallpaper", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("WeatherWallpaper", false);
            }
        }

        // ---------------------------------------------------------------- 卡片设置窗口
        // 改动实时生效、自动保存（像 Windows 设置）：放得下就写进设置；放不下的改动只在桌面上预览、页面上标红，关窗口或换卡片时丢弃
        PageWindow cardWin;
        CardSpec draft;   // 正在编辑的卡片（放不下时和设置里的那张不一样）
        readonly WinTimer cardSaveTimer = new WinTimer { Interval = 500 };   // 拖动、方向键、打字时连续改动：合并成一次写盘

        /// <summary>
        /// 参与显示的卡片：设置里的卡片，正在编辑的那张换成编辑中的样子。隐藏（Off）的卡片不算——
        /// 不显示、不占位置、不取天气、不决定天气画面，它的屏上没有别的卡片时也不显示天气壁纸。
        /// </summary>
        List<CardSpec> EffectiveCards() => AllCards().Where(c => !c.Off).ToList();
        /// <summary>参与显示的天气卡片（不含自定义卡片：它没有城市，不取天气）。按城市分组、取天气用它，别直接拿 EffectiveCards 的 LocKey。</summary>
        List<CardSpec> WeatherCards() => EffectiveCards().Where(c => !c.IsCustom()).ToList();

        /// <summary>所有卡片，含隐藏（Off）的（卡片设置的页签、预览图、摆放检查要按原来的顺序看到它们）。</summary>
        List<CardSpec> AllCards()
        {
            var list = settings.Cards.Select(c => c.Clone()).ToList();   // 副本：NormalizeScenes 会改写 Scene
            if (draft != null)
            {
                int i = list.FindIndex(c => c.Id == draft.Id);
                if (i >= 0) list[i] = draft.Clone(); else list.Add(draft.Clone());
            }
            string prefer = null;
            if (draft != null)
            {
                var stored = settings.Cards.FirstOrDefault(k => k.Id == draft.Id);
                bool moved = stored != null && !draft.Hidden && CardLayout.GroupKey(stored) != CardLayout.GroupKey(draft);   // 换了屏：CommitDraft 不让它抢那块屏原来的画面跟随
                if (draft.Scene && !moved) prefer = draft.Id;
                else
                {
                    // 和 CommitDraft 一致：取消勾选的（哪怕还没提交）把画面跟随交给同屏的另一张，
                    // 否则 NormalizeScenes 会还给排在第一的 draft，页面勾选框又被勾上，和 draft.Scene 对不上。
                    // 刚隐藏（Off）的也走这里（Off 的 Scene 总是 false）：画面跟随交给同屏没隐藏的卡片
                    var key = CardLayout.GroupKey(draft);
                    var other = list.FirstOrDefault(k => k.Id != draft.Id && k.CanTakeScene() && CardLayout.GroupKey(k) == key && k.Scene)
                             ?? list.FirstOrDefault(k => k.Id != draft.Id && k.CanTakeScene() && CardLayout.GroupKey(k) == key);
                    if (other != null) { other.Scene = true; prefer = other.Id; }
                }
            }
            Settings.NormalizeScenes(list, prefer);
            return list;
        }

        static int ScreenIndex(Screen s) => Array.FindIndex(Screen.AllScreens, x => x.DeviceName == s.DeviceName);
        static string ScreenName(Screen s) => "显示器 " + (ScreenIndex(s) + 1);
        string CardName(CardSpec c) => !string.IsNullOrEmpty(c.Name) ? c.Name : c.IsDefault() ? "默认卡片" : c.IsCustom() ? c.Script : c.IsAuto() ? "自动定位"
            : c.PlaceName();

        /// <summary>
        /// 打开卡片设置。id = CardSpec.DefaultId 只是打开 / 调到前台（托盘、托盘面板；窗口开着时正在编辑的卡片和没交上来的编辑都不动）；
        /// 其他 id（如命令行 `card &lt;Id&gt;`）窗口开着时切到那张；id = null：打开后新增一张。
        /// </summary>
        void OpenCardSettings(string id)
        {
            if (env == null) return;
            if (cardWin == null)
            {
                draft = (settings.Cards.FirstOrDefault(c => c.Id == id) ?? settings.Cards[0]).Clone();
                cardWin = new PageWindow(env, "卡片设置 · 天气壁纸", "card.html", 900, 790) { OnMessage = OnCardMessage };
                cardWin.Warm();   // 先让 WebView2 启动起来，下面算布局（CardFitJson）的同时它在加载页面；窗口等页面画好才现身
                cardWin.Reloaded = () => { if (cardWin != null && draft != null) { cardWin.Post(CardInitJson()); cardWin.Post(CardFitJson()); } };
                cardWin.FormClosed += (s, e) =>
                {
                    cardWin = null;
                    FlushCardSave();
                    if (draft != null) { draft = null; if (quitStage == 0) { SyncCities(); UpdateLayout(); } }   // 放不下、没保存的改动：还原（退出时不用）
                };
                cardWin.Post(CardInitJson());
                cardWin.Post(CardFitJson());
                cardWin.Show();
                Native.SetForegroundWindow(cardWin.Handle);
            }
            else
            {
                if (cardWin.WindowState == FormWindowState.Minimized) cardWin.WindowState = FormWindowState.Normal;
                if (id != null && id != CardSpec.DefaultId) SwitchCard(id);
            }
            cardWin.Activate();
            if (id == null) AddCard();
        }

        /// <summary>「＋ 新增卡片」：在放得下的屏上建一张新卡片（马上保存），切过去编辑。都放不下就不新增。script = 自定义卡片用哪个脚本（null = 天气卡片）。</summary>
        void AddCard(string script = null)
        {
            if (cardWin == null || settings.Cards.Count >= Settings.MaxCards) return;
            var prev = draft;
            draft = null;   // 正在编辑的那张如果有放不下、没保存的改动，丢弃
            var c = NewCard(script);
            if (c == null) { draft = prev; UpdateLayout(); return; }
            settings.Cards.Add(c);
            Settings.NormalizeScenes(settings.Cards);
            ScheduleCardSave();
            Log.Write("card added: " + c.Id + " on " + c.Screen + (c.IsCustom() ? " script " + c.Script : ""));
            EditCard(settings.Cards.Last().Clone());
        }

        /// <summary>页签 / 预览图 / 托盘里换了一张卡片（正在编辑的那张如果有放不下、没保存的改动，丢弃）。</summary>
        void SwitchCard(string id)
        {
            var t = settings.Cards.FirstOrDefault(k => k.Id == id);
            if (cardWin == null || t == null || (draft != null && draft.Id == id)) return;
            EditCard(t.Clone());
        }

        void EditCard(CardSpec c)
        {
            draft = c;
            cardWin.Post(CardInitJson());
            SyncCities(); UpdateLayout();
        }

        void DeleteCard()
        {
            if (draft == null || draft.IsDefault()) return;
            var gone = settings.Cards.FirstOrDefault(k => k.Id == draft.Id);
            if (gone != null)
            {
                // 删掉的是这块屏负责"画面跟随"的卡片：交给这块屏剩下的第一张；这块屏没有卡片了，它的天气壁纸也随之关掉
                var keep = gone.Scene ? settings.Cards.FirstOrDefault(k => k != gone && k.CanTakeScene() && CardLayout.GroupKey(k) == CardLayout.GroupKey(gone)) : null;
                settings.Cards.Remove(gone);
                cardErrs.Remove(gone.Id);
                if (keep != null) { keep.Scene = true; Log.Write("scene moved to: " + keep.Id); }
                Settings.NormalizeScenes(settings.Cards, keep?.Id);
                cardSaveTimer.Stop();
                // 文件刚被外部改过（还没重新加载）：Save 不写、改为重新加载，以文件为准——这次删除让位，窗口会换成文件里的样子。没写成的原因 Save 自己记日志
                Log.Write((settings.Save() ? "card deleted: " : "card deleted, not saved (see above): ") + gone.Id);
            }
            EditCard(settings.Cards[0].Clone());
        }

        /// <summary>
        /// 自定义卡片默认的名字（重名时 NewCard 再加「 2」「 3」…）：脚本里的 name，没写用文件名；截到 20 个字，留出加编号的位置（名称最长 24）。
        /// 截完去掉结尾的空格再查重：Normalize 会去掉，带着查就查不出和别的卡片重名。卡片设置页面换脚本时也按它改名（fit 的 scripts[].auto）
        /// </summary>
        static string AutoTitle(CustomCards.Script s, string script) => CardSpec.Cut(s?.Title ?? script, 20).TrimEnd();

        /// <summary>
        /// 新卡片放在哪：设置窗口所在的屏 → 主屏 → 其他屏，第一块放得下的；都放不下返回 null（不新增）。
        /// script = 自定义卡片的脚本：名字和高度先用脚本里写的 name / height（没写用脚本文件名、160）。
        /// probe = 只看放不放得下（卡片设置里判断新增按钮能不能点）：不取 Monitor（要枚举显示设备，拖动时每次重排都会试）。
        /// </summary>
        CardSpec NewCard(string script = null, bool probe = false)
        {
            var c = new CardSpec { Id = CardSpec.NewId() };
            if (script != null)
            {
                var s = FindScript(script);
                c.Script = s?.File ?? script;
                c.Height = s?.Height ?? CustomCards.DefaultHeight;
                var title = AutoTitle(s, c.Script);
                c.Name = title;
                for (int k = 2; settings.Cards.Any(x => x.Name == c.Name); k++) c.Name = title + " " + k;
                c.Normalize();
            }
            else
            {
                int n = settings.Cards.Count + 1;
                while (settings.Cards.Any(k => k.Name == "卡片 " + n)) n++;
                c.Name = "卡片 " + n;
            }
            var order = new List<string>();
            if (cardWin != null) { var here = Screen.FromControl(cardWin); order.Add(here.Primary ? CardSpec.PrimaryScreen : here.DeviceName); }
            order.Add(CardSpec.PrimaryScreen);
            order.AddRange(Screen.AllScreens.Where(s => !s.Primary).Select(s => s.DeviceName));
            foreach (var id in order.Distinct())
            {
                c.Screen = id;
                if (!Fit(c).Ok) continue;
                if (!probe) c.Monitor = CardLayout.MonitorOf(id);   // 只给选中的屏取（要枚举显示设备）
                return c;
            }
            return null;
        }

        void OnCardMessage(string json)
        {
            Dictionary<string, object> d;
            try { d = Js.Deserialize<Dictionary<string, object>>(json); } catch { return; }
            if (d == null || draft == null || cardWin == null) return;
            switch (d.ContainsKey("t") ? d["t"] as string : null)
            {
                case "draft":   // 页面上改了什么：放得下就马上保存，放不下只预览（页面上标红）
                    var c = ParseCard(d);
                    if (c == null) return;
                    // 画面跟随只认勾选框的点击（带 scene 字段）；其余改动沿用宿主这边的值（换屏、删卡片时会被自动转移）。
                    // 换了屏：不带着原来那块屏的画面跟随过去（放不下只预览时也不去抢新屏的画面）；换回设置里的那块屏就用设置里的值
                    if (d.ContainsKey("scene") && d["scene"] is bool) c.Scene = (bool)d["scene"];
                    else if (CardLayout.GroupKey(c) == CardLayout.GroupKey(draft)) c.Scene = draft.Scene;
                    else
                    {
                        var saved = settings.Cards.FirstOrDefault(k => k.Id == c.Id);
                        c.Scene = saved != null && saved.Scene && CardLayout.GroupKey(saved) == CardLayout.GroupKey(c);
                    }
                    if (!c.CanTakeScene()) c.Scene = false;   // 隐藏的卡片不拿画面跟随（刚隐藏的那一下 draft.Scene 还是 true）；自定义卡片没有城市
                    // 什么都没改（如输入框失焦、拖完又落回原处）：不重排、不写盘，只把当前结果再发一遍让页面复位
                    if (Js.Serialize(c) == Js.Serialize(draft)) { cardWin.Post(CardFitJson()); return; }
                    // 换了城市，或者隐藏 / 重新显示（隐藏的卡片不取天气，显示出来时它的城市太久没更新就马上去取）。
                    // 只有真改了城市才按这张卡片的城市名定地名：只是隐藏 / 显示的话，同一地点的其他卡片的地名不变
                    bool cityEdit = !c.IsCustom() && (c.LocKey() != draft.LocKey() || c.City != draft.City);   // 自定义卡片没有城市
                    bool city = cityEdit || c.Off != draft.Off || c.IsCustom() != draft.IsCustom();   // 换成 / 换回自定义卡片：它的城市不用 / 要用了
                    draft = c;
                    if (Fit(c).Ok) CommitDraft();
                    if (city) SyncCities(true, cityEdit ? c : null);   // 换了城市：马上去取新城市的天气，桌面上的卡片跟着变
                    UpdateLayout();
                    break;
                case "search": SearchCity(d.ContainsKey("q") ? Convert.ToString(d["q"]) : ""); break;
                case "identify": ScreenBadge.ShowAll(); break;
                case "add": AddCard(d.ContainsKey("script") && d["script"] != null ? CustomCards.CleanName(Convert.ToString(d["script"])) : null); break;
                case "opencards": OpenCardsDir(); break;
                case "crefresh": RefreshCard(draft.Id); break;
                case "switch": SwitchCard(d.ContainsKey("id") ? Convert.ToString(d["id"]) : ""); break;
                case "delete": DeleteCard(); break;
                case "close": cardWin.CloseNow(); break;
            }
        }

        /// <summary>
        /// 卡片设置里点了自定义卡片的「刷新」：让桌面上运行它的那个页面马上重画一轮（联网的内容不用缓存，真去取），
        /// 画完由页面回 crefreshed。桌面上没在运行它（隐藏了、放不下、屏没接）就直接回一个原因（places 里没有隐藏的卡片，见 EffectiveCards）。
        /// 页面还没就绪（刚崩溃在重新载入）也直接回：排队的话它会抢在补发的 layout 前面到，那时页面里还没有这张卡片，回的是假的「没在运行」。
        /// </summary>
        void RefreshCard(string id)
        {
            var p = places.FirstOrDefault(x => x.Card.Id == id && x.Host != null && x.Rect.HasValue);
            var w = p == null ? null : walls.FirstOrDefault(x => x.DeviceName == p.Host.DeviceName);
            if (w != null && w.Ready) w.Post(Js.Serialize(new { t = "crefresh", id }));
            else cardWin?.Post(Js.Serialize(new { t = "refreshed", id, err = w != null ? "桌面还在加载，稍后再试" : "这张卡片现在没有显示在桌面上" }));
        }

        /// <summary>
        /// 页面发来的卡片：Id 由宿主定（页面改不了），其余字段校验后使用。
        /// 宿主切到别的卡片、重新初始化页面之前页面已经发出的（Id 对不上、gen 早于那次初始化）丢掉；宿主改屏之前发出的（gen 旧了）屏以宿主为准。
        /// </summary>
        CardSpec ParseCard(Dictionary<string, object> d)
        {
            if (!d.ContainsKey("card") || d["card"] == null) return null;
            try
            {
                var c = Js.Deserialize<CardSpec>(Js.Serialize(d["card"]));
                if (c.Id != null && c.Id != draft.Id) return null;
                c.Id = draft.Id;
                int gen = d.ContainsKey("gen") && d["gen"] != null ? Convert.ToInt32(d["gen"], Inv) : -1;
                if (gen < cardInitGen) return null;
                if (gen != cardGen) c.Screen = draft.Screen;
                c.Monitor = c.Screen == draft.Screen ? draft.Monitor : CardLayout.MonitorOf(c.Screen);   // 硬件 ID 由宿主定：换了屏才重新取
                c.Normalize();
                return c;
            }
            catch (Exception ex) { Log.Write("card parse: " + ex.Message); return null; }
        }

        /// <summary>把正在编辑的卡片写进设置（调用前已确认放得下），理顺每块屏的"画面跟随"，稍后写盘。</summary>
        void CommitDraft()
        {
            var c = draft.Clone();
            int i = settings.Cards.FindIndex(k => k.Id == c.Id);
            if (i < 0) return;   // 卡片都是新增时就建好的；不在了（设置文件被改过）就不写
            var old = settings.Cards[i];
            string prefer = c.Scene ? c.Id : null;
            if (CardLayout.GroupKey(old) != CardLayout.GroupKey(c)) { if (!c.Hidden) { c.Scene = false; prefer = null; } }   // 换了屏：不抢那块屏原来的画面跟随（那块屏没别的卡片时会自动给它）
            else if (!c.Scene && old.Scene)
            {
                // 取消勾选、或者隐藏了：交给同屏另一张没隐藏的（只有它一张时 NormalizeScenes 会还给它，隐藏了就没人拿、这块屏也不显示天气壁纸了）
                var other = settings.Cards.FirstOrDefault(k => k.Id != c.Id && k.CanTakeScene() && CardLayout.GroupKey(k) == CardLayout.GroupKey(c));
                if (other != null) { other.Scene = true; prefer = other.Id; }
            }
            settings.Cards[i] = c;
            Settings.NormalizeScenes(settings.Cards, prefer);
            draft.Scene = c.Scene;
            ScheduleCardSave();
        }

        void ScheduleCardSave() { cardSaveTimer.Stop(); cardSaveTimer.Start(); }

        void FlushCardSave()
        {
            if (!cardSaveTimer.Enabled) return;
            cardSaveTimer.Stop();
            // 文件刚被外部改过（还没重新加载）：Save 不写、改为重新加载，以文件为准——卡片设置里还没写盘的改动让位，窗口会换成文件里的样子
            if (!settings.Save()) return;
            Log.Write("cards saved: " + string.Join(" | ", settings.Cards.Select(c => c.Id + " " + c.Screen + " " + c.Pos + " " + (c.IsCustom() ? "script:" + c.Script + " h" + c.Height : c.LocKey())
                + (c.Hidden ? " hidden" : "") + (c.Off ? " off" : "") + (c.Scene ? " scene" : ""))));
        }

        async void SearchCity(string q)
        {
            q = (q ?? "").Trim();
            if (q == "") return;
            object res;
            try
            {
                var list = await ws.SearchCity(q);
                res = new { t = "search", q, items = list.Select(r => new { name = r.Name, detail = r.Detail, lat = r.Lat, lon = r.Lon }).ToList() };
            }
            catch (Exception ex) { res = new { t = "search", q, error = ex.Message }; }
            cardWin?.Post(Js.Serialize(res));
        }

        class FitResult
        {
            public bool Ok;
            public string Msg, Warn;
            public Screen Target;
            public ScreenArea Area;
            public List<CardSpec> On;   // 这块屏上要摆位置的卡片（不含只显示天气画面的、隐藏的）
            public RectangleF?[] Rects;
        }

        /// <summary>
        /// 这张卡片在它选的屏上放不放得下：和这块屏上的其他卡片一起按顺序排一遍（与桌面上的布局同一套算法）。
        /// 放不下、或者会把这块屏上别的卡片挤掉 → 不保存。只显示天气画面的、隐藏的卡片不占位置，总放得下。
        /// </summary>
        FitResult Fit(CardSpec c)
        {
            var f = new FitResult();
            var target = CardLayout.Resolve(c.Screen);
            if (target == null)
            {
                f.Ok = true;
                f.Warn = c.Off ? "这块显示器现在没有连接；这张卡片已隐藏，接回来后也不会显示，重新显示后才会出现在它上面"
                    : "这块显示器现在没有连接，接回来后卡片和天气画面会出现在它上面";
                return f;
            }
            var list = AllCards();   // 含隐藏的：c 是刚取消隐藏的那张时，要按它在列表里原来的顺序排
            int at = list.FindIndex(k => k.Id == c.Id);
            if (at >= 0) list[at] = c.Clone(); else list.Add(c.Clone());
            f.Target = target;
            f.Area = AreaOf(target);
            f.On = list.Where(k => SameScreen(k.Screen, target) && !k.Hidden && !k.Off).ToList();
            f.Rects = CardLayout.Place(f.Area.Work, f.On);
            int me = f.On.FindIndex(k => k.Id == c.Id);
            if (me >= 0)
            {
                // 只算这次改动造成的：改之前（设置里的样子）放得下、现在放不下的卡片。本来就放不下的（换了分辨率、缩放）不算，
                // 不然这块屏上的卡片（包括这张自己）连名称、城市都改不了
                var before = settings.Cards.Where(k => SameScreen(k.Screen, target) && !k.Hidden && !k.Off).ToList();
                var br = CardLayout.Place(f.Area.Work, before);
                var placed = new HashSet<string>(before.Where((k, i) => br[i].HasValue).Select(k => k.Id));
                var saved = settings.Cards.FirstOrDefault(k => k.Id == c.Id);
                bool taller = saved != null && CardLayout.HeightOf(c) > CardLayout.HeightOf(saved) + 0.5f;   // 这次多显示了内容、卡片变高了：提示里先建议少显示几项
                // 自定义卡片的高度是直接设的；autoHeight 的卡片高度由内容定，设置里调不了
                var lower = c.IsCustom() ? (CustomCards.IsAuto(c.Script) ? "缩短卡片内容" : "调小卡片高度") : "少显示几项内容";
                if (!f.Rects[me].HasValue)
                {
                    // 本来就放不下、这次也没挪它（只改了名称、城市、显示内容等）：照常保存，只提示；挪到了另一个同样放不下的地方、或者刚取消隐藏 → 不保存
                    bool samePlace = saved != null && saved.Screen == c.Screen && saved.Pos == c.Pos && saved.OffsetX == c.OffsetX && saved.OffsetY == c.OffsetY
                        && saved.Hidden == c.Hidden && saved.Off == c.Off;
                    if (samePlace && !placed.Contains(c.Id))
                        f.Warn = ScreenName(target) + " 现在放不下这张卡片，桌面上暂时不显示：" + lower + "、换个位置、减小偏移，或者换一块屏";
                    else if (taller && samePlace) f.Msg = ScreenName(target) + " 放不下这么高的卡片：" + lower + "，或者换个位置、换一块屏";
                    else f.Msg = ScreenName(target) + " 已经放不下这张卡片了：换个位置、减小偏移，或者换一块屏";
                }
                else
                {
                    var lost = f.On.Where((k, i) => k.Id != c.Id && !f.Rects[i].HasValue && placed.Contains(k.Id)).FirstOrDefault();
                    if (lost != null) f.Msg = taller ? "卡片变高后会把「" + CardName(lost) + "」卡片挤得放不下：" + lower + "，或者换个位置"
                        : "这样摆会把「" + CardName(lost) + "」卡片挤得放不下，换个位置试试";
                }
            }
            f.Ok = f.Msg == null;
            return f;
        }

        string CardInitJson()
        {
            cardInitGen = ++cardGen;
            return Js.Serialize(new { t = "init", card = draft, autoName = AutoName() ?? "", gen = cardGen });
        }
        // 宿主这边改过正在编辑的卡片的次数，页面的编辑带回来对照：cardGen = 改过屏（显示器换号自愈）或重新初始化；
        // cardInitGen = 最近一次重新初始化页面（换卡片、设置文件被外部改了）时的值
        int cardGen, cardInitGen;

        /// <summary>放不放得下 + 页签（所有卡片）+ 屏幕下拉框（每块屏还能放几张）+ 所选屏的缩略图 + 画面跟随 / 新增按钮的状态。</summary>
        string CardFitJson()
        {
            if (draft == null) return "{\"t\":\"noop\"}";
            var f = Fit(draft);
            var everyCard = AllCards();   // 含隐藏（Off）的：页签、预览图里的虚线框要用
            var eff = everyCard.Where(k => !k.Off).ToList();
            var mine = everyCard.FirstOrDefault(k => k.Id == draft.Id) ?? draft;
            var all = Screen.AllScreens;
            var others = everyCard.Where(k => k.Id != draft.Id && !k.Hidden && !k.Off).ToList();   // 占位置的其他卡片
            int room = Settings.MaxCards - settings.Cards.Count;   // 按设置里的卡片数算（正在编辑的这张也占一个名额；Free 摆放时已把它算进去；隐藏的卡片也占名额）
            var screens = new List<object> { new { id = CardSpec.PrimaryScreen, label = "主屏（始终跟随主屏）" } };
            for (int i = 0; i < all.Length; i++)
            {
                var s = all[i];
                // 按正在编辑的这张卡片的位置和偏移往里塞，和真选过去时的判断一致
                int free = CardLayout.Free(AreaOf(s).Work, others.Where(k => SameScreen(k.Screen, s)).ToList(), draft);
                // 这张本来就在这块屏上：Free 把它占的位置也算成了空位，"还能再放"要扣掉，新增受总数名额限制；
                // 别的屏：Free 里的第一张就是这张挪过去（不占名额），后面的才是新增
                // 只显示天气画面的、隐藏的卡片不占位置：在本屏 Free 没把它算进去，不用扣。别的屏和可见卡片一样算上"这张挪过去"
                // （隐藏的以后要显示在那块屏上也得有它的位置；名额用满时也不至于每块屏都显示"放满了"、看着像挪不过去）
                free = !SameScreen(draft.Screen, s) ? Math.Min(room + 1, free)
                    : draft.Hidden || draft.Off ? Math.Min(room, free) : Math.Min(room, free - 1);
                free = Math.Max(0, free);
                var b = s.Bounds;
                screens.Add(new
                {
                    id = s.DeviceName,
                    label = ScreenName(s) + " · " + b.Width + "×" + b.Height + (s.Primary ? " · 主屏" : b.X < 0 ? " · 左侧" : b.X > 0 ? " · 右侧" : "")
                        + (free > 0 ? " · 还能放 " + free + " 张" : " · 放满了"),
                });
            }
            if (draft.Screen != CardSpec.PrimaryScreen && CardLayout.Resolve(draft.Screen) == null)
                screens.Add(new { id = draft.Screen, label = "未连接的显示器（" + (draft.Screen.StartsWith(CardSpec.AwayPrefix) ? "原 " + draft.Screen.Substring(CardSpec.AwayPrefix.Length) : draft.Screen) + "）" });

            object thumb = null;
            if (f.Target != null)
            {
                var boxes = new List<object>();
                // 隐藏（Off）的卡片：画成虚线框 + 闭眼（排在前面，被正常的卡片盖住），位置是"重新显示出来会摆在哪"——
                // 就按点「显示卡片」时的判断（Fit）排一遍：放不下、或者会把别的卡片挤掉（显示不了）就标红，画在它期望的位置
                foreach (var k in everyCard.Where(x => x.Off && !x.Hidden && SameScreen(x.Screen, f.Target)))
                {
                    var probe = k.Clone();
                    probe.Off = false;
                    var pf = Fit(probe);
                    int at = pf.On == null ? -1 : pf.On.FindIndex(x => x.Id == k.Id);
                    var r = (at >= 0 ? pf.Rects[at] : null) ?? CardLayout.Desired(f.Area.Work, probe);
                    boxes.Add(new { id = k.Id, x = r.X, y = r.Y, w = r.Width, h = r.Height, me = k.Id == draft.Id, bad = !pf.Ok, off = true, scene = false, name = CardName(k) });
                }
                for (int i = 0; i < f.On.Count; i++)
                {
                    var k = f.On[i];
                    bool me = k.Id == draft.Id;
                    var r = f.Rects[i] ?? (me ? CardLayout.Desired(f.Area.Work, k) : (RectangleF?)null);
                    if (r == null) continue;
                    var ek = eff.FirstOrDefault(x => x.Id == k.Id) ?? k;
                    boxes.Add(new { id = k.Id, x = r.Value.X, y = r.Value.Y, w = r.Value.Width, h = r.Value.Height, me, bad = !f.Rects[i].HasValue, off = false, scene = ek.Scene, name = CardName(k) });
                }
                var wk = f.Area.Work;
                thumb = new { w = f.Area.Size.Width, h = f.Area.Size.Height, work = new[] { wk.X, wk.Y, wk.Width, wk.Height }, boxes, name = ScreenName(f.Target) };
            }

            // 画面跟随勾选框：这块屏只有这一张 / 这张只显示天气画面 → 一定是它（打勾禁用）；同屏有只显示天气画面的卡片 → 一定是那张（不勾禁用）；
            // 这张隐藏了 → 不参与（不勾禁用）
            // 自定义卡片不算：它们拿不了画面跟随（CommitDraft / NormalizeScenes 都跳过它们），算进来的话"只有这一张天气卡片"时勾选框能点、点了又弹回去
            var group = eff.Where(k => !k.IsCustom() && CardLayout.GroupKey(k) == CardLayout.GroupKey(draft)).ToList();
            var hid = group.FirstOrDefault(k => k.Hidden && k.Scene && k.Id != draft.Id);   // 这块屏上拿着画面跟随的、只显示天气画面的卡片（NormalizeScenes 让第一张这样的卡片拿）
            string sceneHint = draft.IsCustom() ? "自定义卡片没有城市，不决定天气画面"
                : draft.Off ? "这张卡片已隐藏，不参与天气画面；重新显示后才能设置"
                : hid != null ? "这块屏的天气画面由只显示天气画面的卡片「" + CardName(hid) + "」决定"
                : draft.Hidden ? "这张卡片只显示天气画面，就是用来决定这块屏的天气画面的"
                : group.Count <= 1 ? (eff.Any(k => k.IsCustom() && CardLayout.GroupKey(k) == CardLayout.GroupKey(draft)) ? "这块屏上只有这一张天气卡片" : "这块屏上只有这一张卡片") + "，天气画面一定跟随它" : null;

            // 和 AddCard 同样的条件：新增时会丢掉正在编辑的卡片放不下、没保存的改动，所以按设置里的样子算
            var keep = draft;
            bool canAdd;
            // 自定义卡片的高度各不相同（脚本里写的 height）：按高度试放不放得下。卡片一样宽、排版是按高度往下摞，高的放得下矮的一定放得下，
            // 放得下的是最矮的那几种：先试最高的（通常就放得下，只试一次），再试最矮的（放不下就都放不下），都不是再二分找分界。
            // 拖动、勾选时每次都要算，别每种高度都排一遍
            var byHeight = cardScripts.GroupBy(s => s.Height).OrderBy(g => g.Key).ToList();
            int fitCount = 0;   // 最矮的 fitCount 种放得下
            try
            {
                draft = null;
                bool spare = settings.Cards.Count < Settings.MaxCards;
                canAdd = spare && NewCard(probe: true) != null;
                Func<int, bool> fitsAt = i => NewCard(byHeight[i].First().File, probe: true) != null;
                int n = byHeight.Count;
                if (spare && n > 0)
                {
                    if (fitsAt(n - 1)) fitCount = n;
                    else if (n > 1 && fitsAt(0))
                    {
                        int lo = 1, hi = n - 1;   // [0, lo) 放得下，[hi, n) 放不下
                        while (lo < hi) { int mid = (lo + hi) / 2; if (fitsAt(mid)) lo = mid + 1; else hi = mid; }
                        fitCount = lo;
                    }
                }
            }
            finally { draft = keep; }
            var fits = new HashSet<int>(byHeight.Take(fitCount).Select(g => g.Key));
            bool canAddCustom = fitCount > 0;
            string addHint = settings.Cards.Count >= Settings.MaxCards ? "最多 " + Settings.MaxCards + " 张卡片" : !canAdd && !canAddCustom ? "所有屏幕都放不下新卡片了" : null;
            var cards = everyCard.Select(k => new { id = k.Id, name = CardName(k), scene = k.Scene, hidden = k.Hidden, off = k.Off, custom = k.IsCustom() }).ToList();
            var warn = string.Join("；", new[] { f.Warn, !settings.ShowPanel ? "设置文件里 ShowPanel 为 false：桌面上不显示任何卡片（只有天气画面），改成 true 才显示" : null }.Where(s => s != null));
            // 偏移能调到多大：再大卡片也已经贴边、不会再动（方向键、± 按到这里为止）。按左右 / 上下贴边算，居中位置页面自己减半；
            // 只跟所选的屏有关（scr），页面换了屏、新结果还没到时不用它
            float ch = CardLayout.HeightOf(draft);
            int[] lim = f.Area == null ? null : new[] { (int)Math.Max(0, f.Area.Work.Width - CardLayout.W), (int)Math.Max(0, f.Area.Work.Height - ch) };
            // 设置里保存的位置和显示内容：页面在放不下（没保存）时点「隐藏卡片」，先换回它再隐藏——没保存的改动作废，和换卡片、关窗口一样
            var stored = settings.Cards.FirstOrDefault(k => k.Id == draft.Id);
            var saved = stored == null ? null : new { scr = stored.Screen, pos = stored.Pos, x = stored.OffsetX, y = stored.OffsetY, show = stored.Show, height = stored.Height };
            // 自定义卡片：cards 文件夹里的脚本（下拉框、新增菜单用；btn = 「刷新」按钮上的字，null = 没有这个按钮）；这张卡片现在的错误（桌面上不显示它的原因）
            var scripts = cardScripts.Select(s => new { f = s.File, name = s.Title ?? "", auto = AutoTitle(s, s.File), height = s.Height, fits = fits.Contains(s.Height), btn = s.Button, autoH = s.Auto }).ToList();   // auto 是自动取的卡片名；autoH = 脚本写了 autoHeight（高度随内容变）
            string cerr;
            if (!draft.IsCustom() || draft.Off || !cardErrs.TryGetValue(draft.Id, out cerr)) cerr = "";
            // 拖动吸附用：卡片间距、默认边距（页面 CSS 像素），和这边的排版一致；cw / ch = 这张卡片的大小（「卡片内容」里显示）
            return Js.Serialize(new { t = "fit", id = draft.Id, scr = draft.Screen, ok = f.Ok, msg = f.Msg ?? "", warn, lim, scene = mine.Scene, sceneLock = sceneHint != null, sceneHint = sceneHint ?? "", addable = addHint == null, addHint = addHint ?? "",
                addWeather = canAdd, addCustom = canAddCustom, cards, screens, thumb, saved, scripts, cerr, hmin = CustomCards.MinHeight, hmax = CustomCards.MaxHeight,
                gap = CardLayout.Gap, margin = CardSpec.DefaultOffset, cw = CardLayout.W, ch });
        }

        // ---------------------------------------------------------------- 设置文件
        FileSystemWatcher settingsWatcher;
        readonly WinTimer settingsReload = new WinTimer { Interval = 400 };

        /// <summary>在资源管理器里打开自定义效果文件夹（不在了就重新建上、放回示例）。</summary>
        void OpenEffectsDir()
        {
            Effects.EnsureDir();
            OpenUrl("explorer.exe", "\"" + Effects.Dir + "\"");
        }

        /// <summary>在资源管理器里打开自定义卡片文件夹（不在了就重新建上、放回示例）。</summary>
        void OpenCardsDir()
        {
            CustomCards.EnsureDir();
            RescanCards();   // 刚重新建出来、放了示例：卡片设置的下拉框马上就有
            OpenUrl("explorer.exe", "\"" + CustomCards.Dir + "\"");
        }

        /// <summary>用记事本打开 settings.json（格式化成多行便于编辑），保存后自动重新加载。</summary>
        void OpenSettingsFile()
        {
            // 先格式化写回（文件被外部改过的话 Save 不写、改为重新加载）；文件写坏了：原样打开让用户修，别先用内存里的设置把它盖掉
            settings.Save(keepBroken: true);
            OpenUrl("notepad.exe", "\"" + Settings.FilePath + "\"");
        }

        void WatchSettings()
        {
            settingsWatcher = new FileSystemWatcher(Settings.AppDataDir, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            var ui = SynchronizationContext.Current;
            FileSystemEventHandler h = (s, e) => ui?.Post(_ => { reloadRetries = 0; settingsReload.Stop(); settingsReload.Start(); }, null);   // 新的文件事件：重读次数重新算
            settingsWatcher.Changed += h; settingsWatcher.Created += h;
            settingsWatcher.Renamed += (s, e) => h(s, e);
            // env == null：Init 还没走完（WebView2、缓存迁移都没好），先不重新加载；Init 末尾会主动对一次，这期间的改动不会丢
            settingsReload.Tick += (s, e) => { settingsReload.Stop(); if (quitStage == 0 && env != null) ReloadSettings(); };
            // 写盘时发现文件被外部改过（Save 没写）：排一次重新加载，以文件为准（监视器漏了事件也不会一直卡在"写不进去"）
            Settings.ExternalChange += () => { if (quitStage == 0) { settingsReload.Stop(); settingsReload.Start(); } };
            settingsWatcher.EnableRaisingEvents = true;
        }

        /// <summary>
        /// 自定义效果文件夹：增删、改了 .js 就重新扫一遍，列表变了通过 layout 告诉页面（页面整页重载、重新加载脚本）。
        /// 监视整个设置目录（含子目录）而不是 effects 本身：effects 文件夹被删掉再建回来也照样能收到
        /// </summary>
        void WatchEffects()
        {
            Effects.EnsureDir();
            effects = Effects.Scan();
            effectsJson = Effects.Json(effects);
            if (effects.Count > 0) Log.Write("effects: " + string.Join(", ", effects.Select(e => e.Key)));
            // 自定义卡片文件夹也在设置目录下，共用这一个监视器（各自去抖、各自重扫）
            CustomCards.EnsureDir();
            cardScripts = CustomCards.Scan();
            cardScriptsSig = CustomCards.Sig(cardScripts);
            if (cardScripts.Count > 0) Log.Write("card scripts: " + string.Join(", ", cardScripts.Select(s => s.File)));
            var dir = Effects.Dir.TrimEnd('\\') + "\\";
            var cdir = CustomCards.Dir.TrimEnd('\\') + "\\";
            var ui = SynchronizationContext.Current;
            effectsRescan.Tick += (s, e) => { effectsRescan.Stop(); if (quitStage == 0) RescanEffects(); };
            cardsRescan.Tick += (s, e) => { cardsRescan.Stop(); if (quitStage == 0) RescanCards(); };
            RetryUnread(cardScripts);
            try
            {
                // Attributes：设成隐藏 / 去掉隐藏也要重扫（隐藏的脚本不列出来、不运行）
                effectsWatcher = new FileSystemWatcher(Settings.AppDataDir) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes };
                Action<string> h = p =>
                {
                    if (p == null) return;
                    var q = p.TrimEnd('\\') + "\\";   // 文件夹本身被删 / 改名时，事件里是不带结尾 \ 的文件夹路径
                    // settings.json 之类的不管；编辑器保存时往往连着来好几个事件：合并成一次
                    if (q.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) ui?.Post(_ => { effectsRescan.Stop(); effectsRescan.Start(); }, null);
                    else if (q.StartsWith(cdir, StringComparison.OrdinalIgnoreCase)) ui?.Post(_ => { cardRetries = 0; cardsRescan.Stop(); cardsRescan.Start(); }, null);   // 新的文件事件：重扫次数重新算
                };
                effectsWatcher.Changed += (s, e) => h(e.FullPath);
                effectsWatcher.Created += (s, e) => h(e.FullPath);
                effectsWatcher.Deleted += (s, e) => h(e.FullPath);
                effectsWatcher.Renamed += (s, e) => { h(e.FullPath); h(e.OldFullPath); };
                // 卡片拖动等高频写盘可能挤爆监视缓冲（事件被丢）：出了错就补扫一次，最多晚 600 毫秒，不会一直丢
                effectsWatcher.Error += (s, e) =>
                {
                    Log.Write("effects watch: " + e.GetException().Message);
                    ui?.Post(_ => { effectsRescan.Stop(); effectsRescan.Start(); cardsRescan.Stop(); cardsRescan.Start(); }, null);
                };
                effectsWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.Write("effects watch: " + ex.Message); }
        }

        /// <summary>自定义卡片的脚本增删、改过：layout 里的版本号跟着变，页面重新加载用到它的卡片；卡片设置的下拉框跟着更新（UpdateLayout 里发）。</summary>
        void RescanCards()
        {
            var list = CustomCards.Scan();
            RetryUnread(list);
            var sig = CustomCards.Sig(list);
            if (sig == cardScriptsSig) return;
            cardScripts = list; cardScriptsSig = sig;
            Log.Write("card scripts changed: " + (list.Count == 0 ? "(none)" : string.Join(", ", list.Select(s => s.File))));
            foreach (var w in walls) w.RetryCards();   // 因为页面接连崩溃停掉的自定义卡片：改过脚本了，再试一次
            UpdateLayout();
        }

        int cardRetries;   // 有脚本被占着读不出来时，连着重扫的次数
        /// <summary>有脚本正好被占用、没读出名称高度（编辑器、杀毒软件占着）：过一会儿再扫，最多 10 次（6 秒）——占用结束不一定再有文件事件，不重扫的话要等它下次被改才读到。</summary>
        void RetryUnread(List<CustomCards.Script> list)
        {
            if (!list.Any(s => s.Unread)) { cardRetries = 0; return; }
            if (++cardRetries <= 10) { cardsRescan.Stop(); cardsRescan.Start(); }
            else if (cardRetries == 11) Log.Write("card scripts: still locked, gave up: " + string.Join(", ", list.Where(s => s.Unread).Select(s => s.File)));
        }

        void RescanEffects()
        {
            var list = Effects.Scan();
            var j = Effects.Json(list);
            if (j == effectsJson) return;
            effects = list; effectsJson = j;
            Log.Write("effects changed: " + (list.Count == 0 ? "(none)" : string.Join(", ", list.Select(e => e.Key))));
            UpdateLayout();
        }

        int reloadRetries;   // 文件被占着读不出来时，连着重读的次数
        void ReloadSettings()
        {
            // 自己保存触发的：文件就是上次写进去的内容。之后内存里可能又改了、还没写盘（卡片设置 0.5 秒合并写盘），不能拿文件盖回去
            string text = null;
            try { text = File.ReadAllText(Settings.FilePath); } catch { }
            var n = Settings.TryLoad(text, out var used);   // 用刚读的这份解析：再读一遍的话间隙变了会错位；外层读失败时 used 是 TryLoad 自己重读的那份
            // used == null：文件这会儿不存在（被改名备份、挪走，或编辑器先改名再写的保存方式）。TryLoad 给的是默认值，不能拿来盖掉内存；
            // 文件回来时会再有事件。真被删了也不重建：下次写盘时照常写出来
            if (used == null)
            {
                // 文件在、但重试几次都读不出来（编辑器 / 杀毒 / 同步软件占着）：这次的事件不能就这么吞掉，过一会儿再读
                // 最多再试 10 次（每次 TryLoad 自己要等约 300 毫秒，在 UI 线程上；一直被占着就算了，等下一次文件事件或写盘时的比较）
                if (File.Exists(Settings.FilePath) && ++reloadRetries <= 10) settingsReload.Start();
                else if (reloadRetries > 10) Log.Write("settings reload: file still locked, gave up");
                return;
            }
            reloadRetries = 0;
            if (used == Settings.LastWritten) return;
            if (n == null) { Log.Write("settings reload: parse failed, ignored"); return; }
            Settings.LastWritten = used;   // 内存和文件现在一致：之后用户把文件改回我们上次写的内容，也要能认出来是外部修改
            if (n.Same(settings)) { if (n.NeedsSave()) settings.Save(); return; }   // 整理后和内存一样，但文件本身要整理（如超过 8 张、缺默认卡片）：写回
            bool rebuildWalls = n.Layered != settings.Layered || n.DevTools != settings.DevTools;   // 别叫 walls：会遮住同名字段（壁纸窗口列表）
            bool cards = Js.Serialize(n.Cards) != Js.Serialize(settings.Cards);
            bool backdrop = n.Minimal != settings.Minimal || n.OverlayWall != settings.OverlayWall;
            cardSaveTimer.Stop();   // 以文件为准：排着的卡片合并写盘作废，别过后拿 CopyFrom 之后的内存再写一遍
            settings.CopyFrom(n);
            if (n.NeedsSave()) settings.Save();   // 补了卡片 Id 等：写回，下次重新加载时 Id 不变
            Log.Write("settings reloaded");
            bool editing = cards && cardWin != null && draft != null;
            // 卡片设置窗口开着时设置文件被改了：窗口换成文件里的样子（编辑中的卡片被删了就换到默认卡片）；先换 draft 再整理屏幕，整理的是新的这张
            if (editing) draft = (settings.Cards.FirstOrDefault(k => k.Id == draft.Id) ?? settings.Cards[0]).Clone();
            if (cards) HealAllScreens();
            if (editing)
            {
                cardWin.Post(CardInitJson());
                cardWin.Post(CardFitJson());
            }
            overlay.SetFps(settings.Fps);
            if (cards) SyncCities();
            if (rebuildWalls) RebuildNow();   // 排着的重建、等着的 restore 一并处理，别过后再来一遍
            else { ConfigureOverlay(); if (backdrop) UpdateBackdrops(false); }
            ApplyFlyoutMode();
            PostAbout();   // 「自动检查更新」可能在文件里改了
            Watch();
        }

        // ---------------------------------------------------------------- 检查更新
        UpdateInfo newer;
        ToolStripMenuItem updateItem;
        bool checking, installing;
        string updError, installError, installText;   // 上次检查失败的原因 / 上次自动更新失败的原因 / 正在下载安装的进度（「关于」里显示）
        DateTime lastCheck;             // 上次检查成功的时间（MinValue = 还没查过）
        readonly WinTimer updateTimer = new WinTimer();

        void StartUpdateTimer()
        {
            // 启动 1 分钟后查一次；之后每小时看一眼，离上次查到满 12 小时才再查——按钟表时间算，电脑睡眠、休眠过也不会一拖好几天；
            // 查失败了（没联网）下个小时再试
            updateTimer.Interval = 60 * 1000;
            updateTimer.Tick += (s, e) =>
            {
                updateTimer.Interval = 3600 * 1000;
                if (settings.AutoUpdate && !Updater.IsDevBuild && (DateTime.Now - lastCheck).Duration() >= TimeSpan.FromHours(12)) CheckUpdate(false);
            };
            updateTimer.Start();
        }

        /// <summary>有要提示的新版本：查到了，而且不是用户选了「跳过此版本」的那个（托盘菜单、弹出面板只提示这种；「关于」里跳过的也照样显示）。</summary>
        bool NewerOffered => newer != null && settings.SkipVersion != newer.Version.ToString();

        string UpdateJson() => !NewerOffered ? "{\"t\":\"update\",\"tag\":null}"
            : "{\"t\":\"update\",\"tag\":" + WeatherService.JsonStr(newer.Tag) + ",\"cur\":" + WeatherService.JsonStr(Updater.CurrentText) + "}";

        /// <summary>检查更新。manual：命令行 checkupdate manual，结果用对话框告诉用户；「关于」里点的不弹框，结果显示在窗口里。</summary>
        async void CheckUpdate(bool manual)
        {
            if (checking) return;
            checking = true;
            updError = null;
            PostAbout();
            try
            {
                var info = await Updater.Check();
                lastCheck = DateTime.Now;
                Log.Write("update: latest " + info.Tag + ", current " + Updater.Current);
                if (info.Version <= Updater.Current)
                {
                    SetNewer(null);   // 之前查到的新版本又撤了
                    if (manual) MessageBox.Show("已经是最新版本（v" + Updater.Current + "）。", "天气壁纸", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                // 不弹托盘通知，只在右键菜单（加粗）、左键弹出面板和「关于」里提示
                SetNewer(info);
                if (manual) PromptUpdate(info);
            }
            catch (Exception ex)
            {
                updError = ex.Message;
                Log.Write("update check: " + ex.Message);
                if (manual) MessageBox.Show("检查更新失败：" + ex.Message + "\n\n可以稍后再试，或直接打开发布页：\n" + Updater.ReleasePage, "天气壁纸", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { checking = false; PostAbout(); }
        }

        /// <summary>记下查到的新版本（null = 没有）：右键菜单里的更新项只在有新版本时出现（菜单弹出时按 newer 决定显示）。</summary>
        void SetNewer(UpdateInfo info)
        {
            if (info?.Tag != newer?.Tag) installError = null;   // 换了版本：上一个版本的安装失败不再相关
            newer = info;
            if (info != null) updateItem.Text = "发现新版本 " + info.Tag + "，点击更新（当前 " + Updater.CurrentText + "）";
            flyout?.Post(UpdateJson());
        }

        async void PromptUpdate(UpdateInfo info)
        {
            if (installing) return;
            if (Updater.IsDevBuild)
            {
                // 源码构建的版本不自动覆盖（会改掉构建输出目录），只引导去下载页
                if (MessageBox.Show("发现新版本 " + info.Tag + "。\n\n当前运行的是从源码构建的开发版，不会自动更新。\n是否打开下载页？", "天气壁纸 · 更新",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    OpenUrl(Updater.ReleasePage);
                return;
            }
            var res = MessageBox.Show("发现新版本 " + info.Tag + "（当前 v" + Updater.Current + "）。\n\n是否现在下载并更新？完成后会自动重新启动。\n选\"否\"跳过此版本，\"取消\"下次再说。",
                "天气壁纸 · 更新", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
            if (res == DialogResult.No) { settings.SkipVersion = info.Version.ToString(); settings.Save(); flyout?.Post(UpdateJson()); PostAbout(); return; }
            if (res != DialogResult.Yes) return;
            await InstallUpdate(info);
        }

        async Task InstallUpdate(UpdateInfo info)
        {
            if (installing) return;
            if (Updater.IsDevBuild) { Log.Write("update: dev build, skipped"); return; }
            installing = true;
            installError = null;
            installText = "正在准备更新 " + info.Tag + " …";
            PostAbout();
            try
            {
                var progress = new Progress<string>(t => { updateItem.Text = t; tray.Text = "天气壁纸 · " + t; installText = t; PostAbout(); });
                await Updater.Install(info, progress);
                Quit();   // 让出文件给安装程序；装完由安装包重新拉起
            }
            catch (Exception ex)
            {
                installing = false;
                installError = "自动更新失败：" + ex.Message;
                PostAbout();
                tray.Text = "天气壁纸";   // 下载进度留在托盘提示上会让人以为还在更新
                updateItem.Text = "更新到 " + info.Tag + "（当前 " + Updater.CurrentText + "）";
                Log.Write("update install: " + ex);
                if (MessageBox.Show("自动更新失败：" + ex.Message + "\n\n是否打开下载页手动安装？", "天气壁纸 · 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    OpenUrl(info.MsiUrl ?? Updater.ReleasePage);
            }
        }

        static void OpenUrl(string url, string args = null)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url, args ?? "") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("open url: " + ex.Message); }
        }

        // ---------------------------------------------------------------- 关于
        // 版本、项目地址、检查更新（含自动检查开关）、设置文件和日志目录。页面是 web/about.html
        PageWindow aboutWin;
        static string aboutIcon;   // 程序图标（PNG data URL），第一次打开时生成

        void OpenAbout()
        {
            if (env == null) return;
            if (aboutWin == null)
            {
                aboutWin = new PageWindow(env, "关于 · 天气壁纸", "about.html", 460, 666) { MinimizeBox = false, OnMessage = OnAboutMessage };
                aboutWin.Warm();
                aboutWin.Reloaded = () => { PostAboutIcon(); PostAbout(); };
                aboutWin.FormClosed += (s, e) => aboutWin = null;
                PostAboutIcon();
                PostAbout();
                aboutWin.Show();
                Native.SetForegroundWindow(aboutWin.Handle);
            }
            else if (aboutWin.WindowState == FormWindowState.Minimized) aboutWin.WindowState = FormWindowState.Normal;
            aboutWin.Activate();
        }

        void PostAbout() { if (aboutWin != null) aboutWin.Post(AboutJson()); }

        // 图标几十 KB，单独一条消息，每次页面加载只发一次（状态消息在下载更新时会随进度频繁刷新）
        void PostAboutIcon()
        {
            if (string.IsNullOrEmpty(aboutIcon)) aboutIcon = IconPng(128);   // 取不到（""）不缓存，下次打开再试
            aboutWin?.Post(Js.Serialize(new { t = "icon", src = aboutIcon }));
        }

        string AboutJson()
        {
            // 更新状态：正在安装 > 正在检查 > 有新版本 > 上次检查失败 > 已是最新 > 还没查过
            var state = installing ? "installing" : checking ? "checking" : newer != null ? "newer" : updError != null ? "error" : lastCheck != DateTime.MinValue ? "latest" : "idle";
            return Js.Serialize(new
            {
                t = "about",
                ver = Updater.CurrentText,
                kind = Updater.IsDevBuild ? "从源码构建，不自动更新" : Updater.Installed ? "安装版" : "便携版",
                dev = Updater.IsDevBuild,
                auto = settings.AutoUpdate,
                state,
                tag = newer?.Tag,
                skipped = newer != null && settings.SkipVersion == newer.Version.ToString(),
                // 有新版本时只显示自动更新失败的原因（之后某次后台检查没连上不算，新版本照样有效）
                msg = installing ? installText : state == "newer" ? installError : updError,
                at = lastCheck == DateTime.MinValue ? null : lastCheck.ToString(lastCheck.Date == DateTime.Today ? "HH:mm" : "M 月 d 日 HH:mm"),
                repo = Updater.RepoPage,
            });
        }

        /// <summary>
        /// 内嵌 app.ico 里 size×size 那一帧的 PNG（data URL），取不到返回 ""。128 / 256 两帧在 ico 里本来就是 PNG（见 tools/make-icon.ps1），
        /// 直接从 ico 目录里按偏移取出来——System.Drawing.Icon 读 PNG 帧不可靠。
        /// </summary>
        static string IconPng(int size)
        {
            try
            {
                byte[] b;
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("WeatherWallpaper.app.ico"))
                using (var ms = new MemoryStream())
                {
                    if (s == null) return "";
                    s.CopyTo(ms);
                    b = ms.ToArray();
                }
                int n = b.Length >= 6 ? BitConverter.ToUInt16(b, 4) : 0;
                for (int i = 0; i < n && 6 + 16 * i + 16 <= b.Length; i++)
                {
                    int e = 6 + 16 * i, w = b[e] == 0 ? 256 : b[e];
                    int len = BitConverter.ToInt32(b, e + 8), off = BitConverter.ToInt32(b, e + 12);
                    if (w == size && off > 0 && len > 8 && off + len <= b.Length && b[off] == 0x89 && b[off + 1] == (byte)'P')
                        return "data:image/png;base64," + Convert.ToBase64String(b, off, len);
                }
            }
            catch (Exception ex) { Log.Write("about icon: " + ex.Message); }
            return "";
        }

        void OnAboutMessage(string json)
        {
            Dictionary<string, object> d;
            try { d = Js.Deserialize<Dictionary<string, object>>(json); } catch { return; }
            if (d == null || aboutWin == null) return;
            switch (d.ContainsKey("t") ? d["t"] as string : null)
            {
                case "close": aboutWin.CloseNow(); break;
                case "check": CheckUpdate(false); break;
                case "install":   // 「关于」里点的「立即更新」：用户已经明确要更新，不再弹确认框
                    if (newer == null || installing) break;
                    if (Updater.IsDevBuild) OpenUrl(Updater.ReleasePage);   // 源码构建的版本不覆盖，去下载页
                    else { var _ = InstallUpdate(newer); }
                    break;
                case "auto":
                    settings.AutoUpdate = d.ContainsKey("on") && d["on"] is bool && (bool)d["on"];
                    settings.Save();
                    PostAbout();
                    break;
                case "open":
                    switch (d.ContainsKey("what") ? d["what"] as string : null)
                    {
                        case "repo": OpenUrl(Updater.RepoPage); break;
                        case "releases": OpenUrl(Updater.ReleasePage); break;
                        case "issues": OpenUrl(Updater.IssuesPage); break;
                        case "settings": OpenSettingsFile(); break;
                        case "logs": OpenUrl("explorer.exe", "\"" + Settings.LocalDir + "\""); break;
                        case "effects": OpenEffectsDir(); break;
                        case "cards": OpenCardsDir(); break;
                    }
                    break;
            }
        }

        int quitStage;   // 0 = 没在退出，1 = 等卡片设置窗口交上编辑后关掉，2 = 正在退出

        void Quit()
        {
            if (quitStage == 2) return;
            // 卡片设置开着：先按关窗口的流程让页面把还没发出来的编辑交上来（最多等 0.5 秒），窗口关掉后再接着退出
            if (cardWin != null)
            {
                if (quitStage == 1) return;   // 已经在等了
                quitStage = 1;
                cardWin.FormClosed += (s, e) => Quit();
                cardWin.Close();
                return;
            }
            quitStage = 2;
            watchTimer.Stop(); weatherTimer.Stop(); updateTimer.Stop(); settingsWatcher?.Dispose(); effectsWatcher?.Dispose(); effectsRescan.Stop();
            tray.Visible = false;
            draft = null;
            FlushCardSave();
            foreach (var w in walls) w.Dispose();
            walls.Clear();
            overlay.Dispose();
            eaves.Dispose();
            flyout?.Dispose();
            aboutWin?.CloseNow();
            shell.DestroyHandle();
            DesktopHost.RestoreSystemWallpaper();
            ExitThread();
        }
    }

    /// <summary>隐藏顶层窗口，用来接收 Explorer 重启广播的 TaskbarCreated。</summary>
    class ShellWatcher : NativeWindow
    {
        const string Caption = "WeatherWallpaperShellWatcher";
        readonly uint msgTaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        readonly Action onRestart;
        public Action<string> OnCommand;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }
        [System.Runtime.InteropServices.DllImport("user32")]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref COPYDATASTRUCT l);

        public ShellWatcher(Action onRestart)
        {
            this.onRestart = onRestart;
            CreateHandle(new CreateParams { Caption = Caption, Style = unchecked((int)Native.WS_POPUP), ExStyle = (int)Native.WS_EX_TOOLWINDOW });
        }

        public static void Send(string cmd)
        {
            var h = Native.FindWindow(null, Caption);
            if (h == IntPtr.Zero) return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(cmd);
            var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, ptr, bytes.Length);
                var cds = new COPYDATASTRUCT { dwData = new IntPtr(0x5757), cbData = bytes.Length, lpData = ptr };
                SendMessage(h, 0x4A, IntPtr.Zero, ref cds);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == (int)msgTaskbarCreated) { Log.Write("explorer restarted"); onRestart(); }
            if (m.Msg == 0x4A)
            {
                var cds = (COPYDATASTRUCT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(COPYDATASTRUCT));
                if (cds.dwData == new IntPtr(0x5757) && cds.cbData > 0 && cds.cbData < 4096)
                {
                    var b = new byte[cds.cbData];
                    System.Runtime.InteropServices.Marshal.Copy(cds.lpData, b, 0, b.Length);
                    var cmd = System.Text.Encoding.UTF8.GetString(b);
                    BeginInvokeCommand(cmd);
                    m.Result = new IntPtr(1);
                    return;
                }
            }
            base.WndProc(ref m);
        }

        void BeginInvokeCommand(string cmd)
        {
            // 不在 SendMessage 的同步回调里做重活
            var t = new WinTimer { Interval = 1 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); OnCommand?.Invoke(cmd); };
            t.Start();
        }
    }
}
