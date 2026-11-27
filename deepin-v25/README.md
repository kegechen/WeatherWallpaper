# 天气壁纸 · deepin 25（treeland）版

本目录（`deepin-v25/`）是 deepin 25 专用的：依赖 treeland 0.11 的壁纸扩展协议和 deepin 25 的控制中心，别的发行版用不了。

在 deepin 25 的 treeland 合成器上运行动态壁纸，走 treeland 自带的壁纸扩展机制（`treeland-wallpaper-factory`），
不是在桌面上叠一个窗口，所以桌面图标、任务栏、锁屏都和系统自带的动态壁纸一样。

画面直接用 Windows 版的 `web/`（Qt WebEngine，GPU 合成），宿主换成了注入网页的 `host.js`：
按 IP 定位（或配置的城市）从 Open-Meteo 取实时天气。壁纸叠加、极简模式在 `/etc/wwp/weather.json` 里打开（见下文），
也可以用启动器里的“天气壁纸设置”。控制中心“动态壁纸”里只有一项“天气壁纸”（壁纸包 `weather-web`：天空、云、雨雪、日月 + 天气卡片）。
早期验证链路用的 QML 占位场景 `weather` 和变体包 `weather-overlay` 已删除，重新安装（install.sh 或 deb）时会把旧的一并清掉。

## 原理

1. 控制中心的“动态壁纸”列表只读 `/usr/share/wallpapers/deepin-livewallpapers/metadata.json`，
   选中后通过 `treeland_wallpaper_v1.set_video_source(路径)` 告诉 treeland。treeland 不检查路径内容。
2. treeland 再把路径转发给 `treeland-wallpaper-factory`（按 PATH 查找，`/usr/local/bin` 排在 `/usr/bin` 前面），
   由 factory 为每个路径建一个壁纸 surface。
3. 这里的 factory 是从 treeland 0.11.0 的 `wallpaper-factory/` 改出来的：路径以 `.wwp` 结尾、且是 root 装在
   `/usr/local/share/wallpapers/wwp/` 下的壁纸包时不交给 mpv，而是加载包里的 QML 场景；图片、视频壁纸照旧。

## 目录

| 路径 | 说明 |
|---|---|
| `factory/` | 改过的 factory（`wwpcontroller.*`、`WwpScene.qml`、`main.cpp` 的 `--snapshot` 和 SIGHUP 原地重启是新增，其余同原版） |
| `tools/wwp-set.c` | 命令行查看、切换壁纸（和控制中心走同一个协议） |
| `tools/wwp-reload.sh` | 开发用：换上新编的 factory（SIGHUP 原地重启，不占 treeland 的重启额度），再把壁纸切走又切回 |
| `tools/wwp-unset.sh` | 卸载前把已登录用户正在用的天气壁纸换回系统默认壁纸（装成 `/usr/local/libexec/wwp-unset`） |
| `tools/wwp-watch.sh` + `.service` | 用户服务：treeland 不再拉起壁纸进程时弹通知（装成 `/usr/local/libexec/wwp-watch`），见下文“壁纸停住时的提醒” |
| `packages/<名字>/` | 壁纸包：`manifest.json` + QML 场景 |
| `packages/weather-web/` | 网页版：`main.qml`（`WebEngineView`）+ `host.js`（宿主）+ `web.src`（指向仓库顶层的 `web/`） |
| `settings/` | 设置窗口 `wwp-settings`（Qt Quick，Chameleon/DTK 风格）+ 提权助手 `wwp-config` + polkit 策略 + .desktop + 图标 |
| `deb/` | 打 deb 安装包的脚本、默认配置、版本号，见下 |
| `experiments/webtest/` | 实验：普通窗口里跑 `WebEngineView`，看 WebGL 渲染器、测各天气场景的 CPU |
| `install.sh` | 编译 / 安装 / 登记到控制中心 / 卸载（开发机自用） |

## 编译和安装

