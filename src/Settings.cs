using System;
using System.IO;
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
        public string Monitors { get; set; } = "all";  // all / primary / 显示器 DeviceName
        public string Layered { get; set; } = "auto"; // auto / on / off：24H2 桌面结构下是否给壁纸窗口加 WS_EX_LAYERED
        public bool DevTools { get; set; }

        public static string AppDataDir => Dir(Environment.SpecialFolder.ApplicationData);
        public static string LocalDir => Dir(Environment.SpecialFolder.LocalApplicationData);
        static string Dir(Environment.SpecialFolder f)
        {
            var d = Path.Combine(Environment.GetFolderPath(f), "WeatherWallpaper");
            Directory.CreateDirectory(d);
            return d;
        }
        static string FilePath => Path.Combine(AppDataDir, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch (Exception ex) { Log.Write("settings load: " + ex.Message); }
            return new Settings();
        }

        public void Save()
        {
            try { File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this)); }
            catch (Exception ex) { Log.Write("settings save: " + ex.Message); }
        }
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
