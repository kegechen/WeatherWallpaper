import QtQuick
import QtQuick.Controls
import QtQuick.Dialogs
import QtQuick.Layouts

// 天气壁纸设置：读 /etc/wwp/weather.json 填进界面，"应用"时整份写回（配置里界面没有的字段原样保留）。
// 壁纸页面（host.js）每 2 秒看一次配置文件，写进去几秒内就生效，不用切换壁纸。字段含义见 deepin-v25/README.md。
ApplicationWindow {
    id: win

    title: "天气壁纸设置"
    width: 800
    height: 700
    minimumWidth: 660
    minimumHeight: 480
    visible: true

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
    readonly property var modes: [
        { m: "weather", t: "天气动画", d: "全屏画天空、云、雨雪和日月" },
        { m: "overlay", t: "壁纸叠加", d: "天气叠在选好的壁纸上，天空半透明，云、日月、雨雪照常" },
        { m: "minimal", t: "极简模式", d: "平时只显示壁纸和卡片，几乎不占 CPU；拿到第一份天气、转雨、下雪时整屏播一段动画" }
    ]

    // ---- 界面上的值：loadCfg 从配置读进来，buildCfg 拼回配置
    property var orig: ({})            // 读到的整份配置
    property string savedSig: ""       // 读进来时 buildCfg 的样子，用来判断改没改
    property string locMode: "auto"    // auto 按 IP 定位 / city 指定城市
    property string cityName: ""
    property string latText: ""
    property string lonText: ""
    property string mode: "weather"    // weather / overlay / minimal
    property string backdrop: ""       // 底层壁纸路径，"" = 没选（极简模式下 = 系统默认壁纸）
    property string lastBackdrop: ""   // 上一次叠加用的壁纸：切回“天气动画”时配置里的 backdrop 会清空（host.js 靠它判断模式），路径记在这里，再切回叠加不用重新选
    property string pendingImage: ""   // 刚从文件里选的图片：保存时由后台决定直接引用还是复制进系统目录
    property real skyOpacity: 0.45
    property real cloudAlpha: 0.72
    property int playSeconds: 30
    property bool festive: true
    property bool panel: true
    property string cardPos: "tr"
    property int cardX: 32
    property int cardY: 32
    property var show: []
    property int refreshMin: 15
    property int fps: 24

    property string status: ""
    property bool statusError: false
    property string ipText: ""
    property bool searching: false
    property string searchMsg: ""
    property var cityResults: []       // 城市搜索结果 { name, detail, lat, lon }（不用 ListModel：deepin 默认没装 QtQml.Models）
    property var wallState: ({ ok: false })
    property var systemWalls: []
    property bool closeAfterSave: false
    property bool forceClose: false

    readonly property bool dirty: savedSig !== "" && JSON.stringify(buildCfg()) !== savedSig
    readonly property string problem: checkProblem()

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

    function loadCfg() {
        var c = backend.load()
        var err = backend.loadError()
        orig = c
        var hasLL = c.lat !== undefined && c.lat !== null && c.lat !== "" && c.lon !== undefined && c.lon !== null && c.lon !== ""
        cityName = typeof c.city === "string" ? c.city : ""
        latText = hasLL ? String(c.lat) : ""
        lonText = hasLL ? String(c.lon) : ""
        locMode = cityName.trim() !== "" || hasLL ? "city" : "auto"
        backdrop = typeof c.backdrop === "string" ? c.backdrop.trim() : ""
        lastBackdrop = typeof c.lastBackdrop === "string" ? c.lastBackdrop.trim() : ""
        pendingImage = ""
        mode = yes(c.minimal) ? "minimal" : backdrop !== "" ? "overlay" : "weather"
        skyOpacity = num(c.skyOpacity, 0.45, 0.05, 1)
        cloudAlpha = num(c.cloudAlpha, 0.72, 0.1, 1)
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
        fps = Math.round(num(c.fps, 24, 5, 60))
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
        } else {
            c.city = cityName.trim()
            var la = Number(latText), lo = Number(lonText)
            if (latText.trim() !== "" && lonText.trim() !== "" && isFinite(la) && isFinite(lo)) {
                c.lat = la
                c.lon = lo
            } else {
                delete c.lat
                delete c.lon
            }
        }
        c.backdrop = mode === "weather" ? "" : backdrop
        c.minimal = mode === "minimal"
        // 上一次叠加用的壁纸：backdrop 在天气动画模式下必须空（它是叠加的开关），路径存 lastBackdrop 供下次切回来用
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
        return c
    }

    function checkProblem() {
        if (locMode === "city") {
            var lt = latText.trim(), ln = lonText.trim()
            if ((lt === "") !== (ln === ""))
                return "纬度和经度要一起填"
            if (lt !== "") {
                var a = Number(lt), b = Number(ln)
                if (!isFinite(a) || Math.abs(a) > 90)
                    return "纬度要在 -90 到 90 之间"
                if (!isFinite(b) || Math.abs(b) > 180)
                    return "经度要在 -180 到 180 之间"
            }
            if (cityName.trim() === "" && lt === "")
                return "请填城市（或经纬度），或者改回自动定位"
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
    function cardRect(W, H) {
        var h = cardHeight(), g = num(orig.ground, 0.0444, 0, 0.3)
        var x = cardPos[1] === "l" ? cardX : cardPos[1] === "r" ? W - cardW - cardX : (W - cardW) / 2 + cardX
        var y = cardPos[0] === "b" ? H * (1 - g) - cardY - h : cardY
        return Qt.rect(Math.max(0, Math.min(x, W - cardW)), Math.max(0, Math.min(y, H - h)), cardW, h)
    }

    // 自动定位时看看 IP 定位到哪里（壁纸用的也是 ip-api.com）
    function ipLocate() {
        if (ipText !== "" && ipText.indexOf("失败") < 0)
            return
        ipText = "正在按 IP 定位…"
        var x = new XMLHttpRequest()
        x.onreadystatechange = function () {
            if (x.readyState !== XMLHttpRequest.DONE)
                return
            try {
                var d = JSON.parse(x.responseText)
                if (d.status !== "success")
                    throw new Error(d.message || d.status)
                var place = [d.city, d.regionName, d.country].filter((s, i, a) => s && a.indexOf(s) === i).join(" · ")
                ipText = "按 IP 定位到：" + place + "（" + d.lat.toFixed(2) + ", " + d.lon.toFixed(2) + "）。走代理、VPN 时可能不准，不准就指定城市"
            } catch (e) {
                ipText = "IP 定位失败（" + (x.status ? "HTTP " + x.status : "网络不通") + "），壁纸会自己过一会儿重试"
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
                    searchMsg = "地名库里没有“" + q + "”，换个写法（如“武汉”），或者直接填经纬度"
            } catch (e) {
                searchMsg = "搜索失败：" + e.message
            }
        }
        x.open("GET", "https://geocoding-api.open-meteo.com/v1/search?count=8&language=zh&format=json&name=" + encodeURIComponent(q))
        x.send()
    }

    function refreshWallState() { wallState = backend.wallpaperState() }

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
            confirmClose.open()
        }
    }

    Component.onCompleted: {
        systemWalls = backend.systemWallpapers()
        loadCfg()
        refreshWallState()
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
            }
        }
    }

    Dialog {
        id: confirmClose
        title: "有修改还没应用"
        modal: true
        width: 400          // 不给宽度的话 implicitWidth 和 anchors.centerIn 互相算，Qt 会报绑定环
        anchors.centerIn: parent
        standardButtons: Dialog.Save | Dialog.Discard | Dialog.Cancel
        Label { text: "关闭前要先应用这些修改吗？" }
        onAccepted: {
            win.closeAfterSave = true
            win.save()
        }
        onDiscarded: {
            win.forceClose = true
            win.close()
        }
    }

    component Section: ColumnLayout {
        property alias title: head.text
        Layout.fillWidth: true
        spacing: 8
        Label {
            id: head
            font.bold: true
            font.pixelSize: 16
            Layout.topMargin: 6
        }
    }
    component Hint: Label {
        Layout.fillWidth: true
        wrapMode: Text.Wrap
        opacity: 0.65
        font.pixelSize: 12
    }

    ScrollView {
        id: sv
        anchors.fill: parent
        contentWidth: availableWidth
        // contentHeight 必须显式给：不写的话靠 childrenRect 推断，ColumnLayout 常常推成 0，
        // 内容比窗口高的部分就永远滚不到（看着就是“窗口太长、下面的设置不见了”）
        contentHeight: content.implicitHeight + 20
        clip: true

        ColumnLayout {
            id: content
            x: 24
            y: 10
            width: sv.availableWidth - 48
            spacing: 14

            Item { implicitHeight: 4 }

            // -------------------------------------------------------------------------------------------------------------- 位置
            Section {
                title: "位置"
                ButtonGroup { id: locGroup }
                RadioButton {
                    text: "自动定位（按 IP）"
                    ButtonGroup.group: locGroup
                    checked: win.locMode === "auto"
                    onClicked: { win.locMode = "auto"; win.ipLocate() }
                }
                Hint {
                    visible: win.locMode === "auto"
                    Layout.leftMargin: 30
                    text: win.ipText
                }
                RadioButton {
                    text: "指定城市"
                    ButtonGroup.group: locGroup
                    checked: win.locMode === "city"
                    onClicked: win.locMode = "city"
                }
                ColumnLayout {
                    visible: win.locMode === "city"
                    Layout.leftMargin: 30
                    Layout.fillWidth: true
                    spacing: 6
                    RowLayout {
                        Layout.fillWidth: true
                        TextField {
                            id: cityField
                            Layout.fillWidth: true
                            placeholderText: "城市名，如 武汉、杭州西湖区"
                            text: win.cityName
                            onTextEdited: {
                                win.cityName = text
                                win.latText = ""   // 改了名字：之前选中的经纬度作废（只填名字时壁纸自己去地名库查）
                                win.lonText = ""
                            }
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
                    Repeater {
                        model: win.cityResults
                        delegate: ItemDelegate {
                            required property var modelData
                            Layout.fillWidth: true
                            text: modelData.name + (modelData.detail ? "　" + modelData.detail : "")
                                  + "　(" + modelData.lat.toFixed(2) + ", " + modelData.lon.toFixed(2) + ")"
                            onClicked: {
                                win.cityName = modelData.name
                                win.latText = modelData.lat.toFixed(4)
                                win.lonText = modelData.lon.toFixed(4)
                                win.cityResults = []
                            }
                        }
                    }
                    RowLayout {
                        Label { text: "纬度" }
                        TextField {
                            Layout.preferredWidth: 120
                            placeholderText: "如 30.5928"
                            text: win.latText
                            onTextEdited: win.latText = text
                        }
                        Label { text: "经度"; Layout.leftMargin: 12 }
                        TextField {
                            Layout.preferredWidth: 120
                            placeholderText: "如 114.3055"
                            text: win.lonText
                            onTextEdited: win.lonText = text
                        }
                    }
                    Hint {
                        text: "搜索后点选一个结果会填好经纬度；只填城市名也行，壁纸会自己去地名库查第一个结果。城市名就是卡片上显示的名字"
                    }
                }
            }

            // -------------------------------------------------------------------------------------------------------------- 画面
            Section {
                title: "画面"
                ButtonGroup { id: modeGroup }
                Repeater {
                    model: win.modes
                    delegate: ColumnLayout {
                        required property var modelData
                        spacing: 0
                        Layout.fillWidth: true
                        RadioButton {
                            text: modelData.t
                            ButtonGroup.group: modeGroup
                            checked: win.mode === modelData.m
                            onClicked: {
                                win.mode = modelData.m
                                // 切回壁纸叠加：接着用上次选过的底层壁纸
                                if (win.mode === "overlay" && win.backdrop === "" && win.lastBackdrop !== "")
                                    win.backdrop = win.lastBackdrop
                            }
                        }
                        Hint {
                            Layout.leftMargin: 30
                            text: modelData.d
                        }
                    }
                }

                // 底层壁纸（叠加、极简模式）
                ColumnLayout {
                    visible: win.mode !== "weather"
                    Layout.fillWidth: true
                    Layout.topMargin: 4
                    spacing: 6
                    RowLayout {
                        Layout.fillWidth: true
                        Label {
                            Layout.fillWidth: true
                            elide: Text.ElideMiddle
                            text: "底层壁纸：" + (win.backdrop !== "" ? win.fileName(win.backdrop)
                                                 : win.mode === "minimal" ? "系统默认壁纸" : "还没选")
                        }
                        Button {
                            text: "从文件选择…"
                            onClicked: fileDialog.open()
                        }
                        Button {
                            text: "用系统默认壁纸"
                            visible: win.mode === "minimal" && win.backdrop !== ""
                            onClicked: { win.backdrop = ""; win.pendingImage = "" }
                        }
                    }
                    GridView {
                        id: wallGrid
                        Layout.fillWidth: true
                        Layout.preferredHeight: cellHeight * 2 + 4
                        cellWidth: Math.floor(width / Math.max(1, Math.floor(width / 132)))
                        cellHeight: 84
                        clip: true
                        model: win.systemWalls
                        boundsBehavior: Flickable.StopAtBounds
                        ScrollBar.vertical: ScrollBar {}
                        delegate: Item {
                            required property string modelData
                            width: wallGrid.cellWidth
                            height: wallGrid.cellHeight
                            Rectangle {
                                anchors.fill: parent
                                anchors.margins: 2
                                radius: 8
                                color: "transparent"
                                border.width: 3
                                border.color: win.backdrop === modelData ? win.palette.highlight : "transparent"
                            }
                            Image {
                                anchors.fill: parent
                                anchors.margins: 6
                                source: backend.fileUrl(modelData)
                                sourceSize: Qt.size(240, 150)
                                fillMode: Image.PreserveAspectCrop
                                asynchronous: true
                                cache: true
                            }
                            MouseArea {
                                anchors.fill: parent
                                cursorShape: Qt.PointingHandCursor
                                onClicked: { win.backdrop = modelData; win.pendingImage = "" }
                            }
                        }
                    }
                    Hint {
                        text: "上面是系统自带的壁纸（" + win.systemWalls.length + " 张）。从文件选的图片会复制一份到系统目录（/var/lib/wwp），原图挪走、删掉都不影响"
                    }
                }

                // 叠加：天空、云的不透明度
                GridLayout {
                    visible: win.mode === "overlay"
                    columns: 3
                    Layout.fillWidth: true
                    Label { text: "天空不透明度" }
                    Slider {
                        Layout.fillWidth: true
                        from: 0.05; to: 1; stepSize: 0.05
                        value: win.skyOpacity
                        onMoved: win.skyOpacity = value
                    }
                    Label { text: Math.round(win.skyOpacity * 100) + "%"; Layout.preferredWidth: 44 }
                    Label { text: "云的不透明度" }
                    Slider {
                        Layout.fillWidth: true
                        from: 0.1; to: 1; stepSize: 0.05
                        value: win.cloudAlpha
                        onMoved: win.cloudAlpha = value
                    }
                    Label { text: Math.round(win.cloudAlpha * 100) + "%"; Layout.preferredWidth: 44 }
                }

                RowLayout {
                    Label { text: "每次播放动画" }
                    SpinBox {
                        from: 5; to: 3600; stepSize: 5
                        editable: true
                        value: win.playSeconds
                        onValueModified: win.playSeconds = value
                    }
                    Label { text: "秒" }
                }
                Hint {
                    text: (win.mode === "minimal" ? "极简模式每段天气动画、" : "") + "雨后转晴时的彩虹都播这么久"
                }
                Switch {
                    text: "节日装饰与彩蛋"
                    checked: win.festive
                    onToggled: win.festive = checked
                }
            }

            // -------------------------------------------------------------------------------------------------------------- 卡片
            Section {
                title: "天气卡片"
                Switch {
                    text: "显示天气卡片"
                    checked: win.panel
                    onToggled: win.panel = checked
                }
                RowLayout {
                    visible: win.panel
                    Layout.fillWidth: true
                    spacing: 20
                    ColumnLayout {
                        Layout.alignment: Qt.AlignTop
                        spacing: 6
                        Label { text: "位置" }
                        GridLayout {
                            columns: 3
                            rowSpacing: 0
                            Repeater {
                                model: win.positions
                                delegate: RadioButton {
                                    required property var modelData
                                    text: modelData.t
                                    checked: win.cardPos === modelData.p
                                    onClicked: {
                                        var wasCenter = win.cardPos[1] === "c", isCenter = modelData.p[1] === "c"
                                        win.cardPos = modelData.p
                                        if (wasCenter !== isCenter)
                                            win.cardX = isCenter ? 0 : 32   // 居中时水平距离是离中线的偏移，靠边时是离边的距离
                                    }
                                }
                            }
                        }
                        GridLayout {
                            columns: 3
                            Label { text: win.cardPos[1] === "c" ? "水平偏移" : "离左右边" }
                            SpinBox {
                                from: win.cardPos[1] === "c" ? -2000 : 0; to: 2000; stepSize: 4
                                editable: true
                                value: win.cardX
                                onValueModified: win.cardX = value
                            }
                            Label { text: "像素" }
                            Label { text: win.cardPos[0] === "b" ? "离任务栏" : "离上边" }
                            SpinBox {
                                from: 0; to: 2000; stepSize: 4
                                editable: true
                                value: win.cardY
                                onValueModified: win.cardY = value
                            }
                            Label { text: "像素" }
                        }
                        Hint {
                            Layout.preferredWidth: 300
                            text: win.cardPos[1] === "c" ? "居中时水平偏移为正往右、为负往左。锁屏上卡片的位置和大小与桌面一样"
                                                         : "锁屏上卡片的位置和大小与桌面一样"
                        }
                    }
                    // 屏幕示意图：按屏幕比例画出卡片的位置和大小
                    Rectangle {
                        id: screenBox
                        readonly property real sw: Screen.width > 0 ? Screen.width : 1440
                        readonly property real sh: Screen.height > 0 ? Screen.height : 960
                        readonly property real k: width / sw
                        readonly property rect card: win.cardRect(sw, sh)
                        Layout.alignment: Qt.AlignTop
                        Layout.fillWidth: true
                        Layout.maximumWidth: 360
                        Layout.preferredHeight: width * sh / sw
                        radius: 6
                        clip: true
                        gradient: Gradient {
                            GradientStop { position: 0; color: "#3f7fcf" }
                            GradientStop { position: 1; color: "#a8cff2" }
                        }
                        Image {
                            anchors.fill: parent
                            visible: win.mode !== "weather"
                            source: win.mode === "weather" ? ""
                                    : backend.fileUrl(win.backdrop !== "" ? win.backdrop : backend.defaultBackdrop)
                            sourceSize.width: 720
                            fillMode: Image.PreserveAspectCrop
                            asynchronous: true
                        }
                        Rectangle {   // 叠加时半透明的天空
                            anchors.fill: parent
                            visible: win.mode === "overlay"
                            opacity: win.skyOpacity * 0.8
                            gradient: Gradient {
                                GradientStop { position: 0; color: "#3f7fcf" }
                                GradientStop { position: 1; color: "#a8cff2" }
                            }
                        }
                        Rectangle {   // 任务栏
                            anchors.left: parent.left
                            anchors.right: parent.right
                            anchors.bottom: parent.bottom
                            height: Math.max(3, parent.height * win.num(win.orig.ground, 0.0444, 0, 0.3))
                            color: "#ccf2f5fa"
                        }
                        Rectangle {   // 卡片
                            x: screenBox.card.x * screenBox.k
                            y: screenBox.card.y * screenBox.k
                            width: screenBox.card.width * screenBox.k
                            height: screenBox.card.height * screenBox.k
                            radius: 14 * screenBox.k
                            color: "#55ffffff"
                            border.color: "#ccffffff"
                            Behavior on x { NumberAnimation { duration: 150 } }
                            Behavior on y { NumberAnimation { duration: 150 } }
                            Behavior on height { NumberAnimation { duration: 150 } }
                        }
                    }
                }
                Label {
                    visible: win.panel
                    text: "显示内容（城市和当前天气总是显示，卡片高度跟着变）"
                    Layout.topMargin: 4
                }
                GridLayout {
                    visible: win.panel
                    columns: 4
                    columnSpacing: 12
                    rowSpacing: 0
                    Repeater {
                        model: win.showKeys
                        delegate: CheckBox {
                            required property var modelData
                            text: modelData.t
                            checked: win.show.indexOf(modelData.k) >= 0
                            onToggled: win.setShow(modelData.k, checked)
                        }
                    }
                }
            }

            // -------------------------------------------------------------------------------------------------------------- 其他
            Section {
                title: "其他"
                GridLayout {
                    columns: 3
                    Label { text: "天气刷新间隔" }
                    SpinBox {
                        from: 5; to: 180; stepSize: 5
                        editable: true
                        value: win.refreshMin
                        onValueModified: win.refreshMin = value
                    }
                    Label { text: "分钟" }
                    Label { text: "帧率" }
                    ComboBox {
                        model: [24, 30, 60]
                        currentIndex: [24, 30, 60].indexOf(win.fps)
                        displayText: win.fps + " fps"
                        onActivated: index => win.fps = model[index]
                    }
                    Label { text: "越高越流畅、越费电" }
                }
                Hint {
                    text: "配置文件是 " + backend.configPath + "，也可以直接手改（字段见 README），保存后几秒内生效"
                }
            }

            Item { implicitHeight: 8 }
        }
    }

    footer: Pane {
        padding: 12
        ColumnLayout {
            anchors.left: parent.left
            anchors.right: parent.right
            spacing: 8
            Label {
                Layout.fillWidth: true
                visible: text !== ""
                wrapMode: Text.Wrap
                readonly property bool showProblem: win.problem !== "" && win.dirty
                color: showProblem || win.statusError ? "#d23f31" : win.palette.windowText
                text: showProblem ? win.problem : win.status
            }
            RowLayout {
                Layout.fillWidth: true
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
