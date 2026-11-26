// SPDX-License-Identifier: Apache-2.0 OR LGPL-3.0-only OR GPL-2.0-only OR GPL-3.0-only

#include "wwpcontroller.h"
#include "loggings.h"

#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonDocument>
#include <QJsonObject>

#include <algorithm>
#include <cmath>

#include <sys/stat.h>

// install.sh / deb 装壁纸包的位置（和 install.sh 的 WWP_DIR 一致）
#ifndef WWP_TRUSTED_ROOT
#define WWP_TRUSTED_ROOT "/usr/local/share/wallpapers/wwp"
#endif

// 原版的 treeland.wallfactory 默认只记 warning；WWP 的加载和 treeland 事件默认记 info，方便在 journal 里排查
Q_LOGGING_CATEGORY(WWP, "treeland.wallfactory.wwp", QtInfoMsg)

static QLoggingCategory::CategoryFilter s_previousFilter = nullptr;

static void wwpCategoryFilter(QLoggingCategory *category)
{
    if (s_previousFilter)
        s_previousFilter(category);
    if (qstrcmp(category->categoryName(), "treeland.wallfactory.wwp") == 0)
        category->setEnabled(QtInfoMsg, true);
}

void WwpController::keepInfoLogging()
{
    s_previousFilter = QLoggingCategory::installFilter(wwpCategoryFilter);
}

bool WwpController::isWwpSource(const QString &source)
{
    return source.endsWith(QStringLiteral(".wwp"), Qt::CaseInsensitive);
}

// 属 root，别人改不了（组可写只允许 root 组）。路径已经 canonical 过，没有符号链接
static bool rootOwnedNotShared(const QString &path)
{
    struct stat st;
    if (::stat(QFile::encodeName(path).constData(), &st) != 0)
        return false;
    return st.st_uid == 0 && !(st.st_mode & S_IWOTH) && (!(st.st_mode & S_IWGRP) || st.st_gid == 0);
}

bool WwpController::isTrustedSource(const QString &source, QString *reason)
{
    auto fail = [reason](const QString &why) {
        if (reason)
            *reason = why;
        return false;
    };
    const QString root = QFileInfo(QStringLiteral(WWP_TRUSTED_ROOT)).canonicalFilePath();
    if (root.isEmpty())
        return fail(QStringLiteral(WWP_TRUSTED_ROOT " 不存在"));
    const QFileInfo fi(source);
    const QString dir = fi.isDir() ? fi.canonicalFilePath() : QFileInfo(fi.absolutePath()).canonicalFilePath();
    if (dir.isEmpty())
        return fail(QStringLiteral("壁纸包不存在"));
    if (!dir.startsWith(root + QLatin1Char('/')))
        return fail(QStringLiteral("不在 " WWP_TRUSTED_ROOT " 下"));
    // 包目录一直到 /：每一级都只有 root 能改（否则可以把整个包换掉）
    for (QString d = dir;; d = QFileInfo(d).path()) {
        if (!rootOwnedNotShared(d))
            return fail(d + QStringLiteral(" 不是 root 所有，或者别人可写"));
        if (d == QLatin1String("/"))
            break;
    }
    // manifest 和场景入口文件：在包目录里、只有 root 能改
    const QString manifest = QDir(dir).filePath(QStringLiteral("manifest.json"));
    QString main = QStringLiteral("main.qml");
    if (QFileInfo::exists(manifest)) {
        if (!rootOwnedNotShared(manifest))
            return fail(manifest + QStringLiteral(" 不是 root 所有，或者别人可写"));
        QFile f(manifest);
        if (f.open(QIODevice::ReadOnly))
            main = QJsonDocument::fromJson(f.readAll()).object().value(QStringLiteral("main")).toString(main);
    }
    const QString mainPath = QFileInfo(QDir(dir).filePath(main)).canonicalFilePath();
    if (!mainPath.isEmpty()) {   // 不存在的话 WwpController 自己报“找不到场景文件”
        if (!mainPath.startsWith(dir + QLatin1Char('/')))
            return fail(QStringLiteral("场景文件不在包目录里：") + main);
        if (!rootOwnedNotShared(mainPath))
            return fail(mainPath + QStringLiteral(" 不是 root 所有，或者别人可写"));
    }
    return true;
}

