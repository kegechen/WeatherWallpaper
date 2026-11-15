using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;

namespace WeatherWallpaper
{
    /// <summary>
    /// 任务栏上方的透明点击穿透层：雨滴砸到任务栏顶边的溅射、涟漪、任务栏表面的水珠，下雪时任务栏顶上会积一层雪。
    /// 用 UpdateLayeredWindow 逐像素 alpha 绘制。
    /// </summary>
    class TaskbarOverlay : NativeWindow, IDisposable
    {
        class Droplet { public float X, Y, VX, VY, R; }
        class Ripple { public float X, T, Life, Z; }
        class Bead { public float X, Y, VY, R, T, Life; }

        readonly List<Droplet> droplets = new List<Droplet>();
        readonly List<Ripple> ripples = new List<Ripple>();
        readonly List<Bead> beads = new List<Bead>();
        float[] pile = new float[0];
        float pileMax;

        readonly Random rnd = new Random();
        readonly Timer timer = new Timer();
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastT, lastExternal = -10, lastTop, selfAcc, beadAcc;

        Rectangle area, monitor;
        int edgeY, tbHeight;
        float sc = 1;
        bool enabled, suppressed, covered, shown;
        double rain, snow, wind;

        IntPtr memDC, dib, oldBmp;
        Bitmap bmp;
        Graphics g;

        public TaskbarOverlay()
        {
            CreateHandle(new CreateParams
            {
                Caption = "WeatherWallpaperTaskbarFx",
                Style = unchecked((int)Native.WS_POPUP),
                ExStyle = (int)(Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
            });
            timer.Interval = 16;
            timer.Tick += (s, e) => Frame();
        }

        public void SetFps(int fps) => timer.Interval = Math.Max(10, 1000 / Math.Max(30, fps));

        // 浅色任务栏上白色水滴看不见，按系统主题换配色
        bool light;
        Color dropC, rippleC, beadC, lineC, pileEdgeC;
        void UpdateTheme()
        {
            try
            {
                var v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
                light = v is int && (int)v != 0;
            }
            catch { light = false; }
            if (light)
            {
                dropC = Color.FromArgb(90, 125, 170); rippleC = Color.FromArgb(70, 110, 160);
                beadC = Color.FromArgb(95, 135, 185); lineC = Color.FromArgb(110, 150, 195); pileEdgeC = Color.FromArgb(150, 120, 140, 170);
            }
            else
            {
                dropC = Color.FromArgb(228, 238, 250); rippleC = Color.FromArgb(230, 240, 255);
                beadC = Color.FromArgb(215, 228, 245); lineC = Color.FromArgb(225, 235, 248); pileEdgeC = Color.FromArgb(70, 160, 175, 200);
            }
        }

        /// <summary>taskbar 为空表示没有可用任务栏（自动隐藏 / 不在底部）。</summary>
        public void Configure(Rectangle? taskbar, Rectangle mon, float scale, bool on)
        {
            enabled = on && taskbar.HasValue;
            UpdateTheme();
            monitor = mon;
            sc = scale;
            if (!enabled) { UpdateVisibility(); return; }
            var tb = taskbar.Value;
            int above = (int)(46 * sc);
            var a = new Rectangle(tb.X, tb.Y - above, tb.Width, tb.Height + above);
            edgeY = above;
            tbHeight = tb.Height;
            if (a != area)
            {
                area = a;
                AllocSurface();
                pile = new float[a.Width / 2 + 2];
                droplets.Clear(); ripples.Clear(); beads.Clear();
            }
            UpdateVisibility();
        }

        public void SetScene(double rain, double snow, double wind)
        {
            this.rain = rain; this.snow = snow; this.wind = wind;
            UpdateVisibility();
        }

        public void SetSuppressed(bool s) { if (s != suppressed) { suppressed = s; UpdateVisibility(); } }
        public void SetCovered(bool c) { covered = c; }

        /// <summary>来自壁纸的落地点："x:z,x:z|x:z"（x 为显示器宽度比例）。前半是雨，后半是雪。</summary>
        public void AddHits(string data)
        {
            if (!enabled || suppressed || bmp == null) return;
            lastExternal = clock.Elapsed.TotalSeconds;
            var halves = data.Split('|');
            for (int k = 0; k < halves.Length && k < 2; k++)
            {
                if (halves[k].Length == 0) continue;
                foreach (var item in halves[k].Split(','))
                {
                    var p = item.Split(':');
                    float fx, z;
                    if (p.Length != 2 || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out fx)
                        || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;
                    float x = fx * monitor.Width + monitor.X - area.X;
                    if (k == 0) Splash(x, z); else SnowHit(x, z);
                }
            }
        }

        void Splash(float x, float z)
        {
            if (x < 0 || x >= area.Width) return;
            int n = 2 + rnd.Next(3);
            float lift = covered ? 0.35f : 1f;
            for (int i = 0; i < n && droplets.Count < 700; i++)
            {
                droplets.Add(new Droplet
                {
                    X = x,
                    Y = edgeY - 1,
                    VX = (float)((rnd.NextDouble() - 0.5) * 150 + wind * 45) * sc,
                    VY = -(float)(45 + rnd.NextDouble() * 120) * sc * z * lift,
                    R = (float)(0.7 + rnd.NextDouble() * 0.8) * sc * (0.6f + 0.5f * z),
                });
            }
            if (ripples.Count < 200) ripples.Add(new Ripple { X = x, T = 0, Life = 0.22f + (float)rnd.NextDouble() * 0.12f, Z = z });
        }

        void SnowHit(float x, float z)
        {
            int c = (int)(x / 2);
            if (c < 0 || c >= pile.Length) return;
            float cap = 7f * sc;
            for (int k = -5; k <= 5; k++)
            {
                int i = c + k;
                if (i < 0 || i >= pile.Length) continue;
                if (pile[i] < cap) pile[i] += 0.35f * sc * z * (float)Math.Exp(-(k * k) / 8.0);
            }
        }

        void UpdateVisibility()
        {
            bool active = rain > 0.01 || snow > 0.01 || droplets.Count > 0 || beads.Count > 0 || pileMax > 0.3f;
            bool want = enabled && !suppressed && bmp != null && active;
            if (want == shown) return;
            shown = want;
            if (want)
            {
                lastT = clock.Elapsed.TotalSeconds;
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, area.X, area.Y, area.Width, area.Height, Native.SWP_NOACTIVATE);
                timer.Start();
            }
            else
            {
                timer.Stop();
                Native.ShowWindow(Handle, Native.SW_HIDE);
            }
        }

