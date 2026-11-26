import QtQuick
import QtWebEngine

Item {
    WebEngineView {
        id: web
        anchors.fill: parent
        url: startUrl
        settings.showScrollBars: false
        settings.localContentCanAccessRemoteUrls: true
        settings.localContentCanAccessFileUrls: true

        onJavaScriptConsoleMessage: (level, message, line, source) => {
            console.log("[js]", message, source + ":" + line)
            if (dumpGpu && message === "PROBE_DONE")
                Qt.quit()
        }
        onLoadingChanged: (info) => {
            console.log("loading", info.status, info.url, info.errorDomain, info.errorCode, info.errorString)
            if (dumpGpu && info.status === WebEngineView.LoadSucceededStatus && String(info.url).startsWith("chrome:"))
                dumpTimer.start()
        }
        onRenderProcessTerminated: (status, code) => console.log("render process terminated", status, code)
    }

    // chrome://gpu 的内容在 <info-view> 的 shadow DOM 里
    Timer {
        id: dumpTimer
        interval: 4000
        onTriggered: web.runJavaScript(
            "(function(){var v=document.querySelector('info-view');return (v&&v.shadowRoot?v.shadowRoot.innerText:document.body.innerText);})()",
            function (text) { console.log("=====GPU=====\n" + text + "\n=====END====="); Qt.quit() })
    }
}
