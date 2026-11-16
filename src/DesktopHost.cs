using System;
using System.Collections.Generic;
using System.Drawing;

namespace WeatherWallpaper
{
    /// <summary>Watch() 周期体检的结果。</summary>
    public enum DesktopState { Ok, Repaired, Invalid }

    /// <summary>
    /// 把窗口嵌入到桌面图标下方。兼容两种桌面结构：
    ///  - 经典（Win10 / Win11 23H2 及更早）：0x052C 后 SHELLDLL_DefView 移到一个顶层 WorkerW，壁纸作为其后面那个 WorkerW 的子窗口。
    ///  - 24H2+（"raised desktop"）：WorkerW 与 DefView 都是 Progman 的子窗口，壁纸作为 Progman 的子窗口插在 DefView 与 WorkerW 之间。
    /// 注意：系统会在锁屏 / 息屏 / 显示器变化后悄悄重建 WorkerW / DefView（没有任何广播，日志里可见句柄一直在换）。
    /// 新的 WorkerW 会压到壁纸上面，表现为解锁后壁纸"消失"、变回系统壁纸。CheckWalls 负责发现并修复。
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
            if (!Refresh()) return false;
            Log.Write($"DesktopHost: progman={Hex(Progman)} workerw={Hex(WorkerW)} defview={Hex(DefView)} raised={Raised} parent={Hex(Parent)}");
            return true;
        }

        /// <summary>重新探测当前桌面结构。只枚举窗口、不发送消息，锁屏期间调用也安全。</summary>
        public static bool Refresh()
        {
            var progman = Native.FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return false;
            Progman = progman;
            WorkerW = Native.FindWindowEx(Progman, IntPtr.Zero, "WorkerW", null);
            DefView = Native.FindWindowEx(Progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            Raised = WorkerW != IntPtr.Zero;
            if (Raised) { Parent = Progman; return true; }

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

        /// <summary>
        /// Watch 每 0.7 秒调一次的体检。
        /// 返回 Ok = 一切正常；Repaired = 系统重建 / 挪动过桌面层，已重新压好层级（壁纸窗口与 WebView 都没动）；
        /// Invalid = 壁纸窗口或桌面结构变了，需要整体重建。
        /// </summary>
        public static DesktopState CheckWalls(IntPtr[] walls)
        {
            if (walls == null || walls.Length == 0) return DesktopState.Ok;
            if (Progman == IntPtr.Zero || Parent == IntPtr.Zero || !Native.IsWindow(Progman) || !Native.IsWindow(Parent))
                return DesktopState.Invalid;
            foreach (var h in walls)
                if (h == IntPtr.Zero || !Native.IsWindow(h) || Native.GetParent(h) != Parent)
                    return DesktopState.Invalid;

            var oldParent = Parent; var oldRaised = Raised; var oldWorkerW = WorkerW; var oldDefView = DefView;
            if (!Refresh()) return DesktopState.Invalid;
            if (Parent != oldParent || Raised != oldRaised) return DesktopState.Invalid;   // 桌面结构变了：重建

            if (WorkerW == oldWorkerW && DefView == oldDefView && (!Raised || SandwichOk(walls)))
                return DesktopState.Ok;

            // WorkerW / DefView 被系统重建（句柄变了）或层级被挪过：重新压回图标层与系统壁纸层之间
            foreach (var h in walls) FixZOrder(h);
            Log.Write($"desktop layers changed (workerw {Hex(oldWorkerW)}->{Hex(WorkerW)} defview {Hex(oldDefView)}->{Hex(DefView)}), z-order restored");
            return DesktopState.Repaired;
        }

        /// <summary>IntPtr 的 "X" 格式在 .NET Framework 上会被忽略、输出十进制，排障时容易看错，统一走这里。</summary>
        static string Hex(IntPtr h) => "0x" + h.ToInt64().ToString("X");

        /// <summary>raised 结构的层级不变式：所有壁纸窗口都位于 DefView（图标层）之下、WorkerW（系统壁纸层）之上。</summary>
        static bool SandwichOk(IntPtr[] walls)
        {
            if (DefView == IntPtr.Zero) return false;
            var remaining = new HashSet<IntPtr>(walls);
            bool sawWorker = false;
            for (IntPtr p = DefView; p != IntPtr.Zero; p = Native.GetWindow(p, Native.GW_HWNDNEXT))
            {
                if (p == WorkerW) sawWorker = true;
                else if (remaining.Remove(p) && sawWorker) return false;   // 壁纸被压到系统壁纸下面了
            }
            return remaining.Count == 0;   // 有壁纸不在 DefView 之下（盖住了图标）也算坏
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
