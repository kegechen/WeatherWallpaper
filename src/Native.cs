using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WeatherWallpaper
{
    static class Native
    {
        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        public const uint WS_CHILD = 0x40000000, WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000,
            WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
        public const uint WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8,
            WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
            SWP_SHOWWINDOW = 0x40;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int CX, CY; public SIZE(int x, int y) { CX = x; CY = y; } }
        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA { public uint cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
        }

        public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

        [DllImport("user32")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32")] public static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
        [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
        [DllImport("user32")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32")] public static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT pt, int count);
        [DllImport("user32")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        public const uint GW_HWNDPREV = 3, GW_HWNDNEXT = 2;
        public const uint WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
        [DllImport("user32")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint p, string v, uint flags);
        [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
        [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT val, int size);
        [DllImport("shell32")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
        [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int L, R, T, B; }
        [DllImport("dwmapi")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
        [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32")] public static extern bool AllowSetForegroundWindow(int pid);

        [DllImport("user32")] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, ref POINT dst, ref SIZE size,
            IntPtr hdcSrc, ref POINT src, uint key, ref BLENDFUNCTION blend, uint flags);
        [DllImport("user32")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("user32")] public static extern bool DestroyIcon(IntPtr h);

        [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize, dwTime; }
        [DllImport("user32")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO p);

        /// <summary>距上次键盘 / 鼠标输入的秒数。</summary>
        public static double IdleSeconds()
        {
            var li = new LASTINPUTINFO { cbSize = 8 };
            return GetLastInputInfo(ref li) ? unchecked((uint)Environment.TickCount - li.dwTime) / 1000.0 : 0;
        }

        public static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static bool IsCloaked(IntPtr h)
        {
            int v;
            return DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out v, 4) == 0 && v != 0;
        }

        public static System.Drawing.Rectangle VisibleRect(IntPtr h)
        {
            RECT r;
            if (DwmGetWindowAttribute(h, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out r, Marshal.SizeOf(typeof(RECT))) != 0)
                GetWindowRect(h, out r);
            return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
    }
}