依赖：Qt 6.8（Quick、WaylandClient）、Dtk6Core、treeland-protocols、wayland-scanner、libmpv（没有 libmpv-dev 时，
把 deb 解到 `third_party/mpvdev/` 下即可，链接系统里的 `libmpv.so.2`）。
可选 `qt6-webengine-dev` + `qml6-module-qtwebengine`：有的话 factory 会链接 Qt WebEngine，壁纸包的 QML 里就能用 `WebEngineView`
（Chromium 的 GPU 线程跑在 factory 进程里，和 Qt Quick 共享 GL 上下文，L540 上 WebGL 渲染器是 Mali-G78）。
壁纸包目录里放一个 `web.src`（内容是相对路径），build 时会把那个目录拷进包里的 `web/`，网页版就是这样用上仓库顶层的 `web/` 的；
放一个 `base`（内容是另一个包的名字），build 时先拷那个包，再拷本包的文件覆盖上去，变体包只需要一个 `manifest.json`。

```sh
./install.sh build           # 普通用户：编译，并离线渲染每个壁纸包的缩略图和预览图
sudo ./install.sh install    # 装到 /usr/local，登记进控制中心
./deb/build-deb.sh           # 普通用户：把 build/ 产物打成 deb（发给别的 deepin 25 机器装）
sudo dpkg -i build/wwp-weather-wallpaper_*.deb
```

快照是**固定的产品图**：`--snapshot` 时页面不读机器配置（`host.js` 里 SNAPSHOT 分支），永远画一个晴朗多云的
默认场景——打包机上的配置不会漏进发行包的预览图里。

装好后要让 treeland 换用新 factory：`sudo systemctl restart treeland`（会回到锁屏），
或者给旧 factory 发 SIGHUP 后再切换一次壁纸（`tools/wwp-reload.sh` 做的就是这个）。本工程的 factory 收到 SIGHUP
会原地重启（正常收尾后 exec 同一路径上的新程序，进程号不变），treeland 看不到退出，不占它的重启额度（见下文
“壁纸停住时的提醒”）；正在跑的若是 deepin 原版或更早不认 SIGHUP 的版本，SIGHUP 直接结束它，treeland 按 PATH
拉起新的，这一次照常计数。

安装后的布局（脚本和 deb 相同；deb 的文件清单见 `dpkg -c`）：

```
/usr/local/bin/treeland-wallpaper-factory      factory（/usr/bin 有 deepin 原版，靠 PATH 顺序覆盖）
/usr/local/bin/wwp-set                         命令行切换壁纸
/usr/local/bin/wwp-settings                    设置窗口
/usr/local/libexec/wwp-config                  提权助手（root 运行，写配置、存自选壁纸）
/usr/local/libexec/wwp-unset                   卸载前把正在用的天气壁纸换回默认
/usr/local/libexec/wwp-watch                   壁纸停住时弹通知（用户服务跑它）
/usr/lib/systemd/user/wwp-watch.service        上面那个用户服务；graphical-session.target.wants/ 里有它的链接
/usr/local/share/wallpapers/wwp/<名字>/
    manifest.json  main.qml  web/              壁纸包本身
    thumbnail.png                              控制中心列表里的缩略图
    <名字>.wwp                                 登记给控制中心/treeland 的路径；它本身是一张预览 PNG
/usr/share/wallpapers/deepin-livewallpapers/metadata.json   动态壁纸列表（dpkg-divert 原文件在 .deepin）
/usr/share/polkit-1/actions/io.github.wwp.weather.policy    “本机活动会话免密码”的策略
/usr/share/applications/wwp-settings.desktop                启动器入口
/usr/share/icons/hicolor/256x256/apps/wwp-settings.png
/etc/wwp/weather.json                          网页版配置（deb 里是 conffile：卸载保留，purge 才删）
/var/lib/wwp/                                  设置窗口从文件选的自选壁纸（root 只读的 JPEG，dde 读得到）
```

`<名字>.wwp` 做成 PNG 是为了控制中心：它显示“当前壁纸”时按图片读这个路径。factory 看到 `.wwp` 后缀就去同目录读 `manifest.json`。

`metadata.json` 属于 `deepin-wallpapers` 包，安装时用 `dpkg-divert` 把原文件挪到 `metadata.json.deepin`，
再写入“原列表 + 壁纸包”。`deepin-wallpapers` 升级后运行一次 `sudo ./install.sh register` 重新合并。

