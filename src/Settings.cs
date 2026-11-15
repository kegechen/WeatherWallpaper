using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace WeatherWallpaper
{
    public class Settings
    {
        public string City { get; set; }          // 手动城市显示名（为空 = 自动定位）
        public double? Lat { get; set; }
        public double? Lon { get; set; }
        public int Fps { get; set; } = 30;
        public bool TaskbarFx { get; set; } = true;
        public bool ShowPanel { get; set; } = true;
        public bool WindowFx { get; set; } = true;     // 窗口顶边"屋檐"雨滴 / 积雪
        public string PanelPos { get; set; } = "tr";   // 桌面卡片位置：tr 右上 / tl 左上 / tc 顶部居中 / br 右下 / bl 左下
        public int PanelMarginX { get; set; } = 28;    // 卡片离屏幕左右边缘的距离（CSS 像素）
        public int PanelMarginY { get; set; } = 28;    // 卡片离屏幕上下边缘（底部时为离任务栏）的距离
        public string Monitors { get; set; } = "all";  // all / primary / 显示器 DeviceName
        public string Layered { get; set; } = "auto"; // auto / on / off：24H2 桌面结构下是否给壁纸窗口加 WS_EX_LAYERED
        public bool DevTools { get; set; }
        public bool LowMemory { get; set; }            // 省内存模式：背景半分辨率 + 渐变缓存 + 颜色量化（默认关，画面略软）
        public bool AutoUpdate { get; set; } = true;   // 启动后及每天自动检查更新
        public string SkipVersion { get; set; }        // 用户选择"跳过此版本"的版本号

        public static string AppDataDir => Dir(Environment.SpecialFolder.ApplicationData);
        public static string LocalDir => Dir(Environment.SpecialFolder.LocalApplicationData);
        static string Dir(Environment.SpecialFolder f)
        {
            var d = Path.Combine(Environment.GetFolderPath(f), "WeatherWallpaper");
            Directory.CreateDirectory(d);
            return d;
        }
        public static string FilePath => Path.Combine(AppDataDir, "settings.json");

        static PropertyInfo[] Props => typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        public static Settings Load() => TryLoad() ?? new Settings();

        /// <summary>读取失败（如手动编辑写错了 JSON）返回 null；文件不存在返回默认值。</summary>
        public static Settings TryLoad()
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (!File.Exists(FilePath)) return new Settings();
                    return new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                }
                catch (IOException) { System.Threading.Thread.Sleep(100); }   // 编辑器还在写
                catch (Exception ex) { Log.Write("settings load: " + ex.Message); return null; }
            }
            return null;
        }

        /// <summary>写进 settings.json 的字段说明（JSON 不支持注释，用 "// 字段名" 键代替，读取时会被忽略）。</summary>
        static Dictionary<string, string> Help() => new Dictionary<string, string>
        {
            ["City"] = "手动设置的城市名；City / Lat / Lon 都为 null = 自动定位（建议用托盘菜单「设置城市」修改）",
            ["Lat"] = "纬度，如 30.59",
            ["Lon"] = "经度，如 114.30",
            ["Fps"] = "帧率：30 或 60",
            ["TaskbarFx"] = "任务栏雨雪效果：true 开 / false 关",
            ["ShowPanel"] = "桌面天气卡片：true 显示 / false 隐藏（隐藏后可左键点托盘图标查看）",
            ["WindowFx"] = "窗口顶边雨滴 / 积雪（屋檐）：true / false",
            ["PanelPos"] = "卡片位置：tr 右上(top right) / tl 左上(top left) / tc 顶部居中(top center) / br 右下(bottom right) / bl 左下(bottom left)",
            ["PanelMarginX"] = "卡片离屏幕左右边缘的距离，单位像素（按 100% 缩放计），如 28",
            ["PanelMarginY"] = "卡片离屏幕上边缘（底部位置时为离任务栏）的距离，单位像素，如 28",
            ["Monitors"] = "显示在哪块屏：all 全部 / primary 主屏 / 指定某块屏：" +
                string.Join("、", System.Windows.Forms.Screen.AllScreens.Select(s => s.DeviceName + (s.Primary ? "（主屏）" : ""))),
            ["Layered"] = "兼容选项，一般不用改：auto / on / off",
            ["DevTools"] = "开发调试用：true 允许打开 WebView2 开发者工具",
            ["LowMemory"] = "省内存模式：true 开 / false 关。开启后约少 40–50 MB，代价是背景画面略软、渐变可能有极细微色带",
            ["AutoUpdate"] = "自动检查更新：true / false",
            ["SkipVersion"] = "已选择跳过的版本号，null = 不跳过",
        };

        /// <summary>每项一行写出，前面附一行说明，方便用户手动编辑。</summary>
        public void Save()
        {
            try
            {
                var js = new JavaScriptSerializer();
                var help = Help();
                var lines = Props.Select(p =>
                    (help.ContainsKey(p.Name) ? "  " + js.Serialize("// " + p.Name) + ": " + js.Serialize(help[p.Name]) + ",\r\n" : "") +
                    "  \"" + p.Name + "\": " + js.Serialize(p.GetValue(this)));
                File.WriteAllText(FilePath, "{\r\n" + string.Join(",\r\n", lines) + "\r\n}\r\n");
            }
            catch (Exception ex) { Log.Write("settings save: " + ex.Message); }
        }

        public bool Same(Settings o) => Props.All(p => Equals(p.GetValue(this), p.GetValue(o)));
        public void CopyFrom(Settings o) { foreach (var p in Props) p.SetValue(this, p.GetValue(o)); }
    }

    static class Log
    {
        static readonly object Lock = new object();
        public static void Write(string s)
        {
            try
            {
                lock (Lock)
                {
                    var p = Path.Combine(Settings.LocalDir, "log.txt");
                    if (File.Exists(p) && new FileInfo(p).Length > 512 * 1024) File.Delete(p);
                    File.AppendAllText(p, DateTime.Now.ToString("MM-dd HH:mm:ss ") + s + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
