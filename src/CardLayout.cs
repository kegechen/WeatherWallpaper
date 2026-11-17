using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WeatherWallpaper
{
    /// <summary>一块屏在页面坐标里的样子：整块屏和工作区（去掉任务栏），CSS 像素，原点是这块屏的左上角。</summary>
    class ScreenArea
    {
        public SizeF Size;
        public RectangleF Work;

        public static ScreenArea Of(Screen s, float scale)
        {
            if (scale <= 0.1f) scale = 1f;
            var b = s.Bounds;
            var wa = s.WorkingArea;
            return new ScreenArea
            {
                Size = new SizeF(b.Width / scale, b.Height / scale),
                Work = new RectangleF((wa.X - b.X) / scale, (wa.Y - b.Y) / scale, wa.Width / scale, wa.Height / scale),
            };
        }
    }

    /// <summary>一张卡片的摆放结果。</summary>
    class CardPlace
    {
        public CardSpec Card;
        public Screen Host;         // 实际显示在哪块屏（null = 没有任何屏在显示壁纸）
        public RectangleF? Rect;    // 在 Host 上的位置（CSS 像素）；null = 隐藏卡片，或放不下暂时隐藏
    }

    /// <summary>
    /// 卡片布局：在一块屏的可用区域（工作区，页面 CSS 像素）里按顺序摆卡片，互不重叠。
    ///  1. 按位置 + 偏移算出期望位置（超出可用区域就收进来）；
    ///  2. 和已经摆好的卡片重叠（留 Gap 间距）→ 沿离开锚点边的方向推：顶部位置往下、底部位置往上；
    ///  3. 推出可用区域 → 往屏幕内侧挪一列（右侧往左、左侧往右、居中左右交替）再试；
    ///  4. 所有列都放不下 → 这张卡片放不下（返回 null）。
    /// 效果：同一位置的卡片自动排成一列，一列满了往内侧开第二列，整块屏满了就放不下。
    /// </summary>
    static class CardLayout
    {
        public const float W = 336, H = 480, Gap = 12;   // 与 style.css 里 .card 的尺寸一致

        /// <summary>卡片所在屏的分组键：屏存在 = 它的 DeviceName，屏不在（拔掉了）= 保存的标识。</summary>
        public static string GroupKey(CardSpec c)
        {
            var s = Resolve(c.Screen);
            return s != null ? s.DeviceName : c.Screen;
        }

        /// <summary>
        /// 显示器的硬件 ID（设备接口名，含型号和接口实例）。\\.\DISPLAYn 在重插扩展坞、更新驱动后可能换号，硬件 ID 一般不变：
        /// 卡片同时记下它，换号后按它找回原来那块屏（见 App.HealScreens）。取不到返回 null。
        /// </summary>
        public static string MonitorId(Screen s)
        {
            if (s == null) return null;
            try
            {
                string first = null;
                for (uint i = 0; i < 8; i++)
                {
                    var dd = new Native.DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE)) };
                    if (!Native.EnumDisplayDevices(s.DeviceName, i, ref dd, 1 /*EDD_GET_DEVICE_INTERFACE_NAME*/)) break;
                    if (string.IsNullOrEmpty(dd.DeviceID)) continue;
                    if ((dd.StateFlags & 1 /*DISPLAY_DEVICE_ACTIVE*/) != 0) return dd.DeviceID;
                    if (first == null) first = dd.DeviceID;
                }
                return first;
            }
            catch { return null; }
        }

        /// <summary>卡片设置里的屏幕标识对应的显示器硬件 ID（primary、没接上的屏为 null）。</summary>
        public static string MonitorOf(string id) => string.IsNullOrEmpty(id) || id == CardSpec.PrimaryScreen ? null : MonitorId(Resolve(id));

        /// <summary>卡片设置里的屏幕标识 → 屏幕：primary = 主屏；否则按 DeviceName 找，找不到（拔掉了）返回 null。</summary>
        public static Screen Resolve(string id)
        {
            var all = Screen.AllScreens;
            if (string.IsNullOrEmpty(id) || id == CardSpec.PrimaryScreen) return all.FirstOrDefault(s => s.Primary) ?? all.FirstOrDefault();
            return all.FirstOrDefault(s => s.DeviceName == id);
        }

        /// <summary>
        /// 把所有卡片分到正在显示壁纸的屏上并排好位置。卡片和它的屏绑定：所在屏不存在（拔掉了）→ 这张卡片就不显示，接回来自动恢复。
        /// 隐藏卡片（只显示天气画面）不占位置；放不下 → Rect=null 暂时隐藏，也不挪去别的屏。
        /// </summary>
        public static List<CardPlace> Arrange(IList<CardSpec> cards, IList<Screen> shown, Func<Screen, ScreenArea> area)
        {
            var res = cards.Select(c => new CardPlace { Card = c }).ToList();
            foreach (var p in res)
            {
                var s = Resolve(p.Card.Screen);
                p.Host = s == null ? null : shown.FirstOrDefault(x => x.DeviceName == s.DeviceName);   // 屏不在：不显示，也不挪去别的屏
            }
            foreach (var g in res.Where(p => p.Host != null && !p.Card.Hidden).GroupBy(p => p.Host.DeviceName))
            {
                var list = g.ToList();
                var rects = Place(area(list[0].Host).Work, list.Select(p => p.Card).ToList());
                for (int i = 0; i < list.Count; i++) list[i].Rect = rects[i];
            }
            return res;
        }

        /// <summary>这块屏上还能再放几张卡片（按 like 的位置和偏移往里塞，没给就按默认右上角；最多 MaxCards）。</summary>
        public static int Free(RectangleF work, IList<CardSpec> cards, CardSpec like = null)
        {
            var list = cards.ToList();
            int n = 0;
            while (n < Settings.MaxCards)
            {
                var probe = like != null ? like.Clone() : new CardSpec();
                probe.Id = "probe"; probe.Hidden = false;
                list.Add(probe);
                var r = Place(work, list);
                if (!r[r.Length - 1].HasValue) break;
                n++;
            }
            return n;
        }

        public static RectangleF?[] Place(RectangleF area, IList<CardSpec> cards)
        {
            // 屏幕太矮（小屏开了高缩放）、可用区域比卡片还矮：按卡片高度算，卡片贴着上边、下面一截被任务栏挡住（和旧版一样），不至于一张都放不下
            if (area.Height < H) area.Height = H;
            if (area.Width < W) area.Width = W;   // 同理：竖屏 / 窄屏开了高缩放比卡片还窄，贴着左边放，右边一截露不出来
            var placed = new List<RectangleF>();
            var res = new RectangleF?[cards.Count];
            for (int i = 0; i < cards.Count; i++)
            {
                var r = PlaceOne(area, cards[i], placed);
                res[i] = r;
                if (r.HasValue) placed.Add(r.Value);
            }
            return res;
        }

        /// <summary>位置锚点：贴哪条水平边（bottom = 底部）和水平位置（l 左 / c 中 / r 右）。Desired 和 PlaceOne 用同一份，别各写各的。</summary>
        static void AnchorOf(CardSpec c, out bool bottom, out char hz)
        {
            var p = c.Pos ?? "tr";
            bottom = p[0] == 'b';
            hz = p.Length > 1 ? p[1] : 'r';
        }

        /// <summary>按位置 + 偏移算出的期望位置（不考虑其他卡片），超出可用区域就收进来（区域比卡片矮时贴着上边）。</summary>
        public static RectangleF Desired(RectangleF area, CardSpec c)
        {
            bool bottom; char hz;
            AnchorOf(c, out bottom, out hz);
            float x = hz == 'l' ? area.Left + c.OffsetX : hz == 'r' ? area.Right - c.OffsetX - W : area.Left + (area.Width - W) / 2 + c.OffsetX;
            float y = bottom ? area.Bottom - c.OffsetY - H : area.Top + c.OffsetY;
            x = Math.Max(area.Left, Math.Min(x, area.Right - W));
            y = Math.Max(area.Top, Math.Min(y, area.Bottom - H));
            return new RectangleF(x, y, W, H);
        }

        static RectangleF? PlaceOne(RectangleF area, CardSpec c, List<RectangleF> placed)
        {
            if (area.Width < W - 0.5f || area.Height < H - 0.5f) return null;
            bool bottom; char hz;
            AnchorOf(c, out bottom, out hz);
            var d0 = Desired(area, c);
            float x = d0.X, y = d0.Y;

            float step = W + Gap;
            var dirs = hz == 'r' ? new[] { -1 } : hz == 'l' ? new[] { 1 } : new[] { 1, -1 };
            var r = Scan(area, x, y, bottom, placed);
            for (int k = 1; r == null && k < 64; k++)
            {
                bool any = false;
                foreach (var d in dirs)
                {
                    float cx = x + d * step * k;
                    if (cx < area.Left - 0.5f || cx + W > area.Right + 0.5f) continue;
                    any = true;
                    r = Scan(area, cx, y, bottom, placed);
                    if (r != null) break;
                }
                if (!any) break;
            }
            return r;
        }

        /// <summary>在 x 这一列从 y 开始，沿 up（往上）或往下推，找第一个不和已有卡片重叠的位置。</summary>
        static RectangleF? Scan(RectangleF area, float x, float y, bool up, List<RectangleF> placed)
        {
            for (int guard = 0; guard < 256; guard++)
            {
                if (y < area.Top - 0.5f || y + H > area.Bottom + 0.5f) return null;
                bool hit = false;
                float next = up ? float.MaxValue : float.MinValue;
                foreach (var q in placed)
                {
                    if (x < q.Right + Gap - 0.01f && x + W > q.Left - Gap + 0.01f && y < q.Bottom + Gap - 0.01f && y + H > q.Top - Gap + 0.01f)
                    {
                        hit = true;
                        next = up ? Math.Min(next, q.Top - Gap - H) : Math.Max(next, q.Bottom + Gap);
                    }
                }
                if (!hit) return new RectangleF(x, y, W, H);
                y = next;
            }
            return null;
        }
    }
}