## 壁纸包格式

`manifest.json`：

| 字段 | 默认 | 说明 |
|---|---|---|
| `main` | `main.qml` | 场景文件 |
| `fps` | 24 | 场景时钟的刷新率（1–60） |
| `pausePolicy` | `follow` | `follow`：像视频壁纸一样，pause 定格、slow_down 减速到停；`ignore`：一直动 |
| `debug` | false | 顶部显示调试角标（fps、最近的 treeland 事件、场景时钟） |
| `snapshotDelay` | 1000 | `--snapshot` 离线渲染时等多少毫秒再截图（网页场景要等页面加载完） |
| `host` | — | 网页版专用：原样传给 `host.js` 的配置，字段见下 |

场景里可以用上下文属性 `wallpaper`：`time`（场景时钟，秒）、`running`、`rate`、`fps`、`packageUrl`、`manifest`、`lastEvent`，
以及 `wallpaper.log(文字)`（以 info 级写进 journal；场景里的 `console.log` 是 debug 级，会被 Dtk 的日志规则关掉）。
QML 场景的动画一律由 `wallpaper.time` 驱动，不要用 QML 自带的 Animation，这样帧率和暂停都由 factory 统一控制。

## 网页版的宿主和配置

Windows 版里 C# 宿主做的事（定位、取天气、发布局和底图、暂停恢复），这里由 `host.js` 在网页里做：
`main.qml` 用 `WebEngineScript` 在文档创建时注入它，它提供 `window.chrome.webview`（`postMessage` / `message` 事件），
所以 `web/` 一行不用改。天气接口和参数同 `WeatherService.cs`；定位没配就走 ip-api.com，再用 BigDataCloud 反查地名。
factory 按路径一个壁纸一个页面：桌面和锁屏用同一个壁纸包时只有一个页面；用不同的天气壁纸包时有两个页面，
它们共用 localStorage，一个取到天气另一个直接用。

配置来源（后者覆盖前者）：壁纸包 `manifest.json` 的 `host`，再是 `/etc/wwp/weather.json`（所有天气壁纸包共用；
factory 以 `dde` 用户运行，读不到用户家目录，所以放在 /etc）。`install.sh install` 在它不存在时写一份默认的：

```json
{
    "city": "",
    "backdrop": "",
    "minimal": false,
    "playSeconds": 30,
    "show": "hilo,src,aqi,feel,hum,wind,hours,days,sun,upd"
}
```

