// Copyright (C) 2026 UnionTech Software Technology Co., Ltd.
// SPDX-License-Identifier: Apache-2.0 OR LGPL-3.0-only OR GPL-2.0-only OR GPL-3.0-only

#include "treelandwallpapernotifierclient.h"
#include "wwpcontroller.h"

#include <DLog>

#include <QGuiApplication>
#include <QQmlContext>
#include <QQuickView>
#include <QQuickWindow>
#include <QSGRendererInterface>
#include <QTimer>

#include <algorithm>

#ifdef WWP_HAVE_WEBENGINE
#include <QtWebEngineQuick/qtwebenginequickglobal.h>
#endif

DCORE_USE_NAMESPACE;

// WWP：壁纸包可以用 WebEngineView，必须在建 QGuiApplication 之前初始化（只设共享 GL 上下文等属性，
// 真正的 Chromium 进程要等第一个 WebEngineView 出现才起，所以不用网页的壁纸不受影响）
static void initWebEngine()
{
#ifdef WWP_HAVE_WEBENGINE
    QtWebEngineQuick::initialize();
#endif
}

static QSize parseSize(const char *s, QSize fallback)
{
    const QStringList p = QString::fromLocal8Bit(s).split(QLatin1Char('x'));
    if (p.size() != 2)
        return fallback;
    const QSize size(p[0].toInt(), p[1].toInt());
    return size.isValid() ? size : fallback;
}

// WWP：离线渲染壁纸包的一帧（软件光栅 + offscreen，不连 Wayland），用来生成控制中心缩略图和自查画面
//   treeland-wallpaper-factory --snapshot <包目录> <输出文件> [渲染宽x高] [输出宽x高] [场景秒数]
// 输出一律存 PNG（不看后缀），这样可以直接生成 <名字>.wwp 预览图
static int runSnapshot(int argc, char *argv[])
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QQuickWindow::setGraphicsApi(QSGRendererInterface::Software);
    initWebEngine();
    QGuiApplication app(argc, argv);

    const QString source = QString::fromLocal8Bit(argv[2]);
    const QString out = QString::fromLocal8Bit(argv[3]);
    const QSize renderSize = argc > 4 ? parseSize(argv[4], QSize(1440, 960)) : QSize(1440, 960);
    const QSize outSize = argc > 5 ? parseSize(argv[5], renderSize) : renderSize;
    const qreal at = argc > 6 ? QString::fromLocal8Bit(argv[6]).toDouble() : 8.0;

    QQuickView view;
    auto *controller = new WwpController(source, &view);
    controller->freezeAt(at);
    view.rootContext()->setContextProperty(QStringLiteral("wallpaper"), controller);
    view.setResizeMode(QQuickView::SizeRootObjectToView);
    view.loadFromModule("com.treeland.wallfactory", "WwpScene");
    if (!view.rootObject())
        return 1;
    view.resize(renderSize);
    view.show();

    // 等多久再截：manifest 的 snapshotDelay（毫秒），网页场景要等页面加载、宿主发完消息
    const int delay = std::clamp(controller->manifest().value(QStringLiteral("snapshotDelay"), 1000).toInt(), 200, 15000);
    int ret = 1;
    QTimer::singleShot(delay, &app, [&]() {
        QImage img = view.grabWindow();
        if (img.size() != outSize)
            img = img.scaled(outSize, Qt::IgnoreAspectRatio, Qt::SmoothTransformation);
        ret = img.save(out, "PNG") ? 0 : 1;
        app.quit();
    });
    app.exec();
    return ret;
}

int main(int argc, char *argv[])
{
    if (argc >= 4 && qstrcmp(argv[1], "--snapshot") == 0)
        return runSnapshot(argc, argv);

    // The entire rendering framework, including mpv, is based on OpenGL.
    // QQuickWindow::setGraphicsApi(QSGRendererInterface::OpenGL);
    initWebEngine();
    QGuiApplication app(argc, argv);

    app.setOrganizationName("deepin");
    app.setApplicationName("treeland-wallpaper-factory");

    DLogManager::registerJournalAppender();
    WwpController::keepInfoLogging();

    std::unique_ptr<TreelandWallpaperNotifierClientV1> produce;
    produce.reset(new TreelandWallpaperNotifierClientV1);
    produce->instantiate();

    return app.exec();
}
