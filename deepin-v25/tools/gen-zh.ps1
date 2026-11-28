# 生成 packages/weather-web/zh.js：简繁逐字对照表，在 Windows 上用 PowerShell 7 运行（pwsh tools/gen-zh.ps1）。
# Windows 版简繁转换用的是系统自带的 LCMapStringEx（WeatherService.ZhMap），deepin 上没有：这里把它对每个汉字的转换结果导出来，
# zh.js 照表逐字换，结果和 Windows 版一字不差。表只在 Windows 更新了转换规则时才需要重新生成。
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ZhGen {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int LCMapStringEx(string locale, uint flags, string src, int srcLen, [Out] char[] dest, int destLen, IntPtr ver, IntPtr reserved, IntPtr sortHandle);
    // 和 WeatherService.IsCjk 一样的几段（假名段里没有要换的字，不用扫）
    public static string Pairs(uint flag) {
        var sb = new StringBuilder();
        int[][] ranges = { new[] { 0x3400, 0x4DBF }, new[] { 0x4E00, 0x9FFF }, new[] { 0xF900, 0xFAFF } };
        var buf = new char[8];
        foreach (var r in ranges)
            for (int c = r[0]; c <= r[1]; c++) {
                string a = ((char)c).ToString();
                int n = LCMapStringEx("zh-CN", flag, a, 1, buf, buf.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (n != 1 || buf[0] == a[0]) continue;   // 不变的、换成不止一个字的（没有这种）都不进表
                sb.Append(a).Append(buf[0]);
            }
        return sb.ToString();
    }
}
"@
$t2s = [ZhGen]::Pairs(0x02000000)   # LCMAP_SIMPLIFIED_CHINESE
$s2t = [ZhGen]::Pairs(0x04000000)   # LCMAP_TRADITIONAL_CHINESE
$out = Join-Path $PSScriptRoot "..\packages\weather-web\zh.js"
$js = @"
// 简繁转换（tools/gen-zh.ps1 生成，别手改）：同 Windows 版 WeatherService.ZhMap，那边用系统自带的 LCMapStringEx 逐字转换，
// 这里是从它导出的逐字对照表，转换结果一字不差。地名库给的中文名简繁混着（"武汉市 · 江岸區"、外国城市常只有繁体名），
// 搜城市要简繁各查一遍、显示时统一成简体。两处共用：
//   · 设置窗口搜城市（settings/geo.js 导入，见 settings/CMakeLists.txt）
//   · 壁纸按 IP 定位后反查的地名（main.qml 在 host.js 之前注入，这里的 wwpZh 是页面上的全局变量）
// 不要加 .pragma library：网页里也要跑。
var wwpZh = (function () {
  'use strict';
  // 两个字一对：前一个字换成后一个字
  var T2S = "$t2s";
  var S2T = "$s2t";
  var maps = null;
  function table(pairs) {
    var m = {};
    for (var i = 0; i + 1 < pairs.length; i += 2) m[pairs[i]] = pairs[i + 1];
    return m;
  }
  // 同 WeatherService.IsCjk
  function isCjk(c) {
    var n = c.charCodeAt(0);
    return (n >= 0x4E00 && n <= 0x9FFF) || (n >= 0x3400 && n <= 0x4DBF) || (n >= 0xF900 && n <= 0xFAFF) || (n >= 0x3040 && n <= 0x30FF);
  }
  function hasCjk(s) {
    for (var i = 0; i < s.length; i++) if (isCjk(s[i])) return true;
    return false;
  }
  function map(s, toTraditional) {
    s = s == null ? '' : String(s);
    if (!hasCjk(s)) return s;
    if (!maps) maps = { s: table(T2S), t: table(S2T) };
    var m = toTraditional ? maps.t : maps.s, r = '';
    for (var i = 0; i < s.length; i++) r += m[s[i]] || s[i];
    return r;
  }
  return {
    isCjk: isCjk,
    hasCjk: hasCjk,
    simplified: function (s) { return map(s, false); },
    traditional: function (s) { return map(s, true); }
  };
})();
"@
[IO.File]::WriteAllText($out, $js.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
"wrote $out  (t2s $($t2s.Length / 2), s2t $($s2t.Length / 2))"
