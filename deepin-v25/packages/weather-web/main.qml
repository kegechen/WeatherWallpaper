import QtQuick
import QtWebEngine

// 天气壁纸（网页版）：WebEngineView 跑 Windows 版的 web/（Canvas 2D，Chromium 在 factory 进程里用 GPU 合成）。
// 宿主是包里的 host.js：在文档创建时注入，提供 window.chrome.webview，联网取天气、发布局，见 host.js 开头。
// manifest.json 的 "host" 对象原样传给 host.js（URL 参数 wwp）。
WebEngineView {
    id: web

    readonly property bool snapshot: Qt.platform.pluginName === "offscreen"   // factory --snapshot 离线渲染缩略图

    backgroundColor: "#1f2b3a"
    settings.showScrollBars: false
    settings.localContentCanAccessRemoteUrls: true   // file:// 页面联网取天气
    settings.localContentCanAccessFileUrls: true     // 读 /etc/wwp/weather.json、底层壁纸

    function syncPause() {
        runJavaScript("window.__wwp && window.__wwp.pause(" + !wallpaper.running + ")")
    }

    // 这一页是不是正显示在锁屏上：锁屏界面会把壁纸放大 1.2 倍、裁掉四周，host.js 据此把卡片层反向缩小抵消掉，极简模式锁屏时也不开播。
    // treeland 0.11 发来的事件（pausePolicy 为 ignore 时也照样记在 lastEvent 里）：
    //   锁屏 = pause（桌面那一项）+ play（Greeter 的锁屏那一项）
    //   解锁 = pause + play + slow_down（桌面的"显示桌面"）；桌面刚挂上这张壁纸 = pause
    // 所以 play 之后一小会儿没跟着 slow_down 就是在锁屏上（同一批事件一起到，150 毫秒足够；等久了卡片会先跟着放大一截再缩回）。一个事件都没收到时也算锁屏：桌面在壁纸 ready 后马上会收到 pause，
    // 收不到说明这一页只出现在锁屏上（treeland 刚重启停在锁屏 / 登录界面，或者它只被设成了锁屏壁纸）
    property bool locked: true
    function syncLock() {
        runJavaScript("window.__wwp && window.__wwp.lock(" + locked + ")")
    }
    function setLocked(v) {
        if (locked === v)
            return
        locked = v
        syncLock()
    }
    Timer {
        id: lockTimer
        interval: 150
        onTriggered: web.setLocked(true)
    }

    Component.onCompleted: {
        var s = WebEngine.script()
        s.name = "wwp-host"
        s.sourceUrl = wallpaper.packageUrl + "host.js"
        s.injectionPoint = WebEngineScript.DocumentCreation
        s.worldId = WebEngineScript.MainWorld
        s.runsOnSubFrames = false
        userScripts.insert(s)
        var q = "wwp=" + encodeURIComponent(JSON.stringify(wallpaper.manifest.host || {}))
        url = wallpaper.packageUrl + "web/index.html?" + q + (snapshot ? "&wwpsnap=1" : "")
    }

    // pausePolicy 为 follow 时，treeland 的 pause / slow_down 停住页面动画（ignore 时 running 一直是 true）
    Connections {
        target: wallpaper
        function onRunningChanged() { web.syncPause() }
        function onLastEventChanged() {
            if (wallpaper.lastEvent === "play") {
                lockTimer.restart()
            } else {   // pause / slow_down(…)
                lockTimer.stop()
                web.setLocked(false)
            }
        }
    }
    onLoadingChanged: if (!loading) { syncPause(); if (!snapshot) syncLock() }

    // 宿主日志和页面报错进 journal（console.log 是 debug 级，会被 Dtk 的日志规则关掉）；连续相同的只记一次
    property string lastLog: ""
    onJavaScriptConsoleMessage: (level, message, line, source) => {
        if (message === lastLog)
            return
        if (message.startsWith("wwp host: ")) {
            lastLog = message
            wallpaper.log(message.substring(10))
        } else if (level !== WebEngineView.InfoMessageLevel) {
            lastLog = message
            wallpaper.log("页面" + (level === WebEngineView.ErrorMessageLevel ? "错误" : "警告") + "：" + message
                          + "（" + source.toString().split("/").pop() + ":" + line + "）")
        }
    }

    onRenderProcessTerminated: (status, code) => {
        wallpaper.log("渲染进程退出 status=" + status + " code=" + code + "，3 秒后重载")
        reloadTimer.start()
    }
    Timer {
        id: reloadTimer
        interval: 3000
        onTriggered: web.reload()
    }
}