        void AllocSurface()
        {
            FreeSurface();
            if (area.Width <= 0 || area.Height <= 0) return;
            var bi = new Native.BITMAPINFOHEADER
            {
                biSize = 40, biWidth = area.Width, biHeight = -area.Height, biPlanes = 1, biBitCount = 32,
            };
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screen);
            Native.ReleaseDC(IntPtr.Zero, screen);
            IntPtr bits;
            dib = Native.CreateDIBSection(memDC, ref bi, 0, out bits, IntPtr.Zero, 0);
            oldBmp = Native.SelectObject(memDC, dib);
            bmp = new Bitmap(area.Width, area.Height, area.Width * 4, PixelFormat.Format32bppPArgb, bits);
            g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighSpeed;
        }

        void FreeSurface()
        {
            g?.Dispose(); g = null;
            bmp?.Dispose(); bmp = null;
            if (memDC != IntPtr.Zero) { Native.SelectObject(memDC, oldBmp); Native.DeleteDC(memDC); memDC = IntPtr.Zero; }
            if (dib != IntPtr.Zero) { Native.DeleteObject(dib); dib = IntPtr.Zero; }
        }

        void Frame()
        {
            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.05, now - lastT);
            lastT = now;
            if (g == null) return;

            // 壁纸暂停（被最大化窗口挡住）时自己产生落点，保证任务栏上仍在下雨
            if (now - lastExternal > 0.6)
            {
                selfAcc += rain * 170 * (area.Width / 1920.0) * dt;
                while (selfAcc >= 1) { selfAcc -= 1; Splash((float)(rnd.NextDouble() * area.Width), 0.75f + (float)rnd.NextDouble() * 0.25f); }
                if (snow > 0.01 && rnd.NextDouble() < snow * 60 * dt) SnowHit((float)(rnd.NextDouble() * area.Width), 0.8f);
            }
            // 任务栏表面零星水珠
            beadAcc += rain * 7 * (area.Width / 1920.0) * dt;
            while (beadAcc >= 1 && beads.Count < 90)
            {
                beadAcc -= 1;
                beads.Add(NewBead((float)(rnd.NextDouble() * area.Width), edgeY + 3 + (float)rnd.NextDouble() * (tbHeight * 0.75f)));
            }
            if (beadAcc > 1) beadAcc = 0;

            Simulate(dt);
            Draw();

            if (now - lastTop > 0.3)
            {
                lastTop = now;
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            UpdateVisibility();
        }

        Bead NewBead(float x, float y) => new Bead
        {
            X = x, Y = y, VY = (float)(1 + rnd.NextDouble() * 6) * sc,
            R = (float)(1.0 + rnd.NextDouble() * 1.6) * sc, T = 0, Life = 1.5f + (float)rnd.NextDouble() * 3f,
        };

