// SPDX-License-Identifier: Apache-2.0 OR LGPL-3.0-only OR GPL-2.0-only OR GPL-3.0-only
//
// .wwp 壁纸包的宿主：加载包里的 main.qml；加载失败时显示出错画面（照样回 ready，免得 treeland 一直等）。
// `wallpaper` 是 C++ 设置的上下文属性（WwpController）。

import QtQuick

Item {
    id: root

    property string failure: wallpaper.error

    Loader {
        id: loader
        anchors.fill: parent
        source: root.failure === "" ? wallpaper.mainUrl : ""
        onStatusChanged: {
            if (status === Loader.Ready)
                wallpaper.markLoaded()
            else if (status === Loader.Error)
                root.failure = "场景 QML 有错误（详见日志）"
        }
    }

    Rectangle {
        id: fallback
        anchors.fill: parent
        visible: root.failure !== ""
        color: "#1f2b3a"
        onVisibleChanged: if (visible) wallpaper.markLoaded()
        Component.onCompleted: if (visible) wallpaper.markLoaded()

        Text {
            anchors.centerIn: parent
            width: parent.width * 0.8
            horizontalAlignment: Text.AlignHCenter
            wrapMode: Text.Wrap
            color: "#e0e6ee"
            font.pixelSize: Math.max(16, root.height / 36)
            text: "壁纸包加载失败\n" + root.failure + "\n" + wallpaper.source
        }
    }

    // 调试信息（manifest 里 "debug": true 时显示）：看 treeland 事件有没有到、时钟是否在走。
    // 放在顶部居中：底部会被任务栏挡住
    Rectangle {
        visible: wallpaper.debug
        anchors.horizontalCenter: parent.horizontalCenter
        anchors.top: parent.top
        anchors.topMargin: 12
        width: debugText.implicitWidth + 16
        height: debugText.implicitHeight + 8
        radius: 4
        color: "#80000000"

        Text {
            id: debugText
            anchors.centerIn: parent
            color: "white"
            font.pixelSize: Math.max(11, root.height / 80)
            text: "wwp · " + wallpaper.fps + "fps · " + (wallpaper.followPause ? "跟随暂停" : "忽略暂停")
                  + " · treeland: " + (wallpaper.lastEvent === "" ? "—" : wallpaper.lastEvent)
                  + " · rate " + wallpaper.rate.toFixed(2)
                  + " · " + (wallpaper.running ? "运行" : "停止")
                  + " · t=" + wallpaper.time.toFixed(0) + "s"
        }
    }
}
