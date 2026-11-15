using System;
using System.Collections;
using System.Collections.Generic;
using System.Device.Location;
using System.Globalization;
using System.Net.Http;
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

        public async Task<Loc> Locate(Settings s)
        {
            if (s.Lat.HasValue && s.Lon.HasValue)
                return new Loc { Name = s.City ?? "", Lat = s.Lat.Value, Lon = s.Lon.Value, Source = "manual" };

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
            try
            {
                var d = Parse(await Get(string.Format(Inv,
                    "https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={0}&longitude={1}&localityLanguage=zh", lat, lon)).ConfigureAwait(false));
                string city = S(d, "city"), locality = S(d, "locality");
                if (string.IsNullOrEmpty(city)) city = S(d, "principalSubdivision");
                if (!string.IsNullOrEmpty(locality) && locality != city) return city + " · " + locality;
                return city;
            }
            catch (Exception ex) { Log.Write("reverse geocode failed: " + ex.Message); return ""; }
        }

        public async Task<List<GeoResult>> SearchCity(string name)
        {
            var list = new List<GeoResult>();
            var d = Parse(await Get("https://geocoding-api.open-meteo.com/v1/search?count=8&language=zh&format=json&name="
                + Uri.EscapeDataString(name)).ConfigureAwait(false));
            if (d.ContainsKey("results") && d["results"] is IEnumerable arr)
                foreach (var item in arr)
                {
                    var r = item as Dictionary<string, object>;
                    if (r == null) continue;
                    var parts = new List<string>();
                    foreach (var k in new[] { "admin2", "admin1", "country" })
                    {
                        var v = S(r, k);
                        if (v != "" && !parts.Contains(v)) parts.Add(v);
                    }
                    list.Add(new GeoResult { Name = S(r, "name"), Detail = string.Join(", ", parts), Lat = D(r, "latitude"), Lon = D(r, "longitude") });
                }
            return list;
        }

        /// <summary>拉取天气 + 空气质量，原样打包成给前端的 JSON（解析放在 JS 里做）。</summary>
        public async Task<string> Fetch(Loc loc)
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
            sb.Append("{\"t\":\"weather\",\"loc\":{\"name\":").Append(JsonStr(loc.Name))
              .Append(",\"lat\":").Append(loc.Lat.ToString(Inv)).Append(",\"lon\":").Append(loc.Lon.ToString(Inv))
              .Append(",\"src\":").Append(JsonStr(loc.Source)).Append("},\"fc\":").Append(fc)
              .Append(",\"aq\":").Append(aq ?? "null")
              .Append(",\"ts\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Append('}');
            return sb.ToString();
        }

        public static string JsonStr(string s) => new JavaScriptSerializer().Serialize(s ?? "");
    }
}