        void Simulate(float dt)
        {
            float grav = 1100 * sc;
            for (int i = droplets.Count - 1; i >= 0; i--)
            {
                var d = droplets[i];
                d.VY += grav * dt;
                d.X += d.VX * dt;
                d.Y += d.VY * dt;
                if (d.Y > edgeY + 1 && d.VY > 0)
                {
                    if (rnd.NextDouble() < 0.05 && beads.Count < 90) beads.Add(NewBead(d.X, edgeY + 2 + (float)rnd.NextDouble() * 6 * sc));
                    droplets.RemoveAt(i);
                }
            }
            for (int i = ripples.Count - 1; i >= 0; i--)
            {
                ripples[i].T += dt;
                if (ripples[i].T >= ripples[i].Life) ripples.RemoveAt(i);
            }
            int bottom = area.Height - 1;
            for (int i = beads.Count - 1; i >= 0; i--)
            {
                var b = beads[i];
                b.T += dt;
                b.Y += b.VY * dt;
                if (b.T >= b.Life || b.Y > bottom) beads.RemoveAt(i);
            }

            // 积雪：扩散平滑 + 无雪时融化
            float max = 0, melt = snow > 0.01 ? 0.004f * sc * dt : 0.18f * sc * dt;
            for (int i = 1; i < pile.Length - 1; i++)
            {
                float v = pile[i] * 0.96f + (pile[i - 1] + pile[i + 1]) * 0.02f - melt;
                pile[i] = v > 0 ? v : 0;
                if (pile[i] > max) max = pile[i];
            }
            pileMax = max;
        }

        void Draw()
        {
            g.Clear(Color.Transparent);

            if (rain > 0.01)
            {
                using (var p = new Pen(Color.FromArgb((int)((light ? 60 : 28) + 40 * rain), lineC), 1f * sc))
                    g.DrawLine(p, 0, edgeY + 0.5f * sc, area.Width, edgeY + 0.5f * sc);
            }

            if (pileMax > 0.3f)
            {
                var pts = new List<PointF>(pile.Length + 2) { new PointF(0, edgeY + 1.5f * sc) };
                for (int i = 0; i < pile.Length; i++) pts.Add(new PointF(i * 2, edgeY + 1 - pile[i]));
                pts.Add(new PointF(area.Width, edgeY + 1.5f * sc));
                using (var b = new SolidBrush(Color.FromArgb(245, 250, 252, 255))) g.FillPolygon(b, pts.ToArray());
                using (var p = new Pen(pileEdgeC, 1f)) g.DrawLine(p, 0, edgeY + 1.5f * sc, area.Width, edgeY + 1.5f * sc);
            }

            using (var pen = new Pen(Color.White, 1f))
            {
                foreach (var r in ripples)
                {
                    float k = r.T / r.Life;
                    float w = (3 + 16 * k) * sc * (0.6f + 0.6f * r.Z), h = (1.2f + 2.2f * k) * sc;
                    pen.Color = Color.FromArgb((int)((light ? 190 : 150) * (1 - k)), rippleC);
                    g.DrawEllipse(pen, r.X - w / 2, edgeY - h / 2, w, h);
                }
            }

            using (var b = new SolidBrush(Color.White))
            using (var streak = new Pen(Color.White, 1f))
            {
                foreach (var d in droplets)
                {
                    b.Color = Color.FromArgb(light ? 215 : 200, dropC);
                    g.FillEllipse(b, d.X - d.R, d.Y - d.R, d.R * 2, d.R * 2);
                    streak.Color = Color.FromArgb(light ? 110 : 80, dropC);
                    streak.Width = d.R;
                    g.DrawLine(streak, d.X, d.Y, d.X - d.VX * 0.012f, d.Y - d.VY * 0.012f);
                }
                foreach (var bd in beads)
                {
                    float k = bd.T / bd.Life, a = k < 0.1f ? k / 0.1f : 1 - (k - 0.1f) / 0.9f;
                    b.Color = Color.FromArgb((int)((light ? 95 : 70) * a), beadC);
                    g.FillEllipse(b, bd.X - bd.R, bd.Y - bd.R * 1.15f, bd.R * 2, bd.R * 2.3f);
                    b.Color = Color.FromArgb((int)(170 * a), 255, 255, 255);
                    float hr = bd.R * 0.42f;
                    g.FillEllipse(b, bd.X - bd.R * 0.45f - hr / 2, bd.Y - bd.R * 0.6f - hr / 2, hr, hr);
                }
            }
            g.Flush(FlushIntention.Sync);

            var dst = new Native.POINT(area.X, area.Y);
            var size = new Native.SIZE(area.Width, area.Height);
            var src = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDC, ref src, 0, ref blend, 2);
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Dispose();
            FreeSurface();
            DestroyHandle();
        }
    }
}
