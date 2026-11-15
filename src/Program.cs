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
                    if (first == "uninstall") ShellWatcher.Send("quit");
                    else if (args.Length > 0) ShellWatcher.Send(string.Join(" ", args));
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
        const int PreviewSeconds = 60, DayDemoSeconds = 49;   // 天气预览 1 分钟后恢复实时；一天快进 48 秒走完一遍后恢复
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
                if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect) locked = true;
                else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect) locked = false;
                Watch();
            };
            SystemEvents.UserPreferenceChanged += (s, e) => { if (e.Category == UserPreferenceCategory.General) ConfigureOverlay(); };
            SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == PowerModes.Resume) { weatherTimer.Stop(); weatherTimer.Interval = 15000; weatherTimer.Start(); }
            };
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
            ConfigureOverlay();
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
            overlay.Configure(tb, prim, scale, settings.TaskbarFx);
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
                + ",\"fps\":" + w.Fps + "}";
        }
        static string B(bool b) => b ? "true" : "false";

        void OnWallReady(WallpaperWindow w)
        {
            IntPtr h;
            w.Post(LayoutJson(w, TaskbarRect(out h)));
            if (payload != null) w.Post(payload);
            w.Post(PreviewJson());
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

        void Broadcast(string json) { foreach (var w in walls) w.Post(json); }

        // ---------------------------------------------------------------- 遮挡 / 全屏检测
        void Watch()
        {
            if (walls.Count > 0 && !DesktopHost.StillValid()) { ScheduleRebuild(1500); return; }
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

            if (locked) perfText = "已暂停（锁屏）";
            else if (running == 0) perfText = "已暂停（桌面被遮挡）";
            else perfText = $"{minFps} FPS" + (reasons.Count > 0 ? "（" + string.Join("，", reasons) + "）" : minFps < settings.Fps ? "（桌面大部分被遮挡）" : "");
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
            }
            catch (Exception ex)
            {
                Log.Write("refresh failed: " + ex.Message);
                weatherTimer.Interval = 2 * 60 * 1000;
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
                case "rebuild": BuildWalls(); break;
                case "quit": Quit(); break;
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
                    previewUntil = DateTime.Now.AddSeconds(day ? DayDemoSeconds : PreviewSeconds);
                    previewTimer.Start();
                }
            }
            Broadcast(PreviewJson());
        }

        // ---------------------------------------------------------------- 托盘
        void BuildTray()
        {
            tray.Icon = MakeIcon();
            tray.Text = "天气壁纸";
            tray.Visible = true;
            var menu = new ContextMenuStrip();

            menu.Items.Add("立即刷新天气", null, (s, e) => Refresh(true));
            var city = new ToolStripMenuItem("设置城市…", null, (s, e) => PickCity());
            var auto = new ToolStripMenuItem("使用自动定位", null, (s, e) =>
            {
                settings.City = null; settings.Lat = null; settings.Lon = null; settings.Save(); Refresh(true);
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
                new[] { "drizzle", "毛毛雨" }, new[] { "rain", "中雨" }, new[] { "heavy", "暴雨" }, new[] { "thunder", "雷阵雨" }, new[] { "snow", "雪" }, null,
            };
            foreach (var m in modes)
            {
                if (m == null) { pv.DropDownItems.Add(new ToolStripSeparator()); continue; }
                var key = m[0];
                var it = new ToolStripMenuItem(m[1]) { Tag = key };
                it.Click += (s, e) => SetPreview(key, previewDay);
                pv.DropDownItems.Add(it);
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
                var left = (int)Math.Ceiling((previewUntil - DateTime.Now).TotalSeconds);
                pvTip.Text = previewTimer.Enabled && left > 0 ? $"{left} 秒后恢复实时天气" : "预览 1 分钟后自动恢复实时天气";
            };
            menu.Items.Add(pv);

            var fx = new ToolStripMenuItem("任务栏雨雪效果") { CheckOnClick = true };
            fx.Click += (s, e) => { settings.TaskbarFx = fx.Checked; settings.Save(); ConfigureOverlay(); };
            var wfx = new ToolStripMenuItem("窗口顶边雨滴 / 积雪（屋檐）") { CheckOnClick = true };
            wfx.Click += (s, e) => { settings.WindowFx = wfx.Checked; settings.Save(); Watch(); };
            var card = new ToolStripMenuItem("显示天气卡片") { CheckOnClick = true };
            card.Click += (s, e) => { settings.ShowPanel = card.Checked; settings.Save(); ConfigureOverlay(); };
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
            var status = new ToolStripMenuItem("状态") { Enabled = false };
            var fps60 = new ToolStripMenuItem("高帧率（60 FPS）") { CheckOnClick = true };
            fps60.Click += (s, e) => { settings.Fps = fps60.Checked ? 60 : 30; settings.Save(); overlay.SetFps(settings.Fps); ConfigureOverlay(); };
            var startup = new ToolStripMenuItem("开机自启") { CheckOnClick = true };
            startup.Click += (s, e) => SetAutoStart(startup.Checked);
            menu.Items.Add(fx); menu.Items.Add(wfx); menu.Items.Add(card); menu.Items.Add(mon); menu.Items.Add(fps60); menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(status);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开日志目录", null, (s, e) => System.Diagnostics.Process.Start("explorer.exe", Settings.LocalDir));
            menu.Items.Add("退出", null, (s, e) => Quit());

            menu.Opening += (s, e) =>
            {
                fx.Checked = settings.TaskbarFx; wfx.Checked = settings.WindowFx; card.Checked = settings.ShowPanel;
                status.Text = "当前：" + (perfText == "" ? "启动中" : perfText);
                fps60.Checked = settings.Fps >= 60; startup.Checked = IsAutoStart();
                auto.Checked = !settings.Lat.HasValue;
                city.Text = settings.Lat.HasValue ? "设置城市…（当前：" + settings.City + "）" : "设置城市…";
            };
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Refresh(false); };
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(Color.FromArgb(255, 255, 196, 60))) g.FillEllipse(b, 3, 3, 18, 18);
                    using (var b = new SolidBrush(Color.FromArgb(250, 245, 248, 252)))
                    {
                        g.FillEllipse(b, 9, 14, 14, 12); g.FillEllipse(b, 16, 10, 14, 15); g.FillRectangle(b, 15, 19, 12, 7);
                    }
                }
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
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
            Refresh(true);
        }

        void Quit()
        {
            watchTimer.Stop(); weatherTimer.Stop();
            tray.Visible = false;
            foreach (var w in walls) w.Dispose();
            walls.Clear();
            overlay.Dispose();
            eaves.Dispose();
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