| 字段 | 默认 | 说明 |
|---|---|---|
| `city` | 空 | 城市名。只填它会去 Open-Meteo 地名库查经纬度；和 `lat`/`lon` 一起填则只作显示名 |
| `lat`、`lon` | — | 经纬度。`city`、`lat`、`lon` 都不填就按 IP 定位 |
| `backdrop` | 空 | 底层壁纸：图片的本地路径或 URL，按屏幕物理分辨率裁成铺满。**填了就是壁纸叠加**（垫在天气下面，天空半透明，云、日月、雨雪照常浮在上面）；空 = 不叠加 |
| `minimal` | false | 极简模式：平时只显示底层壁纸 + 卡片，几乎不占 CPU/GPU；拿到第一份天气、转雨 / 暴雨 / 雷雨、下雪时整屏播放一段天气动画再淡出。没填 `backdrop` 时垫 treeland 的默认壁纸（`/usr/share/backgrounds/default_background.jpg`）。和壁纸叠加互斥，两个都开按极简模式 |
| `playSeconds` | 30 | 每次播放天气动画的秒数（5–3600）：极简模式的播放、雨后彩虹共用 |
| `skyOpacity` | 0.45 | 叠加时天空的不透明度（0.05–1） |
| `cloudAlpha` | 0.72 | 叠加时云的不透明度（0.1–1） |
| `fps` | 24 | 页面帧率（5–60），雨雪、闪电按它画（设置窗口里叫“雨雪的帧率”） |
| `cloudFps` | 6 | 云的帧率（1–24；Windows 版固定 12），和 `fps` 各管各的（下雨雪时整页按 `fps` 出帧，云最多也就这么快）。每出一帧的固定开销大，云飘得慢，6 帧和 12 帧看不出差别，CPU 少一截；不下雨雪时页面每秒就出这么多帧 |
| `panel` | true | 显示天气卡片 |
| `cardPos` | `tr` | 卡片贴边位置：`tl/tc/tr`（上左/上中/上右）、`bl/bc/br`（下左/下中/下右）。下排从任务栏上沿起算 |
| `cardX`、`cardY` | 32 | 卡片离参照边的距离（CSS 像素）；`tc`/`bc` 时 `cardX` 是相对屏幕中线的偏移，可为负 |
| `show` | 见下 | 卡片上显示哪些内容，逗号分隔的字符串或数组，键见下表。不填 = 默认的 `hilo,src,aqi,feel,hum,wind,hours,days,sun,upd`（除日期行外都显示）；`""` = 只显示城市和当前天气 |
| `hide` | — | 从 `show`（没填就是默认）里去掉这几项，写法同 `show`。只想关掉一两项时用它省事，如 `"hide": "hours"` |
| `locBy` | — | 设置窗口的记忆字段：指定位置时是“搜索城市”（`search`）还是“输入经纬度”（`coord`），下次打开停在同一页。host.js 不读它 |
| `lastBackdrop` | — | 设置窗口的记忆字段：切回“天气动画”时 `backdrop` 必须清空（它就是叠加的开关），上次选的壁纸路径记在这里，再切回“壁纸叠加”不用重选。host.js 不读它 |
| `cardRight`、`cardTop` | — | 旧版字段，已换成 `cardPos` / `cardX` / `cardY`，写配置时会自动删掉 |
| `lockZoom` | 1.2 | 锁屏界面把壁纸放大的倍数，锁屏时卡片层按它反向缩小；设成 1 则不处理 |
| `ground` | 0.0444 | 底部任务栏占屏幕高度的比例（雨雪落在它上沿） |
| `festive` | true | 节日装饰与彩蛋 |
| `refresh` | 15 | 取天气的间隔（分钟，5–180） |
| `preview` | 空 | 效果预览（同 Windows 版托盘的“效果预览”，录演示视频、调效果用）：`clear` `sunrise` `sunset` `night` `partly` `cloudy` `fog` `drizzle` `rain` `heavy` `thunder` `snow` `rainbow`，节日 `cny` `lantern` `dragon` `midautumn` `national` `newyear` `halloween` `xmas` `easter`；后面加 ` day`（如 `"clear day"`）是按这种天气 48 秒走完一天。一直生效到删掉这一项（或写 `live`） |

`backdrop` 指向的图片要让 `dde` 用户读得到：家目录里的图片读不了，请复制到 `/usr/local/share/wallpapers/` 之类的地方
（系统自带的在 `/usr/share/wallpapers/deepin/`）。读不到时日志里有一行“底层壁纸读不到”，画面退回纯色。

卡片内容（`show` / `hide` 的键，和 Windows 版 `settings.json` 里卡片的 `Show` 一样；城市和当前天气总是显示，卡片高度跟着变）：

| 键 | 内容 | 键 | 内容 |
|---|---|---|---|
| `date` | 公历日期（日期行） | `feel` | 体感温度 |
| `week` | 星期（日期行） | `hum` | 湿度 |
| `lunar` | 农历（日期行） | `wind` | 风向风力 |
| `hilo` | 今日最高 / 最低 | `hours` | 今天逐小时（6 小时） |
| `src` | 定位来源（IP 定位 / 手动） | `days` | 未来几天（含今天共 5 天） |
| `aqi` | 空气质量 | `sun` | 日出日落 |
| | | `upd` | 更新时间 |

例：不要逐小时 `"hide": "hours"`；只留未来几天 `"show": "hilo,days"`；加上日期行 `"show": "date,week,lunar,hilo,src,aqi,feel,hum,wind,hours,days,sun,upd"`。
写错的键会被忽略，日志里有一行“不认识的项”。

