using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WeatherWallpaper
{
    /// <summary>
    /// 自定义卡片：%APPDATA%\WeatherWallpaper\cards\ 里每个 .js 文件是一种卡片（写法见里面的示例和 README「自定义卡片」）。
    /// 脚本在壁纸页面的 Worker 里运行（web/cards.js：断网、没有 DOM、没有定时器），只能通过宿主读 cards 文件夹里的文件、联网——
    /// 这里就是宿主那一半：读脚本、读文件、替它发 HTTP 请求（有大小、超时、频率限制）。卡片里的图片只能是 data: 网址（api.image / api.fetchImage 给的就是）。
    /// </summary>
    static class CustomCards
    {
        public const int MinHeight = 64, MaxHeight = 720, DefaultHeight = 160;
        const int MaxScript = 256 << 10, MaxFile = 256 << 10, MaxNet = 1 << 20, MaxBody = 64 << 10;
        const int MinInterval = 60, MaxPerMinute = 10, NetSeconds = 15, RouteSeconds = 6, DnsSeconds = 5, MaxRedirects = 5;

        public static string Dir => Path.Combine(Settings.AppDataDir, "cards");

        /// <summary>文件夹不在就建上，放几个示例进去（随程序分发的 web/cards/ 下的英文名 → 放进用户文件夹时的名字）；没改过的示例换成新版。
        /// 后面的指纹是 0.9.4 及以前发过的各版（见 ScriptDir.Ensure），以后改示例不用往这里加。</summary>
        public static void EnsureDir() => ScriptDir.Ensure(Dir, "cards",
            new[] { @"cards\clock.js", "时钟天气示例.js", "f2474e27a3f1ab6b", "ee363471c3c10e0c", "a5979dbd2720d9a4" },
            new[] { @"cards\countdown.js", "倒计时示例.js", "62267d2b0ea780a0" },
            new[] { @"cards\quote.js", "每日一句示例.js", "7f09bfe6d031f455", "df32e6cdac856ba1", "3cb5464186ea7af6", "b5b566ad256d7d51" });

        /// <summary>cards 文件夹里的一个卡片脚本。Title / Height 是从源码里粗略找出来的 name / height（卡片设置里显示、新增时的默认高度用），找不到为 null / DefaultHeight。</summary>
        public class Script
        {
            public string File;   // 文件名，不带 .js（设置里 CardSpec.Script 存的就是它）
            public long V;        // 修改时间（毫秒）：变了页面就重新加载用它的卡片
            public string Title;
            public int Height = DefaultHeight;
            /// <summary>卡片设置里「刷新」按钮上的字：脚本里写的 refreshButton: '换一句'（true = 「立即刷新」）；没写的话联网的脚本（用了 api.fetchJson 等）是「立即刷新」，
            /// 不联网的、写了 refreshButton: false 的是 null（不显示按钮）</summary>
            public string Button;
            /// <summary>脚本里写了 autoHeight: true：卡片高度随内容变（页面画完量出来报给宿主，见 Fit）；Height 只是量出来之前先用的高度</summary>
            public bool Auto;
            public bool Unread;   // 扫描时正好被占用、没读出来：Title / Height 是上次的（或默认的），过一会儿再扫（RescanCards）
        }

        /// <summary>
        /// 高度随内容变的卡片，页面量出来的高度（卡片 id → CSS 像素，已限制在 MinHeight–MaxHeight）。CardLayout.HeightOf 优先用它；
        /// 还没量过（刚启动、卡片还没画出来）用设置里的高度。卡片因为放不下被撤掉时不清：清了会按矮的重排、放得下、再量出高的、又放不下，来回闪。
        /// 只在界面线程读写。
        /// </summary>
        public static readonly Dictionary<string, int> Fit = new Dictionary<string, int>();

        /// <summary>这个脚本的卡片高度是不是随内容变（按 Scan 读到的，文件名不分大小写）。</summary>
        public static bool IsAuto(string script)
        {
            Script s;
            return script != null && metaCache.TryGetValue(script, out s) && s.Auto;
        }

        public static int ClampHeight(double h) => (int)Math.Max(MinHeight, Math.Min(MaxHeight, Math.Round(h)));

        static readonly Dictionary<string, Script> metaCache = new Dictionary<string, Script>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> oddNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 名字用不了、跳过的脚本（日志只记一次）
        // 前后都要断开：CSS 里的 line-height: / min-height: 100% / height: 120px 不算
        static readonly Regex TitleRx = new Regex(@"(?<![\w$-])name\s*:\s*(['""`])([^'""`\r\n]{1,40})\1"), HeightRx = new Regex(@"(?<![\w$-])height\s*:\s*(\d{2,3})(?![\w.%])");
        static readonly Regex ButtonRx = new Regex(@"(?<![\w$-])refreshButton\s*:\s*(?:(['""`])([^'""`\r\n]{0,200})\1|(true|false)\b)"), NetRx = new Regex(@"(?<![\w$])fetch(?:Text|Json|Image)(?![\w$])");
        static readonly Regex AutoRx = new Regex(@"(?<![\w$-])autoHeight\s*:\s*true\b");
        public const string DefaultButton = "立即刷新";
        const int MaxButton = 12;   // 按钮上最多几个字（多的截掉）

        /// <summary>refreshButton 的值 → 按钮上的字：false = 不要按钮；true、空白 = 默认的字；太长截到 MaxButton 个字（不切开 emoji 这类两个 char 的字）。</summary>
        static string ButtonText(Match b)
        {
            if (b.Groups[3].Success) return b.Groups[3].Value == "true" ? DefaultButton : null;
            var s = b.Groups[2].Value.Trim();
            if (s == "") return DefaultButton;
            if (s.Length > MaxButton) s = s.Substring(0, char.IsHighSurrogate(s[MaxButton - 1]) ? MaxButton - 1 : MaxButton).TrimEnd();
            return s;
        }

        /// <summary>所有卡片脚本，按文件名排好序（最多 50 个；隐藏的、超过 256 KB 的、名字用不了的不算，见 Usable）。</summary>
        public static List<Script> Scan()
        {
            var list = new List<Script>();
            foreach (var fi in ScriptDir.Scripts(Dir, MaxScript, "cards", Usable))
            {
                var s = new Script { File = ScriptDir.Name(fi), V = ScriptDir.Version(fi) };
                Script old;
                metaCache.TryGetValue(s.File, out old);
                // 没改过：不用重新读。只改了大小写的改名（clock.js → Clock.js）修改时间不变，也要重新来，不然列表里还是旧的写法
                if (old != null && old.V == s.V && old.File == s.File) { list.Add(old); continue; }
                try
                {
                    // 去掉注释（说明注释里也会写 name: / height:，甚至 WeatherWallpaper.card({...})），只看 card({ ... }) 最外一层：
                    // data() 里返回的 { name: … }、render 里的 { height: … } 不算。认不出这一层（card(def)、括号数乱了）就照整段找
                    var all = NoComments(File.ReadAllText(fi.FullName, Encoding.UTF8));
                    int at = all.IndexOf("WeatherWallpaper.card", StringComparison.Ordinal);
                    var src = (at >= 0 ? TopLevel(all.Substring(at)) : null) ?? all;
                    var t = TitleRx.Match(src);
                    var h = HeightRx.Match(src);
                    var b = ButtonRx.Match(src);
                    int hv;
                    if (t.Success) s.Title = t.Groups[2].Value.Trim();
                    if (h.Success && int.TryParse(h.Groups[1].Value, out hv)) s.Height = ClampHeight(hv);
                    // 联不联网看整个脚本（api.fetchJson 写在 data() 里，最外一层看不到）
                    // 写成变量之类认不出来的（refreshButton: LABEL）：当没写
                    s.Button = b.Success ? ButtonText(b) : NetRx.IsMatch(all) ? DefaultButton : null;
                    s.Auto = AutoRx.IsMatch(src);   // 写成变量之类认不出来的：当没写（页面那边也只认直接写的 true）
                    metaCache[s.File] = s;
                }
                catch (Exception)
                {
                    // 正好被删了：不列。正好被占用（编辑器在保存、杀毒软件在查）：照样列出来——不列的话用它的卡片会被当成「不在列表里」停掉，
                    // 要等文件夹里再有动静才恢复。名称、高度先用上次读到的（没有就是默认的），不进 metaCache，下次扫描再读；
                    // 脚本本身页面会来要（ReadScript），那时还读不出来就报原因、30 秒后再要
                    if (!File.Exists(fi.FullName)) continue;
                    if (old != null) { s.Title = old.Title; s.Height = old.Height; s.Button = old.Button; s.Auto = old.Auto; }
                    s.Unread = true;
                }
                list.Add(s);
            }
            // 删掉的（还有排到 50 个以后的）不用再记着
            var names = new HashSet<string>(list.Select(s => s.File), StringComparer.OrdinalIgnoreCase);
            foreach (var k in metaCache.Keys.Where(k => !names.Contains(k)).ToList()) metaCache.Remove(k);
            return list;
        }

        /// <summary>
        /// 设置里存的脚本名会被 CleanName 整理（去掉首尾空白、.js，截到 100 个字），整理完和文件名对不上的（「a.js.js」「 a.js」）选了也读不到：
        /// 不列出来，也不占 50 个的名额（不然排在它们后面的正常脚本会被挤掉）。
        /// </summary>
        static bool Usable(string name)
        {
            if (CleanName(name) == name) return true;
            if (oddNames.Add(name)) Log.Write("cards: skipped \"" + name + ".js\" (name can't start or end with spaces, end with .js, or exceed 100 characters)");
            return false;
        }

        /// <summary>去掉 JS 的 // 和 /* */ 注释（字符串、模板字符串、正则里的不算）。只是找 name / height 用，粗略就行：正则和除号按前一个字符猜。</summary>
        static string NoComments(string s)
        {
            var sb = new StringBuilder(s.Length);
            char prev = '\0';   // 上一个不是空白的字符
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i], n = i + 1 < s.Length ? s[i + 1] : '\0';
                if (c == '/' && n == '/') { while (i + 1 < s.Length && s[i + 1] != '\n') i++; continue; }
                if (c == '/' && n == '*') { int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? s.Length : e + 1; sb.Append(' '); continue; }
                int j = LiteralEnd(s, i, prev);
                if (j >= 0) { sb.Append(s, i, j - i + 1); i = j; prev = c; continue; }   // 字符串 / 正则：原样抄
                sb.Append(c);
                if (!char.IsWhiteSpace(c)) prev = c;
            }
            return sb.ToString();
        }

        /// <summary>s[i] 开始的是字符串 / 模板字符串 / 正则（prev = 前一个不是空白的字符，用来猜 / 是正则还是除号）：返回它最后一个字符的位置，不是返回 -1。
        /// 引号、正则到行尾也算完（写错了别一直吃到文件末尾）；模板字符串里的 ${ } 不细分。</summary>
        static int LiteralEnd(string s, int i, char prev)
        {
            char c = s[i];
            if (!(c == '"' || c == '\'' || c == '`' || (c == '/' && (prev == '\0' || "(,=:[!&|?{};+-*%<>~^".IndexOf(prev) >= 0)))) return -1;
            int j = i + 1;
            bool inClass = false;
            for (; j < s.Length; j++)
            {
                char d = s[j];
                if (d == '\\') { j++; continue; }
                if (d == '\n' && c != '`') break;
                if (c != '/') { if (d == c) break; }
                else if (d == '[') inClass = true;
                else if (d == ']') inClass = false;
                else if (d == '/' && !inClass) break;
            }
            return Math.Min(j, s.Length - 1);
        }

        /// <summary>
        /// s = 去掉注释、从 WeatherWallpaper.card 开始的源码：card( 后面那个 { … } 最外一层的内容（里面的函数体、嵌套的对象、数组、括号去掉，这一层的字符串留着）。
        /// card( 后面不是 {、括号配不上（猜错了正则）返回 null。
        /// </summary>
        static string TopLevel(string s)
        {
            int i = s.IndexOf('(');
            if (i < 0) return null;
            for (i++; i < s.Length && char.IsWhiteSpace(s[i]); i++) { }
            if (i >= s.Length || s[i] != '{') return null;
            var sb = new StringBuilder();
            int depth = 0;
            char prev = '(';
            for (; i < s.Length; i++)
            {
                char c = s[i];
                int j = LiteralEnd(s, i, prev);
                if (j >= 0) { if (depth == 1) sb.Append(s, i, j - i + 1); i = j; prev = c; continue; }
                if (c == '{' || c == '(' || c == '[') depth++;
                else if (c == '}' || c == ')' || c == ']') { if (--depth == 0) return sb.ToString(); if (depth < 0) return null; }
                else if (depth == 1) sb.Append(c);
                if (!char.IsWhiteSpace(c)) prev = c;
            }
            return null;
        }

        /// <summary>比较列表变没变用（文件名、修改时间、读没读出来：被占用的那次之后读出来了，名称高度要换成新的）。</summary>
        public static string Sig(List<Script> list) => string.Join("|", list.Select(s => s.File + "@" + s.V + (s.Unread ? "?" : "")));

        /// <summary>设置里的脚本名整理一下：去掉首尾空白和 .js 扩展名；空 = 不是自定义卡片。名字合不合法读的时候再查（不合法就是"找不到脚本"，别悄悄变成天气卡片）。</summary>
        public static string CleanName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 3).TrimEnd();
            if (s.Length > 100) s = s.Substring(0, 100);
            return s == "" ? null : s;
        }

        /// <summary>cards 文件夹里的一个文件：相对路径（可以带子文件夹，不许 ..、盘符、绝对路径），解析后必须还在文件夹里面。不合法返回 null。</summary>
        static string PathOf(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel) || rel.Length > 200) return null;
            rel = rel.Replace('/', '\\').TrimStart('\\');
            if (rel.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || rel.IndexOfAny(new[] { ':', '*', '?' }) >= 0 || rel.Split('\\').Any(p => p == ".." || p == ".")) return null;
            try
            {
                var root = Root;
                var full = Path.GetFullPath(Path.Combine(root, rel));
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch { return null; }
        }
        static string Root => Path.GetFullPath(Dir).TrimEnd('\\') + "\\";

        /// <summary>
        /// cards 文件夹里面的路径（PathOf 给的）经过符号链接、目录联接：链接可以指到文件夹外面去，脚本拿 api.readText 读出来再发到网上。
        /// cards 文件夹本身是链接不算（用户自己把它挪到了别处）；脚本文件是链接也不管（运行哪份代码是用户自己选的，ReadScript 不查）。
        /// </summary>
        static bool ViaLink(string full)
        {
            var root = Root;
            for (var p = full; p != null && p.Length > root.Length; p = Path.GetDirectoryName(p))
                if (Native.IsLink(p)) return true;
            return false;
        }

        /// <summary>
        /// 读卡片脚本（UTF-8，认 BOM）。返回发给页面的 csrc 消息。
        /// listed = 在不在 Scan 的列表里：不在的（隐藏的、排在 50 个之后的）不运行——卡片设置里选不到它，改了也不会通知页面重新加载（版本号是列表给的）。
        /// </summary>
        public static string ReadScript(string name, bool listed)
        {
            string err;
            var p = name != null && name.IndexOfAny(new[] { '\\', '/' }) < 0 ? PathOf(name + ".js") : null;
            if (p == null) err = "脚本名不合法：" + name;
            else
            {
                try
                {
                    var fi = new FileInfo(p);
                    if (!fi.Exists) err = "找不到脚本：cards 文件夹里没有「" + name + ".js」";
                    else if (fi.Length > MaxScript) err = "脚本太大（超过 256 KB）";
                    else if (!listed) err = (fi.Attributes & FileAttributes.Hidden) != 0 ? "「" + name + ".js」是隐藏文件：隐藏的脚本不运行（去掉隐藏属性就好）"
                        : "「" + name + ".js」不在卡片脚本列表里：cards 文件夹里的脚本超过 " + ScriptDir.MaxFiles + " 个时，按文件名排在后面的不运行";
                    else
                    {
                        var src = File.ReadAllText(p, Encoding.UTF8);
                        return "{\"t\":\"csrc\",\"f\":" + WeatherService.JsonStr(name) + ",\"v\":" + ScriptDir.Version(fi) + ",\"src\":" + WeatherService.JsonStr(src) + "}";
                    }
                }
                catch (Exception ex) { err = "读不出脚本：" + ex.Message; }
            }
            return "{\"t\":\"csrc\",\"f\":" + WeatherService.JsonStr(name) + ",\"err\":" + WeatherService.JsonStr(err) + "}";
        }

        static readonly Dictionary<string, string> ImageTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
            [".svg"] = "image/svg+xml", [".bmp"] = "image/bmp", [".ico"] = "image/x-icon", [".avif"] = "image/avif",
        };

        /// <summary>
        /// api.readText / api.image：读 cards 文件夹里的文件（最大 256 KB）。kind = text：按 UTF-8 读成文字（认 BOM）；
        /// image：读成 data: 网址（卡片里的图片只能是 data: 网址，动图由页面检查）。返回 cfile 回复里 rid 之后的部分。
        /// </summary>
        public static string ReadFile(string rel, string kind)
        {
            var p = PathOf(rel);
            if (p == null) return Err("文件名不合法（只能读 cards 文件夹里的文件，用相对路径）：" + Short(rel));
            string type = null;
            if (kind == "image" && !ImageTypes.TryGetValue(Path.GetExtension(p), out type)) return Err("「" + rel + "」不是支持的图片格式（png / jpg / gif / webp / svg / bmp / ico / avif）");
            try
            {
                var fi = new FileInfo(p);
                var key = (type != null ? "i|" : "t|") + p;
                var at = fi.LastWriteTimeUtc;
                FileEntry hit;
                // 读过、没改过：直接给留着的（refresh = 1 秒的卡片每秒都要同一张图，ViaLink 每一级查一次也要零点几毫秒）。
                // 给的是当初从 cards 文件夹里读到的内容，后来路径上换成了链接也不会因此读到外面的东西；改过的、没读过的照常先查链接
                if (fi.Exists && files.TryGetValue(key, out hit) && hit.At == at && hit.Len == fi.Length) { hit.Used = ++fileUse; return hit.Reply; }
                if (ViaLink(p)) return Err("「" + Short(rel) + "」是符号链接或目录联接（或者在这样的文件夹里）：可能指到 cards 文件夹外面，不能读");
                if (!fi.Exists) return Err("cards 文件夹里没有「" + rel + "」");
                if (fi.Length > MaxFile) return Err("「" + rel + "」太大（超过 256 KB）");
                var reply = type != null ? "\"ok\":true,\"data\":" + WeatherService.JsonStr("data:" + type + ";base64," + Convert.ToBase64String(File.ReadAllBytes(p)))
                    : "\"ok\":true,\"text\":" + WeatherService.JsonStr(File.ReadAllText(p, Encoding.UTF8));
                files.Remove(key);
                if (reply.Length > FileCacheChars / 2) return reply;   // 转义出来特别长的（满是控制字符的文本）不留
                while (files.Count > 0 && (files.Count >= 8 || files.Values.Sum(e => (long)e.Reply.Length) + reply.Length > FileCacheChars))
                    files.Remove(files.OrderBy(e => e.Value.Used).First().Key);   // 扔最久没用的
                files[key] = new FileEntry { At = at, Len = fi.Length, Reply = reply, Used = ++fileUse };
                return reply;
            }
            catch (Exception ex) { return Err("读不出「" + rel + "」：" + ex.Message); }
        }

        // 读过的文件的回复（只在 UI 线程上用）：refresh = 1 秒的卡片每次都 api.image 同一张图，不用每秒读盘、转 base64、拼 JSON
        // （250 KB 的图一次 6 毫秒左右）。按路径 + 修改时间 + 大小认，文件改了就重读；最多 8 个、合计 1M 个字符（2 MB）
        class FileEntry { public DateTime At; public long Len; public string Reply; public long Used; }
        static readonly Dictionary<string, FileEntry> files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        static long fileUse;
        const long FileCacheChars = 1 << 20;

        static string Err(string s) => "\"ok\":false,\"err\":" + WeatherService.JsonStr(s);

        // ---------------------------------------------------------------- 联网（api.fetchText / fetchJson / fetchImage）
        // 只能访问互联网上的网站：本机、局域网的地址不行，跳转过去的也不行（Lookup / Refuse / Pin）。
        // 先走系统代理（和浏览器一样；没设代理就是直连；http 网址先直连，见 Request），这条路不通或 RouteSeconds 秒没响应就换直连 / 代理再试，用剩下的时间（总共 NetSeconds 秒；POST 见 Request）。
        // 只「连不上」才换路是不够的：有的网络直连某些 https 网站会一直卡着（不报错），会把整个超时耗光、轮不到另一条路。
        // 每个网站记住上次走通的那条路（routes），下次先走它，免得每次都先等一轮。
        // 只声明接受 gzip：.NET Framework 按裸 deflate 解 Content-Encoding: deflate，而按规范它带 zlib 头，
        // 两样都接受时有的服务器（如 open-meteo）会选 deflate，结果解压报错「块的长度与它的补集不匹配」。
        // 不存 Cookie：所有卡片共用这两个客户端，存了的话一张卡片登录拿到的 Cookie，别的卡片请求同一个网站时也会带上（要 Cookie 的在 headers 里自己写）。
        // 不自动跟随跳转：每一跳都要先查是不是本机、局域网的地址（Send）
        static readonly HttpClient direct = Client(false), proxied = Client(true);
        static HttpClient Client(bool proxy)
        {
            var c = new HttpClient(new HttpClientHandler { UseProxy = proxy, UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip })
                { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("WeatherWallpaper/1.0 (custom card)");
            return c;
        }

        class NetEntry { public DateTime At; public Task<string> Task; }
        // 同一张卡片的同一个请求（方法、网址、内容、请求头都一样）60 秒内只真发一次：其间再要就给上次的结果（还在取就一起等）。
        // 这样 refresh = 1 秒的卡片在 data() 里每次都 fetch 也不会把人家的服务器打爆
        static readonly Dictionary<string, NetEntry> net = new Dictionary<string, NetEntry>();
        // 每张卡片这一分钟里真发过的请求（键 → 什么时候发的）：限的是不同的请求，同一个请求一分钟里只算一次
        static readonly Dictionary<string, List<KeyValuePair<string, DateTime>>> perCard = new Dictionary<string, List<KeyValuePair<string, DateTime>>>();
        static readonly Dictionary<string, bool> routes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);   // 网站 → 上次是不是直连走通的
        static readonly Regex HeaderName = new Regex(@"^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,64}$");
        static readonly HashSet<string> NoHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Host", "Content-Length", "Connection", "Transfer-Encoding", "Expect", "Upgrade", "TE", "Keep-Alive", "Proxy-Authorization", "Proxy-Connection", "Trailer" };
        // 跳到别的网站时不再带的请求头（和 curl 一样）：给 A 网站的密钥、Cookie，不能让 A 一个跳转就送给 B
        static readonly HashSet<string> Secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Authorization", "Cookie" };

        /// <summary>
        /// 只在 UI 线程上调（字典不加锁；await 之后也回到 UI 线程）。kind = text / image；返回 cget 回复里 rid 之后的部分：
        /// "ok":true,"status":200,"type":"application/json","text":"…"（image 是 "data":"data:image/png;base64,…"），或者 "ok":false,"err":"原因"。
        /// HTTP 错误码也算 ok（带 status），由页面那边按 status 决定要不要当成出错。
        /// fresh = 卡片设置里点了刷新、这一轮的请求：不用 60 秒内取过的结果，真发一次（还在取的照样一起等）。
        /// 每分钟的次数限制照算，但同一个请求一分钟里只算一次：刚取过的再真发一次不多占名额（不然一轮要联网好几次的卡片，刷新一下就超了）。
        /// </summary>
        public static Task<string> Fetch(string card, string kind, string url, string method, string body, IDictionary<string, object> headers, bool fresh = false)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || url.Length > 4000 || !Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https"))
                return Task.FromResult(Err("只能访问 http:// 或 https:// 开头的网址：" + Short(url)));
            method = string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant();
            if (method != "GET" && method != "POST") return Task.FromResult(Err("只支持 GET 和 POST 请求"));
            if (body != null && Encoding.UTF8.GetByteCount(body) > MaxBody) return Task.FromResult(Err("POST 的内容太大（最多 64 KB）"));
            var hs = new List<KeyValuePair<string, string>>();
            if (headers != null)
                foreach (var h in headers.Take(20))
                {
                    if (!HeaderName.IsMatch(h.Key ?? "") || NoHeaders.Contains(h.Key)) return Task.FromResult(Err("这个请求头不能设置：" + h.Key));
                    var v = Convert.ToString(h.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    // 换行、控制字符发送时才抛错（会报成「连不上」）；Latin-1 以外的字符（中文）只取低 8 位发出去，变成乱码：先挡下来说清楚
                    if (v.Any(ch => (ch < ' ' && ch != '\t') || ch == '\x7f' || ch > '\xff'))
                        return Task.FromResult(Err("请求头 " + h.Key + " 的值不能有换行、控制字符或中文之类的字符（要带中文先用 encodeURIComponent 转一下）"));
                    hs.Add(new KeyValuePair<string, string>(h.Key, v));
                }
            var now = DateTime.UtcNow;
            // 每一段前面带上长度：内容里有换行、冒号也拼不出别的请求的键（"a\nX:1" 不带请求头 ≠ "a" 带 X: 1）
            var key = string.Concat(new[] { card, kind, method, u.AbsoluteUri, body }.Concat(hs.SelectMany(h => new[] { h.Key, h.Value }))
                .Select(p => p == null ? "-;" : p.Length + ":" + p + ";"));
            NetEntry hit;
            if (net.TryGetValue(key, out hit) && !(fresh && hit.Task.IsCompleted)) return hit.Task;
            // 一分钟里没发过请求的卡片不用记了（删掉的卡片也就这样清掉）
            foreach (var k in perCard.Where(p => p.Value.All(t => (now - t.Value).TotalSeconds >= 60)).Select(p => p.Key).ToList()) perCard.Remove(k);
            List<KeyValuePair<string, DateTime>> times;
            if (!perCard.TryGetValue(card ?? "", out times)) perCard[card ?? ""] = times = new List<KeyValuePair<string, DateTime>>();
            times.RemoveAll(t => (now - t.Value).TotalSeconds >= 60);
            if (!times.Any(t => t.Key == key))
            {
                if (times.Count >= MaxPerMinute) return Task.FromResult(Err("请求太频繁：每张卡片每分钟最多 " + MaxPerMinute + " 个不同的请求（同一个请求 60 秒内会直接用上次的结果）"));
                times.Add(new KeyValuePair<string, DateTime>(key, now));
            }
            if (net.Count >= 64)
            {
                // 扔最早的已经取完的；还在取的不扔（扔了同一个请求会再真发一遍），全在取就先多存几个（最多 NetSeconds 秒都会结束）
                var old = net.Where(e => e.Value.Task.IsCompleted).OrderBy(e => e.Value.At).Select(e => e.Key).FirstOrDefault();
                if (old != null) net.Remove(old);
            }
            var task = Send(kind, u, method, body, hs);
            var entry = net[key] = new NetEntry { At = now, Task = task };
            Expire(key, entry);
            return task;
        }

        /// <summary>
        /// 取完、过了 MinInterval 秒就从 net 里拿掉：卡片不再联网（删了、改了脚本）之后，最后取的那些结果（图片的 base64 可能上 MB）不会一直留在内存里。
        /// 在 UI 线程上调：await 之后回到 UI 线程，改字典不用加锁。
        /// </summary>
        static async void Expire(string key, NetEntry e)
        {
            try { await e.Task; } catch (Exception) { /* Send 自己把错误变成回复，这里只是防万一 */ }
            var left = e.At.AddSeconds(MinInterval) - DateTime.UtcNow;
            if (left > TimeSpan.Zero) await Task.Delay(left);
            NetEntry cur;
            if (net.TryGetValue(key, out cur) && cur == e) net.Remove(key);   // 已经被挤掉、换成新的了就不动
        }

        static async Task<string> Send(string kind, Uri u, string method, string body, List<KeyValuePair<string, string>> hs)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(NetSeconds)))
            {
                try
                {
                    HttpResponseMessage resp;
                    for (int hop = 0; ; hop++)   // 自己跟随跳转：每一跳都查地址
                    {
                        var ips = await Lookup(u, cts.Token);
                        var no = Refuse(u, ips);
                        if (no != null) return Err(no);
                        resp = await Request(u, Pin(u, ips), method, body, hs, cts.Token);
                        int st = (int)resp.StatusCode;
                        var to = resp.Headers.Location;
                        if (to == null || (st != 301 && st != 302 && st != 303 && st != 307 && st != 308)) break;
                        resp.Dispose();
                        if (hop >= MaxRedirects) return Err("跳转太多次（超过 " + MaxRedirects + " 次）：" + Short(u.AbsoluteUri));
                        if (!to.IsAbsoluteUri) to = new Uri(u, to);
                        if (to.Scheme != "http" && to.Scheme != "https") return Err("跳转到了不支持的网址：" + Short(to.AbsoluteUri));
                        if (st == 303 || (method == "POST" && (st == 301 || st == 302))) { method = "GET"; body = null; }   // 和浏览器一样改成 GET
                        if (Uri.Compare(u, to, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) != 0)
                            hs = hs.Where(h => !Secrets.Contains(h.Key)).ToList();   // 跳到了别的网站（协议、域名、端口有一样不同）
                        u = to;
                    }
                    using (resp)
                    using (cts.Token.Register(() => { try { resp.Dispose(); } catch { } }))   // 读内容时超时：.NET Framework 的响应流不一定理会取消，直接关掉
                    {
                        int status = (int)resp.StatusCode;
                        var ct = resp.Content.Headers.ContentType;
                        string type = ct?.MediaType ?? "";
                        var len = resp.Content.Headers.ContentLength;
                        if (len > MaxNet) return Err("返回的内容太大（" + (len / 1024) + " KB，最多 1 MB）");
                        byte[] bytes;
                        using (var s = await resp.Content.ReadAsStreamAsync())
                        using (var ms = new MemoryStream())
                        {
                            var buf = new byte[16384];
                            int n;
                            while ((n = await s.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                            {
                                if (ms.Length + n > MaxNet) return Err("返回的内容太大（超过 1 MB）");
                                ms.Write(buf, 0, n);
                            }
                            bytes = ms.ToArray();
                        }
                        if (kind == "image")
                        {
                            if (status < 200 || status > 299) return Err("取图片失败：HTTP " + status);
                            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return Err("不是图片（" + (type == "" ? "没有说明类型" : type) + "）");
                            return "\"ok\":true,\"status\":" + status + ",\"type\":" + WeatherService.JsonStr(type) + ",\"data\":" + WeatherService.JsonStr("data:" + type.ToLowerInvariant() + ";base64," + Convert.ToBase64String(bytes));
                        }
                        return "\"ok\":true,\"status\":" + status + ",\"type\":" + WeatherService.JsonStr(type) + ",\"text\":" + WeatherService.JsonStr(Decode(bytes, ct?.CharSet));
                    }
                }
                catch (Exception) when (cts.IsCancellationRequested) { return Err("超过 " + NetSeconds + " 秒没有响应：" + Short(u.AbsoluteUri)); }
                catch (Exception ex)
                {
                    var e = ex;
                    while (e.InnerException != null) e = e.InnerException;
                    return Err("连不上 " + u.Host + "：" + e.Message);
                }
            }
        }

        /// <summary>
        /// 发一次请求（不跟随跳转）：先走这个网站上次走通的那条路，不通换另一条，用剩下的时间。
        /// GET 先试的那条路 RouteSeconds 秒没响应头也换。POST 只在还没发出去（连不上、域名解析不了、TLS 握手失败）时换：
        /// 发出去了只是回得慢，换条路再发一遍服务器就收到两次（打卡、计数之类的会多算）。这种等到总超时，下次先走另一条路。
        /// at = 真正连的网址（Pin），u = 原来的网址（Host 头、记哪条路走通用它）。
        /// </summary>
        static async Task<HttpResponseMessage> Request(Uri u, Uri at, string method, string body, List<KeyValuePair<string, string>> hs, CancellationToken tk)
        {
            bool viaDirect, post = method == "POST";
            var site = u.GetLeftPart(UriPartial.Authority);   // 协议 + 域名 + 端口：同一个域名的 http、https 各记各的
            // 连的是查过的地址（http 网址，见 Pin）：没走过的网站先直连，只有直连才和原来的网址完全一样。走代理的话请求行里是地址，
            // 有的代理按它改写 Host 头（RFC 7230 5.4，比如 Squid；也有原样转的），用虚拟主机的网站就收不到域名了，按域名分流的代理规则也认不出来。
            // 直连不通再走代理（只能走代理的网络里，代理原样转 Host 的还能用）
            if (!routes.TryGetValue(site, out viaDirect)) viaDirect = at != u;
            HttpResponseMessage resp;
            try { resp = post ? await Once(viaDirect ? direct : proxied, u, at, method, body, hs, tk) : await Route(viaDirect ? direct : proxied, u, at, method, body, hs, tk); }
            catch (Exception ex) when (!tk.IsCancellationRequested && (!post || NotSent(ex)))
            {
                viaDirect = !viaDirect;
                resp = await Once(viaDirect ? direct : proxied, u, at, method, body, hs, tk);
            }
            catch (Exception) when (post && tk.IsCancellationRequested) { Remember(site, !viaDirect); throw; }
            Remember(site, viaDirect);
            return resp;
        }
        static void Remember(string site, bool viaDirect)
        {
            if (routes.Count >= 256) routes.Clear();
            routes[site] = viaDirect;
        }

        /// <summary>请求还没发出去就失败了（连不上、域名解析不了、TLS 握手失败）：换条路再发，服务器也只收到一次。</summary>
        static bool NotSent(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var w = e as WebException;
                if (w != null) return w.Status == WebExceptionStatus.ConnectFailure || w.Status == WebExceptionStatus.NameResolutionFailure
                    || w.Status == WebExceptionStatus.ProxyNameResolutionFailure || w.Status == WebExceptionStatus.SecureChannelFailure || w.Status == WebExceptionStatus.TrustFailure;
            }
            return false;
        }

        // 不许访问本机、局域网里的地址：脚本可能是别人写的，会去读路由器、本机服务的页面再发到网上。每一跳都这样查：
        //  · Lookup 查出网址里的主机的地址，Refuse 看有没有在内网的（指向内网的域名也不行）、查不到地址的也不许（不然走代理时由代理去解析，查不到它连的是哪）；
        //  · 只查一次不够：连接时还会再解析一次，第二次可以换成内网地址（DNS rebinding），走代理时代理也会自己解析。
        //    http 网址就直接连查过的那个地址（Pin）；https 不用：再解析到内网的地址，那边拿不出这个域名的证书，握手就失败了。

        /// <summary>网址里的主机的地址：写的是 IP 就是它；localhost、*.localhost 是本机；域名就解析，最多等 DnsSeconds 秒。解析不了、超时返回 null。</summary>
        static async Task<IPAddress[]> Lookup(Uri u, CancellationToken tk)
        {
            var host = u.DnsSafeHost;
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return new[] { ip };
            if (u.IsLoopback || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return new[] { IPAddress.Loopback };
            var dns = Dns.GetHostAddressesAsync(host);
            var first = await Task.WhenAny(dns, Task.Delay(DnsSeconds * 1000, tk));
            tk.ThrowIfCancellationRequested();   // 总时间到了：按超时报
            return first != dns || dns.IsFaulted || dns.IsCanceled || dns.Result.Length == 0 ? null : dns.Result;
        }

        /// <summary>这一跳不许去的原因（查不到地址、有地址在内网）；可以去返回 null。</summary>
        static string Refuse(Uri u, IPAddress[] ips)
        {
            if (ips == null) return "查不到 " + u.Host + " 的地址（网址写错了，或者网络不通）";
            var bad = ips.FirstOrDefault(Inner);
            return bad == null ? null : "不能访问本机或局域网里的地址：" + u.Host + (bad.ToString() == u.DnsSafeHost ? "" : "（" + bad + "）") + "。卡片只能访问互联网上的网站";
        }

        /// <summary>真正要连的网址：http 网址的域名换成查过的地址（Host 头还是原来的域名，见 Once），别的原样。</summary>
        static Uri Pin(Uri u, IPAddress[] ips)
        {
            if (u.Scheme != "http" || u.HostNameType == UriHostNameType.IPv4 || u.HostNameType == UriHostNameType.IPv6) return u;
            // 优先 IPv4：只连这一个地址（不像系统那样一个连不上换下一个），没有 IPv6 的网络更多
            var ip = ips.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? ips[0];
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return new UriBuilder(u) { Host = ip.ToString() }.Uri;
        }

        /// <summary>
        /// 不是互联网上的地址：本机、局域网（10 / 172.16 / 192.168、IPv6 的 fc00::/7）、链路本地、100.64/10（运营商内网、Tailscale）、0.x、组播。
        /// 里面嵌着 IPv4 地址的 IPv6 按嵌着的那个查：::ffff:a.b.c.d、::a.b.c.d、NAT64（64:ff9b::/96；本地用的 64:ff9b:1::/48 都算内网）、6to4（2002::/16），
        /// 不然 64:ff9b::192.168.1.1 这样的地址经 NAT64 网关就到了局域网里。
        /// </summary>
        static bool Inner(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            var b = a.GetAddressBytes();
            if (b.Length == 4)
                return b[0] == 0 || b[0] == 10 || b[0] == 127 || (b[0] == 100 && (b[1] & 0xC0) == 64) || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || b[0] >= 224;
            Func<int, bool> zeros = n => b.Take(n).All(x => x == 0);   // 前 n 个字节都是 0
            if (zeros(12)) return Inner(new IPAddress(b.Skip(12).ToArray()));   // ::a.b.c.d（含 :: 和 ::1）
            if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
            {
                if (b[4] == 0 && b[5] == 1) return true;
                if (b.Skip(4).Take(8).All(x => x == 0)) return Inner(new IPAddress(b.Skip(12).ToArray()));
            }
            if (b[0] == 0x20 && b[1] == 0x02) return Inner(new IPAddress(b.Skip(2).Take(4).ToArray()));
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC;
        }

        /// <summary>GET 先试的那条路：最多等 RouteSeconds 秒拿到响应头，超时抛 OperationCanceledException（总的 tk 没到期，外面会换路）。</summary>
        static async Task<HttpResponseMessage> Route(HttpClient c, Uri u, Uri at, string method, string body, List<KeyValuePair<string, string>> hs, CancellationToken tk)
        {
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(tk))
            {
                t.CancelAfter(TimeSpan.FromSeconds(RouteSeconds));
                return await Once(c, u, at, method, body, hs, t.Token);
            }
        }

        static Task<HttpResponseMessage> Once(HttpClient c, Uri u, Uri at, string method, string body, List<KeyValuePair<string, string>> hs, CancellationToken tk)
        {
            var req = new HttpRequestMessage(method == "POST" ? HttpMethod.Post : HttpMethod.Get, at);
            if (at != u) req.Headers.Host = u.IsDefaultPort ? u.IdnHost : u.IdnHost + ":" + u.Port;   // 连的是地址（Pin），服务器还是要知道访问的是哪个网站
            string ctype = null;
            var forContent = new List<KeyValuePair<string, string>>();
            foreach (var h in hs)
            {
                if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) ctype = h.Value;
                // Content-Language、Content-Encoding 这些是内容的头，加不到请求头上（返回 false）：POST 时加到内容上（GET 没有内容，不带）
                else if (!req.Headers.TryAddWithoutValidation(h.Key, h.Value)) forContent.Add(h);
            }
            if (method == "POST")
            {
                req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body ?? ""));
                req.Content.Headers.TryAddWithoutValidation("Content-Type", ctype ?? "text/plain; charset=utf-8");
                foreach (var h in forContent) req.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            return c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, tk);
        }

        /// <summary>按 BOM / Content-Type 里的 charset 解码，都没有按 UTF-8。</summary>
        static string Decode(byte[] b, string charset)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            Encoding enc = Encoding.UTF8;
            if (!string.IsNullOrWhiteSpace(charset)) try { enc = Encoding.GetEncoding(charset.Trim('"', ' ')); } catch { }
            return enc.GetString(b);
        }

        static string Short(string s) => s == null ? "" : s.Length > 120 ? s.Substring(0, 120) + "…" : s;
    }
}
