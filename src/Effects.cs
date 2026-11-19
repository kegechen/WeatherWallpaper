using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WeatherWallpaper
{
    /// <summary>
    /// 自定义效果：%APPDATA%\WeatherWallpaper\effects\ 里每个 .js 文件是一个效果（写法见里面的「生日示例.js」和 README）。
    /// 壁纸页面从 https://effects.local/ 加载这些脚本；文件列表（名字 + 修改时间）跟着 layout 发给页面，列表一变页面就整页重载。
    /// </summary>
    static class Effects
    {
        public const string Host = "effects.local";
        const string SampleName = "生日示例.js";
        const int MaxFiles = 50, MaxBytes = 2 << 20;
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static string Dir => Path.Combine(Settings.AppDataDir, "effects");

        /// <summary>
        /// 文件夹不在就建上，第一次建的时候放一个示例进去（示例默认不在任何一天出现，只能在托盘「效果预览 → 自定义」里看）。
        /// 用户删了示例不会再放回来；整个文件夹删了，下次启动会重新建、重新放。
        /// </summary>
        public static void EnsureDir()
        {
            try
            {
                var d = Dir;
                if (Directory.Exists(d)) return;
                Directory.CreateDirectory(d);
                var src = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", "effects", "birthday.js");
                if (File.Exists(src)) File.Copy(src, Path.Combine(d, SampleName), false);
                Log.Write("effects: created " + d);
            }
            catch (Exception ex) { Log.Write("effects dir: " + ex.Message); }
        }

        /// <summary>效果文件名（不带 .js）+ 修改时间（毫秒），按名字排好序。</summary>
        public static List<KeyValuePair<string, long>> Scan()
        {
            var list = new List<KeyValuePair<string, long>>();
            try
            {
                var d = Dir;
                if (!Directory.Exists(d)) return list;
                foreach (var f in Directory.GetFiles(d, "*.js"))
                {
                    // "*.js" 也会匹配到 8.3 短文件名碰巧是 .JS 的文件：再按真实扩展名筛一遍
                    if (!string.Equals(Path.GetExtension(f), ".js", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var fi = new FileInfo(f);
                        if ((fi.Attributes & FileAttributes.Hidden) != 0 || fi.Length > MaxBytes) continue;
                        list.Add(new KeyValuePair<string, long>(Path.GetFileNameWithoutExtension(f), (long)(fi.LastWriteTimeUtc - Epoch).TotalMilliseconds));
                    }
                    catch (Exception) { /* 这个文件正好被删 / 被占用：跳过它一个，别把整个列表丢了 */ }
                }
            }
            catch (Exception ex) { Log.Write("effects scan: " + ex.Message); }
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            if (list.Count > MaxFiles) list.RemoveRange(MaxFiles, list.Count - MaxFiles);
            return list;
        }

        public static string Json(List<KeyValuePair<string, long>> list) =>
            "[" + string.Join(",", list.Select(e => "{\"f\":" + WeatherService.JsonStr(e.Key) + ",\"v\":" + e.Value + "}")) + "]";
    }
}