WwpController::WwpController(const QString &source, QObject *parent)
    : QObject(parent)
    , m_source(source)
{
    // source 可以是包目录，也可以是包目录里的任意文件（安装后用的是 <包>/<名字>.wwp：一张预览 PNG，
    // 控制中心按图片读它显示“当前壁纸”预览，原版 factory 拿到它也能交给 mpv 显示成静态图）
    const QFileInfo fi(source);
    QString dir;
    if (fi.isDir())
        dir = fi.absoluteFilePath();
    else if (fi.isFile())
        dir = fi.absolutePath();
    else
        m_error = QStringLiteral("壁纸包不存在");
    const QString manifestPath = dir.isEmpty() ? QString() : QDir(dir).filePath(QStringLiteral("manifest.json"));

    if (m_error.isEmpty() && QFile::exists(manifestPath)) {
        QFile f(manifestPath);
        if (!f.open(QIODevice::ReadOnly)) {
            m_error = QStringLiteral("manifest 读不了：") + f.errorString();
        } else {
            QJsonParseError pe;
            const QJsonDocument doc = QJsonDocument::fromJson(f.readAll(), &pe);
            if (!doc.isObject())
                m_error = QStringLiteral("manifest 不是合法 JSON 对象：") + pe.errorString();
            else
                m_manifest = doc.object().toVariantMap();
        }
    }

    if (m_error.isEmpty()) {
        m_packageUrl = QUrl::fromLocalFile(dir + QLatin1Char('/'));
        const QString main = m_manifest.value(QStringLiteral("main"), QStringLiteral("main.qml")).toString();
        const QString mainPath = QDir(dir).filePath(main);
        if (QFile::exists(mainPath))
            m_mainUrl = QUrl::fromLocalFile(mainPath);
        else
            m_error = QStringLiteral("找不到场景文件 ") + main;
    }

    m_fps = std::clamp(m_manifest.value(QStringLiteral("fps"), 24).toInt(), 1, 60);
    m_followPause = m_manifest.value(QStringLiteral("pausePolicy"), QStringLiteral("follow")).toString()
        != QStringLiteral("ignore");
    m_debug = m_manifest.value(QStringLiteral("debug"), false).toBool();

    m_timer.setTimerType(Qt::PreciseTimer);
    m_timer.setInterval(qRound(1000.0 / m_fps));
    connect(&m_timer, &QTimer::timeout, this, &WwpController::tick);
    m_clock.start();
    m_lastTickMs = 0;
    m_timer.start();

    qCInfo(WWP) << "wwp:" << source << "fps" << m_fps
                      << "pausePolicy" << (m_followPause ? "follow" : "ignore")
                      << (m_error.isEmpty() ? QString() : QStringLiteral("error: ") + m_error);
}

void WwpController::log(const QString &message)
{
    // 走 WWP 分类（keepInfoLogging 保留了它的 info），场景的 console.log 是 debug 级，会被 Dtk 的规则关掉
    qCInfo(WWP).noquote() << "wwp:" << QFileInfo(m_source).fileName() << message;
}

void WwpController::markLoaded()
{
    if (m_loaded)
        return;
    m_loaded = true;
    Q_EMIT loaded();
}

void WwpController::setPlay(bool play)
{
    setLastEvent(play ? QStringLiteral("play") : QStringLiteral("pause"));
    if (!m_followPause || m_frozen)
        return;
    m_slowing = false;
    setRate(1.0);
    setRunning(play);
}

void WwpController::slowDown(uint32_t durationMs)
{
    setLastEvent(QStringLiteral("slow_down(%1)").arg(durationMs));
    if (!m_followPause || m_frozen || !m_running)
        return;
    m_slowing = true;
    m_slowDuration = std::max<uint32_t>(durationMs, 1);
    m_slowClock.restart();
}

void WwpController::freezeAt(qreal seconds)
{
    m_frozen = true;
    m_debug = false;   // 截图（缩略图）不要调试角标；freezeAt 在加载 QML 之前调用，CONSTANT 属性这时改还来得及
    m_slowing = false;
    setRunning(false);
    m_time = seconds;
    Q_EMIT timeChanged();
}

void WwpController::tick()
{
    const qint64 now = m_clock.elapsed();
    // 挂起/卡顿后不要一下子跳很远
    const qreal dt = std::min<qreal>((now - m_lastTickMs) / 1000.0, 0.25);
    m_lastTickMs = now;

    if (m_slowing) {
        // 和 mpv 那边一样用 easeOutExpo：先快后慢地降到 0
        const qreal t = std::clamp(qreal(m_slowClock.elapsed()) / m_slowDuration, qreal(0), qreal(1));
        const qreal ease = t >= 1 ? 1 : 1 - std::pow(2, -10 * t);
        const qreal r = 1 - ease;
        if (t >= 1 || r < 0.02) {
            m_slowing = false;
            setRate(1.0);
            setRunning(false);
            return;
        }
        setRate(r);
    }

    m_time += dt * m_rate;
    Q_EMIT timeChanged();
}

void WwpController::setRunning(bool running)
{
    if (m_running == running)
        return;
    m_running = running;
    if (running) {
        m_lastTickMs = m_clock.elapsed();
        m_timer.start();
    } else {
        m_timer.stop();
    }
    Q_EMIT runningChanged();
}

void WwpController::setRate(qreal rate)
{
    if (qFuzzyCompare(m_rate, rate))
        return;
    m_rate = rate;
    Q_EMIT rateChanged();
}

void WwpController::setLastEvent(const QString &event)
{
    qCInfo(WWP) << "wwp:" << m_source << "treeland event" << event;
    m_lastEvent = event;
    Q_EMIT lastEventChanged();
}
