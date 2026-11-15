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
        public bool Ready { get; private set; }
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
                var st = wv.Settings;
                st.AreDefaultContextMenusEnabled = false;
                st.AreDevToolsEnabled = devTools;
                st.IsStatusBarEnabled = false;
                st.IsZoomControlEnabled = false;
                st.AreBrowserAcceleratorKeysEnabled = false;
                var web = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");
                wv.SetVirtualHostNameToFolderMapping("wallpaper.local", web, CoreWebView2HostResourceAccessKind.Allow);
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
            if (!Ready || ctl == null) { pending.Add(json); return; }
            try { ctl.CoreWebView2.PostWebMessageAsJson(json); } catch (Exception ex) { Log.Write("post failed: " + ex.Message); }
        }

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

        public void FixZOrder() => DesktopHost.FixZOrder(Handle);

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
