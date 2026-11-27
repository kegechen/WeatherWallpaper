using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace WeatherWallpaper
{
    /// <summary>一块显示器对应一个壁纸窗口，内部直接挂 CoreWebView2Controller（不走 WinForms 控件，方便控制窗口样式）。</summary>
    class WallpaperWindow : NativeWindow, IDisposable
    {
        public Rectangle Bounds { get; }
        public bool Primary { get; }
        public string DeviceName { get; }
        public bool Ready { get; private set; }
        public string BackdropJson;   // 系统壁纸底图消息（极简模式 / 壁纸叠加 / 只有自定义卡片的屏）：页面 ready 时由宿主在 layout 之后补发
        public string SceneKey = "";  // 这块屏的全屏天气画面跟着哪个城市（城市分组键）；"" = 不画天气
        public bool HasWeather;       // 这块屏上有天气卡片、画天气；只有自定义卡片的屏不画，只垫系统壁纸 + 卡片
        public string LayoutSent;     // 上次发给页面的布局（UpdateLayout 只在变化时发）
        public string FxSent;         // 上次发给页面的极简模式播放类型（只在变化时发）
        public bool Shown;            // 桌面露出来了、没暂停（Watch 每 0.7 秒更新）
        public float CssWidth;        // 页面上报的视口宽度（CSS 像素），用来换算缩放比例；0 = 还没报
        public event Action<WallpaperWindow> OnReady;
        public event Action<WallpaperWindow, string> OnString;
        public event Action<WallpaperWindow, string> OnJson;

        CoreWebView2Controller ctl;
        readonly List<string> pending = new List<string>();
        bool paused, disposed;

        public WallpaperWindow(Screen s, CoreWebView2Environment env, bool layered, bool devTools)
        {
            Bounds = s.Bounds;
            Primary = s.Primary;
            DeviceName = s.DeviceName;
            CreateHandle(new CreateParams
            {
                Caption = "WeatherWallpaper",
                Style = unchecked((int)(Native.WS_POPUP | Native.WS_CLIPCHILDREN)),
                ExStyle = (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
                Width = Bounds.Width,
                Height = Bounds.Height,
            });
            DesktopHost.Attach(Handle, Bounds, layered);
            resumeCards.Tick += (o, e) => RetryCards();
            Init(env, devTools);
        }

        async void Init(CoreWebView2Environment env, bool devTools)
        {
            try
            {
                ctl = await env.CreateCoreWebView2ControllerAsync(Handle);
                if (disposed) { ctl.Close(); return; }
                ctl.DefaultBackgroundColor = Color.Black;
                ctl.Bounds = new Rectangle(0, 0, Bounds.Width, Bounds.Height);
                ctl.IsVisible = true;
                var wv = ctl.CoreWebView2;
                WebHost.Configure(wv);
                wv.Settings.AreDevToolsEnabled = devTools;
                wv.SetVirtualHostNameToFolderMapping(Backdrop.Host, Backdrop.Dir, CoreWebView2HostResourceAccessKind.Allow);   // 极简模式的系统壁纸底图
                Effects.EnsureDir();
                wv.SetVirtualHostNameToFolderMapping(Effects.Host, Effects.Dir, CoreWebView2HostResourceAccessKind.Allow);     // 用户的自定义效果脚本
                wv.WebMessageReceived += (o, e) =>
                {
                    string str = null;
                    try { str = e.TryGetWebMessageAsString(); } catch { }
                    if (str != null)
                    {
                        if (str == "ready")
                        {
                            // 页面整页重载过（如自定义效果文件变了）：新页面默认是没暂停的，按宿主这边的状态补一句
                            bool again = Ready;
                            Ready = true; Flush();
                            if (again && paused) Post("{\"t\":\"pause\"}");
                            OnReady?.Invoke(this);
                        }
                        else OnString?.Invoke(this, str);
                    }
                    else OnJson?.Invoke(this, e.WebMessageAsJson);
                };
                // 页面进程崩了（如自定义卡片的脚本把内存吃光）/ 卡死：重新载入，页面就绪后宿主把状态全发一遍（OnReady）。
                // 10 分钟内接连崩 3 次：多半是哪个自定义卡片脚本每次都把页面搞崩，先不运行自定义卡片（半小时后、或改过卡片脚本后 RetryCards 恢复）。
                // 卡死不算崩：卡片脚本跑在 Worker 里卡不住页面，卡死多半是系统太忙（刚从睡眠醒来之类），不该因此停掉自定义卡片
                wv.ProcessFailed += (o, ev) =>
                {
                    var kind = ev.ProcessFailedKind;
                    Log.Write("wallpaper " + DeviceName + ": webview process failed " + kind);
                    var now = DateTime.UtcNow;
                    if (kind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                    {
                        crashes.RemoveAll(t => (now - t).TotalMinutes > 10);
                        crashes.Add(now);
                        if (crashes.Count >= 3 && !noCards)
                        {
                            noCards = true;
                            Log.Write("wallpaper " + DeviceName + ": crashed " + crashes.Count + " times in 10 minutes, custom cards paused");
                            // 崩溃不一定是卡片脚本的错（显卡驱动更新、睡眠唤醒、内存紧张也会）：半小时后自己再试一次，真是脚本的话会再崩 3 次、再停半小时
                            resumeCards.Stop(); resumeCards.Start();
                        }
                    }
                    else if (kind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                    {
                        if ((now - lastHang).TotalSeconds < 60) return;   // 一直卡着时这个事件会接连来：最多一分钟重新载入一次
                        lastHang = now;
                    }
                    else return;
                    var u = Url;
                    System.Threading.SynchronizationContext.Current?.Post(_ => { try { if (!disposed) ctl?.CoreWebView2.Navigate(u); } catch { } }, null);
                };
                wv.Navigate(Url);
                if (devTools) wv.OpenDevToolsWindow();
            }
            catch (Exception ex) { Log.Write("webview init failed: " + ex); }
        }

        readonly List<DateTime> crashes = new List<DateTime>();
        DateTime lastHang;   // 上次因为页面卡死重新载入的时间
        bool noCards;   // 页面接连崩溃：先不运行自定义卡片（页面里显示为出错）
        readonly Timer resumeCards = new Timer { Interval = 30 * 60 * 1000 };   // noCards 之后过多久自己恢复
        string Url => "https://wallpaper.local/index.html?" + (Primary ? "primary=1" : "") + (noCards ? "&nocards=1" : "");

        /// <summary>卡片脚本改过了 / 停了半小时了：因为接连崩溃停掉的自定义卡片重新运行（重新载入页面）。</summary>
        public void RetryCards()
        {
            resumeCards.Stop();
            if (!noCards || disposed || ctl == null) return;
            noCards = false; crashes.Clear();
            Log.Write("wallpaper " + DeviceName + ": custom cards resumed");
            try { ctl.CoreWebView2.Navigate(Url); } catch { }
        }

        public void Post(string json)
        {
            if (disposed) return;
            if (!Ready || ctl == null)
            {
                // 页面还没就绪（或 WebView2 起不来、永远不会就绪）：整份状态类的消息不排队——就绪时 OnReady（宿主的 OnWallReady）
                // 会按当时最新的状态全发一遍，排着的只会重复、过时，还会一直堆。其余（暂停 / 恢复、帧率等）照常排队
                if (IsSnapshot(json)) return;
                // 暂停 / 恢复、帧率只有最后一份算数：同组的旧消息去掉再排，一直不就绪也不会越堆越多
                var g = GroupOf(json);
                if (g != null) pending.RemoveAll(j => GroupOf(j) == g);
                pending.Add(json);
                return;
            }
            try { ctl.CoreWebView2.PostWebMessageAsJson(json); } catch (Exception ex) { Log.Write("post failed: " + ex.Message); }
        }

        // 自定义卡片的回复（csrc / cfile / cget）也不排队：页面没就绪 = 发请求的那个页面已经没了，新页面会自己重新要
        static readonly string[] Snapshots = { "layout", "weather", "preview", "fx", "backdrop", "csrc", "cfile", "cget" };
        static string GroupOf(string json) =>
            json.StartsWith("{\"t\":\"pause\"", StringComparison.Ordinal) || json.StartsWith("{\"t\":\"resume\"", StringComparison.Ordinal) ? "pause"
            : json.StartsWith("{\"t\":\"perf\"", StringComparison.Ordinal) ? "perf"
            : json.StartsWith("{\"t\":\"unlock\"", StringComparison.Ordinal) ? "unlock" : null;
        static bool IsSnapshot(string json) => Array.Exists(Snapshots, t => json.StartsWith("{\"t\":\"" + t + "\"", StringComparison.Ordinal));

        void Flush()
        {
            var list = pending.ToArray();
            pending.Clear();
            foreach (var j in list) Post(j);
        }

        public int Fps { get; private set; } = 30;
        int cloudFps;   // 上次发给页面的云帧率（0 = 还没发过）

        /// <summary>暂停 / 帧率上限 / 云的帧率，只在变化时通知页面。</summary>
        public void SetPerf(bool p, int fps, int cloud)
        {
            if (fps != Fps || cloud != cloudFps) { Fps = fps; cloudFps = cloud; Post("{\"t\":\"perf\",\"fps\":" + fps + ",\"cloudFps\":" + cloud + "}"); }
            if (p == paused) return;
            paused = p;
            Post(p ? "{\"t\":\"pause\"}" : "{\"t\":\"resume\"}");
        }

        public async System.Threading.Tasks.Task Capture(string path)
        {
            if (ctl == null) return;
            try
            {
                using (var fs = File.Create(path))
                    await ctl.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            }
            catch (Exception ex) { Log.Write("capture failed: " + ex.Message); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            resumeCards.Dispose();
            try { ctl?.Close(); } catch { }
            ctl = null;
            DestroyHandle();
        }
    }
}