**配置热生效**：`host.js` 每 2 秒看一次 `/etc/wwp/weather.json`，改了（设置窗口保存、手改）几秒内直接生效——
换底图、卡片挪位置、切换叠加/极简都不用切壁纸；换了城市会重新取天气。改坏的文件会被忽略（日志一行“先不换”），
改到一半的文件也不会被应用。日志：`journalctl -b -t treeland-wallpaper-factory | grep wwp:`。

极简模式和 Windows 版的差别：Windows 版要等“桌面露出来、有人在操作”才开播，treeland 下 factory 看不到桌面有没有被窗口挡住、
人在不在，只判断锁屏：锁屏时先记下，解锁后确认天气还对得上再播（雨已经停了就不播）。

### 锁屏

treeland 0.11 的锁屏界面（`Greeter.qml`）把壁纸以屏幕中心放大到 1.2 倍再裁掉四周，每边各少 1/12
（2160×1440 的屏左右各少 180 像素、上下各少 120 像素），靠边的卡片会被裁掉。这个倍数写死在 treeland 里，改不了，
所以由页面自己抵消：`main.qml` 按 treeland 发来的事件判断这一页是不是正显示在锁屏上——锁屏是 `pause`（桌面）+ `play`（锁屏），
解锁是 `pause` + `play` + `slow_down`（显示桌面），即 `play` 之后 150 毫秒内没跟着 `slow_down` 就是锁屏；
一个事件都没收到也算锁屏（桌面挂上壁纸时马上会收到 `pause`）。`host.js` 在锁屏时把卡片层（`#cards`）以屏幕中心缩小到 1/1.2，
和 treeland 的放大正好抵消：卡片在锁屏上的位置、大小和桌面上完全一样，天气画面照样放大；解锁后还原，过渡和 treeland 的缩放动画大致同步。
（只把卡片挪进可见区、不缩小的话，放大 1.2 倍的卡片会压到锁屏的头像和密码框。）treeland 以后改了倍数就改 `lockZoom`。

## 设置窗口（天气壁纸设置）

启动器里搜“天气壁纸”（`/usr/share/applications/wwp-settings.desktop`）。窗口是 Qt Quick + DTK QML
（`org.deepin.dtk`）：`D.ApplicationWindow` + DTK 标题栏（主题菜单、最小化 / 关闭；不给最大化，内容最宽 780，拉大只是多留白），控件走系统的 Chameleon 风格，
卡片分组、颜色、圆角取控制中心的样式，跟随系统深浅色和活动色；选壁纸、关窗确认是 DTK 对话框。
要装 `libdtk6declarative` 和 Chameleon 风格（deb 已依赖），没有 DTK 的系统起不来。内容可滚动；保存走 `pkexec` 提权助手
`wwp-config`（root、`python3 -I`），polkit 策略 `io.github.wwp.weather.configure` 允许**本机活动会话免密码**，
别的会话要管理员密码。“应用”后几秒内生效（配置热加载）。

界面按所选功能只显示该功能的设置：

- **位置**：分段按钮“自动定位 / 搜索城市 / 输入经纬度”。自动定位没有别的输入；搜索城市是搜索框 + 结果列表，
  选中后显示当前城市和经纬度；输入经纬度可以直接粘贴“30.59, 114.31”这类坐标，填好后自动反查地名，
  显示名默认跟着地名走，手动改过就不再覆盖。
- **画面**：分段按钮“天气动画 / 壁纸叠加 / 极简模式”，下面一行说明。天气动画不显示底层壁纸；
  壁纸叠加和极简模式显示紧凑的底层壁纸（缩略图 +“更换…”，对话框里是 44 张系统壁纸网格或从文件选择）；
  只有叠加时才有天空、云的不透明度，用滑块调，旁边是实时预览。三种模式都有播放秒数和节日装饰。
- **天气卡片**：开关、屏幕示意图上点选贴边位置、边距和卡片内容；刷新间隔、雨雪的帧率（`fps`）、云的帧率（`cloudFps`）在“其他”里，帧率用滑块调。

