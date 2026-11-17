using System;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace WeatherWallpaper
{
    /// <summary>托盘面板（FlyoutWindow）和卡片设置、关于（PageWindow）共用的 WebView2 引导配置：安全设置、虚拟主机映射只写这一处。</summary>
    static class WebHost
    {
        public static void Configure(CoreWebView2 wv)
        {
            wv.Settings.AreDefaultContextMenusEnabled = false;
            wv.Settings.IsZoomControlEnabled = false;
            wv.Settings.IsStatusBarEnabled = false;
            wv.Settings.AreBrowserAcceleratorKeysEnabled = false;
            wv.SetVirtualHostNameToFolderMapping("wallpaper.local", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Allow);
        }
    }
}
