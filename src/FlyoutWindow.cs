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
        // 收起 1 分钟后释放 WebView（省一个渲染进程的内存），下次弹出再重建
        readonly Timer releaseTimer = new Timer { Interval = 60000 };
        bool ready, initStarted;
        int cssHeight = 560;
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
            KeyPreview = true;
            releaseTimer.Tick += (s, e) => { releaseTimer.Stop(); if (!Visible) Release(); };
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
                    if (s == "ready") { ready = true; foreach (var j in latest.Values.ToArray()) Send(j); }
                    else if (s.StartsWith("size:")) ResizeTo(s.Substring(5));
                    else if (s.StartsWith("cmd:")) OnCommand?.Invoke(s.Substring(4));
                    else if (s.StartsWith("log:")) Log.Write("flyout: " + s.Substring(4));
                };
                wv.Navigate("https://wallpaper.local/flyout.html");
                if (Visible) ctl.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
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
            if (!initStarted) Init();
            Place();
            Show();
            Place();   // 显示后按实际 DPI 再算一次
            Native.SetForegroundWindow(Handle);
            Activate();
            if (ctl != null)
            {
                ctl.IsVisible = true;
                ctl.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            }
            Post("{\"t\":\"show\"}");
        }

        public void HideFlyout()
        {
            if (!Visible) return;
            LastHidden = DateTime.Now;
            if (ctl != null) ctl.IsVisible = false;
            Hide();
            releaseTimer.Stop(); releaseTimer.Start();
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
            if (disposing) { releaseTimer.Dispose(); Release(); }
            base.Dispose(disposing);
        }
    }
}
