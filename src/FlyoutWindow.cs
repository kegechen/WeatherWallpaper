using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace WeatherWallpaper
{
    /// <summary>
    /// 托盘左键弹出的天气面板，仿 Win11 快捷设置：系统亚克力背景 + 圆角，贴着任务栏右下角弹出，失去焦点自动隐藏。
    /// </summary>
    class FlyoutWindow : Form
    {
        const int CssWidth = 360;
        readonly CoreWebView2Environment env;
        CoreWebView2Controller ctl;
        // 每类消息（weather / preview / update）只留最新一条：页面就绪或重建后重放
        readonly Dictionary<string, string> latest = new Dictionary<string, string>();
        // 省内存模式：收起 1 分钟后释放 WebView（省一个渲染进程的内存），下次弹出再重建。
        // 默认模式（KeepAlive）：启动后预先建好并常驻，点托盘立即弹出。
        readonly Timer releaseTimer = new Timer { Interval = 60000 };
        // 弹出时先用 DWM cloak 隐身显示（对 Chromium 来说窗口可见、会正常出图），页面画好第一帧再现身，
        // 避免先闪一块空白的亚克力背景；超时兜底
        readonly Timer revealTimer = new Timer();
        bool ready, initStarted, revealPending, keepAlive;
        int cssHeight = 560;
        DateTime showSince;
        public DateTime LastHidden { get; private set; }
        public Action<string> OnCommand;

        public FlyoutWindow(CoreWebView2Environment env)
        {
            this.env = env;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;   // 延伸到客户区的 DWM 背景下，黑色 = 透出亚克力
            Text = "天气";
            Icon = Program.AppIcon;
            KeyPreview = true;
            releaseTimer.Tick += (s, e) => { releaseTimer.Stop(); if (!Visible && !keepAlive) Release(); };
            revealTimer.Tick += (s, e) => { revealTimer.Stop(); if (revealPending) { Log.Write("flyout: page not painted in time, showing anyway"); Reveal(); } };
        }

        /// <summary>true = 面板常驻（默认）；false = 省内存模式，收起 1 分钟后释放。</summary>
        public bool KeepAlive
        {
            get { return keepAlive; }
            set
            {
                keepAlive = value;
                releaseTimer.Stop();
                if (!value && !Visible && ctl != null) releaseTimer.Start();
            }
        }

        /// <summary>后台预先创建 WebView 并加载页面，第一次点击时就不用等。</summary>
        public void Warm()
        {
            if (!IsHandleCreated) CreateHandle();
            if (initStarted) return;
            Place();   // 先按最终尺寸布局
            Init();
        }

        void Cloak(bool on)
        {
            int v = on ? 1 : 0;
            Native.DwmSetWindowAttribute(Handle, 13 /*DWMWA_CLOAK*/, ref v, 4);
        }

        void Release()
        {
            if (ctl == null) return;
            try { ctl.Close(); } catch { }
            ctl = null; ready = false; initStarted = false;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= (int)Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1, round = 2 /*DWMWCP_ROUND*/, acrylic = 3 /*DWMSBT_TRANSIENTWINDOW*/;
            Native.DwmSetWindowAttribute(Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref on, 4);
            Native.DwmSetWindowAttribute(Handle, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, 4);
            Native.DwmSetWindowAttribute(Handle, 38 /*DWMWA_SYSTEMBACKDROP_TYPE*/, ref acrylic, 4);
            var m = new Native.MARGINS { L = -1, R = -1, T = -1, B = -1 };
            Native.DwmExtendFrameIntoClientArea(Handle, ref m);
        }

        float Dpi => Math.Max(1f, Native.GetDpiForWindow(Handle) / 96f);

        async void Init()
        {
            initStarted = true;
            try
            {
                ctl = await env.CreateCoreWebView2ControllerAsync(Handle);
                ctl.DefaultBackgroundColor = Color.Transparent;
                ctl.Bounds = ClientRectangle;
                var wv = ctl.CoreWebView2;
                wv.Settings.AreDefaultContextMenusEnabled = false;
                wv.Settings.IsZoomControlEnabled = false;
                wv.Settings.IsStatusBarEnabled = false;
                wv.Settings.AreBrowserAcceleratorKeysEnabled = false;
                wv.SetVirtualHostNameToFolderMapping("wallpaper.local", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Allow);
                wv.WebMessageReceived += (o, e) =>
                {
                    string s = null;
                    try { s = e.TryGetWebMessageAsString(); } catch { }
                    if (s == null) return;
                    if (s == "ready")
                    {
                        ready = true;
                        foreach (var j in latest.Values.ToArray()) Send(j);
                        if (Visible) Send("{\"t\":\"show\"}");
                    }
                    else if (s.StartsWith("size:")) ResizeTo(s.Substring(5));
                    // show → 页面渲染并同步回报 size（已在上面按新高度调好窗口）→ rendered → 宿主发 sync → 页面过两帧回 painted → 现身
                    else if (s == "rendered") { if (revealPending) Send("{\"t\":\"sync\"}"); }
                    else if (s == "painted") { if (revealPending) Reveal(); }
                    else if (s.StartsWith("cmd:")) OnCommand?.Invoke(s.Substring(4));
                    else if (s.StartsWith("log:")) Log.Write("flyout: " + s.Substring(4));
                };
                wv.Navigate("https://wallpaper.local/flyout.html");
            }
            catch (Exception ex) { Log.Write("flyout init failed: " + ex); }
        }

        public void Post(string json)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json, @"""t"":""(\w+)""");
            var type = m.Success ? m.Groups[1].Value : "";
            if (type != "show") latest[type] = json;
            Send(json);
        }

        void Send(string json)
        {
            if (!ready || ctl == null) return;
            try { ctl.CoreWebView2.PostWebMessageAsJson(json); } catch (Exception ex) { Log.Write("flyout post: " + ex.Message); }
        }

        void ResizeTo(string wh)
        {
            var p = wh.Split(',');
            int h;
            if (p.Length != 2 || !int.TryParse(p[1], out h) || h < 100) return;
            cssHeight = h;
            if (Visible) Place();
        }

        /// <summary>贴着任务栏所在屏幕的工作区右下角。</summary>
        void Place()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            float s = Dpi;
            int w = (int)Math.Round(CssWidth * s), h = (int)Math.Round(cssHeight * s), gap = (int)Math.Round(12 * s);
            Bounds = new Rectangle(wa.Right - w - gap, wa.Bottom - h - gap, w, h);
            if (ctl != null) ctl.Bounds = ClientRectangle;
        }

        public void Toggle()
        {
            if (Visible) { HideFlyout(); return; }
            // 点托盘图标本身会先让面板失焦隐藏，紧接着的这次点击不应再弹出来
            if ((DateTime.Now - LastHidden).TotalMilliseconds < 300) return;
            if (!IsHandleCreated) CreateHandle();
            releaseTimer.Stop();
            Place();   // 先按最终尺寸布局，页面量出的高度才准
            if (!initStarted) Init();
            showSince = DateTime.Now;
            revealPending = true;
            revealTimer.Interval = ready ? 600 : 3000;
            revealTimer.Stop(); revealTimer.Start();
            Cloak(true);
            Show();
            Place();   // 显示后按实际 DPI 再算一次
            Native.SetForegroundWindow(Handle);
            Activate();
            if (ctl != null) ctl.IsVisible = true;
            Post("{\"t\":\"show\"}");   // 页面还没加载好时由 ready 处理
        }

        void Reveal()
        {
            revealPending = false; revealTimer.Stop();
            Cloak(false);
            if (ctl != null) ctl.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            var ms = (int)(DateTime.Now - showSince).TotalMilliseconds;
            if (ms > 150) Log.Write("flyout: shown after " + ms + " ms");
        }

        public void HideFlyout()
        {
            revealPending = false; revealTimer.Stop();
            if (!Visible) return;
            LastHidden = DateTime.Now;
            if (ctl != null) ctl.IsVisible = false;
            Hide();
            releaseTimer.Stop();
            if (!keepAlive) releaseTimer.Start();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            HideFlyout();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) HideFlyout();
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { releaseTimer.Dispose(); revealTimer.Dispose(); Release(); }
            base.Dispose(disposing);
        }
    }
}
