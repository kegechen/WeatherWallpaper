using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WeatherWallpaper
{
    /// <summary>
    /// 极简模式的底图：我们的窗口盖在系统壁纸上面，平时要由页面自己显示用户原来的壁纸。
    /// 按每块屏的壁纸和契合方式（填充 / 适应 / 拉伸 / 居中 / 平铺 / 跨区）预先缩放成该屏分辨率的 JPEG，页面直接铺满。
    /// 原图可能是 4K 甚至更大，交给浏览器解码要占上百 MB；缩成屏幕大小只要几 MB。结果按内容指纹缓存，壁纸没变就不重新生成。
    /// </summary>
    static class Backdrop
    {
        public const string Host = "backdrop.local";
        public static string Dir { get { var d = Path.Combine(Settings.LocalDir, "backdrop"); Directory.CreateDirectory(d); return d; } }

        public class Info
        {
            public string Path;          // 壁纸图片；null = 纯色背景
            public int Pos = 4;          // DESKTOP_WALLPAPER_POSITION：0 居中 1 平铺 2 拉伸 3 适应 4 填充 5 跨区
            public Color Color = Color.Black;
            public Rectangle Screen, Virtual;
            public string Key, File;     // 内容指纹 / 输出文件名
            public string Css => "#" + Color.R.ToString("x2") + Color.G.ToString("x2") + Color.B.ToString("x2");
        }

        static string Transcoded => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");

        /// <summary>读取某块屏当前的壁纸设置（在 UI 线程调用：IDesktopWallpaper 是 STA 对象）。只读设置和文件信息，很轻。</summary>
        public static Info Query(Rectangle screen)
        {
            var info = new Info { Screen = screen, Virtual = SystemInformation.VirtualScreen };
            string path = null;
            try
            {
                var dw = (IDesktopWallpaper)new DesktopWallpaperClass();
                try
                {
                    uint n = dw.GetMonitorDevicePathCount();
                    for (uint i = 0; i < n; i++)
                    {
                        string id = dw.GetMonitorDevicePathAt(i);
                        Native.RECT r;
                        try { r = dw.GetMonitorRECT(id); } catch { continue; }   // 已断开的显示器会报错
                        if (Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) == screen) { path = dw.GetWallpaper(id); break; }
                    }
                    info.Pos = dw.GetPosition();
                    uint c = dw.GetBackgroundColor();
                    info.Color = Color.FromArgb((int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF));
                }
                finally { Marshal.ReleaseComObject(dw); }
            }
            catch (Exception ex) { Log.Write("wallpaper query: " + ex.Message); }

            var reg = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;
            if (string.IsNullOrEmpty(path)) path = reg;
            // 纯色背景：注册表里的壁纸路径为空
            if (string.IsNullOrEmpty(path)) info.Path = null;
            // 聚焦（Spotlight）、原图被删等拿不到原图时，用系统转码后的当前壁纸
            else if (System.IO.File.Exists(path)) info.Path = path;
            else info.Path = System.IO.File.Exists(Transcoded) ? Transcoded : null;

            string stamp = "";
            if (info.Path != null)
                try { var fi = new FileInfo(info.Path); stamp = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks; } catch { }
            // 注意别用 string.Join(string, params object[])：第一个元素为 null（纯色背景）时它直接返回空串
            info.Key = $"{info.Path}|{stamp}|{info.Pos}|{info.Css}|{screen}|{(info.Pos == 5 ? info.Virtual.ToString() : "")}";
            using (var md5 = System.Security.Cryptography.MD5.Create())
                info.File = "w" + BitConverter.ToString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(info.Key)), 0, 6).Replace("-", "").ToLowerInvariant() + ".jpg";
            return info;
        }

        static readonly object RenderLock = new object();

        /// <summary>
        /// 生成该屏的底图（已有同指纹的文件就直接复用），返回文件名；纯色背景返回 null。可在后台线程调用。
        /// 有壁纸却读不出来（比如刚换图时文件还被系统占着）会抛异常，由调用方稍后重试，不会被当成纯色背景。
        /// </summary>
        public static string Render(Info w)
        {
            if (w.Path == null) return null;
            // 串行化：同一张图被两次更新同时请求时，第二次直接复用第一次的结果
            lock (RenderLock) return RenderLocked(w);
        }

        static string RenderLocked(Info w)
        {
            var file = System.IO.Path.Combine(Dir, w.File);
            if (System.IO.File.Exists(file)) return w.File;
            Image img;
            try { img = Load(w.Path); }
            catch (Exception ex)
            {
                // HEIC / WebP 等 GDI+ 不认识的格式：退回系统转码后的 JPEG
                if (w.Path == Transcoded || !System.IO.File.Exists(Transcoded)) throw;
                Log.Write("wallpaper load: " + ex.Message + "，改用系统转码的壁纸");
                img = Load(Transcoded);
            }
            try
            {
                int W = w.Screen.Width, H = w.Screen.Height;
                using (var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(w.Color);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        float iw = img.Width, ih = img.Height, s;
                        RectangleF dst;
                        switch (w.Pos)
                        {
                            case 0: dst = new RectangleF((W - iw) / 2, (H - ih) / 2, iw, ih); break;                 // 居中：原尺寸
                            case 1:                                                                                   // 平铺：按像素原尺寸（TextureBrush 会按图片 DPI 缩放，先统一成画布的 DPI）
                                using (var tile = new Bitmap(img))
                                {
                                    tile.SetResolution(bmp.HorizontalResolution, bmp.VerticalResolution);
                                    using (var tb = new TextureBrush(tile)) g.FillRectangle(tb, 0, 0, W, H);
                                }
                                dst = RectangleF.Empty; break;
                            case 2: dst = new RectangleF(0, 0, W, H); break;                                          // 拉伸
                            case 3: s = Math.Min(W / iw, H / ih); dst = new RectangleF((W - iw * s) / 2, (H - ih * s) / 2, iw * s, ih * s); break;   // 适应
                            case 5:                                                                                   // 跨区：整张铺满所有屏的外接矩形，各屏取自己那块
                                var v = w.Virtual;
                                s = Math.Max(v.Width / iw, v.Height / ih);
                                dst = new RectangleF(v.X - w.Screen.X + (v.Width - iw * s) / 2, v.Y - w.Screen.Y + (v.Height - ih * s) / 2, iw * s, ih * s); break;
                            default: s = Math.Max(W / iw, H / ih); dst = new RectangleF((W - iw * s) / 2, (H - ih * s) / 2, iw * s, ih * s); break;   // 填充
                        }
                        if (!dst.IsEmpty)
                            using (var ia = new ImageAttributes())
                            {
                                ia.SetWrapMode(WrapMode.TileFlipXY);   // 避免缩放时边缘一像素发灰
                                g.DrawImage(img, Rectangle.Round(dst), 0, 0, iw, ih, GraphicsUnit.Pixel, ia);
                            }
                    }
                    var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    using (var ep = new EncoderParameters(1))
                    {
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                        var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        bmp.Save(tmp, jpeg, ep);
                        try { System.IO.File.Move(tmp, file); }
                        catch (IOException) when (System.IO.File.Exists(file)) { System.IO.File.Delete(tmp); }
                    }
                }
                return w.File;
            }
            finally { img.Dispose(); }
        }

        /// <summary>读入图片并按 EXIF 方向转正（手机竖拍的照片）。先整个读进内存，不锁住用户的壁纸文件。</summary>
        static Image Load(string path)
        {
            var img = Image.FromStream(new MemoryStream(System.IO.File.ReadAllBytes(path)));
            if (Array.IndexOf(img.PropertyIdList, 0x0112) >= 0)
            {
                var flip = new[] { RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate180FlipNone,
                    RotateFlipType.Rotate180FlipX, RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate270FlipX, RotateFlipType.Rotate270FlipNone };
                int o = img.GetPropertyItem(0x0112).Value[0];
                if (o > 1 && o < flip.Length) img.RotateFlip(flip[o]);
            }
            return img;
        }

        /// <summary>删掉不再使用的旧底图（正在写的临时文件不动，只清理崩溃残留的旧临时文件）。</summary>
        public static void Clean(IEnumerable<string> keep)
        {
            var set = new HashSet<string>(keep.Where(k => k != null), StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.GetFiles(Dir))
                {
                    if (set.Contains(System.IO.Path.GetFileName(f))) continue;
                    if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(f)).TotalMinutes < 10) continue;
                    try { System.IO.File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 定时检查用的廉价变化探测：只读注册表和主题目录里转码壁纸的时间戳，不走跨进程的 IDesktopWallpaper，
        /// Explorer 卡住时也不会拖住 UI 线程。探测结果变了才去完整查询。
        /// </summary>
        public static string Probe()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                    if (k != null)
                    {
                        foreach (var n in new[] { "WallPaper", "WallpaperStyle", "TileWallpaper" }) sb.Append(k.GetValue(n)).Append('|');
                        // 聚焦（Spotlight）会原地替换同名图片
                        if (k.GetValue("WallPaper") is string wp && wp != "" && System.IO.File.Exists(wp))
                        { var fi = new FileInfo(wp); sb.Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append('|'); }
                    }
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors"))
                    if (k != null) sb.Append(k.GetValue("Background")).Append('|');
                var dir = System.IO.Path.GetDirectoryName(Transcoded);
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "Transcoded*").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        var fi = new FileInfo(f);
                        sb.Append(fi.Name).Append(':').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append('|');
                    }
            }
            catch (Exception ex) { sb.Append("err:").Append(ex.Message); }
            return sb.ToString();
        }

        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
            [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
            uint GetMonitorDevicePathCount();
            Native.RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
            void SetBackgroundColor(uint color);
            uint GetBackgroundColor();
            void SetPosition(int position);
            int GetPosition();
        }

        [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
        class DesktopWallpaperClass { }
    }
}
