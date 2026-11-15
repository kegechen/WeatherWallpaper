# 天气壁纸 Weather Wallpaper

Windows 11 动态天气桌面：按当前定位和当地时间实时渲染天空，像手机天气一样在右上角显示半透明天气卡片。

## 效果

- **天空**：太阳的位置和颜色按当地日出日落实时计算（晨昏的橙红、正午的白亮），夜晚有月相和星空。
- **天气**：
  - 晴、多云、阴、雾、毛毛雨、中雨、暴雨、雷阵雨（带闪电）、雪。
  - 云量、风向、风速都来自实时数据。
- **天气卡片**：
  - 城市、温度、最高/最低、体感、湿度、风力。
  - 空气质量（按中国 AQI 标准计算）。
  - 逐小时预报和 5 天预报。
- **任务栏**：下雨时雨点砸在任务栏上沿，溅起水花、留下涟漪，任务栏表面还有水珠；下雪时任务栏上积雪。配色跟随系统深色/浅色主题。
- **窗口屋檐**：大小适中的普通窗口顶边也会被雨打出水花、积起薄雪。最大化和全屏的窗口不算。拖动或最小化窗口时，积雪会被抖掉。
- **多显示器**：可选全部显示器、仅主显示器或某一块显示器。

## 性能

- 桌面被窗口挡住时自动降帧：露出的部分少于 35% 时降到 15 FPS，几乎全被挡住时暂停。
- 锁屏、全屏游戏或全屏视频时暂停。
- 用电池或系统繁忙（CPU > 80% 或内存 ≥ 92%）时自动降帧。
- 背景层最多 15 FPS，降水层只在有雨雪时才绘制。

## 安装

从 [Releases](../../releases) 下载：

- `WeatherWallpaper-x.y.z-x64.msi`：安装包。按当前用户安装，不需要管理员权限，装完自动启动。
- `WeatherWallpaper-x.y.z-x64-portable.zip`：便携版。解压后运行 `WeatherWallpaper.exe` 即可。

需要 Windows 10/11 x64 和 WebView2 运行时（Windows 11 自带）。

## 使用

所有操作都在托盘图标的右键菜单里：

- 刷新天气、设置城市或使用自动定位。
- 效果预览：可以选任意天气；「一天快进演示」会按所选天气用 48 秒走完一天。预览 1 分钟后自动恢复实时天气。
- 任务栏效果、窗口屋檐、天气卡片、显示在哪块屏、60 FPS、开机自启。

命令行（转发给运行中的实例）：

```
WeatherWallpaper.exe preview heavy        # 预览暴雨
WeatherWallpaper.exe preview clear day    # 晴天的一天快进
WeatherWallpaper.exe preview live         # 恢复实时
WeatherWallpaper.exe refresh | quit
```

## 数据来源

- 定位：Windows 定位服务，失败时用 IP 定位（ip-api.com）。
- 地名：BigDataCloud 反向地理编码。
- 天气和空气质量：[Open-Meteo](https://open-meteo.com/)。

## 构建

需要 Visual Studio 2022（用它自带的 Roslyn `csc`）和 .NET Framework 4.8。

```powershell
.\build.ps1                         # -> bin\WeatherWallpaper.exe
.\build.ps1 -Version 1.2.3 -Msi     # 另外生成 dist\ 下的 MSI 和 zip（需要：dotnet tool install -g wix --version 5.0.2）
```

发布一个 GitHub Release（tag 形如 `v1.2.3`）后，Actions 会自动构建 MSI 和便携版 zip，并上传到这个 Release。
