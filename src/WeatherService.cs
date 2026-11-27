using System;
using System.Collections;
using System.Collections.Generic;
using System.Device.Location;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WeatherWallpaper
{
    public class Loc
    {
        public string Name;
        public double Lat, Lon;
        public string Source;
    }

    public class GeoResult
    {
        public string Name;
        public string Detail;
        public double Lat, Lon;
        internal int Match, Level, Order;   // 排序用：名字对得上的程度、行政级别（0 国家 / 1 省市 / 2 区县 / 3 镇 …）、在原来结果里的顺序
        internal double Pop;                // 人口（只有 Open-Meteo 给），同级别时大的在前
        public override string ToString() => string.IsNullOrEmpty(Detail) ? Name : Name + "  (" + Detail + ")";
    }

    class WeatherService
    {
        // 直连优先（IP 定位必须直连，否则拿到的是代理出口的位置），失败再走系统代理
        readonly HttpClient direct = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(12) };
        readonly HttpClient proxied = new HttpClient(new HttpClientHandler { UseProxy = true }) { Timeout = TimeSpan.FromSeconds(15) };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public WeatherService()
        {
            foreach (var c in new[] { direct, proxied })
                c.DefaultRequestHeaders.UserAgent.ParseAdd("WeatherWallpaper/1.0");
        }

        async Task<string> Get(string url, bool allowProxy = true)
        {
            try { return await direct.GetStringAsync(url).ConfigureAwait(false); }
            catch (Exception ex)
            {
                if (!allowProxy) throw;
                Log.Write("direct failed, retry via proxy: " + url.Split('?')[0] + " " + ex.Message);
                return await proxied.GetStringAsync(url).ConfigureAwait(false);
            }
        }

        static Dictionary<string, object> Parse(string json) =>
            new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        static string S(Dictionary<string, object> d, string k) => d != null && d.ContainsKey(k) && d[k] != null ? Convert.ToString(d[k], Inv) : "";
        static double D(Dictionary<string, object> d, string k) => Convert.ToDouble(d[k], Inv);

        /// <summary>卡片指定了城市就直接用；否则自动定位（Windows 定位服务，失败再用 IP 定位）。</summary>
        public async Task<Loc> Locate(CardSpec c)
        {
            if (!c.IsAuto())
                // 手改文件只填了经纬度：名字用坐标（PlaceName，和卡片设置里一致），别显示成"当前位置"、和自动定位分不清
                return new Loc { Name = c.PlaceName(),
                    Lat = c.Lat.Value, Lon = c.Lon.Value, Source = "manual" };

            // 1) Windows 定位服务
            try
            {
                var pos = await Task.Run(() =>
                {
                    using (var w = new GeoCoordinateWatcher(GeoPositionAccuracy.Default))
                    {
                        if (!w.TryStart(true, TimeSpan.FromSeconds(10))) return null;
                        for (int i = 0; i < 30 && w.Position.Location.IsUnknown; i++) System.Threading.Thread.Sleep(300);
                        var c = w.Position.Location;
                        return c.IsUnknown ? null : c;
                    }
                }).ConfigureAwait(false);
                if (pos != null)
                {
                    var loc = new Loc { Lat = pos.Latitude, Lon = pos.Longitude, Source = "gps" };
                    loc.Name = await ReverseName(loc.Lat, loc.Lon).ConfigureAwait(false);
                    Log.Write($"located by windows: {loc.Lat:F4},{loc.Lon:F4} {loc.Name}");
                    return loc;
                }
            }
            catch (Exception ex) { Log.Write("windows location failed: " + ex.Message); }

            // 2) IP 定位（直连）
            try
            {
                var d = Parse(await Get("http://ip-api.com/json/?lang=zh-CN", false).ConfigureAwait(false));
                if (S(d, "status") == "success")
                {
                    var loc = new Loc { Lat = D(d, "lat"), Lon = D(d, "lon"), Source = "ip" };
                    loc.Name = await ReverseName(loc.Lat, loc.Lon).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(loc.Name)) loc.Name = S(d, "city");
                    Log.Write($"located by ip: {loc.Lat:F4},{loc.Lon:F4} {loc.Name}");
                    return loc;
                }
            }
            catch (Exception ex) { Log.Write("ip location failed: " + ex.Message); }
            return null;
        }

        async Task<string> ReverseName(double lat, double lon)
        {
            try { return await LookupName(lat, lon).ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("reverse geocode failed: " + ex.Message); return ""; }
        }

        /// <summary>按经纬度反查地名（"城市 · 区县"），查不到是 ""，联网失败抛异常。自动定位和卡片设置里的「输入经纬度」都用它。
        /// 它的中文名简繁混着给（"武汉市 · 江岸區"），和搜城市一样转成简体。</summary>
        public async Task<string> LookupName(double lat, double lon)
        {
            var d = Parse(await Get(string.Format(Inv,
                "https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={0}&longitude={1}&localityLanguage=zh", lat, lon)).ConfigureAwait(false));
            string city = ZhMap(S(d, "city"), ToSimplified), locality = ZhMap(S(d, "locality"), ToSimplified);
            if (string.IsNullOrEmpty(city)) city = ZhMap(S(d, "principalSubdivision"), ToSimplified);
            if (!string.IsNullOrEmpty(locality) && locality != city) return string.IsNullOrEmpty(city) ? locality : city + " · " + locality;
            return city;
        }

        /// <summary>
        /// 搜城市：两个免费地名库一起查、合并排序。
        ///  · Photon（OpenStreetMap 数据）：国内的区、县、镇、街道、景区都有中文名，但同名的村子很多、它自己的排序不分级别；
        ///  · Open-Meteo（GeoNames 数据）：外国城市有中文名（巴黎、伦敦），但国内很多区县搜不到（尤其带"区""市"后缀时）。
        /// 排序：名字对得上（"黄陂"也算对上"黄陂区"）→ 行政级别高（市 → 区县 → 镇 → 村）→ 人口多 → 各自原来的顺序；
        /// 同一地点（同名同地区、同名相距 30 公里内，或相距 8 公里内、名字像是同一个地方，见 SamePlaceName）只留一个。名字一律转成简体显示。
        /// 8 秒到点就不再等：一个回来了 = 不等慢的那个；都没回来 = 按超时报错（不然要等它自己的超时链，最长约 27 秒）。
        /// </summary>
        public async Task<List<GeoResult>> SearchCity(string name)
        {
            var raw = (name ?? "").Trim();
            name = ZhMap(raw, ToSimplified);
            // Photon 按用户输入的原文查：OSM 里台湾、港澳的地名是繁体（「信義區」），转成简体反而查不到；Open-Meteo 自己会简繁各查一遍
            var tasks = new[] { SearchOpenMeteo(name), SearchPhoton(raw) };
            var deadline = Task.Delay(8000);
            while (true)
            {
                var pending = tasks.Where(t => !t.IsCompleted).Cast<Task>().ToList();
                if (pending.Count == 0 || deadline.IsCompleted) break;
                pending.Add(deadline);
                await Task.WhenAny(pending).ConfigureAwait(false);
            }
            var all = new List<GeoResult>();
            Exception err = null;
            foreach (var t in tasks)
            {
                if (t.Status == TaskStatus.RanToCompletion) all.AddRange(t.Result);
                else if (t.IsFaulted) { err = err ?? t.Exception.GetBaseException(); Log.Write("city search: " + t.Exception.GetBaseException().Message); }
                else { Log.Write("city search: timed out"); _ = t.ContinueWith(x => x.Exception, TaskContinuationOptions.OnlyOnFaulted); }   // 之后失败了也算看过异常
            }
            if (all.Count == 0 && !tasks.Any(t => t.Status == TaskStatus.RanToCompletion))
                throw err ?? new Exception("搜索超时，稍后再试");   // 有一个源成功但没结果不算失败，按"没有找到"处理

            foreach (var r in all) r.Match = MatchTier(r.Name, name);
            var res = new List<GeoResult>();
            foreach (var r in all.OrderBy(r => r.Match).ThenBy(r => r.Level).ThenByDescending(r => r.Pop).ThenBy(r => r.Order))
            {
                if (res.Any(k => k.Name == r.Name && (k.Detail == r.Detail || Km(k, r) < 30) || Km(k, r) < 8 && SamePlaceName(k, r))) continue;
                res.Add(r);
                if (res.Count == 8) break;
            }
            return res;
        }

        static readonly string[] AdminSuffix = { "市", "区", "县", "镇", "乡", "街道", "地区", "林区", "新区", "自治县", "自治州", "自治区", "盟", "旗", "省" };

        /// <summary>
        /// 名字和搜索词对得上的程度：0 = 一样（或只差"市""区""县"等后缀），1 = 以它开头，2 = 其他（地名库的模糊匹配）。
        /// 搜的是拼音 / 英文、给回来的是中文（日文、韩文）名，说明是按别名对上的（"Tokyo" → 东京，"wuhan" → 武汉），也算 0。
        /// </summary>
        static int MatchTier(string n, string q)
        {
            if (string.Equals(n, q, StringComparison.OrdinalIgnoreCase)) return 0;
            if (!q.Any(IsCjk) && !n.Any(c => c < 0x250 && char.IsLetter(c))) return 0;
            foreach (var s in AdminSuffix) if (n == q + s || q == n + s) return 0;
            return n.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        }

        /// <summary>
        /// 相距很近、名字不同的两条是不是同一个地方：去掉"市""区""街道"等后缀后一个包含另一个（"武汉"/"武汉市"、"景洪市"/"景洪农场"），
        /// 或者一个是中文、一个不是（两个库给的写法不同，"巴黎"/"Paris"）——这种只认两条都是市、区县一级往上的（城市边上的公园、村子不并进城市）：
        /// 村子、山峰这类小地方两个库的写法对不齐，跨写法还同名的太多，并错了真地点就丢了，宁可贵一点留着两条。
        /// 相邻的不同区县（"汉阳区"/"汉口"）不算。
        /// </summary>
        static bool SamePlaceName(GeoResult a, GeoResult b)
        {
            if (a.Name.Any(IsCjk) != b.Name.Any(IsCjk)) return Math.Max(a.Level, b.Level) <= 2;
            string x = Stem(a.Name), y = Stem(b.Name);
            return x.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0 || y.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>去掉最长的行政后缀（剩下至少两个字才去）："景洪市" → "景洪"，"陆家嘴街道" → "陆家嘴"。</summary>
        static string Stem(string n)
        {
            var s = AdminSuffix.Where(x => n.EndsWith(x) && n.Length - x.Length >= 2).OrderByDescending(x => x.Length).FirstOrDefault();
            return s == null ? n : n.Substring(0, n.Length - s.Length);
        }

        static double Km(GeoResult a, GeoResult b)
        {
            double k = Math.PI / 180, x = (b.Lon - a.Lon) * k * Math.Cos((a.Lat + b.Lat) / 2 * k), y = (b.Lat - a.Lat) * k;
            return Math.Sqrt(x * x + y * y) * 6371;
        }

        static bool IsCjk(char c) => (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0x3040 && c <= 0x30FF);

        /// <summary>OpenStreetMap 的名字常带当地其他文字（"景洪市 ᦵᦋᧂ…""香港 Hong Kong"）：前半段是中文就只留它。繁体转成简体。</summary>
        static string CleanName(string s)
        {
            s = (s ?? "").Trim();
            int sp = s.IndexOf(' ');
            return ZhMap(sp > 0 && s.Substring(0, sp).Any(IsCjk) ? s.Substring(0, sp) : s, ToSimplified);
        }

        const uint ToSimplified = 0x02000000, ToTraditional = 0x04000000;   // LCMAP_SIMPLIFIED_CHINESE / LCMAP_TRADITIONAL_CHINESE

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int LCMapStringEx(string locale, uint flags, string src, int srcLen, [Out] char[] dest, int destLen, IntPtr ver, IntPtr reserved, IntPtr sortHandle);

        /// <summary>简繁转换（用 Windows 自带的逐字转换）；没有汉字、或转换失败就原样返回。</summary>
        static string ZhMap(string s, uint flag)
        {
            if (string.IsNullOrEmpty(s) || !s.Any(IsCjk)) return s ?? "";
            try
            {
                var buf = new char[s.Length * 2 + 2];
                int n = LCMapStringEx("zh-CN", flag, s, s.Length, buf, buf.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                return n > 0 ? new string(buf, 0, n) : s;
            }
            catch { return s; }
        }

        static string JoinDetail(string name, IEnumerable<string> parts)
        {
            var list = new List<string>();
            foreach (var p in parts.Select(CleanName)) if (p != "" && p != name && !list.Contains(p)) list.Add(p);
            return string.Join(", ", list);
        }

        /// <summary>
        /// GeoNames 的中文名简繁混杂，很多外国城市只有繁体（"纽约""伦敦""西雅图"搜不到，"紐約""倫敦""西雅圖"能搜到）：
        /// 搜索词里有能转繁体的字，就简体、繁体各查一次。
        /// 两个字的词它只认完全一样的名字，而不少大城市记的是带"市"的（"罗马市""紐約市""大阪市"）：再带上"市"各查一次。
        /// </summary>
        async Task<List<GeoResult>> SearchOpenMeteo(string name)
        {
            var names = new List<string> { name, ZhMap(name, ToTraditional) };
            if (name.Length == 2 && name.All(IsCjk)) names.AddRange(names.Select(n => n + "市").ToList());
            var list = await OpenMeteoQuery(names).ConfigureAwait(false);
            // GeoNames 里很多区县没有带后缀的写法（"武汉市""余杭区"搜不到，"武汉""余杭"能搜到）：没结果就去掉后缀再查一次
            if (list.Count == 0 && name.Length > 2 && "市区县".IndexOf(name[name.Length - 1]) >= 0)
            {
                var stem = name.Substring(0, name.Length - 1);
                list = await OpenMeteoQuery(new[] { stem, ZhMap(stem, ToTraditional) }).ConfigureAwait(false);
            }
            return list;
        }

        /// <summary>同时查几种写法（重复的只查一次），合在一起；都失败才算失败。</summary>
        async Task<List<GeoResult>> OpenMeteoQuery(IEnumerable<string> names)
        {
            const string api = "https://geocoding-api.open-meteo.com/v1/search?count=10&language=zh&format=json&name=";
            var tasks = names.Distinct().Select(n => Get(api + Uri.EscapeDataString(n))).ToList();
            var list = new List<GeoResult>();
            Exception err = null;
            bool ok = false;
            foreach (var t in tasks)
                try { ParseOpenMeteo(await t.ConfigureAwait(false), list); ok = true; }
                catch (Exception ex) { err = err ?? ex; }
            if (!ok) throw err;
            return list;
        }

        static void ParseOpenMeteo(string json, List<GeoResult> list)
        {
            var d = Parse(json);
            if (d != null && d.ContainsKey("results") && d["results"] is IEnumerable arr)
                foreach (var item in arr)
                {
                    var r = item as Dictionary<string, object>;
                    if (r == null || !r.ContainsKey("latitude") || !r.ContainsKey("longitude")) continue;
                    var code = S(r, "feature_code");
                    int level = code == "PPLC" || code.StartsWith("PCL") ? 0 : code == "PPLA" || code == "PPLA2" || code == "ADM1" ? 1 : code == "PPLA3" || code == "ADM2" || code == "ADM3" ? 2
                        : code == "PPLA4" || code.StartsWith("ADM") ? 3 : code.StartsWith("PPL") ? 5 : 4;
                    double pop = r.ContainsKey("population") && r["population"] != null ? D(r, "population") : 0;
                    // GeoNames 把不少大城市记成普通居民点（纽约是 PPL）：按人口往前提
                    level = Math.Min(level, pop >= 1000000 ? 0 : pop >= 100000 ? 1 : pop >= 10000 ? 3 : 9);
                    var n = ZhMap(S(r, "name"), ToSimplified);
                    if (string.IsNullOrWhiteSpace(n)) continue;   // 没名字的条目：空串和什么都"匹配"，会排到前面、把真结果当重复吞掉（Photon 那边同样跳过）
                    list.Add(new GeoResult { Name = n, Detail = JoinDetail(n, new[] { S(r, "admin2"), S(r, "admin1"), S(r, "country") }),
                        Lat = D(r, "latitude"), Lon = D(r, "longitude"), Level = level, Pop = pop, Order = list.Count });
                }
        }

        async Task<List<GeoResult>> SearchPhoton(string name)
        {
            // 只要行政区和居民点（外加山峰），不要小区、工厂、商店这些
            var json = await Get("https://photon.komoot.io/api/?limit=30&osm_tag=place&osm_tag=boundary:administrative&osm_tag=natural:peak&q="
                + Uri.EscapeDataString(name)).ConfigureAwait(false);
            var list = new List<GeoResult>();
            var d = Parse(json);
            if (d == null || !d.ContainsKey("features") || !(d["features"] is IEnumerable arr)) return list;
            foreach (var item in arr)
            {
                var f = item as Dictionary<string, object>;
                var p = f != null && f.ContainsKey("properties") ? f["properties"] as Dictionary<string, object> : null;
                var g = f != null && f.ContainsKey("geometry") ? f["geometry"] as Dictionary<string, object> : null;
                var xy = g != null && g.ContainsKey("coordinates") ? g["coordinates"] as IList : null;
                if (p == null || xy == null || xy.Count < 2) continue;
                string key = S(p, "osm_key"), val = S(p, "osm_value"), type = S(p, "type");
                int level;
                if (key == "place")
                    switch (val)
                    {
                        case "country": level = 0; break;
                        case "state": case "province": case "region": case "city": case "municipality": level = 1; break;
                        case "county": case "district": case "borough": level = 2; break;
                        case "town": level = 3; break;
                        case "suburb": case "island": level = 4; break;
                        case "village": level = 5; break;
                        case "hamlet": case "quarter": case "neighbourhood": case "locality": case "islet": level = 6; break;
                        default: continue;   // 广场、农场、单独的房子等
                    }
                else if (key == "boundary") level = type == "state" ? 1 : type == "county" || type == "city" ? 2 : type == "district" ? 4 : 6;
                else level = 4;   // 山峰
                var n = CleanName(S(p, "name"));
                if (n == "") continue;
                list.Add(new GeoResult { Name = n, Detail = JoinDetail(n, new[] { S(p, "county"), S(p, "city"), S(p, "state"), S(p, "country") }),
                    Lat = Convert.ToDouble(xy[1], Inv), Lon = Convert.ToDouble(xy[0], Inv), Level = level, Order = list.Count });
            }
            return list;
        }

        /// <summary>拉取天气 + 空气质量，原样打包成给前端的 JSON（解析放在 JS 里做）。key = 城市分组键，页面按它把天气分给各张卡片。</summary>
        public async Task<string> Fetch(Loc loc, string key)
        {
            string ll = string.Format(Inv, "latitude={0:F4}&longitude={1:F4}", loc.Lat, loc.Lon);
            var fcTask = Get("https://api.open-meteo.com/v1/forecast?" + ll +
                "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,rain,showers,snowfall,weather_code,cloud_cover,wind_speed_10m,wind_direction_10m" +
                "&hourly=temperature_2m,weather_code,precipitation_probability,is_day" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_probability_max" +
                "&timezone=auto&forecast_days=6");
            var aqTask = Get("https://air-quality-api.open-meteo.com/v1/air-quality?" + ll +
                "&current=us_aqi,pm2_5,pm10,ozone,nitrogen_dioxide,carbon_monoxide&timezone=auto");
            string fc = await fcTask.ConfigureAwait(false);
            string aq = null;
            try { aq = await aqTask.ConfigureAwait(false); } catch (Exception ex) { Log.Write("air quality failed: " + ex.Message); }

            var sb = new StringBuilder();
            sb.Append("{\"t\":\"weather\",\"key\":").Append(JsonStr(key)).Append(",\"loc\":{\"name\":").Append(JsonStr(loc.Name))
              .Append(",\"lat\":").Append(loc.Lat.ToString(Inv)).Append(",\"lon\":").Append(loc.Lon.ToString(Inv))
              .Append(",\"src\":").Append(JsonStr(loc.Source)).Append("},\"fc\":").Append(fc)
              .Append(",\"aq\":").Append(aq ?? "null")
              .Append(",\"ts\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Append('}');
            return sb.ToString();
        }

        // LayoutJson 等热路径每块屏、每张卡片都会调：复用一个实例（可能同时跑在几个取天气的回调里，加锁）
        // 不限长度：默认最多 2M 字符，自定义卡片联网取到的 1 MB 文字要是满是 < > & '（每个都转成 6 个字符的 Unicode 转义）会超，抛错就被当成「连不上」了
        static readonly JavaScriptSerializer JsonStrSer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static readonly object JsonStrLock = new object();
        public static string JsonStr(string s) { lock (JsonStrLock) return JsonStrSer.Serialize(s ?? ""); }
    }
}
