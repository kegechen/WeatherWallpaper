// SPDX-License-Identifier: Apache-2.0 OR LGPL-3.0-only OR GPL-2.0-only OR GPL-3.0-only
//
// WWP（WeatherWallpaper 包）支持：treeland 把 .wwp 路径当成 video 源发给 factory，
// 这里不交给 mpv，而是加载包里的 QML 场景。本对象以上下文属性 `wallpaper` 暴露给场景。
//
// 场景时钟：所有动画都应由 wallpaper.time（秒）驱动。factory 按 manifest 的 fps 推进它，
// treeland 发来的 play / pause / slow_down 按 pausePolicy 处理：
//   follow  和视频壁纸一样：pause 定格，slow_down 在给定时长内减速到停
//   ignore  一直按 fps 动（天气壁纸用这个），事件只记录在 lastEvent 里

#pragma once

#include <QElapsedTimer>
#include <QObject>
#include <QTimer>
#include <QUrl>
#include <QVariantMap>

class WwpController : public QObject
{
    Q_OBJECT
    Q_PROPERTY(QString source READ source CONSTANT)
    Q_PROPERTY(QUrl packageUrl READ packageUrl CONSTANT)
    Q_PROPERTY(QUrl mainUrl READ mainUrl CONSTANT)
    Q_PROPERTY(QVariantMap manifest READ manifest CONSTANT)
    Q_PROPERTY(QString error READ error CONSTANT)
    Q_PROPERTY(int fps READ fps CONSTANT)
    Q_PROPERTY(bool followPause READ followPause CONSTANT)
    Q_PROPERTY(bool debug READ debug CONSTANT)
    Q_PROPERTY(bool running READ running NOTIFY runningChanged)
    Q_PROPERTY(qreal rate READ rate NOTIFY rateChanged)
    Q_PROPERTY(qreal time READ time NOTIFY timeChanged)
    Q_PROPERTY(QString lastEvent READ lastEvent NOTIFY lastEventChanged)

public:
    // file_source 以 .wwp 结尾（不分大小写）就走 WWP。可以是包目录（内含 manifest.json），
    // 也可以是包目录里的文件；安装后登记的是 <包目录>/<名字>.wwp，它本身是一张预览 PNG（给控制中心预览用）
    static bool isWwpSource(const QString &source);

    // 包里是 QML/JS，factory 以 dde 用户身份执行它：只认 root 装进 WWP_TRUSTED_ROOT 的包
    // （包目录到 / 的每一级、manifest、场景入口文件都只有 root 能改），否则本机任意用户在 /tmp 放个包、
    // 再用 wwp-set 设成壁纸，就能借 dde 的身份跑代码。不可信时 reason 写原因，调用方按原版逻辑处理（交给 mpv）
    static bool isTrustedSource(const QString &source, QString *reason = nullptr);

    // Dtk 在 DConfig 加载完后会按 org.deepin.dtk.preference 的 rules（默认 "*.debug=false;*.info=false"）
    // 调 QLoggingCategory::setFilterRules，info 全被关掉，启动后只有第一条能进 journal。
    // WWP 的日志很少（每个壁纸一条加载记录 + 偶尔的 treeland 事件），装一个分类过滤器单独保留它的 info。
    static void keepInfoLogging();

    explicit WwpController(const QString &source, QObject *parent = nullptr);

    QString source() const { return m_source; }
    QUrl packageUrl() const { return m_packageUrl; }
    QUrl mainUrl() const { return m_mainUrl; }
    QVariantMap manifest() const { return m_manifest; }
    QString error() const { return m_error; }
    int fps() const { return m_fps; }
    bool followPause() const { return m_followPause; }
    bool debug() const { return m_debug; }
    bool running() const { return m_running; }
    qreal rate() const { return m_rate; }
    qreal time() const { return m_time; }
    QString lastEvent() const { return m_lastEvent; }

    // treeland 事件（经 WallpaperWindow 转发）
    void setPlay(bool play);
    void slowDown(uint32_t durationMs);

    // 离线截图用：时钟固定在 seconds，不再推进
    void freezeAt(qreal seconds);

    // 场景加载完（或已显示出错画面）时由 WwpScene.qml 调用；只生效一次
    Q_INVOKABLE void markLoaded();

    // 场景要进 journal 的日志（info 级，带壁纸包名）
    Q_INVOKABLE void log(const QString &message);

Q_SIGNALS:
    void loaded();
    void runningChanged();
    void rateChanged();
    void timeChanged();
    void lastEventChanged();

private:
    void tick();
    void setRunning(bool running);
    void setRate(qreal rate);
    void setLastEvent(const QString &event);

    QString m_source;
    QUrl m_packageUrl;
    QUrl m_mainUrl;
    QVariantMap m_manifest;
    QString m_error;
    int m_fps = 24;
    bool m_followPause = true;
    bool m_debug = false;

    bool m_running = true;
    bool m_frozen = false;
    bool m_loaded = false;
    qreal m_rate = 1.0;
    qreal m_time = 0.0;
    QString m_lastEvent;

    QTimer m_timer;
    QElapsedTimer m_clock;
    qint64 m_lastTickMs = 0;

    bool m_slowing = false;
    uint32_t m_slowDuration = 0;
    QElapsedTimer m_slowClock;
};