安全设计：助手只收配置 + 图片**字节**（JPEG，最大边 4096），不收路径——从文件选的图片先在窗口侧转成 JPEG、
透明处垫色，发给助手存成 `/var/lib/wwp/backdrop-<ns>.jpg`（root 所有、其他用户只读，`dde` 读得到），
替换旧文件。系统里自带的壁纸（`/usr/share/wallpapers/deepin/`）直接引用路径。窗口关了没保存会问一次。
显示的不是天气壁纸时页脚有“设为桌面和锁屏壁纸”按钮（调 `wwp-set`），“还原”放弃未保存的修改。

## 壁纸停住时的提醒

treeland 0.11 的壁纸进程（factory）异常退出（崩溃、被结束）后会马上重新拉起，但**一次 treeland 运行里累计**超过
10 次就不再拉起（日志 `Wallpaper process restart limit reached after 11 abnormal exits`），计数只在 treeland 重启时清零，
中间跑得再稳也不减。到上限之后桌面和锁屏停在最后一帧（云不动、卡片时间不走，锁屏的卡片也没人缩回来），换壁纸也不生效。

用户服务 `wwp-watch.service`（每个图形会话一份）每 30 秒看一次：treeland 在、它下面却没有 factory 子进程，连续两次就弹一条
“天气壁纸停住了”的通知，说明原因和恢复办法；通知上的“重启 treeland”按钮走 systemd 的 polkit 授权（要输管理员密码），
会关掉所有窗口回到锁屏。只在这次 treeland 运行里见过 factory，或者 treeland 日志里查得到“到上限 / 拉不起来”时才提示，
每次 treeland 运行最多一次。`WWP_WATCH_TEST=1 /usr/local/libexec/wwp-watch` 弹一条测试通知（按钮不重启）；
不想要：`systemctl --user mask wwp-watch.service`。随会话启动靠的是 `/usr/lib/systemd/user/graphical-session.target.wants/`
里的链接，`systemctl --user is-enabled` 只认 /etc 和家目录下的链接，所以显示 `disabled`，实际照样启动。

## deb 安装包

```sh
./install.sh build        # 先有 build/ 产物
./deb/build-deb.sh        # 打包（要 dpkg-dev）：build/wwp-weather-wallpaper_<版本>_<架构>.deb
sudo dpkg -i  *.deb       # 装完控制中心 → 个性化 → 壁纸 → 动态壁纸 里就有“天气壁纸”
sudo dpkg -r wwp-weather-wallpaper   # 卸载，见下
```

- **安装**（`postinst`）：做 install.sh 的 `register` 那套（`dpkg-divert` + 合并 `metadata.json`），只登记本包带的壁纸包；
  `/usr/local/share/wallpapers/wwp/` 下不是本包带的旧壁纸包（install.sh 装过的 `weather` 占位场景、`weather-overlay` 等）
  会被删掉（删之前已登录用户正在用它的，先换成本包的 `weather-web`），控制中心里只剩一个“天气壁纸”。`deepin-wallpapers` 升级时（它的新列表落到被 divert 的 `metadata.json.deepin`）
  由 dpkg 触发器（`interest-noawait /usr/share/wallpapers/deepin-livewallpapers`）自动重新合并。
  已登录用户的 `wwp-watch` 服务当场（重新）启动，其他用户下次登录时启动。
- **卸载**（`dpkg -r`）：`prerm` 先调 `wwp-unset`，已登录用户的桌面 / 锁屏正在用天气壁纸的，换回
  `/usr/share/backgrounds/default_background.jpg`（不换的话桌面和控制中心的“当前壁纸”预览会空着），再停掉他们的 `wwp-watch`；
  `postrm` 还原动态壁纸列表，删掉整个 `/usr/local/share/wallpapers/wwp`（包括不归 dpkg 管的残留），控制中心里不再有天气壁纸。
  `/etc/wwp/weather.json`（conffile）和 `/var/lib/wwp` 的自选壁纸保留，`dpkg -P` 才删。没登录的用户 treeland 还记着旧路径，
  下次登录后在控制中心重选一张即可。
