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
        const int MaxBytes = 2 << 20;

        public static string Dir => Path.Combine(Settings.AppDataDir, "effects");

        /// <summary>文件夹不在就建上，放一个示例进去（示例默认不在任何一天出现，只能在托盘「效果预览 → 自定义」里看）。</summary>
        public static void EnsureDir() => ScriptDir.Ensure(Dir, "effects", new[] { @"effects\birthday.js", "生日示例.js" });

        /// <summary>效果文件名（不带 .js）+ 修改时间（毫秒），按名字排好序。</summary>
        public static List<KeyValuePair<string, long>> Scan() =>
            ScriptDir.Scripts(Dir, MaxBytes, "effects").Select(f => new KeyValuePair<string, long>(ScriptDir.Name(f), ScriptDir.Version(f))).ToList();

        public static string Json(List<KeyValuePair<string, long>> list) =>
            "[" + string.Join(",", list.Select(e => "{\"f\":" + WeatherService.JsonStr(e.Key) + ",\"v\":" + e.Value + "}")) + "]";
    }

    /// <summary>用户脚本文件夹（effects、cards）共用的：建文件夹、放示例，列出里面的脚本。</summary>
    static class ScriptDir
    {
        public const int MaxFiles = 50;
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// 文件夹不在就建上，第一次建的时候放示例进去（samples：程序 web 文件夹下的相对路径 → 放进用户文件夹时的名字）。
        /// 用户删了示例不会再放回来；整个文件夹删了，下次用到时重新建、重新放。tag = 日志里的前缀。
        /// </summary>
        public static void Ensure(string dir, string tag, params string[][] samples)
        {
            try
            {
                if (Directory.Exists(dir)) return;
                Directory.CreateDirectory(dir);
                Log.Write(tag + ": created " + dir);
                foreach (var s in samples)   // 一个一个放：哪个放不进去（被占用、杀毒拦了）不影响别的
                    try
                    {
                        var src = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", s[0]);
                        if (File.Exists(src)) File.Copy(src, Path.Combine(dir, s[1]), false);
                    }
                    catch (Exception ex) { Log.Write(tag + " sample " + s[1] + ": " + ex.Message); }
            }
            catch (Exception ex) { Log.Write(tag + " dir: " + ex.Message); }
        }

        /// <summary>文件夹里的 .js 文件（隐藏的、超过 maxBytes 的、usable 说名字用不了的不算，也不占名额），按文件名（不带 .js）排好序，最多 MaxFiles 个。</summary>
        public static List<FileInfo> Scripts(string dir, long maxBytes, string tag, Func<string, bool> usable = null)
        {
            var list = new List<FileInfo>();
            try
            {
                if (!Directory.Exists(dir)) return list;
                foreach (var f in Directory.GetFiles(dir, "*.js"))
                {
                    // "*.js" 也会匹配到 8.3 短文件名碰巧是 .JS 的文件：再按真实扩展名筛一遍
                    if (!string.Equals(Path.GetExtension(f), ".js", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var fi = new FileInfo(f);
                        if ((fi.Attributes & FileAttributes.Hidden) == 0 && fi.Length <= maxBytes && (usable == null || usable(Name(fi)))) list.Add(fi);
                    }
                    catch (Exception) { /* 这个文件正好被删 / 被占用：跳过它一个，别把整个列表丢了 */ }
                }
            }
            catch (Exception ex) { Log.Write(tag + " scan: " + ex.Message); }
            list.Sort((a, b) => string.CompareOrdinal(Name(a), Name(b)));
            if (list.Count > MaxFiles) list.RemoveRange(MaxFiles, list.Count - MaxFiles);
            return list;
        }

        /// <summary>脚本名：文件名不带 .js。</summary>
        public static string Name(FileInfo f) => Path.GetFileNameWithoutExtension(f.Name);
        /// <summary>修改时间（毫秒）：变了页面就重新加载这个脚本。</summary>
        public static long Version(FileInfo f) => (long)(f.LastWriteTimeUtc - Epoch).TotalMilliseconds;
    }
}
