import QtQuick
import QtQuick.Controls
import QtQuick.Dialogs
import QtQuick.Layouts
import Qt5Compat.GraphicalEffects
import org.deepin.dtk 1.0 as D
import org.deepin.dtk.style 1.0 as DS

// 天气壁纸设置：读 /etc/wwp/weather.json 填进界面，"应用"时整份写回（配置里界面没有的字段原样保留）。
// 壁纸页面（host.js）每 2 秒看一次配置文件，写进去几秒内就生效，不用切换壁纸。字段含义见 deepin-v25/README.md。
// 界面：每组设置一张卡片；"位置""画面"用分段按钮切换，下面只显示选中那一项自己的设置。
// 窗口、按钮、输入框、开关都走 Qt Quick Controls 的标准控件，deepin 上 Chameleon 风格把它们映射成
// DTK QML 实现（主题、字体、圆角、活动色跟着系统走）；只有 DTK 没提供的分段容器用 D.ButtonBox，
// 图片圆角用 Rounded（Qt5Compat 的 OpacityMask，和控制中心的壁纸缩略图一样）。
D.ApplicationWindow {
    id: win

    title: "天气壁纸设置"
    width: 820
    height: 760
    minimumWidth: 680
    minimumHeight: 520
    // 内容最宽 780、居中，再宽只是两边留白：不给最大化，横向也只留一点余量；高度随便拉，多看几行
    maximumWidth: 960
    visible: true
    // DTK 窗口样式：圆角、阴影、DTK 标题栏（和控制中心等系统应用一致）。
    // 标题栏右上角的最小化 / 关闭按钮要看窗口 flags 里有没有对应的 Hint，不写就只剩一个菜单按钮。
    // 没有 WindowMaximizeButtonHint 时 DTK 标题栏不显示最大化按钮，双击标题栏也不会最大化
    D.DWindow.enabled: true
    flags: Qt.Window | Qt.WindowTitleHint | Qt.WindowCloseButtonHint | Qt.WindowMinimizeButtonHint
    header: D.TitleBar {
        icon.name: "wwp-settings"
        menu: Menu {
            D.ThemeMenu {}
            D.MenuSeparator {}
            D.QuitAction {}
        }
    }

    // ---- 和 host.js 一致的常量
    readonly property int cardW: 336
    readonly property string defaultShow: "hilo,src,aqi,feel,hum,wind,hours,days,sun,upd"
    readonly property var showKeys: [
        { k: "date", t: "公历日期" }, { k: "week", t: "星期" }, { k: "lunar", t: "农历" },
        { k: "hilo", t: "今日最高 / 最低" }, { k: "src", t: "定位来源" }, { k: "aqi", t: "空气质量" },
        { k: "feel", t: "体感温度" }, { k: "hum", t: "湿度" }, { k: "wind", t: "风" },
        { k: "hours", t: "今天逐小时" }, { k: "days", t: "未来几天" }, { k: "sun", t: "日出日落" },
        { k: "upd", t: "更新时间" }
    ]
    readonly property var positions: [
        { p: "tl", t: "左上" }, { p: "tc", t: "顶部居中" }, { p: "tr", t: "右上" },
        { p: "bl", t: "左下" }, { p: "bc", t: "底部居中" }, { p: "br", t: "右下" }
    ]
    readonly property var locModes: [
        { v: "auto", t: "自动定位" }, { v: "search", t: "搜索城市" }, { v: "coord", t: "输入经纬度" }
    ]
    readonly property var modes: [
        { v: "weather", t: "天气动画", d: "全屏画天空、云、雨雪和日月，跟着实时天气变化" },
        { v: "overlay", t: "壁纸叠加", d: "天气叠在选好的壁纸上：天空半透明，云、日月、雨雪照常" },
        { v: "minimal", t: "极简模式", d: "平时只显示壁纸和卡片，几乎不占 CPU；拿到第一份天气、转雨、下雪时整屏播一段动画" }
    ]
    readonly property int defFps: 24
    readonly property int defCloudFps: 6
    readonly property int maxCloudFps: 24
    readonly property real defSky: 0.45
    readonly property real defCloud: 0.72

    // ---- 配色
    readonly property color ink: palette.windowText
    readonly property color accent: palette.highlight
    readonly property bool dark: palette.window.hslLightness < 0.5
    // 分组背景、分隔线取控制中心（DccItemBackground）的色值
    readonly property color cardColor: dark ? Qt.rgba(1, 1, 1, 0.05) : Qt.rgba(1, 1, 1, 1)
    readonly property color lineColor: dark ? Qt.rgba(1, 1, 1, 0.05) : Qt.rgba(0, 0, 0, 0.05)
    readonly property color errorColor: "#d23f31"
    readonly property real screenW: Screen.width > 0 ? Screen.width : 1440
    readonly property real screenH: Screen.height > 0 ? Screen.height : 960
    property bool ready: false         // 界面填好之后才开动画（不然一打开分段按钮的高亮块会从第一格滑过去）
    function fade(c, a) { return Qt.rgba(c.r, c.g, c.b, a) }

    // ---- 界面上的值：loadCfg 从配置读进来，buildCfg 拼回配置
    property var orig: ({})            // 读到的整份配置
    property string savedSig: ""       // 读进来时 buildCfg 的样子，用来判断改没改
    property string locMode: "auto"    // auto 按 IP 定位 / search 搜索城市 / coord 输入经纬度（配置里记在 locBy）
    property string cityName: ""       // 卡片上显示的名字：搜索时是选中的结果，输入经纬度时是显示名
    property string latText: ""
    property string lonText: ""
    property string autoName: ""       // 输入经纬度时自动填进显示名的名字：显示名还是它（没手改过）时，经纬度一改就换成新的反查结果
    property string revName: ""        // 按经纬度反查到的地名
    property string revMsg: ""
    property bool revBusy: false
    property int revSeq: 0             // 反查请求的序号：经纬度又改了，旧请求回来的结果就不要了
    property string mode: "weather"    // weather / overlay / minimal
    property string backdrop: ""       // 底层壁纸路径，"" = 没选（极简模式下 = 系统默认壁纸）
    property string lastBackdrop: ""   // 上一次用的底层壁纸：切回“天气动画”时配置里的 backdrop 会清空（host.js 靠它判断模式），路径记在这里，再切回叠加 / 极简不用重新选
    property string pendingImage: ""   // 刚从文件里选的图片：保存时由后台决定直接引用还是复制进系统目录
    property real skyOpacity: defSky
    property real cloudAlpha: defCloud
    property int playSeconds: 30
    property bool festive: true
    property bool panel: true
    property string cardPos: "tr"
    property int cardX: 32
    property int cardY: 32
    property var show: []
    property int refreshMin: 15
    property int fps: defFps
    property int cloudFps: defCloudFps

    property string status: ""
    property bool statusError: false
    property string ipState: ""        // "" 没查 / busy / ok / fail
    property string ipPlace: ""
    property string ipCoord: ""
    property bool searching: false
    property string searchMsg: ""
    property var cityResults: []       // 城市搜索结果 { name, detail, lat, lon }（不用 ListModel：deepin 默认没装 QtQml.Models）
    property var wallState: ({ ok: false })
    property var systemWalls: []
    property bool closeAfterSave: false
    property bool forceClose: false

    readonly property bool dirty: savedSig !== "" && JSON.stringify(buildCfg()) !== savedSig
    readonly property string problem: checkProblem()
    readonly property var modeInfo: modes.find(m => m.v === mode) || modes[0]
    readonly property string posName: (positions.find(p => p.p === cardPos) || positions[2]).t
    // 预览里显示的底层壁纸：叠加没选时没有图；极简没选时是系统默认壁纸
    readonly property url backdropUrl: mode === "weather" || (mode === "overlay" && backdrop === "") ? ""
                                       : backend.fileUrl(backdrop !== "" ? backdrop : backend.defaultBackdrop)

    function yes(v) { return v === true || v === 1 || v === "true" }
    function num(v, d, lo, hi) {
        var n = Number(v)
        if (v === undefined || v === null || v === "" || !isFinite(n))
            return d
        return Math.min(hi, Math.max(lo, n))
    }
    function keyList(v) {
        var a = Array.isArray(v) ? v : typeof v === "string" ? v.split(",") : []
        return a.map(s => String(s).trim().toLowerCase()).filter(s => s !== "")
    }
    function setStatus(text, error) { status = text; statusError = !!error }
    function fileName(p) { return p.substring(p.lastIndexOf("/") + 1) }
    function pct(v) { return Math.round(v * 100) }
    function hasCoords() {
        var lt = latText.trim(), ln = lonText.trim()
        return lt !== "" && ln !== "" && isFinite(Number(lt)) && isFinite(Number(ln))
    }

    function loadCfg() {
        var c = backend.load()
        var err = backend.loadError()
        orig = c
        var hasLL = c.lat !== undefined && c.lat !== null && c.lat !== "" && c.lon !== undefined && c.lon !== null && c.lon !== ""
        cityName = typeof c.city === "string" ? c.city : ""
        latText = hasLL ? String(c.lat) : ""
        lonText = hasLL ? String(c.lon) : ""
        // 只有经纬度（或者上次就是输入经纬度）的按“输入经纬度”显示，有城市名的按“搜索城市”
        locMode = hasLL && (c.locBy === "coord" || cityName.trim() === "") ? "coord"
                : cityName.trim() !== "" || hasLL ? "search" : "auto"
        autoName = ""
        revName = ""
        revMsg = ""
        revBusy = false
        revSeq++
        backdrop = typeof c.backdrop === "string" ? c.backdrop.trim() : ""
        lastBackdrop = typeof c.lastBackdrop === "string" ? c.lastBackdrop.trim() : ""
        pendingImage = ""
        mode = yes(c.minimal) ? "minimal" : backdrop !== "" ? "overlay" : "weather"
        skyOpacity = num(c.skyOpacity, defSky, 0.05, 1)
        cloudAlpha = num(c.cloudAlpha, defCloud, 0.1, 1)
        playSeconds = Math.round(num(c.playSeconds, 30, 5, 3600))
        festive = c.festive !== false
        panel = c.panel !== false
        cardPos = /^[tb][lcr]$/.test(c.cardPos) ? c.cardPos : "tr"
        cardX = Math.round(num(c.cardX, 32, -4000, 4000))
        cardY = Math.round(num(c.cardY, 32, 0, 4000))
        var want = keyList(Array.isArray(c.show) || typeof c.show === "string" ? c.show : defaultShow)
        var hide = keyList(c.hide)
        show = showKeys.map(s => s.k).filter(k => want.indexOf(k) >= 0 && hide.indexOf(k) < 0)
        refreshMin = Math.round(num(c.refresh, 15, 5, 180))
        fps = Math.round(num(c.fps, defFps, 5, 60))
        cloudFps = Math.round(num(c.cloudFps, defCloudFps, 1, maxCloudFps))
        cityResults = []
        searchMsg = ""
        savedSig = JSON.stringify(buildCfg())
        setStatus(err, err !== "")
        if (locMode === "auto")
            ipLocate()
    }

    function buildCfg() {
        var c = Object.assign({}, orig)
        if (locMode === "auto") {
            c.city = ""
            delete c.lat
            delete c.lon
            delete c.locBy
        } else {
            c.city = cityName.trim()
            if (hasCoords()) {
                c.lat = Number(latText)
                c.lon = Number(lonText)
            } else {
                delete c.lat
                delete c.lon
            }
            c.locBy = locMode   // 设置窗口自己记的：下次打开还显示成“搜索城市”还是“输入经纬度”。host.js 不读它
        }
        c.backdrop = mode === "weather" ? "" : backdrop
        c.minimal = mode === "minimal"
        // 上一次用的壁纸：backdrop 在天气动画模式下必须空（它是叠加的开关），路径存 lastBackdrop 供下次切回来用
        if (mode !== "weather" && backdrop !== "")
            c.lastBackdrop = backdrop
        c.playSeconds = playSeconds
        c.skyOpacity = Math.round(skyOpacity * 100) / 100
        c.cloudAlpha = Math.round(cloudAlpha * 100) / 100
        c.festive = festive
        c.panel = panel
        c.cardPos = cardPos
        c.cardX = cardX
        c.cardY = cardY
        c.show = show.join(",")
        delete c.hide          // 界面里勾的就是最终内容，写成完整的 show
        delete c.cardRight     // 旧版字段，换成了 cardPos / cardX / cardY
        delete c.cardTop
        c.refresh = refreshMin
        c.fps = fps
        c.cloudFps = cloudFps
        return c
    }

    function checkProblem() {
        if (locMode === "search") {
            if (cityName.trim() === "" && !hasCoords())
                return "搜索并点选一个城市，或者改回自动定位"
        } else if (locMode === "coord") {
            var lt = latText.trim(), ln = lonText.trim()
            if (lt === "" || ln === "")
                return "纬度和经度都要填"
            var a = Number(lt), b = Number(ln)
            if (!isFinite(a) || Math.abs(a) > 90)
                return "纬度要在 -90 到 90 之间"
            if (!isFinite(b) || Math.abs(b) > 180)
                return "经度要在 -180 到 180 之间"
        }
        if (mode === "overlay" && backdrop === "")
            return "壁纸叠加要先选一张底层壁纸"
        return ""
    }

    function save() {
        if (problem !== "") {
            closeAfterSave = false   // 关窗时点了“保存”但设置有问题：窗口留着改，之后再保存成功也不自动关
            setStatus(problem, true)
            return
        }
        var img = mode !== "weather" && pendingImage !== "" && backdrop === pendingImage ? pendingImage : ""
        setStatus(img !== "" ? "正在处理图片并保存…" : "正在保存…", false)
        backend.save(buildCfg(), img)
    }

    function setLocMode(m) {
        if (m === locMode)
            return
        if (m === "coord")
            autoName = cityName   // 搜到的名字算自动的：接着改经纬度时，显示名跟着反查结果换
        revName = ""
        revMsg = ""
        revBusy = false
        revSeq++
        cityResults = []
        searchMsg = ""
        locMode = m
        if (m === "auto")
            ipLocate()
    }

    function setMode(m) {
        mode = m
        // 切回壁纸叠加 / 极简模式：接着用上次选过的底层壁纸
        if (m !== "weather" && backdrop === "" && lastBackdrop !== "")
            backdrop = lastBackdrop
    }

    function setShow(k, on) {
        var s = show.filter(x => x !== k)
        if (on)
            s.push(k)
        show = showKeys.map(x => x.k).filter(x => s.indexOf(x) >= 0)
    }

    // 卡片高度和位置：同 host.js 的 cardHeight / sendLayout（Windows 版 CardLayout.HeightOf）
    function cardHeight() {
        var has = k => show.indexOf(k) >= 0
        var h = 1 + 18 + 20
        if (has("date") || has("week") || has("lunar"))
            h += 3 + 17
        h += 6 + 76
        if (has("aqi") || has("feel") || has("hum") || has("wind"))
            h += 8 + 22
        if (has("hours"))
            h += 10 + 1 + 8 + 79
        if (has("days"))
            h += (has("hours") ? 4 : 10) + 1 + 8 + 142
        if (has("sun") || (has("upd") && has("src")))
            h += 12 + 15
        return h + 14 + 1
    }
    // 换到 pos 时的水平距离：居中时是离中线的偏移，靠边时是离边的距离，两种之间切换要重置
    function xFor(pos) { return (cardPos[1] === "c") === (pos[1] === "c") ? cardX : pos[1] === "c" ? 0 : 32 }
    function cardRect(W, H, pos) {
        var cx = xFor(pos), h = cardHeight(), g = num(orig.ground, 0.0444, 0, 0.3)
        var x = pos[1] === "l" ? cx : pos[1] === "r" ? W - cardW - cx : (W - cardW) / 2 + cx
        var y = pos[0] === "b" ? H * (1 - g) - cardY - h : cardY
        return Qt.rect(Math.max(0, Math.min(x, W - cardW)), Math.max(0, Math.min(y, H - h)), cardW, h)
    }
    function setCardPos(p) {
        var x = xFor(p)
        cardPos = p
        cardX = x
    }

    // 自动定位时看看 IP 定位到哪里（壁纸用的也是 ip-api.com）
    function ipLocate() {
        if (ipState === "busy" || ipState === "ok")
            return
        ipState = "busy"
        var x = new XMLHttpRequest()
        x.onreadystatechange = function () {
            if (x.readyState !== XMLHttpRequest.DONE)
                return
            try {
                var d = JSON.parse(x.responseText)
                if (d.status !== "success")
                    throw new Error(d.message || d.status)
                ipPlace = [d.city, d.regionName, d.country].filter((s, i, a) => s && a.indexOf(s) === i).join(" · ")
                ipCoord = d.lat.toFixed(2) + ", " + d.lon.toFixed(2)
                ipState = "ok"
            } catch (e) {
                ipPlace = "IP 定位失败（" + (x.status ? "HTTP " + x.status : "网络不通") + "）"
                ipState = "fail"
            }
        }
        x.open("GET", "http://ip-api.com/json/?lang=zh-CN")
        x.send()
    }

    // 城市搜索：Open-Meteo 地名库（壁纸只填城市名时查的也是它），选中后连经纬度一起存，不会查歧义
    function searchCity() {
        var q = cityField.text.trim()
        if (q === "" || searching)
            return
        searching = true
        searchMsg = ""
        cityResults = []
        var x = new XMLHttpRequest()
        x.onreadystatechange = function () {
            if (x.readyState !== XMLHttpRequest.DONE)
                return
            searching = false
            try {
                if (x.status !== 200)
                    throw new Error(x.status ? "HTTP " + x.status : "网络不通")
                var r = JSON.parse(x.responseText).results || []
                cityResults = r.map(it => ({
                    name: it.name,
                    detail: [it.admin2, it.admin1, it.country].filter((s, j, a) => s && s !== it.name && a.indexOf(s) === j).join(" · "),
                    lat: it.latitude,
                    lon: it.longitude
                }))
                if (r.length === 0)
                    searchMsg = "地名库里没有“" + q + "”，换个写法（如“武汉”、外国城市用英文名），或者改用“输入经纬度”"
            } catch (e) {
                searchMsg = "搜索失败：" + e.message
            }
        }
        x.open("GET", "https://geocoding-api.open-meteo.com/v1/search?count=8&language=zh&format=json&name=" + encodeURIComponent(q))
        x.send()
    }
    function pickCity(r) {
        cityName = r.name
        latText = r.lat.toFixed(4)
        lonText = r.lon.toFixed(4)
        cityResults = []
        searchMsg = ""
    }

    // 输入经纬度：停手 0.7 秒后按经纬度反查地名（BigDataCloud，壁纸按 IP 定位时反查用的也是它，写法同 host.js 的 reverseName）
    function coordEdited() {
        revSeq++
        revBusy = false
        revName = ""
        revMsg = ""
        revTimer.restart()
    }
    function reverseLookup() {
        var la = Number(latText), lo = Number(lonText)
        if (locMode !== "coord" || !hasCoords() || Math.abs(la) > 90 || Math.abs(lo) > 180)
            return
        var seq = ++revSeq
        revBusy = true
        var x = new XMLHttpRequest()
        x.onreadystatechange = function () {
            if (x.readyState !== XMLHttpRequest.DONE || seq !== revSeq)
                return
            revBusy = false
            try {
                if (x.status !== 200)
                    throw new Error(x.status ? "HTTP " + x.status : "网络不通")
                var d = JSON.parse(x.responseText)
                var city = d.city || d.principalSubdivision || "", local = d.locality || ""
                var name = local && local !== city ? city + " · " + local : city || d.countryName || ""
                if (name === "") {
                    revMsg = "这个位置反查不到地名，显示名可以自己填；不填的话卡片上显示经纬度"
                    return
                }
                revName = name
                if (cityName.trim() === "" || cityName === autoName) {
                    cityName = name
                    autoName = name
                }
            } catch (e) {
                revMsg = "反查地名失败（" + e.message + "），显示名可以自己填"
            }
        }
        x.open("GET", "https://api.bigdatacloud.net/data/reverse-geocode-client?latitude=" + la + "&longitude=" + lo + "&localityLanguage=zh")
        x.send()
    }
    // 纬度框里粘贴了“30.5928, 114.3055”这种（地图上复制的）：拆开分别填进纬度、经度
    function pasteCoords(t) {
        var m = /^\s*(-?\d+(?:\.\d+)?)\s*[,，\s]\s*(-?\d+(?:\.\d+)?)\s*$/.exec(t)
        if (!m)
            return false
        latText = ""          // 先清一下：值没变的话纬度框的绑定不会刷新，框里会留着整串
        latText = m[1]
        lonText = m[2]
        return true
    }

    function refreshWallState() { wallState = backend.wallpaperState() }

    Timer {
        id: revTimer
        interval: 700
        onTriggered: win.reverseLookup()
    }

    Connections {
        target: backend
        function onSaved(ok, message, cfg) {
            if (!ok) {
                closeAfterSave = false
                setStatus("保存失败：" + message, true)
                return
            }
            if (closeAfterSave) {
                forceClose = true
                win.close()
                return
            }
            loadCfg()   // 按实际写进去的重新填（自选图片换成了 /var/lib/wwp 里的副本）
            refreshWallState()
            setStatus(wallState.ok && !(wallState.desktop && wallState.lockscreen)
                      ? "已保存。桌面或锁屏现在用的不是天气壁纸，点下面的“设为桌面和锁屏壁纸”才看得到"
                      : "已保存，壁纸几秒内就会更新", false)
        }
        function onWallpaperSet(ok, message) {
            refreshWallState()
            setStatus(ok ? "已把桌面和锁屏都设成天气壁纸" : "设置壁纸失败：" + message, !ok)
        }
    }

    onClosing: close => {
        if (dirty && !forceClose) {
            close.accepted = false
            confirmClose.show()
        }
    }

    Component.onCompleted: {
        systemWalls = backend.systemWallpapers()
        loadCfg()
        refreshWallState()
        Qt.callLater(() => win.ready = true)
    }

    FileDialog {
        id: fileDialog
        title: "选择底层壁纸"
        nameFilters: ["图片 (*.jpg *.jpeg *.png *.webp *.bmp)", "所有文件 (*)"]
        onAccepted: {
            var p = backend.localPath(selectedFile)
            if (p !== "") {
                win.backdrop = p
                win.pendingImage = p
                wallDialog.close()
            }
        }
    }

    // 选底层壁纸：系统壁纸网格 + 从文件选择。
    // D.DialogWindow 是个独立的 Window（DTK 标题栏 + 窗口模糊背景），不是 Qt Quick Controls 的 Dialog：
    // 没有 open() / modal / contentItem，用 show() / modality，内容直接写成子项
    D.DialogWindow {
        id: wallDialog
        objectName: "wallDialog"
        title: "选择底层壁纸"
        icon: "wwp-settings"
        modality: Qt.WindowModal
        transientParent: win
        width: Math.min(760, Screen.desktopAvailableWidth - 80)
        height: Math.min(600, Screen.desktopAvailableHeight - 120)
        minimumWidth: width
        minimumHeight: height
        maximumWidth: width
        maximumHeight: height
        ColumnLayout {
            width: parent.width
            height: parent.height
            spacing: 12
            Hint {
                text: "系统自带 " + win.systemWalls.length + " 张，点一下就选中。从文件选的图片会复制一份到系统目录（/var/lib/wwp），原图挪走、删掉都不影响"
            }
            GridView {
                id: wallGrid
                readonly property int cols: Math.max(2, Math.floor(width / 180))
                Layout.fillWidth: true
                Layout.fillHeight: true
                cellWidth: Math.floor(width / cols)
                cellHeight: Math.round(cellWidth * 0.64)
                clip: true
                model: win.systemWalls
                boundsBehavior: Flickable.StopAtBounds
                ScrollBar.vertical: ScrollBar {}
                delegate: Item {
                    id: cell
                    required property string modelData
                    readonly property bool sel: win.backdrop === modelData
                    width: wallGrid.cellWidth
                    height: wallGrid.cellHeight
                    Item {
                        anchors.fill: parent
                        anchors.margins: 10
                        Rounded {
                            anchors.fill: parent
                            radius: 8
                            Rectangle {
                                anchors.fill: parent
                                color: win.fade(win.ink, 0.08)
                            }
                            Image {
                                anchors.fill: parent
                                source: backend.fileUrl(cell.modelData)
                                sourceSize: Qt.size(320, 200)
                                fillMode: Image.PreserveAspectCrop
                                asynchronous: true
                                cache: true
                            }
                        }
                        Rectangle {
                            visible: cell.sel || cellMouse.containsMouse
                            anchors.fill: parent
                            anchors.margins: -5
                            radius: 13
                            color: "transparent"
                            border.width: cell.sel ? 3 : 2
                            border.color: cell.sel ? win.accent : win.fade(win.ink, 0.3)
                        }
                        Badge {
                            visible: cell.sel
                            anchors.right: parent.right
                            anchors.top: parent.top
                            anchors.margins: 6
                        }
                    }
                    MouseArea {
                        id: cellMouse
                        anchors.fill: parent
                        hoverEnabled: true
                        cursorShape: Qt.PointingHandCursor
                        onClicked: {
                            win.backdrop = cell.modelData
                            win.pendingImage = ""
                            wallDialog.close()
                        }
                    }
                }
            }
            RowLayout {
                Button {
                    text: "从文件选择…"
                    onClicked: fileDialog.open()
                }
                Item { Layout.fillWidth: true }
                Button {
                    text: "取消"
                    onClicked: wallDialog.close()
                }
            }
        }
    }

    // 关窗时还有没应用的修改：和 DTK 系统应用的确认框一样，标题 + 一句话 + 一排按钮
    D.DialogWindow {
        id: confirmClose
        objectName: "confirmClose"
        title: "有修改还没应用"
        icon: "wwp-settings"
        modality: Qt.WindowModal
        transientParent: win
        width: 440
        ColumnLayout {
            width: parent.width
            spacing: 20
            Label {
                Layout.fillWidth: true
                horizontalAlignment: Text.AlignHCenter
                wrapMode: Text.Wrap
                text: "关闭前要先应用这些修改吗？"
            }
            RowLayout {
                Layout.fillWidth: true
                Layout.bottomMargin: 10
                spacing: 10
                Button {
                    Layout.fillWidth: true
                    text: "取消"
                    onClicked: confirmClose.close()
                }
                Button {
                    Layout.fillWidth: true
                    text: "放弃修改"
                    onClicked: {
                        win.forceClose = true
                        confirmClose.close()
                        win.close()
                    }
                }
                D.RecommandButton {
                    Layout.fillWidth: true
                    text: "保存并关闭"
                    enabled: !backend.busy
                    onClicked: {
                        win.closeAfterSave = true
                        win.save()
                    }
                }
            }
        }
    }

    // -------------------------------------------------------------------------------------------------------------- 小组件

    // 一组设置的标题
    component SectionTitle: Label {
        font.family: D.DTK.fontManager.t6.family
        font.pixelSize: D.DTK.fontManager.t6.pixelSize
        font.weight: Font.DemiBold
        Layout.topMargin: 14
        Layout.leftMargin: 4
    }
    // 圆角卡片，里面的东西竖着排
    component Card: Rectangle {
        id: card
        default property alias body: inner.data
        readonly property int pad: 18
        Layout.fillWidth: true
        implicitHeight: inner.implicitHeight + pad * 2
        radius: DS.Style.control.radius
        color: win.cardColor
        ColumnLayout {
            id: inner
            x: card.pad
            y: card.pad
            width: card.width - card.pad * 2
            spacing: 14
        }
    }
    component Divider: Rectangle {
        Layout.fillWidth: true
        implicitHeight: 1
        color: win.lineColor
    }
    component Hint: Label {
        Layout.fillWidth: true
        wrapMode: Text.Wrap
        opacity: 0.6
        font: D.DTK.fontManager.t9
    }
    // 一行设置：左边标题和说明，右边控件
    component SettingRow: RowLayout {
        id: row
        property string title
        property string desc
        default property alias trailing: tail.data
        Layout.fillWidth: true
        spacing: 16
        ColumnLayout {
            Layout.fillWidth: true
            spacing: 2
            Label {
                Layout.fillWidth: true
                text: row.title
            }
            Label {
                Layout.fillWidth: true
                visible: row.desc !== ""
                text: row.desc
                wrapMode: Text.Wrap
                opacity: 0.6
                font: D.DTK.fontManager.t9
            }
        }
        RowLayout {
            id: tail
            spacing: 8
        }
    }
    // 帧率滑块：整数，右边显示当前值
    component FpsSlider: RowLayout {
        id: fs
        property alias from: sl.from
        property alias to: sl.to
        property int fps
        signal picked(int v)
        spacing: 8
        Slider {
            id: sl
            Layout.preferredWidth: 260
            stepSize: 1
            snapMode: Slider.SnapAlways
            highlightedPassedGroove: true
            value: fs.fps
            onMoved: fs.picked(Math.round(value))
        }
        Label {
            text: fs.fps + " fps"
            Layout.preferredWidth: 52
            horizontalAlignment: Text.AlignRight
        }
    }
    // 分段按钮：DTK 的 ButtonBox —— 几个可勾选的 ToolButton 连成一排，由 DTK 画选中背板（带滑动动画）。
    // ButtonBox 的 default property 是 buttons，按钮必须直接写成它的子项（不能运行时再创建），
    // 所以这里固定摆 3 个，options 不够 3 项时多的隐藏（目前用到的分段都是 3 项）
    component Segmented: D.ButtonBox {
        id: seg
        property var options: []       // [{ v: 值, t: 文字 }]，最多 3 项
        property var current
        signal picked(var v)
        SegButton { box: seg; idx: 0 }
        SegButton { box: seg; idx: 1 }
        SegButton { box: seg; idx: 2 }
    }
    component SegButton: D.ToolButton {
        id: sb
        property var box
        property int idx
        readonly property var opt: box.options[idx]
        visible: opt !== undefined
        text: opt ? opt.t : ""
        checkable: true
        checked: opt !== undefined && opt.v === box.current
        Layout.minimumWidth: 96
        onClicked: box.picked(opt.v)
    }
    // 可点的标签（卡片显示内容）：选中时是活动色描边、淡活动色底，前面一个勾
    component Chip: AbstractButton {
        id: chip
        property bool sel: false
        hoverEnabled: true
        implicitHeight: 32
        implicitWidth: chipFont.advanceWidth("✓ " + text) + 30   // 按带勾的宽度算：勾选时宽度不变，整排不会跳
        FontMetrics {
            id: chipFont
            font: chip.font
        }
        contentItem: Label {
            text: (chip.sel ? "✓ " : "") + chip.text
            horizontalAlignment: Text.AlignHCenter
            verticalAlignment: Text.AlignVCenter
            color: chip.sel ? win.accent : win.ink
        }
        background: Rectangle {
            radius: height / 2
            color: chip.sel ? win.fade(win.accent, win.dark ? 0.24 : 0.11) : chip.hovered ? win.fade(win.ink, 0.05) : "transparent"
            border.width: 1
            border.color: chip.sel ? win.fade(win.accent, 0.65) : win.fade(win.ink, 0.2)
        }
    }
    // 选中标记：活动色圆点里一个勾
    component Badge: Rectangle {
        implicitWidth: 22
        implicitHeight: 22
        radius: width / 2
        color: win.accent
        Label {
            anchors.centerIn: parent
            text: "✓"
            font.family: D.DTK.fontManager.t8.family
            font.pixelSize: D.DTK.fontManager.t8.pixelSize
            font.bold: true
            color: win.palette.highlightedText
        }
    }
    // 圆角裁剪：里面的子项（图片、天空、云……）整体按 radius 裁成圆角，鼠标事件照常。
    // 和控制中心壁纸缩略图一样用 OpacityMask（Chameleon 风格本来就依赖 qt5compat-graphicaleffects）
    component Rounded: Item {
        id: rd
        property real radius: 8
        layer.enabled: true
        layer.effect: OpacityMask {
            maskSource: maskRect
        }
        Rectangle {
            id: maskRect
            visible: false
            width: rd.width
            height: rd.height
            radius: rd.radius
        }
    }
    // 预览里的一朵云（几个圆叠起来，整体按不透明度画：layer 让重叠的地方不会更白）
    component Cloud: Item {
        width: 90
        height: 46
        layer.enabled: true
        Rectangle { x: 0; y: 20; width: 90; height: 26; radius: 13; color: "#f6f8fb" }
        Rectangle { x: 13; y: 7; width: 34; height: 34; radius: 17; color: "#f6f8fb" }
        Rectangle { x: 35; y: 0; width: 40; height: 40; radius: 20; color: "#f6f8fb" }
        Rectangle { x: 60; y: 13; width: 26; height: 26; radius: 13; color: "#f6f8fb" }
    }
    // 预览用的白天天空
    component SkyGradient: Rectangle {
        anchors.fill: parent
        gradient: Gradient {
            GradientStop { position: 0; color: "#3f7fcf" }
            GradientStop { position: 1; color: "#a8cff2" }
        }
    }

    // -------------------------------------------------------------------------------------------------------------- 内容

    ScrollView {
        id: sv
        anchors.fill: parent
        contentWidth: availableWidth
        // contentHeight 必须显式给：不写的话靠 childrenRect 推断，ColumnLayout 常常推成 0，
        // 内容比窗口高的部分就永远滚不到（看着就是“窗口太长、下面的设置不见了”）
        contentHeight: content.implicitHeight + 36
        clip: true

        ColumnLayout {
            id: content
            width: Math.min(sv.availableWidth - 48, 780)
            x: (sv.availableWidth - width) / 2
            y: 6
            spacing: 8

            // ---- 位置
            SectionTitle { text: "位置" }
            Card {
                Segmented {
                    options: win.locModes
                    current: win.locMode
                    onPicked: v => win.setLocMode(v)
                }

                // 自动定位：只显示定位到哪里
                ColumnLayout {
                    visible: win.locMode === "auto"
                    Layout.fillWidth: true
                    spacing: 4
                    RowLayout {
                        spacing: 8
                        Rectangle {
                            implicitWidth: 8
                            implicitHeight: 8
                            radius: 4
                            color: win.ipState === "ok" ? "#2bb673" : win.ipState === "fail" ? win.errorColor : win.fade(win.ink, 0.3)
                        }
                        Label {
                            text: win.ipState === "ok" || win.ipState === "fail" ? win.ipPlace : "正在按 IP 定位…"
                            font.weight: win.ipState === "ok" ? Font.DemiBold : Font.Normal
                        }
                        Label {
                            visible: win.ipState === "ok"
                            text: win.ipCoord
                            opacity: 0.55
                        }
                    }
                    Hint {
                        text: win.ipState === "fail" ? "壁纸会自己过一会儿重试；一直失败的话改用“搜索城市”或“输入经纬度”"
                                                     : "按 IP 定位，走代理、VPN 时可能不准，不准就改用“搜索城市”或“输入经纬度”"
                    }
                }

                // 搜索城市：搜索框 + 结果列表 + 当前选中的城市（经纬度跟着结果走，这里不能改）
                ColumnLayout {
                    visible: win.locMode === "search"
                    Layout.fillWidth: true
                    spacing: 10
                    RowLayout {
                        Layout.fillWidth: true
                        spacing: 8
                        TextField {
                            id: cityField
                            Layout.fillWidth: true
                            placeholderText: "城市、区县或景区，如 武汉、婺源、神农架"
                            onAccepted: win.searchCity()
                        }
                        Button {
                            text: win.searching ? "搜索中…" : "搜索"
                            enabled: !win.searching && cityField.text.trim() !== ""
                            onClicked: win.searchCity()
                        }
                    }
                    Hint {
                        visible: text !== ""
                        text: win.searchMsg
                    }
                    Rectangle {
                        visible: win.cityResults.length > 0
                        Layout.fillWidth: true
                        implicitHeight: results.implicitHeight + 8
                        radius: 10
                        color: "transparent"
                        border.color: win.lineColor
                        ColumnLayout {
                            id: results
                            x: 4
                            y: 4
                            width: parent.width - 8
                            spacing: 0
                            Repeater {
                                model: win.cityResults
                                delegate: ItemDelegate {
                                    id: res
                                    required property var modelData
                                    Layout.fillWidth: true
                                    contentItem: RowLayout {
                                        spacing: 10
                                        Label {
                                            text: res.modelData.name
                                            font.weight: Font.DemiBold
                                        }
                                        Label {
                                            Layout.fillWidth: true
                                            text: res.modelData.detail
                                            elide: Text.ElideRight
                                            opacity: 0.6
                                        }
                                        Label {
                                            text: res.modelData.lat.toFixed(2) + ", " + res.modelData.lon.toFixed(2)
                                            opacity: 0.45
                                            font: D.DTK.fontManager.t9
                                        }
                                    }
                                    onClicked: win.pickCity(res.modelData)
                                }
                            }
                        }
                    }
                    RowLayout {
                        visible: win.cityName.trim() !== "" || win.hasCoords()
                        Layout.fillWidth: true
                        spacing: 8
                        Badge {
                            implicitWidth: 18
                            implicitHeight: 18
                        }
                        Label {
                            text: win.cityName.trim() !== "" ? win.cityName : "（没有名字）"
                            font.weight: Font.DemiBold
                        }
                        Label {
                            Layout.fillWidth: true
                            elide: Text.ElideRight
                            opacity: 0.55
                            text: win.hasCoords() ? Number(win.latText).toFixed(4) + ", " + Number(win.lonText).toFixed(4)
                                                  : "没定经纬度：壁纸会查地名库里第一个同名的地方，最好搜一下点选"
                        }
                    }
                }

                // 输入经纬度：经纬度 + 显示名（按经纬度自动反查，可以改）
                ColumnLayout {
                    visible: win.locMode === "coord"
                    Layout.fillWidth: true
                    spacing: 10
                    GridLayout {
                        Layout.fillWidth: true
                        columns: 4
                        columnSpacing: 10
                        rowSpacing: 10
                        Label { text: "纬度" }
                        TextField {
                            Layout.fillWidth: true
                            placeholderText: "如 30.5928，南纬为负"
                            text: win.latText
                            onTextEdited: {
                                if (!win.pasteCoords(text))
                                    win.latText = text
                                win.coordEdited()
                            }
                        }
                        Label { text: "经度"; Layout.leftMargin: 8 }
                        TextField {
                            Layout.fillWidth: true
                            placeholderText: "如 114.3055，西经为负"
                            text: win.lonText
                            onTextEdited: {
                                win.lonText = text
                                win.coordEdited()
                            }
                        }
                        Label { text: "显示名" }
                        TextField {
                            Layout.columnSpan: 3
                            Layout.fillWidth: true
                            placeholderText: "卡片上显示的名字，留空显示经纬度"
                            text: win.cityName
                            onTextEdited: win.cityName = text
                        }
                    }
                    RowLayout {
                        Layout.fillWidth: true
                        spacing: 8
                        Hint {
                            text: win.revBusy ? "正在按经纬度反查地名…"
                                  : win.revMsg !== "" ? win.revMsg
                                  : "改了经纬度，显示名会自动换成反查到的地名（自己改过的不动）。可以把地图上复制的“30.5928, 114.3055”直接粘贴进纬度框"
                        }
                        Button {
                            visible: !win.revBusy && win.revName !== "" && win.revName !== win.cityName
                            flat: true
                            text: "改用“" + win.revName + "”"
                            onClicked: {
                                win.cityName = win.revName
                                win.autoName = win.revName
                            }
                        }
                    }
                }
            }

            // ---- 画面
            SectionTitle { text: "画面" }
            Card {
                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: 8
                    Segmented {
                        options: win.modes
                        current: win.mode
                        onPicked: v => win.setMode(v)
                    }
                    Hint { text: win.modeInfo.d }
                }

                // 底层壁纸（叠加、极简）：左边预览，右边换图；叠加时再加天空、云的不透明度，预览跟着变
                Divider { visible: win.mode !== "weather" }
                RowLayout {
                    visible: win.mode !== "weather"
                    Layout.fillWidth: true
                    spacing: 20
                    Rounded {
                        radius: 10
                        Layout.alignment: Qt.AlignTop
                        Layout.preferredWidth: 240
                        Layout.preferredHeight: Math.round(240 * win.screenH / win.screenW)
                        Rectangle {
                            anchors.fill: parent
                            color: "#5b6470"   // 读不到图片时的底色，同 host.js
                        }
                        Image {
                            anchors.fill: parent
                            source: win.backdropUrl
                            sourceSize.width: 480
                            fillMode: Image.PreserveAspectCrop
                            asynchronous: true
                        }
                        SkyGradient {
                            visible: win.mode === "overlay"
                            opacity: win.skyOpacity
                        }
                        Rectangle {   // 太阳：叠加时日月照常不透明
                            visible: win.mode === "overlay"
                            x: parent.width * 0.16
                            y: parent.height * 0.18
                            width: parent.width * 0.1
                            height: width
                            radius: width / 2
                            color: "#ffd66b"
                        }
                        Cloud {
                            visible: win.mode === "overlay"
                            x: parent.width * 0.5
                            y: parent.height * 0.3
                            opacity: win.cloudAlpha
                        }
                        Label {
                            visible: win.mode === "overlay" && win.backdrop === ""
                            anchors.centerIn: parent
                            text: "还没选壁纸"
                            color: "white"
                        }
                        MouseArea {
                            anchors.fill: parent
                            cursorShape: Qt.PointingHandCursor
                            onClicked: wallDialog.show()
                        }
                    }
                    ColumnLayout {
                        Layout.fillWidth: true
                        Layout.alignment: Qt.AlignTop
                        spacing: 10
                        RowLayout {
                            Layout.fillWidth: true
                            spacing: 10
                            ColumnLayout {
                                Layout.fillWidth: true
                                spacing: 2
                                Label { text: win.mode === "overlay" ? "底层壁纸" : "平时显示的壁纸" }
                                Label {
                                    Layout.fillWidth: true
                                    elide: Text.ElideMiddle
                                    opacity: 0.6
                                    font: D.DTK.fontManager.t9
                                    text: win.backdrop !== "" ? win.fileName(win.backdrop) : win.mode === "minimal" ? "系统默认壁纸" : "还没选"
                                }
                            }
                            Button {
                                text: win.backdrop === "" && win.mode === "overlay" ? "选择壁纸…" : "更换…"
                                highlighted: win.backdrop === "" && win.mode === "overlay"
                                onClicked: wallDialog.show()
                            }
                        }
                        Button {
                            visible: win.mode === "minimal" && win.backdrop !== ""
                            flat: true
                            text: "改用系统默认壁纸"
                            onClicked: {
                                win.backdrop = ""
                                win.pendingImage = ""
                            }
                        }
                        GridLayout {
                            visible: win.mode === "overlay"
                            Layout.fillWidth: true
                            columns: 3
                            columnSpacing: 10
                            rowSpacing: 2
                            Label { text: "天空" }
                            Slider {
                                Layout.fillWidth: true
                                from: 0.05; to: 1; stepSize: 0.05
                                highlightedPassedGroove: true
                                value: win.skyOpacity
                                onMoved: win.skyOpacity = value
                            }
                            Label {
                                text: win.pct(win.skyOpacity) + "%"
                                Layout.preferredWidth: 40
                                horizontalAlignment: Text.AlignRight
                            }
                            Label { text: "云" }
                            Slider {
                                Layout.fillWidth: true
                                from: 0.1; to: 1; stepSize: 0.05
                                highlightedPassedGroove: true
                                value: win.cloudAlpha
                                onMoved: win.cloudAlpha = value
                            }
                            Label {
                                text: win.pct(win.cloudAlpha) + "%"
                                Layout.preferredWidth: 40
                                horizontalAlignment: Text.AlignRight
                            }
                        }
                        RowLayout {
                            visible: win.mode === "overlay"
                            Layout.fillWidth: true
                            Hint { text: "不透明度越小，壁纸越清楚。实际的天空颜色随时间和天气变" }
                            Button {
                                flat: true
                                text: "恢复默认"
                                enabled: win.pct(win.skyOpacity) !== win.pct(win.defSky) || win.pct(win.cloudAlpha) !== win.pct(win.defCloud)
                                onClicked: {
                                    win.skyOpacity = win.defSky
                                    win.cloudAlpha = win.defCloud
                                }
                            }
                        }
                    }
                }

                Divider {}
                SettingRow {
                    title: "每次播放动画"
                    desc: (win.mode === "minimal" ? "极简模式每段天气动画、" : "") + "雨后转晴时的彩虹都播这么久"
                    SpinBox {
                        Layout.preferredWidth: 140   // DTK 的 SpinBox 不限宽会撑满整行
                        from: 5; to: 3600; stepSize: 5
                        editable: true
                        value: win.playSeconds
                        onValueModified: win.playSeconds = value
                    }
                    Label { text: "秒" }
                }
                Divider {}
                SettingRow {
                    title: "节日装饰与彩蛋"
                    desc: "春节、中秋、圣诞等节日的装饰，和一些小彩蛋"
                    Switch {
                        checked: win.festive
                        onToggled: win.festive = checked
                    }
                }
            }

            // ---- 天气卡片
            SectionTitle { text: "天气卡片" }
            Card {
                SettingRow {
                    title: "显示天气卡片"
                    desc: "桌面和锁屏上的天气卡片"
                    Switch {
                        checked: win.panel
                        onToggled: win.panel = checked
                    }
                }
                Divider { visible: win.panel }
                RowLayout {
                    id: cardRow
                    visible: win.panel
                    Layout.fillWidth: true
                    spacing: 24
                    // 屏幕示意图：按屏幕比例画出卡片的位置和大小；点六个区域之一把卡片挪过去，悬停时先画出虚影。
                    // 尺寸由整行宽度直接算（不能用 fillWidth 再按 width 算高度：布局会来回算个没完）
                    Rounded {
                        id: screenBox
                        radius: 10
                        readonly property real pw: Math.max(200, Math.min(400, cardRow.width - 250 - cardRow.spacing))
                        readonly property real k: width / win.screenW
                        readonly property rect card: win.cardRect(win.screenW, win.screenH, win.cardPos)
                        property string hoverPos: ""
                        readonly property rect ghost: hoverPos !== "" ? win.cardRect(win.screenW, win.screenH, hoverPos) : Qt.rect(0, 0, 0, 0)
                        Layout.alignment: Qt.AlignTop
                        Layout.preferredWidth: pw
                        Layout.preferredHeight: Math.round(pw * win.screenH / win.screenW)
                        SkyGradient {}
                        Image {
                            anchors.fill: parent
                            visible: win.mode !== "weather"
                            source: win.mode === "weather" ? "" : backend.fileUrl(win.backdrop !== "" ? win.backdrop : backend.defaultBackdrop)
                            sourceSize.width: 800
                            fillMode: Image.PreserveAspectCrop
                            asynchronous: true
                        }
                        SkyGradient {   // 叠加时半透明的天空
                            visible: win.mode === "overlay"
                            opacity: win.skyOpacity
                        }
                        Rectangle {   // 任务栏
                            anchors.left: parent.left
                            anchors.right: parent.right
                            anchors.bottom: parent.bottom
                            height: Math.max(3, parent.height * win.num(win.orig.ground, 0.0444, 0, 0.3))
                            color: "#ccf2f5fa"
                        }
                        Rectangle {   // 悬停位置的虚影
                            visible: screenBox.hoverPos !== "" && screenBox.hoverPos !== win.cardPos
                            x: screenBox.ghost.x * screenBox.k
                            y: screenBox.ghost.y * screenBox.k
                            width: screenBox.ghost.width * screenBox.k
                            height: screenBox.ghost.height * screenBox.k
                            radius: 14 * screenBox.k
                            color: "#26ffffff"
                            border.width: 1.5
                            border.color: "#b3ffffff"
                        }
                        Rectangle {   // 卡片
                            x: screenBox.card.x * screenBox.k
                            y: screenBox.card.y * screenBox.k
                            width: screenBox.card.width * screenBox.k
                            height: screenBox.card.height * screenBox.k
                            radius: 14 * screenBox.k
                            color: "#66ffffff"
                            border.color: "#e6ffffff"
                            Behavior on x { enabled: win.ready; NumberAnimation { duration: 200; easing.type: Easing.OutCubic } }
                            Behavior on y { enabled: win.ready; NumberAnimation { duration: 200; easing.type: Easing.OutCubic } }
                            Behavior on height { enabled: win.ready; NumberAnimation { duration: 150 } }
                        }
                        Repeater {   // 六个点击区：三列两行
                            model: win.positions
                            delegate: MouseArea {
                                required property var modelData
                                x: "lcr".indexOf(modelData.p[1]) * screenBox.width / 3
                                y: modelData.p[0] === "t" ? 0 : screenBox.height / 2
                                width: screenBox.width / 3
                                height: screenBox.height / 2
                                hoverEnabled: true
                                cursorShape: Qt.PointingHandCursor
                                onContainsMouseChanged: {
                                    if (containsMouse)
                                        screenBox.hoverPos = modelData.p
                                    else if (screenBox.hoverPos === modelData.p)
                                        screenBox.hoverPos = ""
                                }
                                onClicked: win.setCardPos(modelData.p)
                            }
                        }
                    }
                    ColumnLayout {
                        Layout.alignment: Qt.AlignTop
                        Layout.preferredWidth: 250
                        spacing: 12
                        ColumnLayout {
                            spacing: 2
                            Label {
                                text: "位置"
                                opacity: 0.6
                                font: D.DTK.fontManager.t9
                            }
                            Label {
                                text: win.posName
                                font.family: D.DTK.fontManager.t4.family
                                font.pixelSize: D.DTK.fontManager.t4.pixelSize
                                font.weight: Font.DemiBold
                            }
                        }
                        GridLayout {
                            columns: 3
                            columnSpacing: 8
                            rowSpacing: 8
                            Label { text: win.cardPos[1] === "c" ? "水平偏移" : "离左右边" }
                            SpinBox {
                                Layout.preferredWidth: 140   // DTK 的 SpinBox 不限宽会撑满整行
                                from: win.cardPos[1] === "c" ? -2000 : 0; to: 2000; stepSize: 4
                                editable: true
                                value: win.cardX
                                onValueModified: win.cardX = value
                            }
                            Label { text: "像素" }
                            Label { text: win.cardPos[0] === "b" ? "离任务栏" : "离上边" }
                            SpinBox {
                                Layout.preferredWidth: 140   // DTK 的 SpinBox 不限宽会撑满整行
                                from: 0; to: 2000; stepSize: 4
                                editable: true
                                value: win.cardY
                                onValueModified: win.cardY = value
                            }
                            Label { text: "像素" }
                        }
                        Hint {
                            text: "点左边示意图换位置（分左中右、上下六块）。" + (win.cardPos[1] === "c" ? "居中时水平偏移为正往右、为负往左。" : "")
                                  + "锁屏上卡片的位置和大小与桌面一样"
                        }
                    }
                }
                Divider { visible: win.panel }
                ColumnLayout {
                    visible: win.panel
                    Layout.fillWidth: true
                    spacing: 10
                    ColumnLayout {
                        spacing: 2
                        Label { text: "显示内容" }
                        Hint { text: "城市和当前天气总是显示，卡片高度跟着变" }
                    }
                    Flow {
                        Layout.fillWidth: true
                        spacing: 8
                        Repeater {
                            model: win.showKeys
                            delegate: Chip {
                                required property var modelData
                                text: modelData.t
                                sel: win.show.indexOf(modelData.k) >= 0
                                onClicked: win.setShow(modelData.k, !sel)
                            }
                        }
                    }
                }
            }

            // ---- 其他
            SectionTitle { text: "其他" }
            Card {
                SettingRow {
                    title: "天气刷新间隔"
                    desc: "多久取一次天气"
                    SpinBox {
                        Layout.preferredWidth: 140   // DTK 的 SpinBox 不限宽会撑满整行
                        from: 5; to: 180; stepSize: 5
                        editable: true
                        value: win.refreshMin
                        onValueModified: win.refreshMin = value
                    }
                    Label { text: "分钟" }
                }
                Divider {}
                SettingRow {
                    title: "雨雪的帧率"
                    desc: "下雨、下雪、打雷时按它画，越高越流畅、越费 CPU。默认 " + win.defFps + " fps"
                    FpsSlider {
                        from: 5; to: 60
                        fps: win.fps
                        onPicked: v => win.fps = v
                    }
                }
                Divider {}
                SettingRow {
                    title: "云的帧率"
                    desc: "云飘得慢，默认 " + win.defCloudFps + " fps 就够；调高更细腻，也更费 CPU"
                    FpsSlider {
                        from: 1; to: win.maxCloudFps
                        fps: win.cloudFps
                        onPicked: v => win.cloudFps = v
                    }
                }
            }
            Hint {
                Layout.leftMargin: 4
                Layout.topMargin: 4
                text: "配置文件是 " + backend.configPath + "，也可以直接手改（字段见 README），保存后几秒内生效"
            }
        }
    }

    footer: Pane {
        padding: 12
        leftPadding: 24
        rightPadding: 24
        background: Rectangle {
            color: win.palette.window
            Rectangle {
                width: parent.width
                height: 1
                color: win.lineColor
            }
        }
        ColumnLayout {
            anchors.left: parent.left
            anchors.right: parent.right
            spacing: 8
            Label {
                Layout.fillWidth: true
                visible: text !== ""
                wrapMode: Text.Wrap
                readonly property bool showProblem: win.problem !== "" && win.dirty
                color: showProblem || win.statusError ? win.errorColor : win.ink
                text: showProblem ? win.problem : win.status
            }
            RowLayout {
                Layout.fillWidth: true
                spacing: 8
                Rectangle {
                    visible: win.wallState.ok
                    implicitWidth: 8
                    implicitHeight: 8
                    radius: 4
                    color: win.wallState.desktop && win.wallState.lockscreen ? "#2bb673" : "#e8a33d"
                }
                Label {
                    visible: win.wallState.ok
                    opacity: 0.75
                    text: win.wallState.desktop && win.wallState.lockscreen ? "桌面和锁屏都在用天气壁纸"
                          : win.wallState.desktop ? "桌面在用天气壁纸，锁屏没有"
                          : win.wallState.lockscreen ? "锁屏在用天气壁纸，桌面没有" : "现在没在用天气壁纸"
                }
                Button {
                    visible: win.wallState.ok && !(win.wallState.desktop && win.wallState.lockscreen)
                    enabled: !backend.busy
                    text: "设为桌面和锁屏壁纸"
                    onClicked: backend.useWeatherWallpaper()
                }
                Item { Layout.fillWidth: true }
                Button {
                    text: "还原"
                    enabled: win.dirty && !backend.busy
                    onClicked: win.loadCfg()
                }
                Button {
                    text: backend.busy ? "保存中…" : "应用"
                    highlighted: true
                    enabled: win.dirty && !backend.busy && win.problem === ""
                    onClicked: win.save()
                }
            }
        }
    }
}
