using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WeatherWallpaper
{
    /// <summary>系统负载 / 供电状态采样，带平滑和回差，给壁纸自动降帧用。</summary>
    class PerfMonitor
    {
        [DllImport("kernel32")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        long lastIdle, lastTotal;
        public double Cpu { get; private set; }      // 平滑后的系统 CPU 占用 0..1
        public int MemLoad { get; private set; }     // 物理内存占用 %
        public bool Busy { get; private set; }       // 系统繁忙（带回差）
        public bool OnBattery { get; private set; }

        public void Sample()
        {
            long idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                long total = kernel + user;
                if (lastTotal != 0 && total > lastTotal)
                {
                    double cur = 1 - (double)(idle - lastIdle) / (total - lastTotal);
                    Cpu = Cpu * 0.85 + Math.Max(0, Math.Min(1, cur)) * 0.15;
                }
                lastIdle = idle; lastTotal = total;
            }
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref m)) MemLoad = (int)m.dwMemoryLoad;

            if (!Busy && (Cpu > 0.80 || MemLoad >= 92)) Busy = true;
            else if (Busy && Cpu < 0.60 && MemLoad < 88) Busy = false;

            try { OnBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline; } catch { }
        }
    }
}
