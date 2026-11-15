using System;
using System.Drawing;

namespace WeatherWallpaper
{
    /// <summary>
    /// 把窗口嵌入到桌面图标下方。兼容两种桌面结构：
    ///  - 经典（Win10 / Win11 23H2 及更早）：0x052C 后 SHELLDLL_DefView 移到一个顶层 WorkerW，壁纸作为其后面那个 WorkerW 的子窗口。
    ///  - 24H2+（"raised desktop"）：WorkerW 与 DefView 都是 Progman 的子窗口，壁纸作为 Progman 的子窗口插在 DefView 与 WorkerW 之间。
    /// </summary>
    static class DesktopHost
    {
        public static IntPtr Progman, WorkerW, DefView, Parent;
        public static bool Raised;

        public static bool Prepare()
        {
            Progman = Native.FindWindow("Progman", null);
            if (Progman == IntPtr.Zero) return false;
            IntPtr r;
            Native.SendMessageTimeout(Progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0, 1000, out r);
            Native.SendMessageTimeout(Progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out r);

            WorkerW = Native.FindWindowEx(Progman, IntPtr.Zero, "WorkerW", null);
            DefView = Native.FindWindowEx(Progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            Raised = WorkerW != IntPtr.Zero;
            if (Raised)
            {
                Parent = Progman;
            }
            else
            {
                IntPtr found = IntPtr.Zero;
                Native.EnumWindows((top, l) =>
                {
                    var dv = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (dv != IntPtr.Zero)
                    {
                        DefView = dv;
                        found = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                    }
                    return true;
                }, IntPtr.Zero);
                WorkerW = found;
                Parent = found != IntPtr.Zero ? found : Progman;
            }
            Log.Write($"DesktopHost: progman={Progman:X} workerw={WorkerW:X} defview={DefView:X} raised={Raised} parent={Parent:X}");
            return Parent != IntPtr.Zero;
        }

        public static void Attach(IntPtr h, Rectangle screenBounds, bool layered)
        {
            long style = Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64();
            style = (style & ~(long)Native.WS_POPUP) | Native.WS_CHILD | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS;
            Native.SetWindowLongPtr(h, Native.GWL_STYLE, new IntPtr(style));
            if (layered)
            {
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64() | Native.WS_EX_LAYERED;
                Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex));
                Native.SetLayeredWindowAttributes(h, 0, 255, 2 /*LWA_ALPHA*/);
            }
            Native.SetParent(h, Parent);

            var pt = new Native.POINT(screenBounds.X, screenBounds.Y);
            Native.MapWindowPoints(IntPtr.Zero, Parent, ref pt, 1);
            Native.SetWindowPos(h, IntPtr.Zero, pt.X, pt.Y, screenBounds.Width, screenBounds.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            FixZOrder(h);
        }

        public static void FixZOrder(IntPtr h)
        {
            if (!Raised) return;
            const uint f = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
            if (DefView != IntPtr.Zero) Native.SetWindowPos(h, DefView, 0, 0, 0, 0, f);   // 紧贴在图标层下面
            if (WorkerW != IntPtr.Zero) Native.SetWindowPos(WorkerW, h, 0, 0, 0, 0, f);   // 系统壁纸层压到我们下面
        }

        public static bool StillValid()
        {
            return Parent != IntPtr.Zero && Native.IsWindow(Parent) && Native.IsWindow(Progman);
        }

        /// <summary>退出时让系统重绘原壁纸。</summary>
        public static void RestoreSystemWallpaper()
        {
            try
            {
                var path = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;
                Native.SystemParametersInfo(0x0014 /*SPI_SETDESKWALLPAPER*/, 0, path ?? "", 0);
            }
            catch { }
        }
    }
}
