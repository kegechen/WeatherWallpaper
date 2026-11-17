using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace WeatherWallpaper
{
    /// <summary>
    /// 一个城市的天气动画"播一次"状态（极简模式）与雨后彩虹。
    /// 极简模式平时不画天气，只在这些时候整屏播放一小段（PlaySeconds）：拿到第一份天气、转雨、转暴雨、转雷雨、转雪、雨后转晴（彩虹）。
    /// 只在"画面城市"是这个城市的屏上播；那些屏被挡住、锁屏或人不在时先记下，等露出来、有人操作时再看是否还需要播。
    /// 彩虹在普通模式下也会出现（雨后转晴的白天挂同样长的时间）。
    /// </summary>
    class WeatherFx
    {
        public readonly string Key;
        public string Kind { get; private set; }      // 正在播的类型（null = 没在播）
        public string Pending { get; private set; }   // 等着播的类型
        public bool HasWeather => wxCat != null;
        string wxCat;   // 上一份实时天气的类别
        bool wxDay;
        double left;
        // 全部用 UTC：夏令时切换时本地时间会跳一小时，"一小时内不重复""雨停 90 分钟内"会算错
        DateTime lastWetAt = DateTime.MinValue, bowShownFor = DateTime.MinValue;
        readonly Dictionary<string, DateTime> shown = new Dictionary<string, DateTime>();

        public WeatherFx(string key) { Key = key; }

        // 解析实时天气用（OnWeather 可能同时跑在几个城市的取天气回调里：共用实例要加锁）
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static readonly object JsonLock = new object();

        static string Category(int code) =>
            code >= 95 ? "thunder" :
            (code >= 71 && code <= 77) || code == 85 || code == 86 ? "snow" :
            code == 65 || code == 67 || code == 82 ? "heavy" :
            (code >= 51 && code <= 67) || (code >= 80 && code <= 82) ? "rain" :
            code == 45 || code == 48 ? "fog" :
            code == 3 ? "cloudy" : "clear";
        static bool Wet(string c) => c == "rain" || c == "heavy" || c == "thunder";
        // 雨停后 90 分钟内转晴的白天，每场雨只挂一次
        bool RainbowDue => wxCat == "clear" && wxDay && lastWetAt > bowShownFor && (DateTime.UtcNow - lastWetAt).TotalMinutes < 90;

        /// <summary>每拿到一份实时天气调用一次：和上一份比，判断要不要播。fresh = false 表示用的是启动时的缓存（可能是很久以前的）。</summary>
        public void OnWeather(string payload, bool fresh, bool minimal)
        {
            int code; bool day;
            try
            {
                Dictionary<string, object> d;
                lock (JsonLock) d = Json.Deserialize<Dictionary<string, object>>(payload);
                var cur = (Dictionary<string, object>)((Dictionary<string, object>)d["fc"])["current"];
                code = Convert.ToInt32(cur["weather_code"]);
                day = !cur.ContainsKey("is_day") || Convert.ToInt32(cur["is_day"]) != 0;
            }
            catch (Exception ex) { Log.Write("fx[" + Key + "]: weather parse " + ex.Message); return; }
            string prev = wxCat, cat = Category(code);
            wxCat = cat; wxDay = day;
            if (Wet(cat) && fresh) lastWetAt = DateTime.UtcNow;   // 缓存里的雨可能是昨天的，别拿来判断"刚下过雨"
            string kind = null;
            if (prev == null) kind = "start";
            else if (cat != prev)
            {
                if (cat == "snow") kind = "snow";
                else if (cat == "thunder") kind = "thunder";
                else if (cat == "heavy" && prev != "thunder") kind = "heavy";
                else if (cat == "rain" && !Wet(prev)) kind = "rain";
            }
            if (!minimal) kind = null;   // 普通模式一直在播天气，只有彩虹需要额外挂一下
            if (kind == null && RainbowDue) kind = "rainbow";
            DateTime t;
            if (kind != null && kind != "start" && shown.TryGetValue(kind, out t) && (DateTime.UtcNow - t).TotalMinutes < 60) kind = null;   // 一小时内同类不重复播
            if (kind != null && kind != Pending && kind != Kind) { Pending = kind; Log.Write("fx[" + Key + "]: pending " + kind + " (" + prev + " -> " + cat + ")"); }
        }

        /// <summary>延后的动画到真要播时再确认一次：天气已经变回去了就不播。</summary>
        bool StillDue(string k)
        {
            switch (k)
            {
                case "snow": return wxCat == "snow";
                case "rain": case "heavy": case "thunder": return Wet(wxCat);
                case "rainbow": return RainbowDue;
                default: return true;
            }
        }

        /// <summary>
        /// 约 0.7 秒调一次。visible = 画面城市是本城市的屏里有没露出来的；canStart = 没锁屏且有人在操作。
        /// 倒计时只算露出来的时间；等着播的在可以开播时开播。返回 true = 正在播的类型变了，需要通知页面。
        /// </summary>
        public bool Step(double dt, bool visible, bool canStart, int seconds)
        {
            bool ended = false;
            if (Kind != null)
            {
                // 刚从外面立即播放的（托盘 / fx 命令）：dt 是距上一次 tick 的时间，开播之前那一截不算
                if (visible) left -= Math.Min(dt, Math.Max(0, (DateTime.UtcNow - playedAt).TotalSeconds));
                if (left > 0) return false;
                Log.Write("fx[" + Key + "]: end " + Kind);
                Kind = null;
                ended = true;
            }
            if (Pending != null && visible && canStart)
            {
                var k = Pending;
                Pending = null;
                if (StillDue(k)) { Play(k, seconds); return true; }   // 紧接着播下一段：直接换类型，不先淡出再淡入
                Log.Write("fx[" + Key + "]: dropped " + k + " (now " + wxCat + ")");
            }
            return ended;
        }

        /// <summary>
        /// 没有哪块屏的画面跟着这个城市了：dropStart = 这个城市确实不再显示（卡片删了 / 换了城市 / 屏上没有它的卡片），
        /// 正在播的停掉、等着的"启动"动画丢掉（它只对拿到第一份天气时就在屏上的城市有意义）；转雨、下雪、彩虹留着——
        /// 这个城市之后又成了某块屏的画面城市时再播，播之前 StillDue 会确认天气还对得上。
        /// dropStart = false = 壁纸窗口正在重建（桌面还没就绪）：什么都不动，新窗口就绪后接着播（OnWallReady 会把类型发过去），倒计时只算露出来的时间。
        /// 返回 true = 停掉了正在播的。
        /// </summary>
        public bool Detach(bool dropStart)
        {
            if (!dropStart) return false;
            if (Pending == "start") { Pending = null; Log.Write("fx[" + Key + "]: dropped start (not a scene city)"); }
            if (Kind == null) return false;
            Kind = null; left = 0;
            return true;
        }

        /// <summary>立即播放（off = 停止）。</summary>
        DateTime playedAt;   // 这一段开播的时间（UTC）
        public void Play(string kind, int seconds)
        {
            if (kind == "off") { Kind = null; left = 0; Pending = null; return; }
            Kind = kind; left = seconds; playedAt = DateTime.UtcNow;
            if (Pending != "rainbow") Pending = null;   // 正在按当前天气播了，等着的转雨 / 下雪不用再播一遍（彩虹画面不同，留着）
            shown[kind] = DateTime.UtcNow;
            if (kind == "rainbow") bowShownFor = lastWetAt;
            Log.Write("fx[" + Key + "]: play " + kind + " " + left + "s");
        }
    }
}
