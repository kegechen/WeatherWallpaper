using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace WeatherWallpaper
{
    /// <summary>
    /// "屋檐"效果：下雨时雨点砸在普通窗口顶边上溅起水花、留下涟漪，下雪时窗口顶上积一层薄雪。
    /// 每个目标窗口配一条透明点击穿透的分层窗口，z 序紧贴在目标窗口之上，因此会被更上层的窗口正确遮住。
    /// 哪些窗口算目标由 App.Watch() 决定（不含最大化 / 全屏 / 过小 / 过大的窗口）。
    /// </summary>
    class EaveManager : IDisposable
    {
        readonly Dictionary<IntPtr, Eave> eaves = new Dictionary<IntPtr, Eave>();
        readonly Timer timer = new Timer { Interval = 33 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastT, lastZ;
        bool enabled = true, suppressed;
        internal double Rain, Snow, Wind;

        public EaveManager() { timer.Tick += (s, e) => Frame(); }

        public void SetFps(int fps) => timer.Interval = Math.Max(15, 1000 / Math.Max(20, Math.Min(fps, 60)));
        public void SetEnabled(bool on) { enabled = on; if (!on) Clear(); }
        public void SetSuppressed(bool s) { suppressed = s; if (s) Clear(); }
        public void SetScene(double rain, double snow, double wind) { Rain = rain; Snow = snow; Wind = wind; }
        public int Count => eaves.Count;

        bool Weather => Rain > 0.01 || Snow > 0.01;

        /// <summary>targets：当前合格的目标窗口（z 序从上到下）。</summary>
        public void Update(IList<IntPtr> targets)
        {
            if (!enabled || suppressed) { Clear(); return; }
            var want = new HashSet<IntPtr>(Weather ? targets : new IntPtr[0]);
            foreach (var h in eaves.Keys.ToList())
            {
                var e = eaves[h];
                // 不再合格：有积雪 / 水花的先让它自然消退，消退完再删
                if (!want.Contains(h)) { e.Retiring = true; if (!Native.IsWindow(h) || e.Idle) { e.Dispose(); eaves.Remove(h); } }
                else e.Retiring = false;
            }
            foreach (var h in want)
                if (!eaves.ContainsKey(h)) eaves[h] = new Eave(h, this);
            if (eaves.Count > 0 && !timer.Enabled) { lastT = clock.Elapsed.TotalSeconds; timer.Start(); }
            if (eaves.Count == 0) timer.Stop();
        }

        void Frame()
        {
            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.05, now - lastT);
            lastT = now;
            bool fixZ = now - lastZ > 0.25;
            if (fixZ) lastZ = now;
            foreach (var h in eaves.Keys.ToList())
            {
                var e = eaves[h];
                if (!e.Tick(dt, fixZ) || (e.Retiring && e.Idle)) { e.Dispose(); eaves.Remove(h); }
            }
            if (eaves.Count == 0) timer.Stop();
        }

        void Clear()
        {
            foreach (var e in eaves.Values) e.Dispose();
            eaves.Clear();
            timer.Stop();
        }

        public void Dispose() { Clear(); timer.Dispose(); }
    }

    class Eave : NativeWindow, IDisposable
    {
        class Droplet { public float X, Y, VX, VY, R; }
        class Ripple { public float X, T, Life, Z; }
        class Puff { public float X, Y, VX, VY, R, T, Life; }

        readonly List<Puff> puffs = new List<Puff>();
        readonly IntPtr target;
        readonly EaveManager m;
        readonly Random rnd = new Random();
        readonly List<Droplet> drops = new List<Droplet>();
        readonly List<Ripple> ripples = new List<Ripple>();
        float[] pile = new float[0];
        float pileMax, sc = 1, acc;
        int edgeY, width, height, capW;
        Rectangle tr;
        bool visible;
        public bool Retiring;

        IntPtr memDC, dib, oldBmp;
        Bitmap bmp;
        Graphics g;

        public Eave(IntPtr target, EaveManager m)
        {
            this.target = target;
            this.m = m;
            CreateHandle(new CreateParams
            {
                Caption = "WeatherWallpaperEave",
                Style = unchecked((int)Native.WS_POPUP),
                ExStyle = (int)(Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
            });
        }

        public bool Idle => drops.Count == 0 && ripples.Count == 0 && puffs.Count == 0 && pileMax < 0.3f;

        /// <summary>窗口一动，顶上的积雪就被抖下来：变成一团雪粒向上 / 两侧散开再落下淡出。</summary>
        void ShakeOff(float dx)
        {
            int n = Math.Min(pile.Length, width / 2 + 1);
            for (int i = 0; i < n; i++)
            {
                if (pile[i] < 0.6f || rnd.NextDouble() > 0.35) { pile[i] = 0; continue; }
                int k = 1 + (int)(pile[i] / (2 * sc));
                for (int j = 0; j < k && puffs.Count < 500; j++)
                    puffs.Add(new Puff
                    {
                        X = i * 2, Y = edgeY - (float)rnd.NextDouble() * pile[i],
                        VX = (float)((rnd.NextDouble() - 0.5) * 90 * sc - dx * 4),
                        VY = -(float)(30 + rnd.NextDouble() * 110) * sc,
                        R = (float)(0.8 + rnd.NextDouble() * 1.4) * sc,
                        Life = 0.5f + (float)rnd.NextDouble() * 0.5f,
                    });
                pile[i] = 0;
            }
            pileMax = 0;
        }

        /// <summary>返回 false 表示目标窗口已失效，应删除。</summary>
        public bool Tick(float dt, bool fixZ)
        {
            if (!Native.IsWindow(target)) return false;
            if (!Native.IsWindowVisible(target) || Native.IsIconic(target) || Native.IsZoomed(target) || Native.IsCloaked(target))
            {
                // 最小化 / 最大化 / 隐藏：积雪和水花都算被抖掉了
                Array.Clear(pile, 0, pile.Length); pileMax = 0;
                drops.Clear(); ripples.Clear(); puffs.Clear();
                Hide();
                return true;
            }
            var r = Native.VisibleRect(target);
            if (r.Width < 50 || r.Height < 50) { Hide(); return true; }
            if (r != tr || bmp == null) Layout(r);

            if (!Retiring)
            {
                float usable = Math.Max(0, width - 16 * sc);
                acc += (float)(m.Rain * 220 * (usable / 1920.0) * dt);
                while (acc >= 1) { acc -= 1; Splash(8 * sc + (float)rnd.NextDouble() * usable, 0.55f + (float)rnd.NextDouble() * 0.45f); }
                if (m.Snow > 0.01 && rnd.NextDouble() < m.Snow * 40 * (width / 1920.0) * dt) SnowHit((float)(rnd.NextDouble() * width), 0.8f);
            }
            Simulate(dt);
            Draw();
            if (!visible)
            {
                visible = true;
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                fixZ = true;
            }
            if (fixZ) FixZ();
            return true;
        }

        void Hide()
        {
            if (!visible) return;
            visible = false;
            Native.ShowWindow(Handle, Native.SW_HIDE);
        }

        /// <summary>z 序放到目标窗口正上方：被目标之上的窗口遮挡，自己盖住目标的标题栏顶边。</summary>
        void FixZ()
        {
            var prev = Native.GetWindow(target, Native.GW_HWNDPREV);
            if (prev == Handle) return;
            Native.SetWindowPos(Handle, prev == IntPtr.Zero ? IntPtr.Zero /*HWND_TOP*/ : prev, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        void Layout(Rectangle r)
        {
            uint dpi = Native.GetDpiForWindow(target);
            float nsc = dpi > 0 ? dpi / 96f : 1f;
            bool moved = bmp != null && r.Location != tr.Location;
            if (moved && pileMax > 0.3f && (Math.Abs(r.X - tr.X) + Math.Abs(r.Y - tr.Y) > 2)) ShakeOff(Math.Max(-30, Math.Min(30, r.X - tr.X)));
            if (moved) foreach (var p in puffs) { p.X -= r.X - tr.X; p.Y -= r.Y - tr.Y; }   // 抖落的雪留在屏幕原处，不跟着窗口走
            int above = (int)(34 * nsc), below = (int)(5 * nsc);
            int w = r.Width, h = above + below;
            if (bmp == null || w > capW || h != height || nsc != sc)
            {
                sc = nsc;
                edgeY = above;
                height = h;
                AllocSurface(((w + 255) / 256) * 256, h);
                var np = new float[capW / 2 + 2];
                Array.Copy(pile, np, Math.Min(pile.Length, np.Length));
                pile = np;
            }
            width = w;
            tr = r;
            // 被拖动时溅起的水花跟着窗口走（本地坐标），只需要挪层窗口位置
            Native.SetWindowPos(Handle, IntPtr.Zero, r.X, r.Y - edgeY, width, height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            if (moved) for (int i = drops.Count - 1; i >= 0; i--) if (drops[i].X > width) drops.RemoveAt(i);
        }

        void Splash(float x, float z)
        {
            int n = 1 + rnd.Next(3);
            for (int i = 0; i < n && drops.Count < 300; i++)
                drops.Add(new Droplet
                {
                    X = x, Y = edgeY - 1,
                    VX = (float)((rnd.NextDouble() - 0.5) * 130 + m.Wind * 40) * sc,
                    VY = -(float)(55 + rnd.NextDouble() * 140) * sc * z,
                    R = (float)(0.7 + rnd.NextDouble() * 0.8) * sc * (0.6f + 0.5f * z),
                });
            if (ripples.Count < 120) ripples.Add(new Ripple { X = x, T = 0, Life = 0.2f + (float)rnd.NextDouble() * 0.12f, Z = z });
        }

        void SnowHit(float x, float z)
        {
            int c = (int)(x / 2);
            float cap = 5f * sc;
            for (int k = -5; k <= 5; k++)
            {
                int i = c + k;
                if (i < 0 || i >= pile.Length || i * 2 > width) continue;
                if (pile[i] < cap) pile[i] += 0.35f * sc * z * (float)Math.Exp(-(k * k) / 8.0);
            }
        }

        void Simulate(float dt)
        {
            float grav = 1100 * sc;
            for (int i = drops.Count - 1; i >= 0; i--)
            {
                var d = drops[i];
                d.VY += grav * dt; d.X += d.VX * dt; d.Y += d.VY * dt;
                if ((d.Y > edgeY && d.VY > 0) || d.X < -4 || d.X > width + 4) drops.RemoveAt(i);
            }
            for (int i = ripples.Count - 1; i >= 0; i--)
            {
                ripples[i].T += dt;
                if (ripples[i].T >= ripples[i].Life) ripples.RemoveAt(i);
            }
            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                var p = puffs[i];
                p.T += dt; p.VY += 320 * sc * dt; p.VX *= 1 - 1.5f * dt;
                p.X += p.VX * dt; p.Y += p.VY * dt;
                if (p.T >= p.Life || p.Y > height + 4) puffs.RemoveAt(i);
            }
            float max = 0, melt = m.Snow > 0.01 && !Retiring ? 0.003f * sc * dt : 0.25f * sc * dt;
            int n = Math.Min(pile.Length - 1, width / 2 + 1);
            // Win11 圆角：两端积雪逐渐变薄
            float corner = 8 * sc / 2;
            for (int i = 1; i < n; i++)
            {
                float v = pile[i] * 0.96f + (pile[i - 1] + pile[i + 1]) * 0.02f - melt;
                float edge = Math.Min(i, n - i);
                if (edge < corner) v = Math.Min(v, pile[i] * (0.5f + 0.5f * edge / corner));
                pile[i] = v > 0 ? v : 0;
                if (pile[i] > max) max = pile[i];
            }
            pileMax = max;
        }

        void Draw()
        {
            g.Clear(Color.Transparent);
            float rain = (float)m.Rain;
            if (rain > 0.01 && !Retiring)
            {
                // 被雨打湿的顶边高光
                using (var p = new Pen(Color.FromArgb((int)(40 + 60 * Math.Min(1, rain)), 225, 235, 248), 1f * sc))
                    g.DrawLine(p, 6 * sc, edgeY + 0.5f * sc, width - 6 * sc, edgeY + 0.5f * sc);
            }
            if (pileMax > 0.3f)
            {
                int n = Math.Min(pile.Length, width / 2 + 1);
                var pts = new List<PointF>(n + 2) { new PointF(0, edgeY + 1.5f * sc) };
                for (int i = 0; i < n; i++) pts.Add(new PointF(i * 2, edgeY + 1 - pile[i]));
                pts.Add(new PointF(width, edgeY + 1.5f * sc));
                using (var b = new SolidBrush(Color.FromArgb(240, 250, 252, 255))) g.FillPolygon(b, pts.ToArray());
                using (var p = new Pen(Color.FromArgb(60, 160, 175, 200), 1f)) g.DrawLines(p, pts.ToArray());
            }
            using (var pen = new Pen(Color.White, 1f))
                foreach (var r in ripples)
                {
                    float k = r.T / r.Life;
                    float w = (3 + 14 * k) * sc * (0.6f + 0.6f * r.Z), h = (1.1f + 2f * k) * sc;
                    pen.Color = Color.FromArgb((int)(160 * (1 - k)), 230, 240, 255);
                    g.DrawEllipse(pen, r.X - w / 2, edgeY - h / 2, w, h);
                }
            using (var b = new SolidBrush(Color.FromArgb(210, 228, 238, 250)))
            using (var streak = new Pen(Color.FromArgb(90, 228, 238, 250), 1f))
                foreach (var d in drops)
                {
                    g.FillEllipse(b, d.X - d.R, d.Y - d.R, d.R * 2, d.R * 2);
                    streak.Width = d.R;
                    g.DrawLine(streak, d.X, d.Y, d.X - d.VX * 0.012f, d.Y - d.VY * 0.012f);
                }
            if (puffs.Count > 0)
                using (var b = new SolidBrush(Color.White))
                    foreach (var p in puffs)
                    {
                        b.Color = Color.FromArgb((int)(235 * (1 - p.T / p.Life)), 250, 252, 255);
                        g.FillEllipse(b, p.X - p.R, p.Y - p.R, p.R * 2, p.R * 2);
                    }
            g.Flush(FlushIntention.Sync);

            var dst = new Native.POINT(tr.X, tr.Y - edgeY);
            var size = new Native.SIZE(width, height);
            var src = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDC, ref src, 0, ref blend, 2);
        }

        void AllocSurface(int w, int h)
        {
            FreeSurface();
            capW = w;
            var bi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screen);
            Native.ReleaseDC(IntPtr.Zero, screen);
            IntPtr bits;
            dib = Native.CreateDIBSection(memDC, ref bi, 0, out bits, IntPtr.Zero, 0);
            oldBmp = Native.SelectObject(memDC, dib);
            bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
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

        public void Dispose()
        {
            FreeSurface();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
