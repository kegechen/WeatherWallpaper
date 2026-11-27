using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WeatherWallpaper
{
    class UpdateInfo
    {
        public Version Version;
        public string Tag, MsiUrl, ZipUrl, MsiFallback, ZipFallback;
    }

    /// <summary>已经下好、等着装的新版本：在 %LOCALAPPDATA%\WeatherWallpaper\update\ 里，pending.json 记着是哪个版本、放在哪。</summary>
    class StagedUpdate
    {
        public Version Version;
        public string Tag;
        public string Kind;      // msi：安装版，Package 是 MSI 文件；zip：便携版，Package 是解压出来的目录
        public string Package;
        public string Dst;       // 给哪个程序目录下的（便携版要覆盖它；安装版就是安装目录）
        public bool Tried;       // 启动时已经自动装过一次：现在还是旧版本，说明没装上，不再在启动时自动装（免得每次开机都卡在这一步）
    }

    /// <summary>
    /// 检查更新：先走 gh 加速地址（返回纯文本下载链接列表，方便访问不了 GitHub 的用户），失败再用 GitHub API。
    /// </summary>
    static class Updater
    {
        const string Repo = "kegechen/WeatherWallpaper";
        const string MirrorLatest = "https://gh.ifly.qzz.io/WeatherWallpaper/releases/latest";
        public const string RepoPage = "https://github.com/" + Repo;
        public const string ReleasePage = RepoPage + "/releases/latest";
        public const string IssuesPage = RepoPage + "/issues";

        static readonly HttpClient http = CreateClient();
        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("WeatherWallpaper/" + Current);
            return c;
        }

        public static Version Current
        {
            get { var v = Assembly.GetExecutingAssembly().GetName().Version; return new Version(v.Major, v.Minor, v.Build); }
        }

        /// <summary>build.ps1 不带 -Version 构建出来的源码版本（0.0.0），不参与自动更新。</summary>
        public static bool IsDevBuild => Current == new Version(0, 0, 0);
        /// <summary>给用户看的当前版本，如 "v0.2.0" / "开发版"。</summary>
        public static string CurrentText => IsDevBuild ? "开发版" : "v" + Current;

        /// <summary>是不是通过 MSI 装的（装在 %LOCALAPPDATA%\Programs\WeatherWallpaper）。便携版只能引导手动下载。</summary>
        public static bool Installed
        {
            get
            {
                var dir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
                var inst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WeatherWallpaper");
                return string.Equals(dir, inst, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static async Task<UpdateInfo> Check()
        {
            UpdateInfo mirror = null, gh = null;
            try { mirror = await FromMirror().ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("update mirror failed: " + ex.Message); }
            if (mirror == null)
            {
                try { gh = await FromGitHub().ConfigureAwait(false); }
                catch (Exception ex) { Log.Write("update github failed: " + ex.Message); }
            }
            var info = mirror ?? gh;
            if (info == null) throw new Exception("无法连接更新服务器");
            // 加速下载失败时回退的 GitHub 直链
            info.MsiFallback = $"https://github.com/{Repo}/releases/download/{info.Tag}/WeatherWallpaper-{info.Version}-x64.msi";
            info.ZipFallback = $"https://github.com/{Repo}/releases/download/{info.Tag}/WeatherWallpaper-{info.Version}-x64-portable.zip";
            return info;
        }

        static async Task<UpdateInfo> FromMirror()
        {
            var text = await http.GetStringAsync(MirrorLatest).ConfigureAwait(false);
            var urls = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.StartsWith("http")).ToList();
            var info = new UpdateInfo
            {
                MsiUrl = urls.FirstOrDefault(u => u.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)),
                ZipUrl = urls.FirstOrDefault(u => u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)),
            };
            var m = Regex.Match(info.MsiUrl ?? info.ZipUrl ?? "", @"/download/(v?(\d+\.\d+\.\d+))/");
            if (!m.Success) return null;
            info.Tag = m.Groups[1].Value;
            info.Version = new Version(m.Groups[2].Value);
            return info;
        }

        static async Task<UpdateInfo> FromGitHub()
        {
            var json = await http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest").ConfigureAwait(false);
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var tag = Convert.ToString(d["tag_name"]);
            var info = new UpdateInfo { Tag = tag, Version = new Version(tag.TrimStart('v')) };
            if (d.ContainsKey("assets") && d["assets"] is IEnumerable assets)
                foreach (var a in assets.OfType<Dictionary<string, object>>())
                {
                    var u = Convert.ToString(a["browser_download_url"]);
                    if (u.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) info.MsiUrl = u;
                    else if (u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) info.ZipUrl = u;
                }
            return info;
        }

        static string AppDir => Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');

        // ---- 自动更新：查到新版本先在后台下好（Stage），点「重启更新」或下次正常启动时再装（Apply）
        static string UpdateDir => Path.Combine(Settings.LocalDir, "update");
        static string PendingFile => Path.Combine(UpdateDir, "pending.json");
        static string Kind => Installed ? "msi" : "zip";
        static readonly byte[] MsiMagic = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };

        /// <summary>下好的、给本程序（安装版 / 这个便携版目录）的新版本；没有、对不上或文件不在了返回 null。不比较版本号。</summary>
        public static StagedUpdate LoadStaged()
        {
            try
            {
                if (!File.Exists(PendingFile)) return null;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(PendingFile));
                if (d == null) return null;
                string S(string k) => d.TryGetValue(k, out var v) ? v as string : null;
                var s = new StagedUpdate { Tag = S("tag"), Kind = S("kind"), Package = S("package"), Dst = S("dst"), Tried = d.TryGetValue("tried", out var t) && t is bool b && b };
                if (!Version.TryParse(S("version") ?? "", out var ver) || string.IsNullOrEmpty(s.Tag)) return null;
                s.Version = ver;
                if (s.Kind != Kind || !string.Equals(s.Dst, AppDir, StringComparison.OrdinalIgnoreCase) || !InDir(s.Package, UpdateDir)) return null;
                if (s.Kind == "msi" ? !File.Exists(s.Package) : !File.Exists(Path.Combine(s.Package, "WeatherWallpaper.exe"))) return null;
                return s;
            }
            catch (Exception ex) { Log.Write("update: pending.json: " + ex.Message); return null; }
        }

        static void SaveStaged(StagedUpdate s)
        {
            var json = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                ["version"] = s.Version.ToString(), ["tag"] = s.Tag, ["kind"] = s.Kind, ["package"] = s.Package, ["dst"] = s.Dst, ["tried"] = s.Tried,
            });
            var tmp = PendingFile + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(PendingFile)) File.Delete(PendingFile);
            File.Move(tmp, PendingFile);
        }

        /// <summary>删掉下好的更新（装上了、撤了、关了自动更新）。</summary>
        public static void ClearStaged()
        {
            try { if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, true); }
            catch (Exception ex) { Log.Write("update: clean " + UpdateDir + ": " + ex.Message); }
        }

        /// <summary>
        /// 下好的更新已经装上了（版本不比当前新）就删掉：MSI 装完拉起新版本时还可能占着安装包，所以启动一会儿后再调。
        /// 还没装上的留着，托盘里点「重启更新」或下次启动接着用；别的程序目录（另一份便携版）下的不动。
        /// </summary>
        public static void CleanStale()
        {
            if (IsDevBuild || !File.Exists(PendingFile)) return;
            var s = LoadStaged();
            if (s == null || s.Version > Current) return;
            ClearStaged();
            Log.Write("update: " + s.Tag + " installed, staged package removed");
        }

        static bool InDir(string p, string dir)
        {
            if (string.IsNullOrEmpty(p)) return false;
            try { return Path.GetFullPath(p).StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>
        /// 在后台把新版本下好：安装版下 MSI；便携版下 zip 解压好（程序目录要写得进去）。记进 pending.json，同一个版本下好过就直接用。
        /// </summary>
        public static async Task<StagedUpdate> Stage(UpdateInfo info, IProgress<string> progress)
        {
            var old = LoadStaged();
            if (old != null && old.Version == info.Version) return old;
            if (!Installed && !CanWrite(AppDir)) throw new Exception("程序目录没有写入权限：" + AppDir);
            ClearStaged();   // 之前下的别的版本、没下完的
            Directory.CreateDirectory(UpdateDir);
            var s = new StagedUpdate { Version = info.Version, Tag = info.Tag, Kind = Kind, Dst = AppDir };
            if (Installed)
            {
                s.Package = Path.Combine(UpdateDir, $"WeatherWallpaper-{info.Version}-x64.msi");
                await Download(new[] { info.MsiUrl, info.MsiFallback }, s.Package, MsiMagic, info, progress).ConfigureAwait(false);
            }
            else
            {
                var zip = Path.Combine(UpdateDir, $"WeatherWallpaper-{info.Version}-x64-portable.zip");
                await Download(new[] { info.ZipUrl, info.ZipFallback }, zip, ZipMagic, info, progress).ConfigureAwait(false);
                progress?.Report("正在解压 " + info.Tag + " …");
                var stage = Path.Combine(UpdateDir, "portable-" + info.Version);
                try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }   // 上次解压的残留：留着 ExtractToDirectory 会失败
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, stage);
                // 兼容 zip 里多套一层目录的情况
                var root = stage;
                if (!File.Exists(Path.Combine(root, "WeatherWallpaper.exe")))
                    root = Directory.GetDirectories(stage).FirstOrDefault(d => File.Exists(Path.Combine(d, "WeatherWallpaper.exe")))
                           ?? throw new Exception("更新包里没有找到 WeatherWallpaper.exe");
                try { File.Delete(zip); } catch { }
                s.Package = root;
            }
            SaveStaged(s);
            return s;
        }

        /// <summary>
        /// 装上下好的新版本。交给与版本无关的 PowerShell 脚本：等本进程退出 → 安装版静默装 MSI（不弹进度窗，装完安装包自己拉起新版本）/
        /// 便携版覆盖程序目录 → 这时还没有在跑的就把程序拉起来（装失败时拉起来的是旧版本）。返回后调用方应立即退出。
        /// </summary>
        public static void Apply(StagedUpdate s)
        {
            var script = Path.Combine(UpdateDir, "apply.ps1");
            File.WriteAllText(script, ApplyScript);
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -Kind {s.Kind} -Src \"{s.Package}\" -Dst \"{s.Dst}\" -OldPid {Process.GetCurrentProcess().Id} -Log \"{Path.Combine(Settings.LocalDir, "log.txt")}\"")
                { UseShellExecute = false, CreateNoWindow = true });
        }

        /// <summary>
        /// 正常启动（开机自启、开始菜单，不带参数）时调：有下好的新版本就交给脚本去装，返回 true，调用方直接退出（装好后新版本被拉起来）。
        /// 启动时装过一次还是旧版本（没装上）的不再自动装，托盘里点「重启更新」可以再试。
        /// </summary>
        public static bool ApplyAtStartup()
        {
            if (IsDevBuild) return false;
            var s = LoadStaged();
            if (s == null || s.Version <= Current || s.Tried) return false;   // 装上了的留给新版本启动后清理（这会儿 MSI 可能还占着安装包）
            try
            {
                s.Tried = true;
                SaveStaged(s);
                Apply(s);
                Log.Write("update: installing " + s.Tag + " at startup");
                return true;
            }
            catch (Exception ex) { Log.Write("update: install at startup failed: " + ex.Message); return false; }
        }

        // 只用 ASCII：PowerShell 5 会把无 BOM 的脚本当 ANSI 读。
        // msiexec 用 Process.WaitForExit 等：Start-Process -Wait 会连它拉起的子进程（安装包装完启动的新版本）一起等
        const string ApplyScript = @"param([string]$Kind, [string]$Src, [string]$Dst, [int]$OldPid, [string]$Log)
