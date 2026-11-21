using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace WeatherWallpaper
{
    /// <summary>
    /// 一张桌面天气卡片：在哪块屏、什么位置、显示哪个城市。第一张（Id = default）是默认卡片，不能删除。
    /// 天气跟着卡片走：放了卡片的屏才显示天气壁纸，画面城市由这块屏上负责「画面跟随」的卡片决定。
    /// </summary>
    public class CardSpec
    {
        public const string DefaultId = "default", PrimaryScreen = "primary";
        public const string AwayPrefix = "away:";   // Screen = away:原编号：那块显示器不在了、原编号已经是另一块屏（见 App.HealScreens），按没接上处理
        public static readonly string[] Positions = { "tl", "tc", "tr", "bl", "bc", "br" };
        public const int DefaultOffset = 28;                  // 默认离屏幕边缘的距离（卡片设置里拖动时也吸附到它）

        public string Id { get; set; }
        public string Name { get; set; }                      // 卡片名称（空 = 默认卡片 / 城市名）
        public string Screen { get; set; } = PrimaryScreen;   // primary = 始终在主屏；否则为显示器的设备名 \\.\DISPLAYn
        public string Monitor { get; set; }                   // 那块显示器的硬件 ID（自动维护）：DISPLAYn 换了号时按它找回原来的屏
        public string Pos { get; set; } = "tr";               // tl 左上 / tc 顶部居中 / tr 右上 / bl 左下 / bc 底部居中 / br 右下
        public int OffsetX { get; set; } = DefaultOffset;     // 离屏幕左右边缘的距离（居中位置为相对中线的偏移），CSS 像素
        public int OffsetY { get; set; } = DefaultOffset;     // 离屏幕上边缘（底部位置为离任务栏）的距离
        public string City { get; set; }                      // City / Lat / Lon 都为 null = 自动定位
        public double? Lat { get; set; }
        public double? Lon { get; set; }
        public bool Scene { get; set; }                       // 这块屏的全屏天气画面跟随这张卡片的城市
        public bool Hidden { get; set; }                      // 只显示天气画面：卡片不画出来，但这块屏的天气城市由它决定
        // 隐藏这张卡片（相当于暂时删掉，不用删了再加）：不显示、不占位置、不取天气、不参与天气画面，也不让它的屏显示天气壁纸。默认卡片不能隐藏
        public bool Off { get; set; }
        // 卡片上显示哪些内容（城市和当前天气总是显示），逗号分隔，见 ShowKeys；卡片高度按它算（CardLayout.HeightOf）。null = 默认（DefaultShow）
        public string Show { get; set; } = DefaultShow;
        // 自定义卡片：cards 文件夹里的脚本名（不带 .js），内容由脚本画（见 CustomCards / README「自定义卡片」）；null = 天气卡片。默认卡片总是天气卡片
        public string Script { get; set; }
        // 自定义卡片的高度（CSS 像素，64–720，宽度和天气卡片一样固定 336）；天气卡片为 null（高度按 Show 算）
        public int? Height { get; set; }

        // 可选内容，按卡片上从上到下的顺序：公历日期、星期、农历（日期行）；今日最高 / 最低、定位来源（手动 / IP 定位）；
        // 空气、体感、湿度、风（指标标签）；今天逐小时预报、未来几天预报；日出日落、更新时间（底部）。
        // 页面里各有一份同样的：web/card.html 的 SHOW_KEYS / DEFAULT_SHOW、web/weather.js 的 DEFAULT_SHOW，改了要一起改
        public static readonly string[] ShowKeys = { "date", "week", "lunar", "hilo", "src", "aqi", "feel", "hum", "wind", "hours", "days", "sun", "upd" };
        public const string DefaultShow = "hilo,src,aqi,feel,hum,wind,hours,days,sun,upd";   // 和加这个设置之前的卡片一样（没有日期行）
        /// <summary>卡片上显示不显示这一项。逐个比对、不拼字符串：排版时每张卡片要问十几次，卡片设置开着拖动时一秒要排几十遍</summary>
        public bool Has(string key)
        {
            var s = Show ?? "";
            for (int i = s.IndexOf(key, StringComparison.Ordinal); i >= 0; i = s.IndexOf(key, i + 1, StringComparison.Ordinal))
                if ((i == 0 || s[i - 1] == ',') && (i + key.Length == s.Length || s[i + key.Length] == ',')) return true;
            return false;
        }

        public bool IsDefault() => Id == DefaultId;
        /// <summary>自定义卡片：不取天气、不参与全屏天气画面（Scene / Hidden 都没有意义），只占卡片的位置。</summary>
        public bool IsCustom() => Script != null;
        /// <summary>能拿"画面跟随"（Scene）：显示着的天气卡片。隐藏的、自定义卡片不参与，这块屏的画面交给别的卡片。</summary>
        public bool CanTakeScene() => !Off && !IsCustom();
        public bool IsAuto() => !(Lat.HasValue && Lon.HasValue);
        /// <summary>手动卡片的城市名；手改文件只填了经纬度时用坐标（桌面卡片、托盘面板、卡片设置都用这一个）。</summary>
        public string PlaceName() => !string.IsNullOrEmpty(City) ? City
            : Lat.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ", " + Lon.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        /// <summary>天气数据的分组键：自动定位为 auto，指定城市按经纬度（两位小数，约 1 公里）——同一城市的卡片共用一份天气。
        /// 自定义卡片没有城市（Normalize 清掉了经纬度），这里也是 auto：用之前先把它们排除（Program.WeatherCards）。</summary>
        public string LocKey() => IsAuto() ? "auto" : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2},{1:F2}", Lat.Value, Lon.Value);
        public CardSpec Clone() => (CardSpec)MemberwiseClone();

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(Id)) Id = NewId();
            if (Id == DefaultId) Off = false;   // 默认卡片和不能删除一样，也不能隐藏（托盘面板显示的就是它的天气）
            if (Off) Scene = false;
            Name = string.IsNullOrWhiteSpace(Name) ? null : Cut(Name.Trim(), 24);
            if (string.IsNullOrWhiteSpace(Screen)) Screen = PrimaryScreen;
            if (Screen == PrimaryScreen || string.IsNullOrWhiteSpace(Monitor)) Monitor = null;
            if (!Positions.Contains(Pos)) Pos = "tr";
            OffsetX = Math.Max(Pos[1] == 'c' ? -4000 : 0, Math.Min(4000, OffsetX));   // 只有居中位置的水平偏移可以是负数（往左）
            OffsetY = Math.Max(0, Math.Min(4000, OffsetY));
            if (IsAuto() || Math.Abs(Lat.Value) > 90 || Math.Abs(Lon.Value) > 180) { Lat = Lon = null; City = null; }
            Script = Id == DefaultId ? null : CustomCards.CleanName(Script);
            if (Script != null)
            {
                // 自定义卡片没有天气：城市、画面跟随、只显示天气画面都清掉（手改文件写了也不认，免得它去取天气、抢这块屏的天气画面）
                Scene = Hidden = false;
                City = null; Lat = Lon = null;
                Height = Math.Max(CustomCards.MinHeight, Math.Min(CustomCards.MaxHeight, Height ?? CustomCards.DefaultHeight));
            }
            else Height = null;
            // 显示内容：去掉不认识的、重复的，按固定顺序排（比较改没改过、写进文件都用同一个样子）；"" = 只显示城市和当前天气
            if (Show == null) Show = DefaultShow;
            else
            {
                var want = new HashSet<string>(Show.Split(',').Select(s => s.Trim().ToLowerInvariant()));
                Show = string.Join(",", ShowKeys.Where(want.Contains));
            }
        }

        public static string NewId() => "c" + Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>截到最多 n 个字符，不把 emoji 等代理对切成半个。</summary>
        internal static string Cut(string s, int n)
        {
            if (s.Length <= n) return s;
            if (char.IsHighSurrogate(s[n - 1])) n--;
            return s.Substring(0, n);
        }
    }

    public class Settings
    {
        // 旧版本的单卡片设置：只在读取旧文件时用来迁移成默认卡片，不再写回
        public string City { get; set; }
        public double? Lat { get; set; }
        public double? Lon { get; set; }
        public string PanelPos { get; set; } = "tr";
        public int PanelMarginX { get; set; } = 28;
        public int PanelMarginY { get; set; } = 28;
        public string Monitors { get; set; }           // 旧版「显示在哪几块屏」：现在由卡片放在哪块屏决定，只在迁移时用（MigrateMonitors）
        static readonly HashSet<string> Legacy = new HashSet<string> { "City", "Lat", "Lon", "PanelPos", "PanelMarginX", "PanelMarginY", "Monitors" };

        public List<CardSpec> Cards { get; set; }      // 桌面卡片；第一张是默认卡片
        public int Fps { get; set; } = 30;
        public bool TaskbarFx { get; set; } = true;
        public bool ShowPanel { get; set; } = true;    // 所有卡片的总开关
        public bool WindowFx { get; set; } = true;     // 窗口顶边"屋檐"雨滴 / 积雪
        public bool Festive { get; set; } = true;      // 节日装饰与彩蛋（灯笼、烟花、圣诞树、雪人、流星…）
        public string Layered { get; set; } = "auto"; // auto / on / off：24H2 桌面结构下是否给壁纸窗口加 WS_EX_LAYERED
        public bool DevTools { get; set; }
        public bool LowMemory { get; set; }            // 省内存模式：背景半分辨率 + 渐变缓存 + 颜色量化（默认关，画面略软）
        public bool Minimal { get; set; }              // 极简模式：平时只显示系统壁纸 + 天气卡片，天气有明显变化时才播放一小段天气动画
        public bool OverlayWall { get; set; }          // 壁纸叠加：系统壁纸常驻垫底，天空那层半透明，云、日月、雨雪浮在壁纸上（和极简模式互斥）
        // 天空那层的不透明度（壁纸叠加时）：和 PlaySeconds 一样声明成 object，手写成字符串 / 小数 / 巨大值也不会让整个文件读失败
        public object SkyOpacity { get; set; } = DefaultSkyOpacity;    // 壁纸叠加时天空那层的不透明度（0.05–1，默认 0.45）
        public object CloudAlpha { get; set; } = DefaultCloudAlpha;    // 壁纸叠加时云的不透明度（0.1–1，默认 0.72）
        // 每次播放天气动画的秒数（极简模式播放、雨后彩虹、效果预览共用）。声明成 object：用户手写成字符串、小数或超出 int 的大数时，按 int 反序列化会让整个文件读取失败；
        // 这里什么值都先收下，加载后由 NormalizePlaySeconds 校验，不合法就回到默认值
        public object PlaySeconds { get; set; } = DefaultPlaySeconds;
        public bool AutoUpdate { get; set; } = true;   // 启动时及之后每 12 小时自动检查更新
        public string SkipVersion { get; set; }        // 用户选择"跳过此版本"的版本号

        public static string AppDataDir => Dir(Environment.SpecialFolder.ApplicationData);
        public static string LocalDir => Dir(Environment.SpecialFolder.LocalApplicationData);
        static string Dir(Environment.SpecialFolder f)
        {
            var d = Path.Combine(Environment.GetFolderPath(f), "WeatherWallpaper");
            Directory.CreateDirectory(d);
            return d;
        }
        public static string FilePath => Path.Combine(AppDataDir, "settings.json");

        static PropertyInfo[] props;
        static PropertyInfo[] Props => props ?? (props = typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => !Legacy.Contains(p.Name)).ToArray());

        public const int DefaultPlaySeconds = 30, MinPlaySeconds = 5, MaxPlaySeconds = 3600, MaxCards = 8;
        // 壁纸叠加时天空那层的不透明度：0.45 ≈ 天气看得清、壁纸也透得出来；1 = 和普通模式一样完全遮住
        public const double DefaultSkyOpacity = 0.45, MinSkyOpacity = 0.05, MaxSkyOpacity = 1;
        public const double DefaultCloudAlpha = 0.72, MinCloudAlpha = 0.1, MaxCloudAlpha = 1;

        public CardSpec DefaultCard() => Cards[0];

        bool migrated;   // 这次是从旧版设置迁移来的（文件里还没有 Cards）：启动时写回一次，以后就不再迁移
        bool dirty;      // 读取时补了卡片 Id、去掉了超出上限的卡片：要写回文件
        /// <summary>读进来后改过（迁移、补了 Id、卡片太多），要写回文件。</summary>
        public bool NeedsSave() => migrated || dirty;

        /// <summary>
        /// 最近一次和内存里一致的 settings.json 内容（自己写进去的，或者读进来的）：文件监视看到的就是它 = 自己保存触发的，不用重新加载；
        /// 文件和它不一样 = 被外部改过、还没重新加载。
        /// </summary>
        public static string LastWritten { get; set; }

        /// <summary>保证卡片列表合法：第一张是默认卡片（没有时由旧版的城市 / 卡片位置设置迁移而来），Id 不重复，最多 MaxCards 张。</summary>
        public void NormalizeCards()
        {
            var list = (Cards ?? new List<CardSpec>()).Where(c => c != null).ToList();
            var def = list.FirstOrDefault(c => c.Id == CardSpec.DefaultId);
            if (def == null)
            {
                def = new CardSpec
                {
                    // 旧版的顶部居中按屏幕正中摆、不看 PanelMarginX；新版居中位置的 OffsetX 是离中线的偏移
                    Id = CardSpec.DefaultId, Screen = CardSpec.PrimaryScreen, Pos = PanelPos, OffsetX = PanelPos == "tc" ? 0 : PanelMarginX, OffsetY = PanelMarginY,
                    City = City, Lat = Lat, Lon = Lon,
                };
                dirty = true;   // 文件里没有默认卡片（手删了）：补出来的要写回，不然每轮加载都重新生成、文件里第一张一直不是它
            }
            list.Remove(def);
            list.Insert(0, def);
            var ids = new HashSet<string>();
            foreach (var c in list)
            {
                if (string.IsNullOrWhiteSpace(c.Id)) dirty = true;   // 手加的卡片没写 Id：补上的 Id 要写回文件，不然每次重新加载都换一个
                c.Normalize();
                while (!ids.Add(c.Id)) { c.Id = CardSpec.NewId(); dirty = true; }
            }
            if (list.Count > MaxCards)
            {
                // 手动在文件里加多了：多出来的不用，写回文件（不然内存和文件不一致，之后哪次保存时才悄悄删掉）
                Log.Write("settings: " + list.Count + " cards, only the first " + MaxCards + " are kept, dropped: " + string.Join(", ", list.Skip(MaxCards).Select(c => c.Id)));
                list = list.Take(MaxCards).ToList();
                dirty = true;
            }
            Cards = list;
            NormalizeScenes(Cards);
        }

        /// <summary>
        /// 旧版的「显示在」（Monitors）迁移成卡片。旧版只有一张卡片、在一块屏上，其余显示天气壁纸的屏只有天气画面：
        /// all（旧版默认）→ 其他每块屏各加一张同城市的隐藏卡片（只提供天气画面，和原来看到的一样）；
        /// 指定某块屏 → 默认卡片放到那块屏上（那块屏现在没接上就留在主屏，和旧版"退回主屏"一致）；primary 不用动。
        /// </summary>
        void MigrateMonitors()
        {
            migrated = true;
            var m = Monitors == "" ? "all" : Monitors;
            var def = Cards[0];
            var all = System.Windows.Forms.Screen.AllScreens;
            if (m == "all")
            {
                foreach (var s in all.Where(x => !x.Primary))
                {
                    if (Cards.Count >= MaxCards) break;
                    var c = new CardSpec { Screen = s.DeviceName, Monitor = CardLayout.MonitorId(s), Hidden = true, City = def.City, Lat = def.Lat, Lon = def.Lon };
                    c.Normalize();
                    Cards.Add(c);
                }
            }
            else if (m != CardSpec.PrimaryScreen && all.Any(x => x.DeviceName == m && !x.Primary)) { def.Screen = m; def.Monitor = CardLayout.MonitorOf(m); }
            // 旧版在菜单里关掉了「显示天气卡片」：新版没有这个菜单了，换成只显示天气画面的卡片（Hidden，天气画面照旧），想要哪张卡片时在卡片设置里取消勾选「只显示天气画面」
            if (!ShowPanel)
            {
                foreach (var c in Cards) c.Hidden = true;
                ShowPanel = true;
            }
            NormalizeScenes(Cards);
        }

        /// <summary>
        /// 每块屏恰好有一张卡片负责"全屏天气画面跟随"（Scene = true）：没有的补上这块屏的第一张，有多张的只留一张。
        /// 只显示天气画面的卡片（Hidden）就是用来提供天气画面的：同屏有它时总是它。否则 prefer = 刚被用户勾选的那张（同一块屏上有多张标记时留它）。
        /// 隐藏（Off）的卡片、自定义卡片不参与：不拿画面跟随，这块屏的画面交给其他卡片（只有自定义卡片的屏用默认卡片的城市，见 App.SceneKeyOf）。
        /// </summary>
        public static void NormalizeScenes(IList<CardSpec> cards, string prefer = null)
        {
            foreach (var c in cards) if (!c.CanTakeScene()) c.Scene = false;
            foreach (var g in cards.Where(c => c.CanTakeScene()).GroupBy(CardLayout.GroupKey))
            {
                var list = g.ToList();
                var pick = list.FirstOrDefault(c => c.Hidden) ?? list.FirstOrDefault(c => c.Id == prefer && c.Scene) ?? list.FirstOrDefault(c => c.Scene) ?? list[0];
                foreach (var c in list) c.Scene = c == pick;
            }
        }

        /// <summary>播放秒数（已校验，总在 5–3600 之间）。</summary>
        public int PlaySecondsValue() => PlaySeconds is int n ? n : DefaultPlaySeconds;

        /// <summary>天空那层的不透明度（已校验，总在 0.05–1 之间）。JavaScriptSerializer 读回来的数可能是 int / long / decimal / double 中的任意一种。</summary>
        public double SkyOpacityValue() => OpacityValue(SkyOpacity, DefaultSkyOpacity, MinSkyOpacity, MaxSkyOpacity);

        /// <summary>云的不透明度（已校验，总在 0.1–1 之间）。</summary>
        public double CloudAlphaValue() => OpacityValue(CloudAlpha, DefaultCloudAlpha, MinCloudAlpha, MaxCloudAlpha);

        /// <summary>把 object 里的数读成范围内的 double；接受数字和数字字符串（命令行调参 / 手写文件），不是数、NaN 都用默认值。</summary>
        static double OpacityValue(object o, double def, double min, double max)
        {
            double v;
            switch (o)
            {
                case double d: v = d; break;
                case decimal m: v = (double)m; break;
                case int i: v = i; break;
                case long l: v = l; break;
                case string t:
                    if (!double.TryParse(t.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return def;
                    break;
                default: return def;
            }
            return double.IsNaN(v) ? def : Math.Max(min, Math.Min(max, v));
        }

        /// <summary>校验两档不透明度：接受数字和数字字符串，不是数、超出范围都回到默认值再夹回去（所有加载路径的统一入口）。</summary>
        public void NormalizeOpacities()
        {
            SkyOpacity = OpacityValue(SkyOpacity, DefaultSkyOpacity, MinSkyOpacity, MaxSkyOpacity);
            CloudAlpha = OpacityValue(CloudAlpha, DefaultCloudAlpha, MinCloudAlpha, MaxCloudAlpha);
        }

        /// <summary>
        /// 把 PlaySeconds 规整成合法的 int：接受整数、小数（四舍五入）和数字字符串；
        /// null、布尔、非数字、超出 5–3600（含超出 int / long 范围的大数）一律回到默认 30。
        /// 先在 decimal / double 上比较范围再转 int，不会溢出。
        /// </summary>
        void NormalizePlaySeconds()
        {
            double v;
            var o = PlaySeconds;
            if (o is int || o is long || o is decimal || o is double) v = Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture);
            else if (!(o is string s) || !double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) v = double.NaN;
            PlaySeconds = !double.IsNaN(v) && v >= MinPlaySeconds && v <= MaxPlaySeconds ? (int)Math.Round(v) : DefaultPlaySeconds;
        }

        static readonly System.Text.RegularExpressions.Regex BoundedNums =
            new System.Text.RegularExpressions.Regex(@"(""(?:PlaySeconds|SkyOpacity|CloudAlpha)""\s*:\s*)(-?[0-9][0-9.eE+-]*)");

        public static Settings Load()
        {
            // 只读一次文件：读两遍的间隙文件变了的话，解析进内存的和 LastWritten 记下的对不上，外部修改检测从此错位
            string text = null;
            try { if (File.Exists(FilePath)) text = File.ReadAllText(FilePath); } catch { }
            var s = TryLoad(text, out var used);   // 外层读失败（编辑器还占着文件）时，used 是 TryLoad 自己重读并解析的那份
            // 读不进来：按默认值运行。LastWritten 留空——之后写盘时 Save 会认出文件是坏的、先备份再写；用户修好了则按外部修改重新加载
            if (s == null) { s = new Settings(); s.NormalizeCards(); return s; }
            LastWritten = used;   // 内存和这份文件内容一致（Save 的"比较再写"以它为准，所以要在下面的 Save 之前记）
            if (s.NeedsSave())
            {
                bool saved = s.Save();
                if (s.migrated) Log.Write("settings migrated" + (saved ? "" : " (not saved yet)") + ": Monitors=" + (s.Monitors ?? "(none)") + " -> " + string.Join(" | ", s.Cards.Select(c => c.Id + " " + c.Screen + (c.Hidden ? " hidden" : ""))));
            }
            return s;
        }

        // 有范围约束的数字字段（PlaySeconds / SkyOpacity / CloudAlpha）里超出 double 范围的数字（如 1e400）连 JSON 解析都过不去，会让整个文件读取失败：先换成 null，按默认值处理
        static string FixNumbers(string text) => BoundedNums.Replace(text, m => double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _) ? m.Value : m.Groups[1].Value + "null");

        /// <summary>这份内容能不能读成设置（和 TryLoad 同一套解析，但不做整理、迁移，不记日志）。</summary>
        static bool Parses(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;   // 空文件：反序列化成 null 不报错，但不是一份设置（多半是写到一半）
            try { new JavaScriptSerializer().Deserialize<Settings>(FixNumbers(text)); return true; }
            catch { return false; }
        }

        /// <summary>Save 发现文件被外部改过（还没重新加载）、没有写盘：宿主应马上重新加载，以文件为准。</summary>
        public static event Action ExternalChange;

        /// <summary>读取失败（如手动编辑写错了 JSON）返回 null；文件不存在返回默认值。text != null 时直接解析它（调用方刚读过文件）。
        /// parsed = 实际解析的那份文件内容（null = 文件不存在），LastWritten 记它才和内存对得上。</summary>
        public static Settings TryLoad(string text, out string parsed)
        {
            parsed = text;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Settings s;
                    bool legacy = false;   // 旧版设置文件（还没有 Cards）：迁移「显示在」
                    if (text == null && File.Exists(FilePath)) parsed = text = File.ReadAllText(FilePath);
                    if (text == null)
                    {
                        s = new Settings();
                        // 没有设置文件：旧版一直用默认设置（从不写盘，默认「所有显示器」）的老用户也是这样。
                        // 旧版留下的天气缓存 cache.json 还在（Init 里迁移完才删）= 是升级上来的，按旧默认值迁移
                        if (File.Exists(Path.Combine(LocalDir, "cache.json"))) { s.Monitors = "all"; legacy = true; }
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("file is empty");   // 别当成默认值读进来、盖掉内存里的设置
                        s = new JavaScriptSerializer().Deserialize<Settings>(FixNumbers(text)) ?? new Settings();
                        legacy = s.Cards == null && s.Monitors != null;   // 旧版每次保存都会写 Monitors；新版不写。手动删掉 Cards 的新文件不算旧版
                    }
                    s.NormalizePlaySeconds();
                    s.NormalizeOpacities();
                    if (s.OverlayWall && s.Minimal) s.Minimal = false;   // 手改文件把互斥的两项都写成 true：按「打开壁纸叠加会关掉极简模式」处理（所有加载路径都从这走，托盘 / 命令行入口各自也照应）
                    s.NormalizeCards();
                    if (legacy) s.MigrateMonitors();
                    return s;
                }
                catch (IOException) { text = null; parsed = null; System.Threading.Thread.Sleep(100); }   // 编辑器还在写
                catch (Exception ex) { Log.Write("settings load: " + ex.Message); return null; }
            }
            return null;
        }

        /// <summary>写进 settings.json 的字段说明（JSON 不支持注释，用 "// 字段名" 键代替，读取时会被忽略）。</summary>
        static Dictionary<string, string> Help() => new Dictionary<string, string>
        {
            ["Cards"] = "桌面天气卡片（建议用托盘菜单「卡片设置」修改），第一张是默认卡片、不能删除，最多 " + MaxCards + " 张。放了卡片的屏才显示天气壁纸。每张：" +
                "Screen 所在屏（primary = 主屏，或显示器设备名；away: 开头 = 原来那块显示器没接上）；Monitor 显示器硬件 ID（自动维护，不用改）；Pos 位置：tl 左上 / tc 顶部居中 / tr 右上 / bl 左下 / bc 底部居中 / br 右下；" +
                "OffsetX / OffsetY 离屏幕边缘的距离，单位像素（按 100% 缩放计）；City / Lat / Lon 都为 null = 自动定位；" +
                "Name 卡片名称；Scene = true 表示这块屏的全屏天气画面跟随这张卡片的城市（每块屏恰好一张，没有会自动补上）；" +
                "Hidden = true 不画卡片、只显示天气画面（这块屏的天气城市由它决定）；" +
                "Off = true 隐藏这张卡片（默认卡片除外）：不显示、不占位置、不更新天气，也不决定天气画面，相当于暂时删掉，改回 false 即恢复；" +
                "Show 卡片上显示的内容（城市和当前天气总是显示，卡片高度随之变化），逗号分隔：date 公历日期、week 星期、lunar 农历、hilo 今日最高 / 最低、src 定位来源、" +
                "aqi 空气、feel 体感、hum 湿度、wind 风、hours 今天逐小时、days 未来几天、sun 日出日落、upd 更新时间（\"\" = 只显示城市和当前天气）；" +
                "Script 自定义卡片的脚本名（cards 文件夹里的 .js 文件，不带扩展名；null = 天气卡片，默认卡片总是天气卡片），自定义卡片不取天气、不决定天气画面，City / Scene / Hidden / Show 对它无效；" +
                "Height 自定义卡片的高度，" + CustomCards.MinHeight + "–" + CustomCards.MaxHeight + " 像素（天气卡片为 null）",
            ["Fps"] = "帧率：30 或 60",
            ["TaskbarFx"] = "任务栏雨雪效果：true 开 / false 关",
            ["ShowPanel"] = "桌面天气卡片总开关：true 显示 / false 全部隐藏（隐藏后可左键点托盘图标查看）",
            ["WindowFx"] = "窗口顶边雨滴 / 积雪（屋檐）：true / false",
            ["Festive"] = "节日装饰与彩蛋：true 开 / false 关。按天气所在城市的当地日期，春节、元宵、端午、中秋、国庆、万圣节、圣诞节、复活节挂上应景的装饰" +
                "（灯笼、粽子、月饼、国旗、南瓜灯、圣诞树、彩蛋兔子），春节、元宵、国庆、跨年的夜里放烟花；平时下雪下久了地上会堆起雪人，晴朗的夜里偶尔划过一颗流星",
            ["Layered"] = "兼容选项，一般不用改：auto / on / off",
            ["DevTools"] = "开发调试用：true 允许打开 WebView2 开发者工具",
            ["LowMemory"] = "省内存模式：true 开 / false 关。开启后约少 30–50 MB，代价是背景画面略软、渐变可能有极细微色带，托盘面板每次打开要等约 1 秒",
            ["Minimal"] = "极简模式：true 开 / false 关。开启后平时只显示你原来的系统壁纸和天气卡片，几乎不占 CPU / GPU；" +
                "启动时、转雨 / 下雪 / 雷雨 / 暴雨、雨后转晴（彩虹）时播放一小段天气动画（桌面被挡住或人不在时等你回来再播）",
            ["OverlayWall"] = "壁纸叠加：true 开 / false 关。开启后保留你原来的系统壁纸当底，天空那层半透明，云、太阳、月亮、雨雪浮在壁纸上，" +
                "能在看到壁纸的同时看到天气效果。和极简模式互斥（开启壁纸叠加会关掉极简模式）",
            ["SkyOpacity"] = "壁纸叠加时天空那层的不透明度：" + MinSkyOpacity + "–" + MaxSkyOpacity + " 的小数，如 " + DefaultSkyOpacity +
                "（越小壁纸越清楚、天气越淡；1 = 和普通模式一样完全遮住）",
            ["CloudAlpha"] = "壁纸叠加时云的不透明度：" + MinCloudAlpha + "–" + MaxCloudAlpha + " 的小数，如 " + DefaultCloudAlpha +
                "（越小云越透，壁纸从云后面露出来的越多）",
            ["PlaySeconds"] = "每次播放天气动画的时长（极简模式的天气变化播放、雨后彩虹、托盘「效果预览」都用它），单位秒，" + MinPlaySeconds + "–" + MaxPlaySeconds + " 的整数，如 30；写错按默认 " + DefaultPlaySeconds + " 秒",
            ["AutoUpdate"] = "自动检查更新：true / false",
            ["SkipVersion"] = "已选择跳过的版本号，null = 不跳过",
        };

        /// <summary>
        /// 每项一行写出，前面附一行说明，方便用户手动编辑。所有写盘都走这里，"比较再写"：
        ///  · 文件在上次读写之后被外部改过（记事本刚保存、重新加载还排在去抖里）：不写——以文件为准，发 ExternalChange 让宿主马上重新加载
        ///   （这期间内存里的改动让位给文件；卡片设置窗口会换成文件里的样子）；
        ///  · 文件被手改坏了（读不进来）：先备份成 settings.json.bad 再写，备份不成就不写（读到别人写到一半的半截内容也按这条处理，内容在 .bad 里）；
        ///  · 读不出来（正被别的程序占着）：照写，写不进去记日志。
        /// keepBroken = 文件是坏的就不写（用记事本打开前：原样留给用户修）。返回 true = 写进去了。
        /// </summary>
        public bool Save(bool keepBroken = false)
        {
            string text;
            try
            {
                var js = new JavaScriptSerializer();
                var help = Help();
                var lines = Props.Select(p =>
                    (help.ContainsKey(p.Name) ? "  " + js.Serialize("// " + p.Name) + ": " + js.Serialize(help[p.Name]) + ",\r\n" : "") +
                    "  \"" + p.Name + "\": " + (p.Name == "Cards"
                        ? "[\r\n" + string.Join(",\r\n", Cards.Select(c => "    " + js.Serialize(c))) + "\r\n  ]"   // 每张卡片一行
                        : js.Serialize(p.GetValue(this))));
                // 按写盘的编码往返一次：孤立的代理字符（粘贴进来的异常文本）写下去会变成 U+FFFD，
                // LastWritten 要记磁盘上真实的样子，不然读回来永远对不上、被当成外部修改
                var utf8 = new System.Text.UTF8Encoding(false);
                text = utf8.GetString(utf8.GetBytes("{\r\n" + string.Join(",\r\n", lines) + "\r\n}\r\n"));
            }
            catch (Exception ex) { Log.Write("settings save: " + ex.Message); return false; }
            string old = null;
            try { if (File.Exists(FilePath)) old = File.ReadAllText(FilePath); } catch { }
            if (old == text) { LastWritten = text; return true; }   // 文件已经是这个样子：不用写（也不惊动文件监视）
            if (old != null && old != LastWritten)
            {
                if (Parses(old))
                {
                    Log.Write("settings save: file changed outside, not overwriting (reloading it)");
                    ExternalChange?.Invoke();
                    return false;
                }
                if (keepBroken) return false;
                try { File.Copy(FilePath, FilePath + ".bad", true); Log.Write("settings: file invalid, backed up to settings.json.bad before overwriting"); }
                catch (Exception ex) { Log.Write("settings: file invalid and backup failed, not overwriting: " + ex.Message); return false; }
            }
            // 先写临时文件再替换：写到一半崩了 / 断电，settings.json 还是完整的旧内容
            string tmp = FilePath + ".tmp";
            try
            {
                File.WriteAllText(tmp, text);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null, true); else File.Move(tmp, FilePath);
                LastWritten = text;
                return true;
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                // 替换不了（如文件系统不支持）：退回直接写
                try { File.WriteAllText(FilePath, text); LastWritten = text; return true; }
                catch (Exception ex2) { Log.Write("settings save: " + ex.Message + " / " + ex2.Message); return false; }
            }
        }

        // 按序列化结果比较：Cards 是列表，引用比较永远不等
        public bool Same(Settings o)
        {
            var js = new JavaScriptSerializer();
            return Props.All(p => js.Serialize(p.GetValue(this)) == js.Serialize(p.GetValue(o)));
        }
        public void CopyFrom(Settings o) { foreach (var p in Props) p.SetValue(this, p.GetValue(o)); }
    }

    static class Log
    {
        static readonly object Lock = new object();
        public static void Write(string s)
        {
            try
            {
                lock (Lock)
                {
                    var p = Path.Combine(Settings.LocalDir, "log.txt");
                    if (File.Exists(p) && new FileInfo(p).Length > 512 * 1024) File.Delete(p);
                    File.AppendAllText(p, DateTime.Now.ToString("MM-dd HH:mm:ss ") + s + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
