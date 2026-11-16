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
        string payload, preview = "live", sceneText = "";
        bool previewDay;
        DateTime previewUntil;
        readonly WinTimer previewTimer = new WinTimer { Interval = 1000 };
        // 天气预览的时长与极简模式每次播放的时长是同一个设置（PlaySeconds）；一天快进固定 48 秒走完一遍后恢复
        const int DayDemoSeconds = 49;
        Loc loc;
        DateTime locTime;
        bool refreshing, locked;
        string layeredMode, perfText = "";
        readonly PerfMonitor perf = new PerfMonitor();

        static string CachePath => Path.Combine(Settings.LocalDir, "cache.json");

        public App()
        {
            shell = new ShellWatcher(() => ScheduleRebuild(2500)) { OnCommand = OnCommand };
            BuildTray();
            overlay.SetFps(settings.Fps);
            rebuildTimer.Tick += (s, e) => { rebuildTimer.Stop(); BuildWalls(); };
            weatherTimer.Tick += (s, e) => Refresh(false);
            previewTimer.Tick += (s, e) => { if (DateTime.Now >= previewUntil) SetPreview("live", false); };
            watchTimer.Interval = 700;
            watchTimer.Tick += (s, e) => Watch();
            SystemEvents.DisplaySettingsChanged += (s, e) => ScheduleRebuild(1500);
            SystemEvents.SessionSwitch += (s, e) =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
                { locked = true; Log.Write("session: lock (" + e.Reason + ")"); }
                else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
                { locked = false; Log.Write("session: unlock (" + e.Reason + ")"); }
                Watch();
            };
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General) ConfigureOverlay();
                if (e.Category == UserPreferenceCategory.Desktop && settings.Minimal) { backdropTimer.Stop(); backdropTimer.Start(); }   // 换了系统壁纸
            };
            backdropTimer.Tick += (s, e) => { backdropTimer.Stop(); UpdateBackdrops(true); };
            SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == PowerModes.Resume) { weatherTimer.Stop(); weatherTimer.Interval = 15000; weatherTimer.Start(); }
            };
            StartUpdateTimer();
            WatchSettings();
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
            try { if (File.Exists(CachePath)) payload = File.ReadAllText(CachePath); } catch { }
            BuildWalls();
            watchTimer.Start();
            Refresh(true);
            // 壁纸起来后再在后台预热托盘面板，不和壁纸抢启动时间
            var warm = new WinTimer { Interval = 4000 };
            warm.Tick += (s, e) => { warm.Stop(); warm.Dispose(); ApplyFlyoutMode(); };
            warm.Start();
        }

        // ---------------------------------------------------------------- 壁纸窗口
        void ScheduleRebuild(int ms)
        {
            rebuildTimer.Stop();
            rebuildTimer.Interval = ms;
            rebuildTimer.Start();
        }

        void BuildWalls()
        {
            if (env == null) return;
            foreach (var w in walls) w.Dispose();
            walls.Clear();
            if (!DesktopHost.Prepare())
            {
                Log.Write("desktop not ready, retry");
                ScheduleRebuild(3000);
                return;
            }
            layeredMode = settings.Layered;
            bool layered = layeredMode == "on" || (layeredMode == "auto" && DesktopHost.Raised);
            foreach (var s in SelectedScreens())
            {
                var w = new WallpaperWindow(s, env, layered, settings.DevTools);
                w.OnReady += OnWallReady;
                w.OnString += OnWallString;
                w.OnJson += OnWallJson;
                walls.Add(w);
            }
            DesktopHost.FixZOrder(walls.Select(w => w.Handle).ToList());
            ConfigureOverlay();
            UpdateBackdrops(false);
        }

        /// <summary>settings.Monitors: "all" / "primary" / 某块屏的 DeviceName。指定的屏不存在时退回主屏。</summary>
        List<Screen> SelectedScreens()
        {
            var all = Screen.AllScreens;
            var m = settings.Monitors ?? "all";
            if (m == "all") return all.ToList();
            var pick = all.Where(s => s.DeviceName == m).ToList();
            return pick.Count > 0 ? pick : all.Where(s => s.Primary).ToList();
        }

        /// <summary>显示天气卡片、上报场景的那块：主屏在显示就用主屏，否则用第一块。</summary>
        WallpaperWindow MainWall => walls.FirstOrDefault(w => w.Primary) ?? walls.FirstOrDefault();

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
            var prim = Screen.PrimaryScreen.Bounds;
            float scale = tbh != IntPtr.Zero ? Native.GetDpiForWindow(tbh) / 96f : 1f;
            if (scale <= 0) scale = 1;
            // 任务栏效果只画在主屏任务栏上：壁纸没显示在主屏（如设成仅副屏）就不画
            overlay.Configure(tb, prim, scale, settings.TaskbarFx && walls.Any(w => w.Primary));
            foreach (var w in walls) w.Post(LayoutJson(w, tb));
        }

        string LayoutJson(WallpaperWindow w, Rectangle? tb)
        {
            double ground = 0;
            bool hasTb = tb.HasValue && w.Bounds.Contains(new Point(tb.Value.X + tb.Value.Width / 2, tb.Value.Y + tb.Value.Height / 2));
            if (hasTb) ground = (w.Bounds.Bottom - tb.Value.Top) / (double)w.Bounds.Height;
            bool main = w == MainWall;
            return "{\"t\":\"layout\",\"primary\":" + B(main) + ",\"ground\":" + ground.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"overlay\":" + B(hasTb && settings.TaskbarFx && w.Primary) + ",\"panel\":" + B(settings.ShowPanel && main)
                + ",\"fps\":" + w.Fps + ",\"pos\":" + WeatherService.JsonStr(settings.PanelPos ?? "tr")
                + ",\"lowmem\":" + B(settings.LowMemory) + ",\"minimal\":" + B(settings.Minimal)
                + ",\"mx\":" + settings.PanelMarginX + ",\"my\":" + settings.PanelMarginY + "}";
        }
        static string B(bool b) => b ? "true" : "false";

        void OnWallReady(WallpaperWindow w)
        {
            IntPtr h;
            w.Post(LayoutJson(w, TaskbarRect(out h)));
            if (payload != null) w.Post(payload);
            w.Post(PreviewJson());
            w.Post(FxJson());
            if (w.BackdropJson != null) w.Post(w.BackdropJson);
        }

        void OnWallString(WallpaperWindow w, string s)
        {
            if (s.Length > 0 && s[0] == 'H') { if (w.Primary) overlay.AddHits(s.Substring(1)); }
            else if (s.StartsWith("log:")) Log.Write("js: " + s.Substring(4));
        }

        void OnWallJson(WallpaperWindow w, string json)
        {
            if (w != MainWall) return;
            try
            {
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (d == null || !d.ContainsKey("t") || (string)d["t"] != "scene") return;
                Func<string, double> num = k => d.ContainsKey(k) ? Convert.ToDouble(d[k], System.Globalization.CultureInfo.InvariantCulture) : 0;
                overlay.SetScene(num("rain"), num("snow"), num("wind"));
                eaves.SetScene(num("rain"), num("snow"), num("wind"));
                sceneText = d.ContainsKey("text") ? Convert.ToString(d["text"]) : "";
                var tip = "天气壁纸" + (sceneText != "" ? "\n" + sceneText : "");
                tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            }
            catch (Exception ex) { Log.Write("scene parse: " + ex.Message); }
        }

        void Broadcast(string json) { foreach (var w in walls) w.Post(json); flyout?.Post(json); }

        FlyoutWindow flyout;
        FlyoutWindow Flyout()
        {
            if (env == null) return null;
            if (flyout == null)
            {
                flyout = new FlyoutWindow(env) { OnCommand = FlyoutCommand };
                if (payload != null) flyout.Post(payload);
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
                case "refresh": Refresh(true); break;
                case "city": flyout.HideFlyout(); PickCity(); break;
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
            var prim = Screen.PrimaryScreen;
            var rects = tops.Select(h => new { h, r = Native.VisibleRect(h) }).Where(x => x.r.Width > 0 && x.r.Height > 0).ToList();

            // 全局帧率上限：电池 / 系统繁忙
            int cap = settings.Fps;
            var reasons = new List<string>();
            if (perf.OnBattery) { cap = Math.Min(cap, 20); reasons.Add("电池供电"); }
            if (perf.Busy) { cap = Math.Min(cap, 12); reasons.Add($"系统繁忙 CPU {perf.Cpu:P0} 内存 {perf.MemLoad}%"); }

            int running = 0, minFps = int.MaxValue;
            foreach (var w in walls)
            {
                var scr = Screen.AllScreens.FirstOrDefault(s => s.Bounds == w.Bounds) ?? prim;
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
                if (w.Primary) overlay.SetCovered(vis < 0.03);
                if (!pause) { running++; minFps = Math.Min(minFps, fps); }
            }
            overlay.SetSuppressed(fullscreenOnPrimary || locked);
            overlay.SetFps(perf.Busy ? 20 : perf.OnBattery ? 30 : settings.Fps);

            // 屋檐：只挑"大小适中"的普通窗口——最大化 / 全屏 / 太小 / 几乎铺满屏幕的都跳过
            eaves.SetEnabled(settings.WindowFx);
            eaves.SetSuppressed(locked || perf.Busy);
            eaves.SetFps(perf.OnBattery ? 20 : 30);
            if (settings.WindowFx && !locked && !perf.Busy)
            {
                var shown = walls.Select(w => w.Bounds).ToList();
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

            wallsShown = running > 0;
            FxStep();
            if (settings.Minimal && ++backdropCheck >= 43) { backdropCheck = 0; CheckBackdrops(); }   // 约 30 秒查一次（幻灯片放映换图不一定有广播）

            if (locked) perfText = "已暂停（锁屏）";
            else if (running == 0) perfText = "已暂停（桌面被遮挡）";
            else if (settings.Minimal && fxKind == null && preview == "live" && !previewDay) perfText = "极简模式：静止，天气有变化时播放动画";
            else perfText = $"{minFps} FPS" + (reasons.Count > 0 ? "（" + string.Join("，", reasons) + "）" : minFps < settings.Fps ? "（桌面大部分被遮挡）" : "");
        }

        // ---------------------------------------------------------------- 极简模式：系统壁纸做底
        readonly WinTimer backdropTimer = new WinTimer { Interval = 1000 };
        int backdropGen, backdropCheck, backdropFails;
        string backdropKeys = "", backdropProbe;
        DateTime backdropRetryAt;

        /// <summary>
        /// 极简模式下把每块屏的系统壁纸缩放好交给页面垫底；onlyIfChanged = 只在壁纸设置变了时才重新生成（定时检查用）。
        /// 非极简模式下通知页面撤掉底图。
        /// </summary>
        async void UpdateBackdrops(bool onlyIfChanged)
        {
            var list = walls.ToList();
            if (!settings.Minimal)
            {
                ++backdropGen;   // 作废还在生成中的底图
                backdropKeys = "";
                foreach (var w in list) SendBackdrop(w, "{\"t\":\"backdrop\",\"url\":null}");
                return;
            }
            if (list.Count == 0) return;   // 桌面还没就绪：别拿空列表去清缓存
            try
            {
                var infos = list.Select(w => Backdrop.Query(w.Bounds)).ToList();
                var keys = string.Join("\n", infos.Select(i => i.Key));
                if (onlyIfChanged && keys == backdropKeys) return;   // 没变：也不能作废正在生成的那一次
                int gen = ++backdropGen;
                backdropKeys = keys;
                var files = await Task.Run(() => infos.Select(Backdrop.Render).ToList());
                if (gen != backdropGen) return;
                for (int i = 0; i < list.Count; i++)
                    SendBackdrop(list[i], "{\"t\":\"backdrop\",\"url\":" + (files[i] == null ? "null" : WeatherService.JsonStr("https://" + Backdrop.Host + "/" + files[i]))
                        + ",\"color\":" + WeatherService.JsonStr(infos[i].Css) + "}");
                Backdrop.Clean(files);
                backdropFails = 0;
                Log.Write("backdrop: " + string.Join(", ", infos.Select((x, i) => (files[i] ?? "color " + x.Css) + " pos " + x.Pos)));
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
        // 极简模式平时不画天气，只在这些时候整屏播放一小段（PlaySeconds，默认 30 秒）：启动后拿到第一份天气、转雨、转暴雨、转雷雨、转雪、雨后转晴（彩虹）。
        // 桌面被挡住、锁屏或人不在电脑前时先记下，等桌面露出来、有人操作时再看是否还需要播（雨已经停了就不播了）。
        // 彩虹在普通模式下也会出现（雨后转晴的白天挂同样长的时间）。
        const double FxIdleSeconds = 120;
        string fxKind, fxPending, wxCat;   // 正在播 / 等着播的类型；上一份实时天气的类别
        double fxLeft;
        // 全部用 UTC：夏令时切换时本地时间会跳一小时，"一小时内不重复""雨停 90 分钟内"会算错
        DateTime fxTick = DateTime.UtcNow, lastWetAt = DateTime.MinValue, bowShownFor = DateTime.MinValue;
        bool wxDay, wallsShown;
        readonly Dictionary<string, DateTime> fxShown = new Dictionary<string, DateTime>();

        string FxJson() => "{\"t\":\"fx\",\"kind\":" + (fxKind == null ? "null" : WeatherService.JsonStr(fxKind)) + "}";
        void PostFx() { foreach (var w in walls) w.Post(FxJson()); }

        static string Category(int code) =>
            code >= 95 ? "thunder" :
            (code >= 71 && code <= 77) || code == 85 || code == 86 ? "snow" :
            code == 65 || code == 67 || code == 82 ? "heavy" :
            (code >= 51 && code <= 67) || (code >= 80 && code <= 82) ? "rain" :
            code == 45 || code == 48 ? "fog" :
            code == 3 ? "cloudy" : "clear";
        static bool Wet(string c) => c == "rain" || c == "heavy" || c == "thunder";
        // 雨停后 90 分钟内转晴的白天，每场雨只挂一次
        bool RainbowDue => wxCat == "clear" && wxDay && lastWetAt > bowShownFor && (DateTime.UtcNow - lastWetAt).TotalMinutes < 90;

        /// <summary>换了城市：上一个城市的天气不能拿来比较（否则会误判转雨、雨后彩虹），按"刚启动"处理。</summary>
        void ResetWeatherFx() { wxCat = null; lastWetAt = DateTime.MinValue; fxPending = null; }

        /// <summary>每拿到一份实时天气调用一次：和上一份比，判断要不要播。fresh = false 表示用的是启动时的缓存（可能是很久以前的）。</summary>
        void OnWeather(bool fresh = true)
        {
            int code; bool day;
            try
            {
                var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(payload);
                var cur = (Dictionary<string, object>)((Dictionary<string, object>)d["fc"])["current"];
                code = Convert.ToInt32(cur["weather_code"]);
                day = !cur.ContainsKey("is_day") || Convert.ToInt32(cur["is_day"]) != 0;
            }
            catch (Exception ex) { Log.Write("fx: weather parse " + ex.Message); return; }
            string prev = wxCat, cat = Category(code);
            wxCat = cat; wxDay = day;
            if (Wet(cat) && fresh) lastWetAt = DateTime.UtcNow;   // 缓存里的雨可能是昨天的，别拿来判断"刚下过雨"
            string kind = null;
            if (prev == null) kind = "start";
            else if (cat != prev)
            {
                if (cat == "snow") kind = "snow";
                else if (cat == "thunder") kind = "thunder";
                else if (cat == "heavy" && prev != "thunder") kind = "heavy";
                else if (cat == "rain" && !Wet(prev)) kind = "rain";
            }
            if (!settings.Minimal) kind = null;   // 普通模式一直在播天气，只有彩虹需要额外挂一下
            if (kind == null && RainbowDue) kind = "rainbow";
            DateTime t;
            if (kind != null && kind != "start" && fxShown.TryGetValue(kind, out t) && (DateTime.UtcNow - t).TotalMinutes < 60) kind = null;   // 一小时内同类不重复播
            if (kind != null && kind != fxPending && kind != fxKind) { fxPending = kind; Log.Write("fx: pending " + kind + " (" + prev + " -> " + cat + ")"); }
            FxStep();
        }

        /// <summary>延后的动画到真要播时再确认一次：天气已经变回去了就不播。</summary>
        bool FxStillDue(string k)
        {
            switch (k)
            {
                case "snow": return wxCat == "snow";
                case "rain": case "heavy": case "thunder": return Wet(wxCat);
                case "rainbow": return RainbowDue;
                default: return true;
            }
        }

        /// <summary>Watch 每 0.7 秒调一次：倒计时只算桌面露出来的时间；等着播的在桌面可见且有人操作时开播。</summary>
        void FxStep()
        {
            var now = DateTime.UtcNow;
            double dt = Math.Max(0, Math.Min(2, (now - fxTick).TotalSeconds));
            fxTick = now;
            bool ended = false;
            if (fxKind != null)
            {
                if (wallsShown) fxLeft -= dt;
                if (fxLeft > 0) return;
                Log.Write("fx: end " + fxKind);
                fxKind = null;
                ended = true;
            }
            if (fxPending != null && wallsShown && !locked && Native.IdleSeconds() <= FxIdleSeconds)
            {
                var k = fxPending;
                fxPending = null;
                if (FxStillDue(k)) { PlayFx(k); return; }   // 紧接着播下一段：直接换类型，不先淡出再淡入
                Log.Write("fx: dropped " + k + " (now " + wxCat + ")");
            }
            if (ended) PostFx();
        }

        void PlayFx(string kind)
        {
            if (kind == "off") { fxKind = null; fxLeft = 0; fxPending = null; PostFx(); return; }
            fxKind = kind; fxLeft = settings.PlaySecondsValue(); fxTick = DateTime.UtcNow;
            if (fxPending != "rainbow") fxPending = null;   // 正在按当前天气播了，等着的转雨 / 下雪不用再播一遍（彩虹画面不同，留着）
            fxShown[kind] = DateTime.UtcNow;
            if (kind == "rainbow") bowShownFor = lastWetAt;
            Log.Write("fx: play " + kind + " " + fxLeft + "s");
            PostFx();
        }

        // ---------------------------------------------------------------- 天气
        async void Refresh(bool relocate)
        {
            if (refreshing) return;
            refreshing = true;
            weatherTimer.Stop();
            try
            {
                if (relocate || loc == null || (DateTime.Now - locTime).TotalHours > 2)
                {
                    var l = await ws.Locate(settings);
                    if (l != null) { loc = l; locTime = DateTime.Now; }
                }
                if (loc == null) throw new Exception("定位失败");
                payload = await ws.Fetch(loc);
                try { File.WriteAllText(CachePath, payload); } catch { }
                Broadcast(payload);
                weatherTimer.Interval = 15 * 60 * 1000;
                Log.Write("weather updated: " + loc.Name);
                OnWeather();
            }
            catch (Exception ex)
            {
                Log.Write("refresh failed: " + ex.Message);
                weatherTimer.Interval = 2 * 60 * 1000;
                if (wxCat == null && payload != null) OnWeather(false);   // 启动时联网失败：先按缓存的天气播启动动画
            }
            finally
            {
                refreshing = false;
                weatherTimer.Start();
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
                case "refresh": Refresh(true); break;
                case "fx": PlayFx(arg == "" ? "start" : arg.ToLowerInvariant()); break;   // fx [start|rain|heavy|thunder|snow|rainbow|off]：立即播放（测试用）
                case "rebuild": BuildWalls(); break;
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

            menu.Items.Add("立即刷新天气", null, (s, e) => Refresh(true));
            var city = new ToolStripMenuItem("设置城市…", null, (s, e) => PickCity());
            var auto = new ToolStripMenuItem("使用自动定位", null, (s, e) =>
            {
                settings.City = null; settings.Lat = null; settings.Lon = null; settings.Save(); ResetWeatherFx(); Refresh(true);
            });
            menu.Items.Add(city);
            menu.Items.Add(auto);
            menu.Items.Add(new ToolStripSeparator());

            var pv = new ToolStripMenuItem("效果预览");
            var modes = new[]
            {
                new[] { "live", "实时天气" }, null,
                new[] { "clear", "晴天" }, new[] { "sunrise", "晴 · 日出" }, new[] { "sunset", "晴 · 日落" }, new[] { "night", "晴 · 夜晚" },
                new[] { "partly", "多云" }, new[] { "cloudy", "阴天" }, new[] { "fog", "雾" }, null,
                new[] { "drizzle", "毛毛雨" }, new[] { "rain", "中雨" }, new[] { "heavy", "暴雨" }, new[] { "thunder", "雷阵雨" }, new[] { "snow", "雪" },
                new[] { "rainbow", "雨后彩虹" }, null,
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
            var dayIt = new ToolStripMenuItem("一天快进演示（按上面所选天气，48 秒一遍）");
            dayIt.Click += (s, e) => SetPreview(preview, !previewDay);
            pv.DropDownItems.Add(dayIt);
            var pvTip = new ToolStripMenuItem("") { Enabled = false };
            pv.DropDownItems.Add(pvTip);
            pv.DropDownOpening += (s, e) =>
            {
                foreach (var o in pv.DropDownItems.OfType<ToolStripMenuItem>()) if (o.Tag is string) o.Checked = (string)o.Tag == preview;
                dayIt.Checked = previewDay;
                // 极简模式：「当前天气」只在真正播放时打勾；平时桌面上是系统壁纸，什么都不勾
                if (settings.Minimal && preview == "live" && !previewDay && fxKind == null) liveIt.Checked = false;
                liveIt.Text = settings.Minimal ? "当前天气（立即播放 " + settings.PlaySecondsValue() + " 秒）" : "实时天气";
                var back = settings.Minimal ? "收起" : "恢复实时天气";
                var left = (int)Math.Ceiling((previewUntil - DateTime.Now).TotalSeconds);
                pvTip.Text = previewTimer.Enabled && left > 0 ? $"{left} 秒后{back}" : $"预览 {settings.PlaySecondsValue()} 秒后自动{back}";
            };
            menu.Items.Add(pv);

            var fx = new ToolStripMenuItem("任务栏雨雪效果") { CheckOnClick = true };
            fx.Click += (s, e) => { settings.TaskbarFx = fx.Checked; settings.Save(); ConfigureOverlay(); };
            // 省内存模式不再作为常规选项：想省资源用极简模式（静止时 CPU / GPU 为 0，内存也更低）；settings.json 里的 LowMemory 仍然有效。
            // 只在已经开着时显示这一项，让以前开过的人能直接在菜单里关掉
            var lowMem = new ToolStripMenuItem("省内存模式") { CheckOnClick = true };
            lowMem.Click += (s, e) => { settings.LowMemory = lowMem.Checked; settings.Save(); ConfigureOverlay(); ApplyFlyoutMode(); };
            var wfx = new ToolStripMenuItem("窗口顶边雨滴 / 积雪（屋檐）") { CheckOnClick = true };
            wfx.Click += (s, e) => { settings.WindowFx = wfx.Checked; settings.Save(); Watch(); };
            var card = new ToolStripMenuItem("显示天气卡片") { CheckOnClick = true };
            card.Click += (s, e) => { settings.ShowPanel = card.Checked; settings.Save(); ConfigureOverlay(); };
            var minimal = new ToolStripMenuItem("极简模式") { CheckOnClick = true };   // 文字在菜单弹出时按 PlaySeconds 填
            minimal.Click += (s, e) => { settings.Minimal = minimal.Checked; settings.Save(); ConfigureOverlay(); UpdateBackdrops(false); Watch(); };
            var mon = new ToolStripMenuItem("显示在");
            mon.DropDownOpening += (s, e) =>
            {
                mon.DropDownItems.Clear();
                Action<string, string> add = (key, text) =>
                {
                    var it = new ToolStripMenuItem(text) { Checked = (settings.Monitors ?? "all") == key };
                    it.Click += (o, a) => { settings.Monitors = key; settings.Save(); BuildWalls(); };
                    mon.DropDownItems.Add(it);
                };
                add("all", "所有显示器");
                add("primary", "仅主显示器");
                mon.DropDownItems.Add(new ToolStripSeparator());
                var screens = Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    var sc = screens[i];
                    add(sc.DeviceName, $"仅显示器 {i + 1}（{sc.Bounds.Width}×{sc.Bounds.Height}{(sc.Primary ? "，主" : "")}{(sc.Bounds.X < 0 ? "，左侧" : sc.Bounds.X > 0 ? "，右侧" : "")}）");
                }
            };
            mon.DropDownItems.Add("…");
            var pos = new ToolStripMenuItem("卡片位置");
            foreach (var p in new[] { new[] { "tr", "右上角" }, new[] { "tl", "左上角" }, new[] { "tc", "顶部居中" }, new[] { "br", "右下角" }, new[] { "bl", "左下角" } })
            {
                var key = p[0];
                var it = new ToolStripMenuItem(p[1]) { Tag = key };
                it.Click += (s, e) => { settings.PanelPos = key; settings.Save(); ConfigureOverlay(); };
                pos.DropDownItems.Add(it);
            }
            pos.DropDownItems.Add(new ToolStripSeparator());
            pos.DropDownItems.Add(new ToolStripMenuItem("调整边距…（编辑 PanelMarginX / PanelMarginY，保存即生效）", null, (s, e) => OpenSettingsFile()));
            pos.DropDownOpening += (s, e) => { foreach (var o in pos.DropDownItems.OfType<ToolStripMenuItem>()) if (o.Tag is string) o.Checked = (string)o.Tag == (settings.PanelPos ?? "tr"); };
            var status = new ToolStripMenuItem("状态") { Enabled = false };
            var fps60 = new ToolStripMenuItem("高帧率（60 FPS）") { CheckOnClick = true };
            fps60.Click += (s, e) => { settings.Fps = fps60.Checked ? 60 : 30; settings.Save(); overlay.SetFps(settings.Fps); ConfigureOverlay(); };
            var startup = new ToolStripMenuItem("开机自启") { CheckOnClick = true };
            startup.Click += (s, e) => SetAutoStart(startup.Checked);
            menu.Items.Add(minimal);
            menu.Items.Add(fx); menu.Items.Add(wfx); menu.Items.Add(card); menu.Items.Add(pos); menu.Items.Add(mon); menu.Items.Add(fps60); menu.Items.Add(lowMem); menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(status);
            menu.Items.Add(new ToolStripSeparator());
            updateItem = new ToolStripMenuItem("检查更新", null, (s, e) => { if (newer != null) PromptUpdate(newer); else CheckUpdate(true); });
            var autoUpd = new ToolStripMenuItem("自动检查更新") { CheckOnClick = true };
            autoUpd.Click += (s, e) => { settings.AutoUpdate = autoUpd.Checked; settings.Save(); };
            menu.Items.Add(updateItem); menu.Items.Add(autoUpd);
            menu.Items.Add("编辑设置文件…", null, (s, e) => OpenSettingsFile());
            menu.Items.Add("打开日志目录", null, (s, e) => System.Diagnostics.Process.Start("explorer.exe", Settings.LocalDir));
            menu.Opening += (s, e) =>
            {
                autoUpd.Checked = settings.AutoUpdate;
                if (newer == null) updateItem.Text = "检查更新（当前 " + Updater.CurrentText + "）";
            };
            menu.Items.Add("退出", null, (s, e) => Quit());

            menu.Opening += (s, e) =>
            {
                lowMem.Checked = settings.LowMemory; lowMem.Visible = settings.LowMemory;
                fx.Checked = settings.TaskbarFx; wfx.Checked = settings.WindowFx; card.Checked = settings.ShowPanel;
                minimal.Checked = settings.Minimal;
                minimal.Text = "极简模式（平时只显示卡片，天气变化时播放 " + settings.PlaySecondsValue() + " 秒）";
                status.Text = "当前：" + (perfText == "" ? "启动中" : perfText);
                fps60.Checked = settings.Fps >= 60; startup.Checked = IsAutoStart();
                auto.Checked = !settings.Lat.HasValue;
                city.Text = settings.Lat.HasValue ? "设置城市…（当前：" + settings.City + "）" : "设置城市…";
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

        async void PickCity()
        {
            using (var f = new CityDialog(ws))
            {
                if (f.ShowDialog() != DialogResult.OK || f.Result == null) return;
                settings.City = f.Result.Name;
                settings.Lat = f.Result.Lat;
                settings.Lon = f.Result.Lon;
                settings.Save();
            }
            await Task.Yield();
            ResetWeatherFx();
            Refresh(true);
        }

        // ---------------------------------------------------------------- 设置文件
        FileSystemWatcher settingsWatcher;
        readonly WinTimer settingsReload = new WinTimer { Interval = 400 };

        /// <summary>用记事本打开 settings.json（格式化成多行便于编辑），保存后自动重新加载。</summary>
        void OpenSettingsFile()
        {
            settings.Save();
            OpenUrl("notepad.exe", "\"" + Settings.FilePath + "\"");
        }

        void WatchSettings()
        {
            settingsWatcher = new FileSystemWatcher(Settings.AppDataDir, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            var ui = SynchronizationContext.Current;
            FileSystemEventHandler h = (s, e) => ui?.Post(_ => { settingsReload.Stop(); settingsReload.Start(); }, null);
            settingsWatcher.Changed += h; settingsWatcher.Created += h;
            settingsWatcher.Renamed += (s, e) => h(s, e);
            settingsReload.Tick += (s, e) => { settingsReload.Stop(); ReloadSettings(); };
            settingsWatcher.EnableRaisingEvents = true;
        }

        void ReloadSettings()
        {
            var n = Settings.TryLoad();
            if (n == null) { Log.Write("settings reload: parse failed, ignored"); return; }
            if (n.Same(settings)) return;   // 自己保存触发的
            bool walls = n.Monitors != settings.Monitors || n.Layered != settings.Layered || n.DevTools != settings.DevTools;
            bool loc = n.Lat != settings.Lat || n.Lon != settings.Lon;
            bool minimal = n.Minimal != settings.Minimal;
            settings.CopyFrom(n);
            Log.Write("settings reloaded");
            overlay.SetFps(settings.Fps);
            if (walls) BuildWalls(); else { ConfigureOverlay(); if (minimal) UpdateBackdrops(false); }
            ApplyFlyoutMode();
            Watch();
            if (loc) { ResetWeatherFx(); Refresh(true); }
        }

        // ---------------------------------------------------------------- 检查更新
        UpdateInfo newer;
        ToolStripMenuItem updateItem;
        bool checking, installing;
        readonly WinTimer updateTimer = new WinTimer();

        void StartUpdateTimer()
        {
            updateTimer.Interval = 60 * 1000;   // 启动 1 分钟后查一次，之后每 12 小时
            updateTimer.Tick += (s, e) =>
            {
                updateTimer.Interval = 12 * 3600 * 1000;
                if (settings.AutoUpdate && !Updater.IsDevBuild) CheckUpdate(false);
            };
            updateTimer.Start();
        }

        string UpdateJson() => newer == null || settings.SkipVersion == newer.Version.ToString() ? "{\"t\":\"update\",\"tag\":null}"
            : "{\"t\":\"update\",\"tag\":" + WeatherService.JsonStr(newer.Tag) + ",\"cur\":" + WeatherService.JsonStr(Updater.CurrentText) + "}";

        async void CheckUpdate(bool manual)
        {
            if (checking) return;
            checking = true;
            try
            {
                var info = await Updater.Check();
                Log.Write("update: latest " + info.Tag + ", current " + Updater.Current);
                if (info.Version <= Updater.Current)
                {
                    if (manual) MessageBox.Show("已经是最新版本（v" + Updater.Current + "）。", "天气壁纸", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                // 不弹托盘通知，只在右键菜单（加粗）和左键弹出面板里提示
                newer = info;
                updateItem.Text = "发现新版本 " + info.Tag + "，点击更新（当前 " + Updater.CurrentText + "）";
                updateItem.Font = new Font(updateItem.Font, FontStyle.Bold);
                flyout?.Post(UpdateJson());
                if (manual) PromptUpdate(info);
            }
            catch (Exception ex)
            {
                Log.Write("update check: " + ex.Message);
                if (manual) MessageBox.Show("检查更新失败：" + ex.Message + "\n\n可以稍后再试，或直接打开发布页：\n" + Updater.ReleasePage, "天气壁纸", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { checking = false; }
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
            if (res == DialogResult.No) { settings.SkipVersion = info.Version.ToString(); settings.Save(); flyout?.Post(UpdateJson()); return; }
            if (res != DialogResult.Yes) return;
            await InstallUpdate(info);
        }

        async Task InstallUpdate(UpdateInfo info)
        {
            if (installing) return;
            if (Updater.IsDevBuild) { Log.Write("update: dev build, skipped"); return; }
            installing = true;
            try
            {
                var progress = new Progress<string>(t => { updateItem.Text = t; tray.Text = "天气壁纸 · " + t; });
                await Updater.Install(info, progress);
                Quit();   // 让出文件给安装程序；装完由安装包重新拉起
            }
            catch (Exception ex)
            {
                installing = false;
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

        void Quit()
        {
            watchTimer.Stop(); weatherTimer.Stop(); updateTimer.Stop(); settingsWatcher?.Dispose();
            tray.Visible = false;
            foreach (var w in walls) w.Dispose();
            walls.Clear();
            overlay.Dispose();
            eaves.Dispose();
            flyout?.Dispose();
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

    class CityDialog : Form
    {
        public GeoResult Result;
        readonly TextBox box = new TextBox();
        readonly ListBox list = new ListBox();
        readonly Label status = new Label();
        readonly WeatherService ws;

        public CityDialog(WeatherService ws)
        {
            this.ws = ws;
            Text = "设置城市";
            Icon = Program.AppIcon;
            Font = new Font("Microsoft YaHei UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 330);
            TopMost = true;

            var tip = new Label { Text = "输入城市名（中文或英文，如 伦敦 / London）：", Left = 14, Top = 12, Width = 390, Height = 24 };
            box.SetBounds(14, 40, 300, 28);
            var search = new Button { Text = "搜索", Left = 322, Top = 38, Width = 84, Height = 32 };
            list.SetBounds(14, 80, 392, 180);
            status.SetBounds(14, 266, 392, 24);
            var ok = new Button { Text = "确定", Left = 230, Top = 290, Width = 84, Height = 32 };
            var cancel = new Button { Text = "取消", Left = 322, Top = 290, Width = 84, Height = 32, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { tip, box, search, list, status, ok, cancel });
            AcceptButton = search;
            CancelButton = cancel;

            search.Click += async (s, e) =>
            {
                var q = box.Text.Trim();
                if (q == "") return;
                status.Text = "搜索中…";
                list.Items.Clear();
                try
                {
                    var res = await ws.SearchCity(q);
                    foreach (var r in res) list.Items.Add(r);
                    status.Text = res.Count == 0 ? "没有找到，换个写法试试" : "选择一个结果后点「确定」";
                    if (res.Count > 0) list.SelectedIndex = 0;
                }
                catch (Exception ex) { status.Text = "搜索失败：" + ex.Message; }
            };
            EventHandler accept = (s, e) =>
            {
                Result = list.SelectedItem as GeoResult;
                if (Result == null) { status.Text = "请先搜索并选择城市"; return; }
                DialogResult = DialogResult.OK;
                Close();
            };
            ok.Click += accept;
            list.DoubleClick += accept;
        }
    }
}