function L($s) { try { Add-Content -LiteralPath $Log -Value ((Get-Date -Format 'MM-dd HH:mm:ss ') + 'apply-update: ' + $s) -Encoding UTF8 } catch {} }
function Running {
  # only processes running the updated copy count: another portable copy in a
  # different directory must not keep us from relaunching this one
  $exe = Join-Path $Dst 'WeatherWallpaper.exe'
  foreach ($p in Get-Process -Name WeatherWallpaper -ErrorAction SilentlyContinue) {
    try { if ($p.Path -eq $exe) { return $true } } catch {}
  }
  return $false
}
try { Wait-Process -Id $OldPid -Timeout 30 -ErrorAction Stop } catch {}
Start-Sleep -Milliseconds 500
if ($Kind -eq 'msi') {
  $msiLog = Join-Path (Split-Path $Log) 'update-msi.log'
  $a = '/i ' + [char]34 + $Src + [char]34 + ' /qn /norestart /le ' + [char]34 + $msiLog + [char]34
  $p = [Diagnostics.Process]::Start('msiexec.exe', $a)
  $p.WaitForExit()
  L ('msiexec exit ' + $p.ExitCode)
  if ($p.ExitCode -ne 0) { L 'msiexec failed, keeping staged MSI' }
  for ($i = 0; $i -lt 20 -and -not (Running); $i++) { Start-Sleep -Milliseconds 500 }
} else {
  $ok = $true
  foreach ($f in Get-ChildItem -LiteralPath $Src -Recurse -File) {
    $rel = $f.FullName.Substring($Src.TrimEnd('\').Length + 1)
    $to = Join-Path $Dst $rel
    New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
    $done = $false
    for ($i = 0; $i -lt 30 -and -not $done; $i++) {
      try { Copy-Item -LiteralPath $f.FullName -Destination $to -Force -ErrorAction Stop; $done = $true } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $done) { $ok = $false; L ('copy failed ' + $rel) }
  }
  L ('copied to ' + $Dst + ', ok=' + $ok)
}
if (-not (Running)) { Start-Process -FilePath (Join-Path $Dst 'WeatherWallpaper.exe') -WorkingDirectory $Dst }
";

        /// <summary>下载到 path（先写 .part，下完核对大小和文件头再改名）。</summary>
        static async Task Download(string[] urls, string path, byte[] magic, UpdateInfo info, IProgress<string> progress)
        {
            Exception last = new Exception("没有可用的下载地址");
            var part = path + ".part";
            foreach (var url in urls.Where(u => !string.IsNullOrEmpty(u)).Distinct())
            {
                try
                {
                    long total, done = 0;
                    using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        total = resp.Content.Headers.ContentLength ?? 0;
                        int lastPct = -1;
                        using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var fs = File.Create(part))
                        {
                            var buf = new byte[81920];
                            int n;
                            while ((n = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                            {
                                await fs.WriteAsync(buf, 0, n).ConfigureAwait(false);
                                done += n;
                                int pct = total > 0 ? (int)(done * 100 / total) : -1;
                                if (pct != lastPct && pct % 5 == 0) { lastPct = pct; progress?.Report($"正在下载 {info.Tag} … {pct}%"); }
                            }
                        }
                    }
                    if (done < 50 * 1024 || (total > 0 && done != total)) throw new Exception("下载的文件不完整");
                    var head = new byte[magic.Length];
                    using (var fs = File.OpenRead(part)) if (fs.Read(head, 0, head.Length) != head.Length || !head.SequenceEqual(magic)) throw new Exception("下载的不是安装包（可能被网络劫持）");
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(part, path);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Write("update download failed: " + url + " " + ex.Message);
                    try { File.Delete(part); } catch { }   // 下一个地址可能返回的是错误页，删掉才不会被当成完好的包
                }
            }
            try { File.Delete(part); } catch { }
            throw last;
        }

        static bool CanWrite(string dir)
        {
            try
            {
                var p = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(p, "");
                File.Delete(p);
                return true;
            }
            catch { return false; }
        }
    }
}
