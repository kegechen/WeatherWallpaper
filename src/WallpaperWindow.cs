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
        public string BackdropJson;   // 极简模式底图消息：页面 ready 时由宿主在 layout 之后补发
        public string SceneKey = "";  // 这块屏的全屏天气画面跟着哪个城市（城市分组键）
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
                wv.WebMessageReceived += (o, e) =>
                {
                    string str = null;
                    try { str = e.TryGetWebMessageAsString(); } catch { }
                    if (str != null)
                    {
                        if (str == "ready") { Ready = true; Flush(); OnReady?.Invoke(this); }
                        else OnString?.Invoke(this, str);
                    }
                    else OnJson?.Invoke(this, e.WebMessageAsJson);
                };
                wv.Navigate("https://wallpaper.local/index.html" + (Primary ? "?primary=1" : ""));
                if (devTools) wv.OpenDevToolsWindow();
            }
            catch (Exception ex) { Log.Write("webview init failed: " + ex); }
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

        static readonly string[] Snapshots = { "layout", "weather", "preview", "fx", "backdrop" };
        static string GroupOf(string json) =>
            json.StartsWith("{\"t\":\"pause\"", StringComparison.Ordinal) || json.StartsWith("{\"t\":\"resume\"", StringComparison.Ordinal) ? "pause"
            : json.StartsWith("{\"t\":\"perf\"", StringComparison.Ordinal) ? "perf" : null;
        static bool IsSnapshot(string json) => Array.Exists(Snapshots, t => json.StartsWith("{\"t\":\"" + t + "\"", StringComparison.Ordinal));

        void Flush()
        {
            var list = pending.ToArray();
            pending.Clear();
            foreach (var j in list) Post(j);
        }

        public int Fps { get; private set; } = 30;

        /// <summary>暂停 / 帧率上限，只在变化时通知页面。</summary>
        public void SetPerf(bool p, int fps)
        {
            if (fps != Fps) { Fps = fps; Post("{\"t\":\"perf\",\"fps\":" + fps + "}"); }
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
            try { ctl?.Close(); } catch { }
            ctl = null;
            DestroyHandle();
        }
    }
}
