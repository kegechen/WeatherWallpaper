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
    /// 卡片布局：在一块屏的可用区域（工作区，页面 CSS 像素）里摆卡片，互不重叠。
    ///  1. 按位置 + 偏移算出期望位置（超出可用区域就收进来）；
    ///  2. 按期望位置的先后摆：顶部位置的从上往下，再摆底部位置的从下往上（期望位置一样的按列表顺序）；
    ///  3. 和已经摆好的卡片重叠（留 Gap 间距）→ 沿离开锚点边的方向推：顶部位置往下、底部位置往上；
    ///  4. 推出可用区域 → 先放着，等所有放得下的卡片都在自己那一列摆好，再往屏幕内侧挪一列（右侧往左、左侧往右、居中左右交替）
    ///     找空位：先从期望的高度找，没有再从那一列的头上找（顶部位置从上往下、底部位置从下往上）；
    ///  5. 所有列都放不下 → 这张卡片放不下（返回 null）。
    /// 效果：同一位置的卡片自动排成一列；一张卡片变高（autoHeight 的自定义卡片内容变长、多显示了几项）时自己不动，
    /// 把它下面的卡片往下挤，一列装不下了是最下面的卡片挪去内侧那一列，别的列上本来的卡片也不动；整块屏满了就放不下。
    /// 新增卡片不按这个排（它不该挤动已有的卡片）：Settle 在剩下的空位里给它找位置，把偏移定在那里。
    /// </summary>
    static class CardLayout
    {
        public const float W = 336, Gap = 12;   // 卡片宽度与 style.css 里 .card 一致；高度随显示的内容变（HeightOf）
        const float Tol = 0.6f;                 // 判断重叠的容差，见 Scan

        /// <summary>
        /// 卡片高度（CSS 像素）：按显示的内容把各段的高度加起来。各段高度、间距与 style.css 里 .card 下各元素写死的 height / margin-top 一一对应，
        /// 改一处要连着改另一处（页面按宿主给的高度画卡片，两边对不上内容会被裁掉或留白）。
        /// 全显示（不含日期）446，去掉未来几天 291，只留上面部分 166。自定义卡片的高度是设置里写的（Normalize 已限制在 64–720）；
        /// 脚本里写了 autoHeight: true 的，用页面量出来的内容高度（CustomCards.Fit），还没量过才用设置里的。
        /// </summary>
        public static float HeightOf(CardSpec c)
        {
            if (c.IsCustom())
            {
                int fit;
                if (CustomCards.IsAuto(c.Script) && CustomCards.Fit.TryGetValue(c.Id, out fit)) return fit;
                return c.Height ?? CustomCards.DefaultHeight;
            }
            float h = 1 + 18 + 20;                                                 // 上边框 + 上内边距 + 城市行
            if (c.Has("date") || c.Has("week") || c.Has("lunar")) h += 3 + 17;      // 日期行
            h += 6 + 76;                                                            // 当前天气
            if (c.Has("aqi") || c.Has("feel") || c.Has("hum") || c.Has("wind")) h += 8 + 22;   // 指标标签
            if (c.Has("hours")) h += 10 + 1 + 8 + 79;                               // 分隔线 + 逐小时
            if (c.Has("days")) h += (c.Has("hours") ? 4 : 10) + 1 + 8 + 142;        // 分隔线 + 未来几天（紧跟逐小时的分隔线离得近些）
            // 底部一行。只有更新时间、又不显示定位方式时，更新时间放在城市行右边（定位方式的位置），没有底部一行（见 weather.js panelHtml）
            if (c.Has("sun") || (c.Has("upd") && c.Has("src"))) h += 12 + 15;
            return h + 14 + 1;                                                      // 下内边距 + 下边框
        }

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
        /// 只显示天气画面的卡片（Hidden）不占位置；放不下 → Rect=null 暂时隐藏，也不挪去别的屏。
        /// 隐藏（Off）的卡片不要传进来（调用方用 EffectiveCards，已经去掉了）。
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

        /// <summary>
        /// 这块屏上还能再放几张卡片（按 like 的位置和偏移往里塞，没给就按默认右上角；最多 MaxCards）。
        /// 和新增卡片（Settle）一样只往空位里塞，不挤动已有的卡片。
        /// </summary>
        public static int Free(RectangleF work, IList<CardSpec> cards, CardSpec like = null)
        {
            var probe = like != null ? like.Clone() : new CardSpec();
            probe.Id = "probe"; probe.Hidden = false; probe.Off = false;
            var area = Room(work, cards.Concat(new[] { probe }));
            var placed = Taken(area, cards);
            int n = 0;
            for (; n < Settings.MaxCards; n++)
            {
                var r = PlaceOne(area, probe, placed, true);
                if (!r.HasValue) break;
                placed.Add(r.Value);
            }
            return n;
        }

        /// <summary>
        /// 给新卡片 c 找位置：others（同一块屏上已有的卡片）照常摆好，c 从它的期望位置开始在剩下的空位里找（沿列推、满了换列），
        /// 找到了把 c 的偏移改成那里（位置不变，还是按原来的锚点算偏移）——之后按期望位置先后摆，它正好落在这个空位上、不挤动别的卡片。
        /// 放不下返回 false，c 不变。
        /// </summary>
        public static bool Settle(RectangleF work, IList<CardSpec> others, CardSpec c)
        {
            var area = Room(work, others.Concat(new[] { c }));
            var r = PlaceOne(area, c, Taken(area, others), true);
            if (!r.HasValue) return false;
            bool bottom; char hz;
            AnchorOf(c, out bottom, out hz);
            var v = r.Value;
            // 取整差不到半像素，Scan 的容差 Tol 盖得住，不会因此和挨着的卡片算重叠
            c.OffsetX = (int)Math.Round(hz == 'l' ? v.X - area.Left : hz == 'r' ? area.Right - v.Right : v.X - (area.Left + (area.Width - W) / 2));
            c.OffsetY = (int)Math.Round(bottom ? area.Bottom - v.Bottom : v.Y - area.Top);
            return true;
        }

        /// <summary>这些卡片照常摆好后占掉的位置。</summary>
        static List<RectangleF> Taken(RectangleF area, IList<CardSpec> cards) => Place(area, cards).Where(r => r.HasValue).Select(r => r.Value).ToList();

        /// <summary>
        /// 屏幕太矮（小屏开了高缩放）、可用区域比卡片还矮：按最高那张天气卡片的高度算，卡片贴着上边、下面一截被任务栏挡住（和旧版一样），不至于一张都放不下。
        /// 自定义卡片不算：它的高度是自己设的（最高 720，常见的屏都比这矮），放不下就提示调小；算进来的话区域被它拉高，同屏贴底的卡片都会压到任务栏下面。
        /// 这样区域也和自定义卡片多高无关：高的放得下、矮的一定放得下（CardFitJson 靠这个少试几次）。
        /// 同理：竖屏 / 窄屏开了高缩放比卡片还窄，贴着左边放，右边一截露不出来。
        /// </summary>
        static RectangleF Room(RectangleF area, IEnumerable<CardSpec> cards)
        {
            float tallest = cards.Where(c => !c.IsCustom()).Select(HeightOf).DefaultIfEmpty(0).Max();
            if (area.Height < tallest) area.Height = tallest;
            if (area.Width < W) area.Width = W;
            return area;
        }

        /// <summary>摆好这块屏上的卡片（见类注释），结果和 cards 一一对应；null = 放不下。</summary>
        public static RectangleF?[] Place(RectangleF area, IList<CardSpec> cards)
        {
            area = Room(area, cards);
            var res = new RectangleF?[cards.Count];
            var placed = new List<RectangleF>();
            // 按期望位置的先后：顶部位置的按上边从上往下，底部位置的排在后面、按下边从下往上。OrderBy 是稳定排序，期望位置一样的保持列表顺序
            // （同一位置、同样偏移的卡片还是按列表顺序摞成一列，和以前一样）
            var order = Enumerable.Range(0, cards.Count).Select(i =>
            {
                bool bottom; char hz;
                AnchorOf(cards[i], out bottom, out hz);
                var d = Desired(area, cards[i]);
                return new { i, bottom, key = bottom ? -d.Bottom : d.Y };
            }).OrderBy(o => o.bottom).ThenBy(o => o.key).Select(o => o.i).ToList();
            // 先都在自己那一列摆（推出可用区域的先放着），再给放不下的换列：挪走的总是被挤到最下面（底部位置是最上面）的那张，
            // 换过去也不挤动那一列本来的卡片
            var later = new List<int>();
            foreach (int i in order)
            {
                var r = PlaceOne(area, cards[i], placed, false);
                if (r.HasValue) { res[i] = r; placed.Add(r.Value); }
                else later.Add(i);
            }
            foreach (int i in later)
            {
                var r = PlaceOne(area, cards[i], placed, true);
                if (r.HasValue) { res[i] = r; placed.Add(r.Value); }
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
            float H = HeightOf(c);
            float x = hz == 'l' ? area.Left + c.OffsetX : hz == 'r' ? area.Right - c.OffsetX - W : area.Left + (area.Width - W) / 2 + c.OffsetX;
            float y = bottom ? area.Bottom - c.OffsetY - H : area.Top + c.OffsetY;
            x = Math.Max(area.Left, Math.Min(x, area.Right - W));
            y = Math.Max(area.Top, Math.Min(y, area.Bottom - H));
            return new RectangleF(x, y, W, H);
        }

        /// <summary>在已经摆好的 placed 之外给 c 找位置：先在它自己那一列沿离开锚点边的方向推；far = 这一列放不下时往内侧换列（见类注释第 4 步）。</summary>
        static RectangleF? PlaceOne(RectangleF area, CardSpec c, List<RectangleF> placed, bool far)
        {
            float H = HeightOf(c);
            if (area.Width < W - 0.5f || area.Height < H - 0.5f) return null;
            bool bottom; char hz;
            AnchorOf(c, out bottom, out hz);
            var d0 = Desired(area, c);
            float x = d0.X, y = d0.Y;
            var r = Scan(area, x, y, H, bottom, placed);
            if (r != null || !far) return r;

            // 换到的那一列：期望的高度往后没有空位（那一列本来的卡片占着），再从这一列的头上找——顶部位置从上往下、底部位置从下往上，
            // 边距按这张的偏移、最多默认边距（偏移很大的卡片挪过去也贴着头上排，不留一大截空）
            float m = Math.Min(c.OffsetY, CardSpec.DefaultOffset);
            float y2 = bottom ? Math.Max(area.Top, area.Bottom - m - H) : Math.Max(area.Top, Math.Min(area.Top + m, area.Bottom - H));
            float step = W + Gap;
            var dirs = hz == 'r' ? new[] { -1 } : hz == 'l' ? new[] { 1 } : new[] { 1, -1 };
            for (int k = 1; r == null && k < 64; k++)
            {
                bool any = false;
                foreach (var d in dirs)
                {
                    float cx = x + d * step * k;
                    if (cx < area.Left - 0.5f || cx + W > area.Right + 0.5f) continue;
                    any = true;
                    r = Scan(area, cx, y, H, bottom, placed);
                    if (r == null && Math.Abs(y2 - y) > 0.5f) r = Scan(area, cx, y2, H, bottom, placed);
                    if (r != null) break;
                }
                if (!any) break;
            }
            return r;
        }

        /// <summary>在 x 这一列从 y 开始，沿 up（往上）或往下推，找第一个不和已有卡片重叠的位置（这张卡片高 H）。</summary>
        static RectangleF? Scan(RectangleF area, float x, float y, float H, bool up, List<RectangleF> placed)
        {
            for (int guard = 0; guard < 256; guard++)
            {
                if (y < area.Top - 0.5f || y + H > area.Bottom + 0.5f) return null;
                bool hit = false;
                float next = up ? float.MaxValue : float.MinValue;
                // 容差 Tol：偏移是整数 CSS 像素，工作区却可能是小数（125% 缩放下 1392 物理像素高 = 1113.6）——
                // 按偏移算回来的位置和"正好挨着、留 Gap"差不到半像素，不能算重叠（卡片设置里吸附到挨着另一张时就是这样）
                foreach (var q in placed)
                {
                    if (x < q.Right + Gap - Tol && x + W > q.Left - Gap + Tol && y < q.Bottom + Gap - Tol && y + H > q.Top - Gap + Tol)
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