- **依赖**：动态库用 `dpkg-shlibdeps` 按二进制直接链接的库算（带版本）；运行时加载的 QML 模块（`qml6-module-*`）、
  Chromium 渲染进程 `QtWebEngineProcess`（`libqt6webenginecore6-bin`，WebEngine 的库包不依赖它）、`python3`、`pkexec`、
  `wwp-watch` 用的 `libnotify-bin`（`notify-send`）和 `procps`（`pgrep`）手写；设置窗口用的 DTK QML（`libdtk6declarative`）、Chameleon 风格、Qt5Compat 图形效果也在其中。架构随打包机（`dpkg --print-architecture`）。
  `treeland` 限定 0.11.x（`>= 0.11~, << 0.12~`）：factory 是从 0.11 改出来的，和它的私有壁纸协议绑定，
  treeland 出新版本时 apt 会先挡住它的升级（或要求先卸掉本包），要拿新版本重新合一遍 factory、发新 deb。
- **版本号**：`deb/version`；环境变量 `DEB_VERSION` 可覆盖（CI 用）；`DEB_MAINTAINER` 默认 GitHub noreply 地址，
  不取本机 git 的邮箱。升级时文件直接覆盖（dpkg 处理 conffile 保留本地改动）；treeland 仍要按上文方式换用新 factory。
- **只认装好的包**：factory 只执行 root 装进 `/usr/local/share/wallpapers/wwp/` 的壁纸包（包目录到 `/` 的每一级、
  `manifest.json`、场景入口文件都只有 root 能改）。别处的 `.wwp`（比如 `/tmp` 里的、`build/pkg/` 里的）按原版逻辑交给
  mpv，显示成它那张预览图：壁纸包是 QML/JS，factory 以 `dde` 用户身份运行它，不能让本机任意用户借 `wwp-set` 跑代码。
  开发时离线看效果用 `--snapshot`（以自己的身份跑，不受这个限制）。

### GitHub Actions

`.github/workflows/deepin-v25-deb.yml` 在官方 deepin 25 容器（`linuxdeepin/deepin:crimson-arm64` / `:crimson`）里原生编译
arm64 和 amd64 两个 deb，步骤就是上面的 `install.sh build` + `deb/build-deb.sh`（普通用户跑；容器里关掉 Chromium 沙箱
离线渲染缩略图），再在容器里试装、试卸一遍。改到 `deepin-v25/`、`web/` 的 push / PR 会试构建，deb 在 workflow 的
artifact 里；发布 Release 时版本号取 tag，两个 deb 传到该 Release。Release 和 Windows 版共用（同一个 `v1.2.3`
tag 下既有 MSI 也有 deb，Windows 版自动更新读的“最新 Release”不受影响），所以发版后 deb 的版本号跟着产品版本走，
`deb/version` 只管 push / PR 试构建和本机打包。真机只验证过 arm64（擎云 L540）。

## 开发

```sh
wwp-set                                  # 看每个屏幕当前的桌面/锁屏壁纸
wwp-set /usr/local/share/wallpapers/wwp/weather-web/weather-web.wwp   # 桌面和锁屏都设成它
wwp-set --image /usr/share/backgrounds/default_background.jpg        # 换回默认图片壁纸
build/treeland-wallpaper-factory --snapshot build/pkg/weather-web out.png 1440x960   # 离线渲染一帧（要拼好的包，含 web/）
journalctl -b -t treeland-wallpaper-factory   # 加载记录和 treeland 事件（pause/play/slow_down）
```

注意：

- treeland 把 factory 的每次退出都算作崩溃，累计 10 次后不再拉起，要 `sudo systemctl restart treeland` 才清零
  （见上文“壁纸停住时的提醒”，到上限时 `wwp-watch` 会弹通知）。换 factory 用 `wwp-reload.sh`（SIGHUP 原地重启，不计数），
  别直接 kill；`journalctl -b _PID=$(pgrep -o -x treeland) -g 'restart count'` 看这次 treeland 运行里已经用了几次。
- treeland 在 factory 重启后不会把正在显示的壁纸挂到新进程上，要切到别的壁纸再切回来。
- treeland 升级后要拿新版本的 `wallpaper-factory/` 重新合一遍（协议可能变），再重新编译安装。
- 卸载（install.sh uninstall 或 dpkg -r）会把已登录用户正在用的天气壁纸换回默认；没登录的用户下次登录后在控制中心重选。
