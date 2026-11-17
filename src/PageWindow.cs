using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace WeatherWallpaper
{
    /// <summary>
    /// 网页做内容的设置类窗口（卡片设置 web/card.html、关于 web/about.html）：带标题栏的普通窗口，Mica 背景，
    /// 和宿主用 JSON 消息往来（页面就绪前发的消息先排队）。
    /// 点 × 时先发 {"t":"flush"} 让页面把还没发出来的编辑交上来，页面回 close（宿主调 CloseNow）后再关。
    /// </summary>
    class PageWindow : Form
    {
        readonly CoreWebView2Environment env;
        readonly string page;
        CoreWebView2Controller ctl;
        bool ready, closing, flushing;   // flushing：点了 ×，正在等页面把防抖中的编辑交上来
        readonly List<string> pending = new List<string>();
        public Action<string> OnMessage;   // 页面发来的 JSON
        public Action Reloaded;            // 页面重新加载过（状态已丢）：宿主重发 init / fit

        /// <param name="cssW">内容大小（页面 CSS 像素，按所在屏的缩放换算）：内容全部放得下不用滚动；屏幕矮时按工作区收小（页面可滚动）</param>
        public PageWindow(CoreWebView2Environment env, string title, string page, int cssW, int cssH)
        {
            this.env = env;
            this.page = page;
            Text = title;
            Icon = Program.AppIcon;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;   // 延伸到客户区的 DWM 背景下，黑色 = 透出 Mica
            // 开在鼠标所在的屏（从托盘菜单点开时就是任务栏那块），按那块屏的缩放算大小
            var scr = Screen.FromPoint(Cursor.Position);
            float k = Native.ScaleOf(scr);
            var wa = scr.WorkingArea;
            ClientSize = new Size((int)Math.Min(cssW * k, wa.Width - 40 * k), (int)Math.Min(cssH * k, wa.Height - 60 * k));
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + Math.Max(0, (wa.Height - Height) / 2));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1, mica = 2 /*DWMSBT_MAINWINDOW*/;
            Native.DwmSetWindowAttribute(Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref on, 4);
            Native.DwmSetWindowAttribute(Handle, 38 /*DWMWA_SYSTEMBACKDROP_TYPE*/, ref mica, 4);
            var m = new Native.MARGINS { L = -1, R = -1, T = -1, B = -1 };
            Native.DwmExtendFrameIntoClientArea(Handle, ref m);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Init();
        }

        async void Init()
        {
            try
            {
                var c = await env.CreateCoreWebView2ControllerAsync(Handle);
                if (IsDisposed) { c.Close(); return; }
                ctl = c;
                ctl.DefaultBackgroundColor = Color.Transparent;
                ctl.Bounds = ClientRectangle;
                var wv = ctl.CoreWebView2;
                WebHost.Configure(wv);
                // 名称框、城市搜索框要能右键剪切 / 复制 / 粘贴：页面自己拦了输入框以外的右键（card.html、about.html 的 contextmenu），这里不再全局关掉；
                // 同时关掉开发者工具，菜单里不出现「检查」
                wv.Settings.AreDefaultContextMenusEnabled = true;
                wv.Settings.AreDevToolsEnabled = false;
                wv.WebMessageReceived += (o, ev) =>
                {
                    string json = null;
                    try { json = ev.WebMessageAsJson; } catch { }
                    if (json == null) return;
                    if (json.StartsWith("{\"t\":\"ready\""))
                    {
                        // 页面又就绪了一次（重新加载、渲染进程崩溃后重新载入）：页面状态全没了，请宿主重发 init / fit
                        if (ready) { BeginInvoke((Action)(() => { if (!IsDisposed) Reloaded?.Invoke(); })); return; }
                        ready = true;
                        var list = pending.ToArray();
                        pending.Clear();
                        foreach (var j in list) Post(j);
                        return;
                    }
                    // 放回消息循环里再处理：处理时可能会关掉这个窗口（「完成」），不能在 WebView2 自己的事件回调里把它销毁
                    BeginInvoke((Action)(() => { if (!IsDisposed) OnMessage?.Invoke(json); }));
                };
                wv.ProcessFailed += (o, ev) =>
                {
                    Log.Write(page + ": webview process failed " + ev.ProcessFailedKind);
                    if (ev.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited || ev.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                        BeginInvoke((Action)(() => { try { if (!IsDisposed) ctl?.CoreWebView2.Reload(); } catch { } }));   // 重新载入后页面会再发 ready → Reloaded
                };
                wv.Navigate("https://wallpaper.local/" + page);
                if (ContainsFocus) ctl.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            }
            catch (Exception ex) { Log.Write(page + " window init failed: " + ex); }
        }

        public void Post(string json)
        {
            if (IsDisposed) return;
            if (!ready || ctl == null)
            {
                // 页面就绪前（WebView2 冷启动要一两秒，这期间布局、下载进度可能变好几次）：init / fit / about 这些整份状态各只留最后一份
                foreach (var head in new[] { "{\"t\":\"fit\"", "{\"t\":\"init\"", "{\"t\":\"about\"" })
                    if (json.StartsWith(head)) pending.RemoveAll(j => j.StartsWith(head));
                pending.Add(json);
                return;
            }
            try { ctl.CoreWebView2.PostWebMessageAsJson(json); } catch (Exception ex) { Log.Write(page + " post: " + ex.Message); }
        }

        /// <summary>页面已经把编辑都交上来了（「完成」、Esc），或者程序要退出：直接关。</summary>
        public void CloseNow()
        {
            closing = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 点标题栏 ×、Alt+F4：页面上可能还有防抖中没发出来的编辑（名称、偏移）。先让页面交上来，页面回 close 后再关；页面 0.5 秒没反应照关。
            // 等的这段时间里再点 × 也一样等（不然连点两下就直接关了，编辑没交上来）
            if (!closing && ready && ctl != null && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                if (flushing) return;
                flushing = true;
                Post("{\"t\":\"flush\"}");
                var t = new Timer { Interval = 500 };
                t.Tick += (s, a) => { t.Stop(); t.Dispose(); if (!IsDisposed) CloseNow(); };
                t.Start();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (ctl != null) ctl.Bounds = ClientRectangle;
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            try { ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            // 拖到缩放比例不同的屏上：按系统建议的大小调整窗口（.NET Framework 的 WinForms 默认不处理）
            if (m.Msg == 0x02E0 /*WM_DPICHANGED*/)
            {
                var r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                Native.SetWindowPos(Handle, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { ctl?.Close(); } catch { } ctl = null; }
            base.Dispose(disposing);
        }
    }

    /// <summary>「识别屏幕」：在每块屏中间显示 3 秒大号编号（和菜单、卡片设置里的"显示器 N"一致），不抢焦点。</summary>
    class ScreenBadge : Form
    {
        readonly string num, sub;
        readonly float k;

        public static void ShowAll()
        {
            var all = Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
            {
                var s = all[i];
                var b = new ScreenBadge(s, (i + 1).ToString(), s.Bounds.Width + "×" + s.Bounds.Height + (s.Primary ? " · 主屏" : ""));
                b.Show();
                var t = new Timer { Interval = 3000 };
                t.Tick += (o, e) => { t.Stop(); t.Dispose(); b.Close(); b.Dispose(); };
                t.Start();
            }
        }

        ScreenBadge(Screen s, string num, string sub)
        {
            this.num = num; this.sub = sub;
            k = Native.ScaleOf(s);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(28, 34, 48);
            Opacity = 0.92;
            DoubleBuffered = true;
            int w = (int)(240 * k), h = (int)(220 * k);
            Bounds = new Rectangle(s.Bounds.X + (s.Bounds.Width - w) / 2, s.Bounds.Y + (s.Bounds.Height - h) / 2, w, h);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST);
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2 /*DWMWCP_ROUND*/, on = 1;
            Native.DwmSetWindowAttribute(Handle, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, 4);
            Native.DwmSetWindowAttribute(Handle, 20, ref on, 4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var big = new Font("Segoe UI", 120 * k, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var small = new Font("Microsoft YaHei UI", 17 * k, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(num, big, Brushes.White, new RectangleF(0, 0, Width, Height * 0.78f), sf);
                using (var br = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                    g.DrawString(sub, small, br, new RectangleF(0, Height * 0.68f, Width, Height * 0.24f), sf);
            }
        }
    }
}
