using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

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

        /// <summary>文件夹不在就建上，放一个示例进去（示例默认不在任何一天出现，只能在托盘「效果预览 → 自定义」里看）；没改过的示例换成新版。
        /// 后面的指纹是 0.9.4 及以前发过的各版（见 ScriptDir.Ensure），以后改示例不用往这里加。</summary>
        public static void EnsureDir() => ScriptDir.Ensure(Dir, "effects", new[] { @"effects\birthday.js", "生日示例.js", "acbfafc37de3b11b", "3d6d22186f17e533" });

        /// <summary>效果文件名（不带 .js）+ 修改时间（毫秒），按名字排好序。</summary>
        public static List<KeyValuePair<string, long>> Scan() =>
            ScriptDir.Scripts(Dir, MaxBytes, "effects").Select(f => new KeyValuePair<string, long>(ScriptDir.Name(f), ScriptDir.Version(f))).ToList();

        public static string Json(List<KeyValuePair<string, long>> list) =>
            "[" + string.Join(",", list.Select(e => "{\"f\":" + WeatherService.JsonStr(e.Key) + ",\"v\":" + e.Value + "}")) + "]";
    }

    /// <summary>用户脚本文件夹（effects、cards）共用的：建文件夹、放示例、把没改过的示例换成新版，列出里面的脚本。</summary>
    static class ScriptDir
    {
        public const int MaxFiles = 50;
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static readonly HashSet<string> refreshed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 这次运行已经查过示例要不要换新的文件夹

        /// <summary>
        /// 文件夹不在就建上，放示例进去；文件夹在的话（每次运行第一次调用时）把用户没改过的示例换成这一版程序带的（Refresh）。
        /// samples 每项：[0] 程序 web 文件夹下的相对路径，[1] 放进用户文件夹时的名字，[2..] 开始记 samples.txt 之前发过的各版的指纹（Hash）。
        /// 用户删了示例不会再放回来；整个文件夹删了，下次用到时重新建、重新放。tag = 日志里的前缀，也是 samples.txt 里的文件夹名。只在界面线程上调。
        /// </summary>
        public static void Ensure(string dir, string tag, params string[][] samples)
        {
            try
            {
                if (Directory.Exists(dir)) { if (refreshed.Add(dir)) Refresh(dir, tag, samples); return; }
                Directory.CreateDirectory(dir);
                refreshed.Add(dir);
                Log.Write(tag + ": created " + dir);
                foreach (var s in samples)   // 一个一个放：哪个放不进去（被占用、杀毒拦了）不影响别的
                    try
                    {
                        var src = SamplePath(s[0]);
                        if (!File.Exists(src)) continue;
                        File.Copy(src, Path.Combine(dir, s[1]), false);
                        Placed(tag, s[1], Hash(src));
                    }
                    catch (Exception ex) { Log.Write(tag + " sample " + s[1] + ": " + ex.Message); }
                SaveRecord();
            }
            catch (Exception ex) { Log.Write(tag + " dir: " + ex.Message); }
        }

        static string SamplePath(string rel) => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", rel);

        /// <summary>
        /// 示例跟着程序升级：示例只在建文件夹时放一次，程序更新了里面的示例（比如每日一句加了 autoHeight），用户文件夹里的还是老的。
        /// 用户文件夹里的示例和以前放进去的某一版一模一样（没改过），就换成程序带的这一版；改过的、删掉的不动。
        /// 「以前放进去的」认两样：samples.txt 里记的（放示例、换示例都记下来），和 samples 里写死的、开始记之前发过的各版（以后改示例不用往那里加）。
        /// </summary>
        static void Refresh(string dir, string tag, string[][] samples)
        {
            foreach (var s in samples)
                try
                {
                    string src = SamplePath(s[0]), dst = Path.Combine(dir, s[1]), rec;
                    if (!File.Exists(src) || !File.Exists(dst)) continue;
                    string now = Hash(src), have = Hash(dst);
                    if (have != now)
                    {
                        if (!(Record.TryGetValue(tag + "\\" + s[1], out rec) && rec == have) && Array.IndexOf(s, have, 2) < 0) continue;   // 用户改过
                        File.Copy(src, dst, true);
                        File.SetLastWriteTimeUtc(dst, DateTime.UtcNow);   // File.Copy 会带上源文件的修改时间：换成现在，页面靠修改时间认出脚本变了、重新加载
                        Log.Write(tag + ": updated sample " + s[1]);
                    }
                    Placed(tag, s[1], now);   // 已经是这一版的也记下来（开始记之前放进去的）：以后再改示例认得出它
                }
                catch (Exception ex) { Log.Write(tag + " sample " + s[1] + ": " + ex.Message); }
            SaveRecord();
        }

        /// <summary>示例内容的指纹：去掉 BOM 和所有 \r（安装包里是 CRLF、仓库里是 LF，都算同一版），UTF-8 的 SHA-256 取前 16 个十六进制字符。</summary>
        static string Hash(string path)
        {
            var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r", "");
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 8).Replace("-", "").ToLowerInvariant();
        }

        // %LOCALAPPDATA%\WeatherWallpaper\samples.txt：用户文件夹里的示例放进去的是哪一版，每行「指纹 文件夹\文件名」（cards\每日一句示例.js）。
        // 丢了也没关系：只是认不出那些示例没改过、不换新的
        static Dictionary<string, string> record;
        static bool recordDirty;
        static string RecordPath => Path.Combine(Settings.LocalDir, "samples.txt");

        static Dictionary<string, string> Record
        {
            get
            {
                if (record != null) return record;
                record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(RecordPath))
                        foreach (var line in File.ReadAllLines(RecordPath, Encoding.UTF8))
                        {
                            int sp = line.IndexOf(' ');
                            if (sp > 0) record[line.Substring(sp + 1)] = line.Substring(0, sp);
                        }
                }
                catch (Exception ex) { Log.Write("samples record: " + ex.Message); }
                return record;
            }
        }

        static void Placed(string tag, string name, string hash)
        {
            string old, key = tag + "\\" + name;
            if (Record.TryGetValue(key, out old) && old == hash) return;
            record[key] = hash;
            recordDirty = true;
        }

        static void SaveRecord()
        {
            if (!recordDirty) return;
            try { File.WriteAllLines(RecordPath, record.Select(p => p.Value + " " + p.Key), new UTF8Encoding(false)); recordDirty = false; }
            catch (Exception ex) { Log.Write("samples record: " + ex.Message); }
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
