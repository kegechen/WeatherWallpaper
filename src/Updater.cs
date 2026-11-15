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

    /// <summary>
    /// 检查更新：先走 gh 加速地址（返回纯文本下载链接列表，方便访问不了 GitHub 的用户），失败再用 GitHub API。
    /// </summary>
    static class Updater
    {
        const string Repo = "kegechen/WeatherWallpaper";
        const string MirrorLatest = "https://gh.ifly.qzz.io/WeatherWallpaper/releases/latest";
        public const string ReleasePage = "https://github.com/" + Repo + "/releases/latest";

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

        /// <summary>
        /// 下载并启动更新，返回后调用方应立即退出程序。
        /// MSI 版：下载 MSI 静默升级，装完由安装包重新拉起。
        /// 便携版：下载 zip 解压到临时目录，由辅助脚本等本进程退出后覆盖原目录并重启。
        /// </summary>
        public static async Task Install(UpdateInfo info, IProgress<string> progress)
        {
            if (Installed)
            {
                var msi = Path.Combine(Path.GetTempPath(), $"WeatherWallpaper-{info.Version}-x64.msi");
                await Download(new[] { info.MsiUrl, info.MsiFallback }, msi, info, progress).ConfigureAwait(false);
                progress?.Report("正在安装 " + info.Tag + " …");
                Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\" /passive /norestart") { UseShellExecute = false });
                return;
            }

            if (!CanWrite(AppDir)) throw new Exception("程序目录没有写入权限：" + AppDir);
            var zip = Path.Combine(Path.GetTempPath(), $"WeatherWallpaper-{info.Version}-x64-portable.zip");
            await Download(new[] { info.ZipUrl, info.ZipFallback }, zip, info, progress).ConfigureAwait(false);
            progress?.Report("正在解压 " + info.Tag + " …");
            var stage = Path.Combine(Path.GetTempPath(), "WeatherWallpaper-update-" + info.Version);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, stage);
            // 兼容 zip 里多套一层目录的情况
            var root = stage;
            if (!File.Exists(Path.Combine(root, "WeatherWallpaper.exe")))
                root = Directory.GetDirectories(stage).FirstOrDefault(d => File.Exists(Path.Combine(d, "WeatherWallpaper.exe")))
                       ?? throw new Exception("更新包里没有找到 WeatherWallpaper.exe");
            try { File.Delete(zip); } catch { }
            progress?.Report("正在更新 " + info.Tag + " …");
            // 覆盖文件交给与版本无关的 PowerShell 脚本：等本进程退出 → 复制 → 启动新版本（新旧版本都不需要认识额外参数）
            var script = Path.Combine(Path.GetTempPath(), "WeatherWallpaper-update.ps1");
            File.WriteAllText(script, ApplyScript);
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -Src \"{root}\" -Dst \"{AppDir}\" -OldPid {Process.GetCurrentProcess().Id} -Log \"{Path.Combine(Settings.LocalDir, "log.txt")}\"")
                { UseShellExecute = false, CreateNoWindow = true });
        }

        // 只用 ASCII：PowerShell 5 会把无 BOM 的脚本当 ANSI 读
        const string ApplyScript = @"param([string]$Src, [string]$Dst, [int]$OldPid, [string]$Log)
function L($s) { try { Add-Content -LiteralPath $Log -Value ((Get-Date -Format 'MM-dd HH:mm:ss ') + 'apply-update: ' + $s) -Encoding UTF8 } catch {} }
try { Wait-Process -Id $OldPid -Timeout 30 -ErrorAction Stop } catch {}
Start-Sleep -Milliseconds 800
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
Start-Process -FilePath (Join-Path $Dst 'WeatherWallpaper.exe') -WorkingDirectory $Dst
if ($ok) { Remove-Item -LiteralPath $Src -Recurse -Force -ErrorAction SilentlyContinue }
";

        static async Task Download(string[] urls, string path, UpdateInfo info, IProgress<string> progress)
        {
            Exception last = new Exception("没有可用的下载地址");
            foreach (var url in urls.Where(u => !string.IsNullOrEmpty(u)).Distinct())
            {
                try
                {
                    using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
                        int lastPct = -1;
                        using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var fs = File.Create(path))
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
                    if (new FileInfo(path).Length < 50 * 1024) throw new Exception("下载的文件不完整");
                    return;
                }
                catch (Exception ex) { last = ex; Log.Write("update download failed: " + url + " " + ex.Message); }
            }
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
